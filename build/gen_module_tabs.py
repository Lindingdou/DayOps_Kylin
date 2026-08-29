#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 Avalonia 模块页 Ribbon，托管 MainWindow.axaml 里「开始」之后到 </TabControl> 的整段。
数据来自原版各模块 Plugin.cs 的 AddTab/AddGroup/AddButton。7 个模块按原版顺序；
groups=None 的先出占位 tab。随迭代逐个补全 groups 即可。DropDownButton/SplitButton 暂渲为顶层大按钮。"""
import re, io

MW = r"C:\Users\0doudou\Desktop\DayOps\Kylin\src\Views\MainWindow.axaml"

# (真实TabHeader, groups | None)；groups = [ (组名, [ (按钮名, iconKey, 'L'|'M') ]) ]
MODULES = [
 ("地质与工程信息数据库", [
    ("钻孔管理", [("导入钻孔数据","bh_import","L"),("展绘钻孔","bh_plot","L"),("虚拟钻孔","bh_virtual","L"),
        ("开孔坐标管理","bh_data_manage","M"),("原始钻孔柱状图","bh_histogram","M"),("展绘层位数据","bh_layer_plot","M")]),
    ("煤质管理", [("煤质数据管理","coal_data","L"),("空间分布","coal_spatial","L"),
        ("数据看板","coal_dashboard","M"),("统计分析","coal_stats","M"),("钻孔柱状图","bh_column_3d","M")]),
    ("工艺参数管理", [("工艺架构定义","process_arch","L"),("平盘工艺地图","process_map","L"),
        ("参数模板库","process_tmpl","M"),("现场验收录入","process_accept","M")]),
    ("设备管理", [("机群总览","equip_cockpit","L"),("设备信息管理","equip_info","L"),("设备智能编组","equip_grouping","L"),
        ("设备数据分析","equip_analysis","M"),("设备生产数据","equip_prod","M"),("班次效能预测","equip_forecast_shift","M"),
        ("设备效能预测","equip_forecast","M"),("设备能力","equip_capability","M"),("数据导入导出","equip_import","M")]),
 ]),
 ("三维地质建模", [
    ("建模", [("创建三角网","icon_Triangle","L"),("固化成体","mb_solidify","M"),("侧面三角网","mb_side_tri","M"),
        ("快速建模","mb_quickmodel","M"),("地质体建模","mb_geomodel","M"),("展点","mb_show_pts","M"),("基本几何体","mb_primitives","M")]),
    ("倾斜摄影", [("加载倾斜摄影","mb_oblique_import","L"),("转化为三角格网","mb_oblique_tin","L"),
        ("隐藏","mb_oblique_hide","M"),("删除","mb_oblique_del","M")]),
    ("编辑", [("点编辑","edit_point","L"),("线编辑","edit_line","L"),("面编辑","edit_face","L"),("体编辑","edit_body","L"),("工具","me_contour","L")]),
    ("地质统计学分析", [("快速估值","stat_quick","L"),("克里金估值","stat_kriging","L")]),
    ("更新地质模型", [("补勘钻孔写实","mu_borehole","L"),("现状写实","mu_current","L"),("更新煤层面","mu_update","L")]),
 ]),
 ("点云处理", [
    ("点云数据", [("加载点云","pc_load","L"),("点云管理","pc_manage","M"),("显示/隐藏","pc_visibility","M"),
        ("点云着色","pc_colorize","M"),("清除全部","pc_view_clean","M")]),
    ("点云修复", [("地面点滤波","pc_classify","L"),("SOR去噪","pc_denoise","L"),("ROR去噪","pc_repair","L"),
        ("补洞(三角网)","pc_fill_hole","M"),("剔面(三角网)","pc_remove_obs","M")]),
    ("点云编辑", [("点云抽稀","pc_thin","L"),("分割点云","pc_crop_cloud","L"),
        ("高程截断","pc_z_clip","M"),("坐标转换","pc_coord_xform","M")]),
    ("点云分析（逐点）", [("逐点坡度/坡向","pc_slope","L"),("点云剖面","pc_cloud_profile","L"),("位移监测 C2C","pc_c2c","L"),
        ("法向估计","pc_normals","M"),("质量统计","pc_quality","M"),("坡顶底线提取","pc_slope_edges","M")]),
    ("三角网重建", [("2.5D TIN","pc_tin25d","L"),("两期点云算量","pc_volume_diff","L"),
        ("圈范围算量","pc_volume","M"),("三角网着色","pc_tin_color","M"),("分割三角网","pc_tin_split","M")]),
    ("三角网分析", [("等高线生产","pc_contour","L"),("剖面分析","pc_profile","M"),("工艺参数分析","pc_process_param","M"),
        ("坡度着色","pc_slope_shade","M"),("粗糙度","pc_roughness","M"),("坡向着色","pc_aspect","M"),("曲率","pc_curvature","M")]),
 ]),
 ("剥采排工程辅助设计", [
    ("剥采工程", [("参数化模板","mineass_template_param","L"),("批量台阶扩帮","mineass_bench_expand_batch","L"),("处理尖灭","mineass_bench_pinch","L"),
        ("驱动距离","mineass_drive_template","L"),("驱动量","mineass_qty_driven","L"),("创建工程位置","mineass_create_loc","L"),("创建工作线","mineass_workline","L"),
        ("分帮扩帮","mineass_bench_perwall","M"),("局部台阶","mineass_bench_expand","M"),("组合工作线","mineass_workline_group","M"),("最终并段","mineass_merge_bench","M"),
        ("批量扩坑","mineass_pit_expand","M"),("连接台阶线","mineass_bench_join","M"),("动态调整","mineass_bench_edit","M"),("编辑台阶","mineass_adjust_bench","M"),("煤层露头着色","mineass_seam_outcrop","M")]),
    ("运输工程", [("约束条件设置","mineass_transport_config","L"),("运量驱动布线","mineass_road_layout","L"),("直线坑线","mineass_road_straight","L"),("坑线落地","mineass_build_transport","L"),
        ("撤销坑线","mineass_road_cut","M"),("平盘联络道","mineass_bench_connector","M"),("螺旋坑线","mineass_road_spiral","M"),("画道路中线","mineass_road_centerline","M"),("折返坑线","mineass_road_switchback","M")]),
    ("排土工程", [("排土模板","mineass_dump_template","L"),("排土场放坡","mineass_outer_dump_slope","L"),("排土场按量推进","mineass_inner_dump","L"),("排土条带","mineass_dump_strip","L"),("排土场容量校核","mineass_volume","L")]),
 ]),
 ("道路运输系统", [
    ("路网构建", [("提取道路中心线","pc_road_centerline","L"),("手动标定线路","road_mark","L"),("中心线管理","road_centerline_manage","L"),
        ("基础道路网络构建","road_network_build","L"),("破碎站位置设置","road_points","L")]),
    ("运输表征", [("路况显示","road_styled","L"),("结构路面","road_pavement","L"),("路网预览","road_network_preview","L")]),
    ("路网维护与延拓", [("增量增删边","road_update","L"),("时段快照","road_snapshot","L"),
        ("路网存档","road_routelib","M"),("演化对比","road_evolution","M"),("分色显示","road_colordisplay","M"),
        ("路网更新","road_extend","M"),("边状态","road_edgestate","M"),("延拓触发设置","road_trigger","M")]),
    ("寻径与运距", [("点对点寻径","road_pathfind","L"),("等效运距","road_distance","L"),("运输指标报表","road_report","L")]),
 ]),
 ("生产计划编制", [
    ("优化开采设计", [("境界圈定","pit_opt_config","L"),("确定境界","pit_opt_solve","L"),("采区划分","panel_div_config","L"),
        ("开采程序确定","panel_div_solve","M"),("刀量切割","plan_lt_cutting","M"),("剥采比均衡","plan_lt_ratio_balance","M")]),
    ("中长远进度计划编制", [("中长远进度计划编制","plan_longterm","L"),("规划计算","plan_lt_compute","L"),("中长远规划动态模拟","plan_sim_longterm","L"),("采场/排土场圈定","plan_st_mineable_area","L"),
        ("派生计划方案","plan_lt_derive","M"),("方案综合对比","plan_lt_compare","M"),("进度计划方案出图","plan_lt_chart","M")]),
    ("短期生产计划编制", [("短期生产计划编制","plan_shortterm","L"),("月度计划编制","plan_st_monthly","L"),("量驱动采剥接续","plan_st_qty_driven","L"),("短期进度计划动态模拟","plan_sim_shortterm","L"),
        ("采场参数识别","plan_st_param_extract","M"),("标注台阶标高","plan_st_bench_label","M"),("确定开采程序","plan_st_mine_program","M"),("采排配对","plan_st_pairing","M"),("采掘单元清单","plan_st_unit_ledger","M"),("派生计划方案","plan_st_derive","M")]),
 ]),
 ("日常生产组织", [
    ("基础数据", [("班次日历","task_calendar","L"),("作业面台账","task_face","L"),
        ("去向台账","task_layout","M"),("检修档期","task_calendar","M"),("影像底图","task_layout","M")]),
    ("任务编制", [("生产任务编制","task_create","L"),("作业区划分","task_layout","L"),("周计划编制","task_week","L"),
        ("编制配置","task_config","M"),("钻爆计划衔接","task_blast","M")]),
    ("任务下达", [("生产任务书","task_order","L"),("任务下达","task_dispatch","L"),
        ("派车单","task_dispatch","M"),("班组派工","task_crew","M")]),
    ("执行调度", [("调度态势看板","task_board","L"),("实绩录入","task_actual","L"),
        ("设备状态·故障报修","task_equipstatus","M"),("工序进度跟踪","task_progress","M")]),
    ("评价分析", [("生产报告","task_report","L"),("达成度评价","task_attainment","L"),
        ("产量统计","task_stats","M"),("质量·配煤分析","task_coal","M")]),
    ("调整·推演", [("生产任务动态调整","task_adjust","L"),("班内工艺·工序推演","task_simulate","L")]),
 ]),
]

def esc(t): return t.replace("&","&amp;").replace("<","&lt;").replace(">","&gt;")

def large_btn(name, icon):
    return (f'                                        <Button Classes="ribbon-btn large" Tag="{esc(name)}" Click="OnRibbonCommand" ToolTip.Tip="{esc(name)}">\n'
            f'                                            <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center"><Image Source="{{StaticResource {icon}}}" /><TextBlock Text="{esc(name)}" /></StackPanel>\n'
            f'                                        </Button>\n')

def small_btn(name, icon):
    return (f'                                            <Button Classes="ribbon-btn small" Tag="{esc(name)}" Click="OnRibbonCommand" ToolTip.Tip="{esc(name)}">\n'
            f'                                                <StackPanel Orientation="Horizontal"><Image Source="{{StaticResource {icon}}}" /><TextBlock Text="{esc(name)}" /></StackPanel>\n'
            f'                                            </Button>\n')

def group_xaml(caption, buttons):
    larges = [b for b in buttons if b[2]=="L"]; middles = [b for b in buttons if b[2]=="M"]
    x  = '                            <Border Classes="ribbon-group">\n                                <DockPanel>\n'
    x += f'                                    <TextBlock Classes="ribbon-caption" DockPanel.Dock="Bottom" Text="{esc(caption)}" />\n'
    x += '                                    <StackPanel Orientation="Horizontal">\n'
    for n,i,_ in larges: x += large_btn(n,i)
    if middles:
        x += '                                        <WrapPanel Orientation="Vertical" MaxHeight="72">\n'
        for n,i,_ in middles: x += small_btn(n,i)
        x += '                                        </WrapPanel>\n'
    x += '                                    </StackPanel>\n                                </DockPanel>\n                            </Border>\n'
    return x

def tab_xaml(header, groups):
    if groups is None:
        return (f'            <TabItem Header="{esc(header)}">\n'
                f'                <Border Background="#F7F8FA" BorderBrush="#DCDFE4" BorderThickness="0,0,0,1">\n'
                f'                    <TextBlock Text="（待复刻）" Foreground="#8A9099" Margin="16,22" VerticalAlignment="Center" />\n'
                f'                </Border>\n            </TabItem>\n')
    x  = f'            <TabItem Header="{esc(header)}">\n'
    x += '                <Border Background="#F7F8FA" BorderBrush="#DCDFE4" BorderThickness="0,0,0,1">\n'
    x += '                    <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">\n'
    x += '                        <StackPanel Orientation="Horizontal" Height="108">\n'
    for cap, btns in groups: x += group_xaml(cap, btns)
    x += '                        </StackPanel>\n                    </ScrollViewer>\n                </Border>\n            </TabItem>\n'
    return x

region = '            <!-- ===== 模块页（由 build/gen_module_tabs.py 生成，勿手改） ===== -->\n'
region += ''.join(tab_xaml(h, g) for h, g in MODULES)

s = io.open(MW, encoding="utf-8").read()
# 替换「开始」TabItem 之后、</TabControl> 之前的整段模块页
pat = re.compile(r'            <!-- ===== 模块页.*?(?=\n        </TabControl>)', re.S)
if not pat.search(s):
    raise SystemExit("未找到模块页区域标记")
s = pat.sub(region.rstrip('\n'), s)
io.open(MW, "w", encoding="utf-8").write(s)
done = sum(1 for _,g in MODULES if g)
btns = sum(len(gr[1]) for _,g in MODULES if g for gr in g)
print(f"生成 {len(MODULES)} 个模块 tab（{done} 全量 / {len(MODULES)-done} 占位），共 {btns} 按钮")
