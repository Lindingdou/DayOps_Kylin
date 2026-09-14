"""
Ribbon 按钮接线对账（§三三〇 起）——把"可点的按钮"与"代码里派发过的命令名"对一遍，
列出仍未接线的按钮。用法：python build/audit_unwired.py [输出文件]

【为什么用纯子串匹配而不是正则】(§三三〇 踩过)
  · 命令名里带括号（`圆弧(三点)`）会把正则打炸；
  · 派发写法五花八门：`cmd == "X"` / `cmd.Trim().StartsWith("X")` / `case "X":` /
    窗口登记表 `Openers["X"]` / 绘图工具映射 `"X" or "Y" => new XTool()`。
    松正则会漏掉 `.Trim().StartsWith(`，于是把已接的报成未接（第一版正是把「正多边形」报错了）。
  故这里只做**字面量子串**判断，宁可少报也不误报。
"""
import io, os, re, sys, glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def ribbon_tags():
    """所有挂了 OnRibbonCommand 的按钮/菜单项 Tag。"""
    tags = set()
    for pat in ('src/Styles/*.axaml', 'src/Views/*.axaml'):
        for p in glob.glob(os.path.join(ROOT, pat)):
            s = io.open(p, encoding='utf-8').read()
            tags |= set(re.findall(r'Tag="([^"]+)"\s*Click="OnRibbonCommand"', s))
            tags |= set(re.findall(r'<MenuItem[^>]*Tag="([^"]+)"[^>]*Click="OnRibbonCommand"', s))
    return tags


def all_source():
    parts = []
    for p in glob.glob(os.path.join(ROOT, 'src/**/*.cs'), recursive=True):
        n = p.replace(os.sep, '/')
        if '/bin/' in n or '/obj/' in n:
            continue
        parts.append(io.open(p, encoding='utf-8').read())
    return "\n".join(parts)


def dispatched(tag, src):
    q = '"' + tag + '"'
    return any(pat in src for pat in (
        '== ' + q,            # cmd == "X"
        'StartsWith(' + q,    # cmd.StartsWith("X") / cmd.Trim().StartsWith("X")
        'case ' + q,          # case "X":
        '[' + q + ']',        # Openers["X"]
        q + ' =>',            # "X" => new XTool()
        q + ' or ',           # "X" or "Y" =>
        ' or ' + q,
    ))


def main():
    src = all_source()
    tags = ribbon_tags()
    missing = sorted(t for t in tags if not dispatched(t, src))
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'unwired_audit.txt')
    io.open(out, 'w', encoding='utf-8').write("\n".join(missing) + "\n")
    print(f"Tag 总数 {len(tags)} | 仍未派发 {len(missing)} -> {os.path.relpath(out, ROOT)}")


if __name__ == '__main__':
    main()
