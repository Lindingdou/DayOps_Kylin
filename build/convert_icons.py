#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把原版 WPF IconDict.xaml 的矢量图标 1:1 转成 Avalonia 版。
用法：
  python convert_icons.py            # 转 Host 主图标 → src/Styles/IconDict.axaml
  python convert_icons.py GeoDataBase  # 转某模块 → src/Styles/IconDict.GeoDataBase.axaml
系统性差异：命名空间 / Pen LineCap(单一) / DashStyle.Dashes 空格→逗号 /
LinearGradient 相对点→百分比(仅渐变标签内) / 去 DrawingGroup.ClipGeometry。"""
import re, sys, io

ROOT = r"C:\Users\0doudou\Desktop\DayOps"
KYLIN_STYLES = ROOT + r"\Kylin\src\Styles"

def convert(src, dst):
    s = io.open(src, "r", encoding="utf-8").read()
    # 1) 根命名空间
    s = s.replace('xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
                  'xmlns="https://github.com/avaloniaui"')
    # 2) Pen 端帽：StartLineCap/EndLineCap → 单一 LineCap
    s = re.sub(r'EndLineCap=', 'LineCap=', s)
    s = re.sub(r'\s*StartLineCap="\w+"', '', s)
    # 2b) DashStyle.Dashes 空格→逗号
    s = re.sub(r'Dashes="([^"]*)"',
               lambda m: 'Dashes="' + ','.join(re.split(r'[ ,]+', m.group(1).strip())) + '"', s)
    # 3) 仅 LinearGradientBrush 标签内 StartPoint/EndPoint 相对点→百分比（避开 LineGeometry 绝对坐标）。
    #    MappingMode="Absolute" 的用绝对坐标，保持裸数字不转。
    def fix_grad(m):
        tag = m.group(0)
        if 'MappingMode="Absolute"' in tag:
            return tag
        def pct(mm):
            k, x, y = mm.group(1), float(mm.group(2)), float(mm.group(3))
            return f'{k}="{("%g" % (x*100))}%,{("%g" % (y*100))}%"'
        return re.sub(r'(StartPoint|EndPoint)="([-\d.]+),([-\d.]+)"', pct, tag)
    s = re.sub(r'<LinearGradientBrush[^>]*>', fix_grad, s)
    # 3b) Avalonia 无 MappingMode / DashCap；FillRule 枚举大小写 Nonzero→NonZero
    s = re.sub(r'\s*MappingMode="[^"]*"', '', s)
    s = re.sub(r'\s*DashCap="[^"]*"', '', s)
    s = s.replace('FillRule="Nonzero"', 'FillRule="NonZero"')
    s = s.replace('SweepDirection="Counterclockwise"', 'SweepDirection="CounterClockwise"')
    # 4) 去 DrawingGroup.ClipGeometry
    s = re.sub(r'\s*ClipGeometry="[^"]*"', '', s)
    # 顶部注释
    s = s.replace('<ResourceDictionary xmlns="https://github.com/avaloniaui"',
                  '<!-- 由 build/convert_icons.py 从原版自动 1:1 转换，勿手改 -->\n<ResourceDictionary xmlns="https://github.com/avaloniaui"', 1)
    io.open(dst, "w", encoding="utf-8").write(s)
    n = len(re.findall(r'<DrawingImage x:Key="', s))
    print(f"  {src.split(chr(92))[-1]} -> {dst.split(chr(92))[-1]} : {n} icons")

if __name__ == "__main__":
    if len(sys.argv) > 1:
        mod = sys.argv[1]
        convert(f"{ROOT}\\PitMine3D\\Modules\\{mod}\\Assets\\IconDict.xaml",
                f"{KYLIN_STYLES}\\IconDict.{mod}.axaml")
    else:
        convert(f"{ROOT}\\PitMine3D\\Host\\PitMineApp\\Assets\\IconDict.xaml",
                f"{KYLIN_STYLES}\\IconDict.axaml")
