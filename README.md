# PitMine3D · 麒麟版最小实现 (Avalonia + OpenGL)

> 目标：在**麒麟 SP10 (Linux)** 上跑起来、能打包、能渲染，主界面照 PitMine3D 现有 Ribbon 还原。
> 定位：这是**迁移骨架 / 技术验证**，不是功能移植版。它坐实了上一轮定下的路线——
> **UI 用 Avalonia（保住 C#）、渲染 D3D11→OpenGL、打包走 .deb/AppImage**——三条路各出一个可运行的最小样例。

---

## 它证明了什么

| 迁移风险点 | 本最小版的落点 | 状态 |
|-----------|--------------|------|
| WPF 在 Linux 不存在 | 整个 UI 用 **Avalonia 11**（net8.0，非 net8.0-windows） | ✅ 跑通 |
| Fluent.Ribbon 无 Avalonia 版 | **自研 Ribbon**（`Styles/Ribbon.axaml`），照现有九个分组/按钮还原 | ✅ 跑通 |
| D3D11 在 Linux 不存在 | 视口用 **OpenGL**（`OpenGlControlBase`），网格+轴+着色实体+鼠标轨道 | ✅ 跑通 |
| 承载 WindowsFormsHost 只在 Windows | GL 直接渲进 Avalonia 控件，**无 airspace** | ✅ 跑通 |
| Inno Setup(.exe) 在 Linux 无效 | **self-contained 发布 + .deb + AppImage** 三种打包脚本 | ✅ 脚本就绪 |
| .NET 跨架构 | x64/arm64 官方；**loongarch64 需社区运行时** | ⚠️ 见下 |

Windows 上渲染后端是 `OpenGL ES 3.0`（走 ANGLE）；麒麟上会走原生 GL/EGL——**同一份代码**。

## ✅ 已在 Linux 上实测运行（不只是交叉编译）

在 **Ubuntu 22.04 + WSLg**（Debian 系 glibc x86-64，作麒麟的本机代理）上实跑通过：
- Ribbon 全渲染、**中文字体正确**（`fonts-noto-cjk`）；
- **3D OpenGL 视口真渲染**出网格+Z轴+着色立方体，叠加层显示 `渲染后端: OpenGL 4.0`；
- 截图见 [docs/running-on-linux.png](docs/running-on-linux.png)。

**踩到并已修的坑（对麒麟同样关键）**：
1. **硬件 GL 在 WSLg 下 core dump** —— WSLg 的 D3D12-Mesa 驱动问题，用软件渲染（`LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe`）绕过。真麒麟有独显+驱动时走硬件 GL 即可。
2. **Avalonia 默认拉黑 llvmpipe** —— 报 `Renderer 'llvmpipe' is blacklisted`，导致 `OpenGlControlBase` 拿不到 GLX 上下文、视口空白。**已在 [Program.cs](src/Program.cs) 用 `X11PlatformOptions.GlxRendererBlacklist = Array.Empty<string>()` 放行**。这对**无独显/驱动未就绪的信创整机**是必需的，否则视口永远空白。
3. 无 GPU 加速的机器启动前设 `export LIBGL_ALWAYS_SOFTWARE=1` 即可软件渲染兜底。

## 目录结构

```
Kylin/
├── src/
│   ├── PitMine3D.Kylin.csproj      net8.0 · Avalonia 11 · 跨平台
│   ├── Program.cs / App.axaml      入口 + 平台自动探测
│   ├── Views/MainWindow.axaml      Ribbon(开始+模块页) + 视口 + 命令行 + 状态栏
│   ├── Controls/
│   │   ├── CadGlViewport.cs        OpenGL 视口（对应内核 xllAcGi 的落点）
│   │   ├── GlExtras.cs             GetProcAddress 解析 VAO/UniformMatrix4fv（跨 GLES/GL）
│   │   └── Mat4.cs                 列主序矩阵（GL 约定，自控避免 System.Numerics 歧义）
│   └── Styles/
│       ├── Ribbon.axaml           自研 Ribbon 外观（扁平分组按钮/标题/标签页）
│       └── Icons.axaml            折线字形图标（零依赖，无弧线解析风险）
├── build/
│   ├── publish-linux.sh           交叉发布 x64/arm64/loongarch64
│   ├── package-deb.sh             fpm 打 .deb（麒麟桌面）
│   ├── package-appimage.sh        打 AppImage 单文件
│   └── pitmine3d.desktop          桌面入口
└── dist/                          发布产物（git 忽略）
```

## 构建 & 运行

**Windows（验证用）**——Avalonia 跨平台，Windows 上就能跑起来看效果：
```bash
cd src
dotnet run -c Debug
```

**麒麟 / Linux**：
```bash
cd build
./publish-linux.sh linux-x64        # 或 linux-arm64
../dist/linux-x64/PitMine3D.Kylin   # 目标机直接运行，无需装 .NET
```

## 打包

```bash
cd build
./publish-linux.sh linux-x64                 # 1) 先发布自包含产物
./package-deb.sh    linux-x64 0.1.0          # 2a) 出 .deb  → sudo dpkg -i
./package-appimage.sh linux-x64              # 2b) 或出 AppImage 单文件
```
`.deb` 装到 `/opt/pitmine3d`，注册桌面入口与 `/usr/bin/pitmine3d`。

## 交互

- Ribbon 按钮 → 命令行回显该命令（证明 UI 已接线）
- 命令行输入 + 回车 → 状态栏回显执行
- 视口：左键拖拽 = 轨道旋转，滚轮 = 缩放

---

## ⚠️ 这是最小版——以下**还没做**（完整迁移的剩余工作）

1. **渲染内核**：这里是一个**新写的** OpenGL 小样例，**没有接** PitMine3D 的 C++ 内核（xllAcGi 的 D3D11 → OpenGL 尚未移植，40 个 HLSL→GLSL、D2D/DirectWrite→Skia 都还没做）。
2. **299 个 C API / P/Invoke**：未接 `xllAcEd.so`。真实几何/编辑全走这层。
3. **285K 行 C# 域逻辑**：TaskLib/MineAssLib/PlanLib 等**一个都没搬**。上一轮决策是「保住 C#」，迁移方式是把各模块的 View 从 WPF 改写到 Avalonia、逻辑基本不动——本骨架给的是 UI/渲染两条腿，域逻辑接入是后续。
4. **中文输入法**：Linux 走 fcitx/ibus，命令行/文字实体录入需专门回归。
5. **LoongArch**：`publish-linux.sh linux-loongarch64` 依赖社区/openEuler 的 .NET 运行时；且 **SkiaSharp 原生库在 loongarch 可能要自己编**（Avalonia 依赖它）——这是龙芯目标的两颗雷，x64/arm64 无此问题。
6. **字体**：目标机需装思源/文泉驿等中文字体（DirectWrite 换 Skia 后靠 fontconfig 匹配）。

> 一句话：**外壳（Ribbon）和渲染腿（OpenGL）已经站住，接下来是把 C++ 内核的 GL 后端和 285K 行 C# 域逻辑往这副骨架上接。**
