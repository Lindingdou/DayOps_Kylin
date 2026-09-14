#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把 src/Data/Migrations/*.sql(SQLite 方言)翻译成 src/Data/MigrationsPg/*.sql(openGauss)。

用生成器而不是手改 51 个文件的理由同前: 手改一次就散了, 以后 SQLite 版加迁移 PG 版必然漏跟;
规则集中在这里可复核; 遇到没见过的构造直接 abort, 不静默产出"看着像样、跑起来炸"的 SQL。

**openGauss 内核衍生自 PostgreSQL 9.2**, 不能按现代 PG 写。已避开的现代语法:
  · GENERATED ... AS IDENTITY (PG 10+)  -> 用 SERIAL
  · EXECUTE FUNCTION           (PG 11+) -> 用 EXECUTE PROCEDURE
  · CREATE INDEX IF NOT EXISTS (PG 9.5+) -> 去掉(迁移只跑一次, 有登记表保证)

相对达梦, 大量东西**不用改**: TEXT/INTEGER 是 PG 原生类型; date/status/value/year 这些列名
在 PG 里是非保留关键字, 不必加引号; 参数前缀仍是 '@'(Npgsql 原生认)。
"""
import re, os, glob, collections

SRC = 'src/Data/Migrations'
DST = 'src/Data/MigrationsPg'

report = collections.Counter()
serial_tables = []          # (表, 自增列) —— 末尾要修正序列
_fn_emitted = set()         # 每个输出文件里触发器函数只需建一次


def mask_literals(s):
    """把字符串字面量挖出来占位, 免得后续替换伤到数据。'' 是转义的单引号。"""
    lits, out, i, n = [], [], 0, len(s)
    while i < n:
        if s[i] == "'":
            j = i + 1
            while j < n:
                if s[j] == "'":
                    if j + 1 < n and s[j + 1] == "'":
                        j += 2
                        continue
                    break
                j += 1
            lits.append(s[i:j + 1])
            out.append('\x00L%d\x00' % (len(lits) - 1))
            i = j + 1
        else:
            out.append(s[i])
            i += 1
    return ''.join(out), lits


def unmask(s, lits):
    return re.sub(r'\x00L(\d+)\x00', lambda m: lits[int(m.group(1))], s)


def strip_comments(s):
    """仅供兜底检查用: 注释里出现某些字样不算漏网。"""
    s = re.sub(r'/\*.*?\*/', ' ', s, flags=re.S)
    return re.sub(r'--[^\n]*', ' ', s)


def map_coldef(line, table):
    """一行列定义: SQLite 类型 -> PG 类型; 自增主键 -> SERIAL。"""
    m = re.match(r'^(\s+)([a-z0-9_]+)(\s+)(TEXT|REAL|INTEGER)\b(.*)$', line, re.I)
    if not m:
        return line
    ind, col, sp, ty, rest = m.groups()
    ty = ty.upper()

    if ty == 'INTEGER' and re.search(r'PRIMARY\s+KEY\s+AUTOINCREMENT', rest, re.I):
        # PG 9.2 没有 GENERATED AS IDENTITY, 用 BIGSERIAL(不是 SERIAL, 见下面类型宽度那条)。
        # BIGSERIAL 允许 INSERT 时显式给值(种子数据正是这么干的), 但那样序列不会前进 ——
        # 文件末尾会补 setval 修正, 否则之后第一条自增插入就主键冲突。
        rest = re.sub(r'PRIMARY\s+KEY\s+AUTOINCREMENT', 'PRIMARY KEY', rest, flags=re.I)
        serial_tables.append((table, col))
        report['自增主键 -> BIGSERIAL'] += 1
        return '%s%s%sBIGSERIAL%s' % (ind, col, sp, rest)

    # INTEGER 必须映射成 **BIGINT 而不是 INTEGER**: SQLite 的 INTEGER 本来就是 64 位,
    # 驱动一律按 Int64 返回, 数据层的 record 也就都声明成 long。映射成 PG 的 INTEGER(32 位)
    # 之后驱动返回 Int32, Dapper 找不到匹配的构造函数, 整表物化直接失败 ——
    # 实测被 CoalSeamDefs / CoalBoreholes 等 4 个方法抓到。既是类型宽度问题, 也是物化问题。
    new = {'TEXT': 'TEXT', 'REAL': 'DOUBLE PRECISION', 'INTEGER': 'BIGINT'}[ty]
    report['%s -> %s' % (ty, new)] += 1
    return '%s%s%s%s%s' % (ind, col, sp, new, rest)


TRIG_FN_TOUCH = """CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;"""

TRIG_FN_TOUCH_VER = """CREATE OR REPLACE FUNCTION fn_touch_row() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    NEW.row_version := OLD.row_version + 1;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;"""


def rewrite_trigger(block, out_key):
    """
    SQLite:  AFTER UPDATE ON t FOR EACH ROW BEGIN UPDATE t SET updated_at=CURRENT_TIMESTAMP[, row_version=NEW.row_version+1] WHERE pk=NEW.pk; END;
    PG    :  BEFORE UPDATE ON t FOR EACH ROW EXECUTE PROCEDURE fn_touch_*();

    必须换成 BEFORE + 函数赋值: 原写法是"更新 t 时回头再 UPDATE t 自己", 在 PG 下会递归触发
    (PG 的触发器默认就会递归), 而 BEFORE 里改 NEW 无递归、无额外写入, 语义等价且更省。
    PG 的触发器体必须放在函数里, 不能内联 —— 全库 31 个触发器的时间列都叫 updated_at、
    版本列都叫 row_version, 所以一个共用函数就够, 不必一表一个。
    """
    m = re.search(
        r'CREATE\s+TRIGGER\s+(?:IF\s+NOT\s+EXISTS\s+)?(\w+)\s*'
        r'AFTER\s+UPDATE\s+ON\s+(\w+)\s*'
        r'FOR\s+EACH\s+ROW\s*'
        r'BEGIN\s*'
        r'UPDATE\s+\2\s+SET\s+(\w+)\s*=\s*CURRENT_TIMESTAMP'
        r'(\s*,\s*(\w+)\s*=\s*NEW\.\5\s*\+\s*1)?'
        r'\s+WHERE\s+.*?;\s*'
        r'END\s*;', block, re.S | re.I)
    if not m:
        raise SystemExit('!! 触发器不符合已知模板, 拒绝猜测:\n' + block[:300])

    name, tbl, col, vercol = m.group(1), m.group(2), m.group(3), m.group(5)
    if col.lower() != 'updated_at' or (vercol and vercol.lower() != 'row_version'):
        raise SystemExit('!! 触发器列名超出共用函数假设(%s/%s), 拒绝猜测' % (col, vercol))

    fn = 'fn_touch_row' if vercol else 'fn_touch_updated_at'
    prefix = ''
    if (out_key, fn) not in _fn_emitted:
        _fn_emitted.add((out_key, fn))
        prefix = (TRIG_FN_TOUCH_VER if vercol else TRIG_FN_TOUCH) + '\n\n'

    report['触发器 -> BEFORE + 共用函数'] += 1
    if vercol:
        report['触发器 含行版本号自增'] += 1

    return (prefix +
            'DROP TRIGGER IF EXISTS %s ON %s;\n'
            'CREATE TRIGGER %s\n'
            'BEFORE UPDATE ON %s\n'
            'FOR EACH ROW\n'
            'EXECUTE PROCEDURE %s();' % (name, tbl, name, tbl, fn))


def load_keys():
    """
    从全部 SQLite 迁移里抽每张表的主键与唯一键, 供生成 ON CONFLICT 的冲突目标。
    PG 的 ON CONFLICT 必须指定一组"有唯一约束的列"; 猜错就是运行时报错, 所以只用真实约束。
    """
    src = ''
    for f in sorted(glob.glob(os.path.join(SRC, '*.sql'))):
        src += open(f, encoding='utf-8').read() + '\n'

    keys = {}
    for m in re.finditer(r'CREATE TABLE(?:\s+IF NOT EXISTS)?\s+(\w+)\s*\((.*?)\n\);', src, re.S | re.I):
        t, b = m.group(1), m.group(2)
        cand = []
        tm = re.search(r'\n\s*PRIMARY KEY\s*\(([^)]*)\)', b, re.I)
        if tm:
            cand.append([c.strip() for c in tm.group(1).split(',')])
        else:
            cm = re.search(r'\n\s*([a-z0-9_]+)\s+\w+[^\n,]*PRIMARY KEY', b, re.I)
            if cm:
                cand.append([cm.group(1)])
        for um in re.finditer(r'\n\s*UNIQUE\s*\(([^)]*)\)', b, re.I):
            cand.append([c.strip() for c in um.group(1).split(',')])
        # 列级唯一约束: `serial_number TEXT UNIQUE,` —— 漏了这种写法会让键列混进
        # ON DUPLICATE KEY UPDATE 的 SET 里, openGauss 直接拒绝:
        #   0A000: don't allow update on primary key or unique key
        # (实测被 V002 的 equipment.serial_number / asset_code 抓到过)
        for um in re.finditer(r'\n\s*([a-z0-9_]+)\s+\w+(?:\([^)]*\))?[^\n,]*\bUNIQUE\b', b, re.I):
            col = um.group(1)
            if col.upper() != 'UNIQUE':
                cand.append([col])
        keys[t] = cand

    # 表外的唯一索引也算。带表达式的(如 IFNULL(design_version,''))**不能跳过** ——
    # 这些键现在只用于"把键列排除出 ON DUPLICATE KEY UPDATE 的 SET 列表", 漏一个就报
    # 0A000 don't allow update on primary/unique key(实测被 V038 的 seam_bench_param 抓到)。
    # 表达式里的列名照样是唯一键的一部分, 逐个揪出来。宁可多排除, 不可漏。
    FUNCS = {'ifnull', 'coalesce', 'lower', 'upper', 'trim', 'cast', 'nvl', 'abs', 'round'}
    idx = re.compile(r'CREATE UNIQUE INDEX(?:\s+IF NOT EXISTS)?\s+\w+\s+ON\s+(\w+)\s*\((.*?)\);', re.S | re.I)
    for m in idx.finditer(src):
        t, cols = m.group(1), m.group(2)
        if '(' not in cols:
            keys.setdefault(t, []).append([c.strip() for c in cols.split(',')])
            continue
        names = [n for n in re.findall(r'\b[a-z_][a-z0-9_]*\b', cols, re.I)
                 if n.lower() not in FUNCS]
        for n in names:                       # 拆成单列条目, 只为逐列排除
            keys.setdefault(t, []).append([n])
    return keys


KEYS = None


def stmt_end(body, i):
    """从 i 起找语句结尾的分号(字面量已掩蔽, 直接找即可)。"""
    j = body.find(';', i)
    return j if j >= 0 else len(body)


def add_conflict_clauses(body):
    """
    冲突处理。**这里的写法是在真实 openGauss 6.0.0-lite 上实测定下来的, 不是照搬 PG 文档。**

    实测结论(见 OpenGaussIntegrationTests):
      · `ON CONFLICT` 根本不支持 —— DO NOTHING 和 DO UPDATE 都报 42601 语法错。
        官方文档说支持, 社区论坛说轻量版不支持, 实测证明论坛是对的。
      · 支持 MySQL 风格的 `ON DUPLICATE KEY UPDATE`, 且**不必指定冲突键** ——
        这反而比 PG 的写法省事: 原先要为"没提供主键"的 6 张表特殊处理, 现在不用了。
      · 引用新行的值用 `VALUES(列)`, 不是 PG 的 `EXCLUDED.列`。
      · 自赋值(用于"冲突就跳过")**必须挑非键列**, 拿主键自赋值会被拒:
        0A000: INSERT ON DUPLICATE KEY UPDATE don't allow update on primary key or unique key

    于是:
      INSERT OR IGNORE  -> … ON DUPLICATE KEY UPDATE <非键列> = <非键列>   (空操作)
      INSERT OR REPLACE -> … ON DUPLICATE KEY UPDATE <非键列> = VALUES(<非键列>), …

    整条语句的列全是键列时退化成普通 INSERT: 没有可更新的非键列, 而 SQLite 下
    OR REPLACE 对这种行的效果也就是"要么插入要么保持原样", 与普通 INSERT 冲突后
    报错的差别只在报不报错 —— 宁可让它报出来, 也不猜。
    """
    out, i = [], 0
    pat = re.compile(r'\bINSERT\s+OR\s+(IGNORE|REPLACE)\s+INTO\s+(\w+)\s*\(([^)]*)\)', re.I)
    while True:
        m = pat.search(body, i)
        if not m:
            out.append(body[i:])
            break
        kind, table = m.group(1).upper(), m.group(2)
        cols = [c.strip() for c in m.group(3).split(',')]
        end = stmt_end(body, m.end())

        head = 'INSERT INTO %s (%s)' % (table, m.group(3))
        mid = body[m.end():end]

        # 该表所有键列(主键 + 唯一键)的并集 —— ON DUPLICATE KEY UPDATE 不许改这些
        keycols = {c for cand in KEYS.get(table, []) if cand for c in cand}
        setable = [c for c in cols if c not in keycols]

        if not setable:
            tail = ''
            report['退化为普通 INSERT(无非键列可更新)'] += 1
        elif kind == 'IGNORE':
            # 自赋值 = 空操作。挑第一个非键列。
            tail = ' ON DUPLICATE KEY UPDATE %s = %s' % (setable[0], setable[0])
            report['INSERT OR IGNORE -> ON DUPLICATE KEY UPDATE(空操作)'] += 1
        else:
            tail = ' ON DUPLICATE KEY UPDATE %s' % ', '.join(
                '%s = VALUES(%s)' % (c, c) for c in setable)
            report['INSERT OR REPLACE -> ON DUPLICATE KEY UPDATE'] += 1

        out.append(body[i:m.start()])
        out.append(head + mid + tail)
        i = end
    return rewrite_native_upsert(''.join(out))


def rewrite_native_upsert(body):
    """
    SQLite 3.24+ 自己也支持 `ON CONFLICT(键) DO UPDATE SET a = excluded.a, …`,
    源迁移里就直接这么写了一处(V002 设备种子)。它不经过 INSERT OR REPLACE 那条路,
    得单独翻。openGauss 同样不认 ON CONFLICT, 转成:
        ON DUPLICATE KEY UPDATE a = VALUES(a), …
    冲突目标列要丢掉 —— 一来 ON DUPLICATE KEY 不需要指定, 二来键列不许出现在 SET 里。
    """
    pat = re.compile(r'\bON\s+CONFLICT\s*\(([^)]*)\)\s*DO\s+UPDATE\s+SET\s+(.*?)(?=;)', re.I | re.S)

    def sub(m):
        # 冲突目标列要排除, **该表其它唯一键列同样要排除** —— openGauss 不允许在
        # ON DUPLICATE KEY UPDATE 里更新任何主键/唯一键列, 不只是冲突目标那几个。
        # (实测: equipment 的 serial_number / asset_code 是列级 UNIQUE, 漏掉就报
        #  0A000 don't allow update on primary key or unique key)
        target = {c.strip() for c in m.group(1).split(',')}
        im = None
        for cand in re.finditer(r'\bINSERT\s+INTO\s+(\w+)', body[:m.start()], re.I):
            im = cand
        if im:
            for k in KEYS.get(im.group(1), []):
                target.update(k)
        sets = []
        for asg in m.group(2).split(','):
            am = re.match(r'\s*(\w+)\s*=\s*excluded\.(\w+)\s*', asg, re.I)
            if not am:
                raise SystemExit('!! 不认识的 upsert 赋值, 拒绝猜测: %r' % asg[:60])
            col = am.group(1)
            if col in target:          # 键列不能更新
                continue
            sets.append('%s = VALUES(%s)' % (col, am.group(2)))
        if not sets:
            raise SystemExit('!! upsert 去掉键列后没有可更新列: %r' % m.group(0)[:80])
        report['原生 ON CONFLICT -> ON DUPLICATE KEY UPDATE'] += 1
        return 'ON DUPLICATE KEY UPDATE\n    ' + ',\n    '.join(sets)

    return pat.sub(sub, body)


def translate(sql, fname):
    body, lits = mask_literals(sql)
    out_key = fname

    # 触发器整块换掉(先做, 免得后面的规则伤到它内部)
    body = re.sub(r'CREATE\s+TRIGGER.*?\bEND\s*;',
                  lambda m: rewrite_trigger(m.group(0), out_key), body, flags=re.S | re.I)

    # 独立的 `DROP TRIGGER IF EXISTS 名;`(V051 重建触发器时用)在 openGauss 上非法:
    #   0A000: drop trigger without table name only support in B-format database
    # 它要求 `DROP TRIGGER 名 ON 表`。而上面重写出来的 CREATE 已经自带了带表名的 DROP,
    # 所以这里直接删掉这些裸 DROP —— 删掉而不是补表名, 免得同一个触发器被 DROP 两次。
    body, n = re.subn(r'[ \t]*DROP\s+TRIGGER\s+IF\s+EXISTS\s+\w+\s*;[ \t]*\n?', '', body, flags=re.I)
    if n:
        report['删掉无表名的 DROP TRIGGER(重写后的 CREATE 已自带)'] += n

    # CREATE TABLE IF NOT EXISTS: PG 9.1+ 支持, 保留。
    # CREATE INDEX IF NOT EXISTS: PG 9.5 才有, openGauss 未确认 -> 去掉。
    # 迁移只跑一次(有登记表), 去掉是安全的; V040 那处重建索引源脚本自己带了 DROP INDEX IF EXISTS。
    body, n = re.subn(r'\b(CREATE\s+(?:UNIQUE\s+)?INDEX)\s+IF\s+NOT\s+EXISTS\b', r'\1', body, flags=re.I)
    report['CREATE INDEX 去掉 IF NOT EXISTS'] += n

    # 列定义
    out, in_table, cur_table = [], False, ''
    for line in body.split('\n'):
        mt = re.match(r'\s*CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+(\w+)', line, re.I)
        if mt:
            in_table, cur_table = True, mt.group(1)
        elif in_table and re.match(r'\s*\)\s*;?\s*$', line):
            in_table = False
        elif in_table:
            line = map_coldef(line, cur_table)
        out.append(line)
    body = '\n'.join(out)

    # ALTER TABLE ... ADD COLUMN
    def _alter(m):
        ty = m.group(3).upper()
        new = {'TEXT': 'TEXT', 'REAL': 'DOUBLE PRECISION', 'INTEGER': 'BIGINT'}[ty]
        report['ADD COLUMN %s -> %s' % (ty, new)] += 1
        return '%s%s %s' % (m.group(1), m.group(2), new)
    body = re.sub(r'(ALTER\s+TABLE\s+\w+\s+ADD\s+COLUMN\s+)([a-z0-9_]+)\s+(TEXT|REAL|INTEGER)\b',
                  _alter, body, flags=re.I)

    # INSERT OR IGNORE / OR REPLACE -> ON CONFLICT 子句(在已掩蔽字面量的文本上做, 分号可靠)
    body = add_conflict_clauses(body)

    # 函数
    body, n = re.subn(r'\bIFNULL\s*\(', 'COALESCE(', body, flags=re.I)
    report['IFNULL -> COALESCE'] += n
    body, n = re.subn(r'\bdate\s*\(\s*\x00L\d+\x00\s*\)', 'CURRENT_DATE', body, flags=re.I)
    report["date('now') -> CURRENT_DATE"] += n
    # CAST(x AS INTEGER) 在 PG 下合法, 不动。

    body = unmask(body, lits)

    # 兜底: 不该再有 SQLite 独有写法漏网
    chk = strip_comments(body)
    for bad, why in [(r'\bAUTOINCREMENT\b', '自增没换掉'),
                     (r'\bPRAGMA\b', 'PRAGMA 不该出现'),
                     (r'\bINSERT\s+OR\b', 'INSERT OR 没换掉'),
                     (r'\bIFNULL\s*\(', 'IFNULL 没换掉'),
                     (r'\bREAL\b', 'REAL 没换成 DOUBLE PRECISION'),
                     (r'\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\b', '索引的 IF NOT EXISTS 没去掉'),
                     (r'\bEXECUTE\s+FUNCTION\b', 'EXECUTE FUNCTION 是 PG11+ 语法'),
                     (r'\bGENERATED\s+.*?\bAS\s+IDENTITY\b', 'IDENTITY 是 PG10+ 语法')]:
        if re.search(bad, chk, re.I):
            raise SystemExit('!! %s: %s' % (fname, why))
    return body


def main():
    global KEYS
    KEYS = load_keys()
    os.makedirs(DST, exist_ok=True)
    files = sorted(glob.glob(os.path.join(SRC, '*.sql')))
    if not files:
        raise SystemExit('!! %s 下没有脚本' % SRC)

    outputs = []
    for f in files:
        base = os.path.basename(f)
        outputs.append((base, translate(open(f, encoding='utf-8').read(), base)))

    # 序列修正: 种子里带显式 id 插进 SERIAL 列, 序列不会跟着走,
    # 不修的话之后第一条自增插入必然主键冲突。放最后一个迁移的末尾, 建库时一次性对齐。
    if serial_tables:
        fix = ['', '-- ─── 序列对齐 ────────────────────────────────────────────────────────────',
               '-- 种子数据是带显式主键插入的, SERIAL 的序列不会自动前进;',
               '-- 不在这里对齐, 之后第一条自增插入就会撞主键。']
        for t, c in sorted(set(serial_tables)):
            fix.append("SELECT setval(pg_get_serial_sequence('%s','%s'), "
                       "COALESCE((SELECT MAX(%s) FROM %s), 1));" % (t, c, c, t))
        base, txt = outputs[-1]
        outputs[-1] = (base, txt.rstrip() + '\n' + '\n'.join(fix) + '\n')
        report['序列对齐 setval'] += len(set(serial_tables))

    for base, txt in outputs:
        header = ('-- 由 build/sqlite2pg.py 从 %s 自动生成, 请勿手改。\n'
                  '-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py\n\n' % base)
        with open(os.path.join(DST, base), 'w', encoding='utf-8', newline='\n') as fh:
            fh.write(header + txt)

    print('生成 %d 份 -> %s\n' % (len(outputs), DST))
    for k, v in sorted(report.items(), key=lambda kv: -kv[1]):
        print('  %5d  %s' % (v, k))


if __name__ == '__main__':
    main()
