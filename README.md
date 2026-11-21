# 远景工坊 · L4D2 自动 LOD 工具（v7）

> 🇨🇳 中文说明如下 · 🇺🇸 [Jump to English README](#english)

这是一款为《求生之路 2》（Left 4 Dead 2）模型工程自动生成多级 LOD 的 Windows 桌面工具：
它调用 Blender 把模型减面为 `*_lodN.glb/smd`，再以幂等方式把 `$lod` 块写回 QC 文件
（全程自动备份、原子写入，可一键还原）。v7 为**从零重写**，不包含、也不依赖任何
旧版（v4.x / FIELD v5.x）源码。

- 交付形态：**单个 exe**（`dist\L4D2LOD远景工坊.exe`），双击即用，无需安装
- 运行时：.NET Framework 4.8 —— Windows 10 1903+ / Windows 11 均已自带，无需额外安装
- 外部依赖：Blender 4.0 / 4.1（已实测通过；4.2+ 留有兼容分支，但尚未验收）；`nekomdl.exe`（可选，仅用于「生成后试编译」）
- 界面：WPF 自绘 —— 浅色「日测」/ 暗色「夜测」双主题、自绘标题栏、「导入文件夹 / 导入文件」两个入口、LOD 数值即时编辑、CJK / 拉丁 / 等宽三级字体回退
- 引擎：纯 .NET 实现、无 UI 依赖，可通过 `--selftest` / `--plan` / `--batch` 在无界面环境下运行
- 版本：程序版本 7.0.0、文件版本 7.0.0.0；版权 © 2026 远景工坊

## 最短路径（使用者）

1. 构建出 `dist\L4D2LOD远景工坊.exe`（见下文「构建」），或直接下载 Release 里的分发包。
2. 打开「设置 → 引擎」页，确认 `blender.exe` 路径（通常会自动找到）。
3. 把工程文件夹拖进窗口，等待自动扫描；点「扫描预览」确认计划无误后，再点「开始生成」。
4. 回到工程目录，双击原来的 `批量编译.bat` 即可；如不放心，可用 QC 旁的 `.lodbak` 备份一键还原。
5. 完整说明与 FAQ 见 `docs/03_使用说明.md`。

## 构建

本机没有安装 .NET SDK 时，可直接使用便携版 SDK（脚本会按下面的顺序自动查找）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

- 编译器查找顺序：参数 `-DotNet` → 环境变量 `DOTNET_EXE` → `C:\Users\TPP\.tools\dotnet-sdk-8.0.404\dotnet.exe`
  → `%ProgramFiles%\dotnet\dotnet.exe` → PATH 中的 `dotnet`
- 可用参数：`-Clean`（先删除 `src\bin` / `src\obj` / `.build`）、`-NoDist`（只构建不打包）、`-Debug`、`-Configuration <名称>`、`-DotNet <路径>`
- 目标框架为 `net48`，`LangVersion=latest`（借助 SDK 自带的 Roslyn 编译现代 C#），`AnyCPU` / `Prefer32Bit=false`
- 构建产出：`dist\L4D2LOD远景工坊.exe`（单文件，内嵌 `Assets/blender_backend.py` 与图标，带 7.0.0.0 版本资源）
- `build.ps1` 还会把 exe 的完整性级别修正为 **Medium**（受限环境下编译产物默认是 Low，而 Low 进程无法写普通文件），
  并在末尾打印体积 / SHA256 / FileVersion / integrity；成功时末行输出 `BUILD OK`，失败则输出 `BUILD FAILED` 并以退出码 1 结束
- 全程不生成 `app.config` / `pdb`，也不引用任何第三方 NuGet 包或旁挂 DLL

## 运行

```powershell
dist\L4D2LOD远景工坊.exe                  # 打开界面
dist\L4D2LOD远景工坊.exe <文件|文件夹>...  # 打开界面并把路径加入队列（单实例：已有窗口则交给它处理）
dist\L4D2LOD远景工坊.exe --version        # 打印版本
dist\L4D2LOD远景工坊.exe --help           # 打印用法
dist\L4D2LOD远景工坊.exe --selftest [--filter 关键字] [--out 目录]    # 引擎自测
dist\L4D2LOD远景工坊.exe --ui-audit --out <目录> [--shots-out <目录>] # UI 验收（截图 / 布局 / 对比度 / 主题往返 / 缩放）
dist\L4D2LOD远景工坊.exe --plan  <工程目录> [选项]  # 只读计划（输出 plan.json，不写任何文件）
dist\L4D2LOD远景工坊.exe --batch <工程目录> [选项]  # 无界面执行一次批量（batch.json / 报告 / 日志）
```

维护用入口（不面向普通用户）：`--icon-sheet [--out]`（图标总表）、`--widget-preview [--out]`（控件状态矩阵）、
`--settings-preview [--out]`（设置 5 页 / 备份联动 / 诊断 / 跳过主模型选择器的截图）。
`--plan` / `--batch` 支持的选项（`--preset` / `--dist` / `--ratio` / `--levels` / `--mode` / `--workers` / `--min-triangles` /
`--force` / `--compile` / `--facial` / `--skip-main` / `--no-backup` / `--fast` / `--blender` / `--nekomdl` /
`--report-dir` / `--out`）详见 `docs/03_使用说明.md §14`。

注意：WinExe 程序不会自动把 CLI 输出打到控制台。在脚本中调用时请重定向 stdout，或直接读取 `--out` 目录里的
`plan.json` / `batch.json` 及报告文件。

测试 / 便携相关的环境变量：

- `L4D2LODSTUDIO_SETTINGS_DIR`：覆盖设置目录（自测、E2E、UI 审计都用它来隔离真实用户配置）。
- `L4D2LOD_SETTINGS_DIR`：覆盖旧版设置目录（仅供只读导入；测试中可指向空目录）。
- `DOTNET_EXE`：为 `build.ps1` 指定 SDK 路径（等价于 `-DotNet` 参数）。
- 临时目录固定为 `%TEMP%\L4D2LOD远景工坊\`（存放任务 JSON、脚本、SMD 中间文件、崩溃日志），启动时会自动清理陈旧文件。

## 测试

```powershell
# 1) 引擎自测（要求 SELFTEST: N passed, 0 failed；WinExe 需重定向 stdout，结果同时写入 selftest.txt）
$env:L4D2LODSTUDIO_SETTINGS_DIR = "$env:TEMP\lodstudio_selftest"
& .\dist\L4D2LOD远景工坊.exe --selftest --out "$env:TEMP\lodstudio_selftest_out"
Get-Content "$env:TEMP\lodstudio_selftest_out\selftest.txt" -Tail 3

# 2) 端到端测试（12 个场景：真实 Blender + 合成工程，覆盖 include 链 / GBK / 手写 $lod / 坏标记块 / SMD / 中文空格长路径 / 包装 QC / 取消）
powershell -ExecutionPolicy Bypass -File tests\run_e2e.ps1 -Blender "D:\blender 4.1\blender.exe"
# 可选参数：-SkipBuild -KeepTemp -IncludeCancel -Levels 2 -Dist "20,50" -Ratio "60,25"

# 3) UI 审计（状态矩阵截图 + 布局审计 + WCAG 对比度 + 主题往返 + 三档缩放；FAIL=0 才算通过）
& .\dist\L4D2LOD远景工坊.exe --ui-audit --out "$env:TEMP\ui-audit"
```

- 所有测试都在临时目录中构造数据，**不会触碰用户的真实工程与配置**（设置目录可用 `L4D2LODSTUDIO_SETTINGS_DIR` 覆盖）。
- 证据位置：自测结果写在 `--out` 目录的 `selftest.txt`（历史采样目录 `%TEMP%\selftest_probe` 仅作参考）；
  E2E 结果在 `%TEMP%\lodstudio_e2e\e2e_report.txt` 与 `logs\`；UI 审计结果在 `<out>\audit.txt` 及 PNG 截图，
  关键截图会归档到 `docs\screenshots\main\`。
- 验收报告（含环境、命令、断言清单与全部实测数字）见 `docs/04_验收报告.md`（§10–§17 为第二至九轮 UI 打磨的验收 / 部署记录）。

## 目录结构

```
L4D2_LOD_Studio/
├─ build.ps1                 一键构建（SDK 定位 / publish / dist / 完整性修正 / 哈希）
├─ dist/                     交付产物（exe）
├─ src/
│  ├─ L4D2LodStudio.csproj   net48 + WPF + 嵌入资源 + 版本资源
│  ├─ app.manifest           PerMonitorV2 DPI / longPathAware / asInvoker / activeCodePage=UTF-8
│  ├─ Program.cs             入口（GUI / CLI 分发 / 单实例 / 崩溃日志）
│  ├─ AppInfo.cs             版本、设置目录、临时目录
│  ├─ SingleInstance.cs      单实例投递（第二个实例把路径转交给已打开的窗口）
│  ├─ MessageBar.cs          未处理异常的最小提示
│  ├─ Engine/                引擎（无 UI 依赖）
│  │  ├─ Qc/                 QC 编码探测 / 词法 / 解析 / 注入 / 备份
│  │  ├─ Model/              SMD / GLB 编解码与互转（含骨骼权重）
│  │  ├─ Blender/            Blender 定位 / 脚本释放 / 进程调度 / 看门狗 / 崩溃续跑 / 取消
│  │  ├─ Pipeline/           扫描 / nekomdl 试编译 / vtx 解析 / 报告 / 诊断
│  │  ├─ Batch/              计划器 / 执行器 / 计划与结果模型
│  │  └─ Platform/           Job Object / 运行环境（管理员 / 低完整性）/ 输出位置
│  ├─ Controls/              自绘控件（图标、分段、进度条、比例条、Toast、对话框、阶梯图控件…）
│  ├─ Themes/                双主题令牌 + 字体阶梯 + 控件 / 浮层样式
│  ├─ ViewModels/            MVVM（Shell / 队列 / LOD 方案 / 日志 / 计划）
│  ├─ Views/                 主窗口 / 设置窗口（5 页）/ 对话框 / 跳过主模型选择器
│  ├─ Services/              主题切换、离屏截图、值转换器
│  ├─ Cli/                   自测 / UI 审计 / 批量命令行 / 图标表 / 控件与设置预览
│  └─ Assets/                app.ico、blender_backend.py（嵌入资源）
├─ tests/
│  ├─ gen_fixtures.py        用真实 Blender 生成 E2E 合成工程
│  └─ run_e2e.ps1            端到端回归（11 个场景 + 可选取消 = 12 个场景）
├─ tools/
│  └─ make_icon.py           图标源生成（多方案 → app.ico + 总表）
└─ docs/
   ├─ 00_方案_设计与决策.md   需求、选型、架构、UI 定位、测试与验收标准
   ├─ 01_开发约定.md          工程约定、自测写法、模块分工、验收硬指标
   ├─ 01_引擎规格.md          引擎实现规格（模块 / 协议 / 格式 / 不变量 / API 索引）
   ├─ 02_UI设计规范.md        设计语言、令牌、区域规格、状态、动效、验收
   ├─ 03_使用说明.md          用户手册（界面 / 设置 / 快捷键 / CLI / FAQ）
   ├─ 04_验收报告.md          验收证据（构建 / 自测 / E2E / UI 审计 / 真实工程 / 性能 / 打包）
   ├─ 05_代码评审_引擎.md      独立代码评审报告（P0 / P1 / P2 / P3 清单与修复对照）
   └─ screenshots/            图标 / 控件 / 设置 / 主界面验收截图（main、widgets、设置页 PNG）
```

## 设计要点（为什么这么做）

- **安全第一**：所有 QC 修改都遵循「备份 → 全部备份成功才写入 → 原子替换 → 失败回滚」的流程；备份文件 `.lodbak` 永不覆盖；
  工具标记块幂等；缺少结束标记时拒绝修改；任何失败都不会删除唯一副本；GBK / UTF-8 / BOM / 换行符均原样保留。
- **全局去重**：同一个模型被多个 QC 或多个任务引用时只减面一次；同一输出路径只运行一次。
- **可预期**：扫描预览 / 作业计划会先告诉你「将要做什么、哪些会跳过、为什么」，确认后再动手。
- **可恢复**：停止操作只影响后续任务，已生成的 LOD 会保留、QC 不再改动；子进程全部纳入 Job Object，主程序退出后不留孤儿进程。
- **单 exe 交付**：无安装、无运行库、无旁挂文件；Blender 脚本按内容哈希释放到临时目录，并由启动清理机制回收。

## 当前状态与已知限制

**已完成并实测**（证据见 `docs/04_验收报告.md`）：

- 单文件 exe 约 854 KB、完整性 Medium、FileVersion 7.0.0.0、无第三方依赖；`--version` 响应 75 ms；
- 引擎自测 **77 passed / 0 failed**（含真实 Blender 4.0.2 / 4.1.1 与 nekomdl 用例）；
- 端到端测试 **12/12 通过**（覆盖三种写入方式、force、坏标记块拒绝、包装 QC、SMD 往返、GBK / 中文 / 空格 / 长路径、取消）；
- 真实工程：只读计划 **646 QC / 1065 模型 / 4196 任务 / 6.9M 面 / 13.1 s / 0 错误**；
  真实子工程批量 **30/30 减面成功、132,350 → 33,331 面、幂等复跑 0 字节变化**；真实 nekomdl 试编译 18/18 成功，并成功读回 vtx LOD 面数；
- UI 审计（参考，最近一次）**PASS 80 / FAIL 0 / NOTE 4**（16 种状态 × 双主题、36 组布局、46 项对比度、主题往返 0 差异、三档缩放、8 级行高固定 40 / 外框边线断言）；第九轮按用户要求未重跑自检 / 审计。

**已知限制**（均为有意取舍而非缺陷，已在 `docs/03_使用说明.md` 中说明）：

- 只支持 `.glb` / `.smd`；`.dmx` 与旧版一致不支持（计划中会明确显示「跳过：不是 glb/smd」）；
- 冷启动 2.6 ~ 4.8 s（WPF 富界面的固有开销，见验收报告 §5）；热启动约 2.6 s；
- 只读 / 编码异常 / 工具标记块损坏的 QC 会被**拒绝修改**（数据安全优先，见使用说明 §13）；
- `innode` 条目、面数低于门槛的小部件、`$lod` 每条最多 4 个权重影响 —— 以上行为与旧版保持一致；
- Blender 4.2+、超过 260 字符的长路径 / UNC、真实拖放、管理员 / 低完整性、多屏与 Win10 环境尚未做手工验收（见验收报告 §7）。

## 文档导航

建议从「[docs/00_方案_设计与决策.md](docs/00_方案_设计与决策.md)」开始读起；实现约定见
「[docs/01_开发约定.md](docs/01_开发约定.md)」与「[docs/01_引擎规格.md](docs/01_引擎规格.md)」；
界面规范见「[docs/02_UI设计规范.md](docs/02_UI设计规范.md)」；用户手册见
「[docs/03_使用说明.md](docs/03_使用说明.md)」；验收证据见「[docs/04_验收报告.md](docs/04_验收报告.md)」；
独立代码评审见「[docs/05_代码评审_引擎.md](docs/05_代码评审_引擎.md)」。

## 常见问题（精选）

- **构建报「找不到 dotnet」**：用 `-DotNet <dotnet.exe 路径>` 指定便携版 SDK，或设置环境变量 `DOTNET_EXE`。
- **双击 exe 后写文件全部失败**：查看顶栏权限指示是否为「低完整性」（红点）；把 exe 复制到桌面 / 解除锁定，
  或执行 `icacls "<exe>" /setintegritylevel Medium`。构建脚本已尽量把 dist exe 提升到 Medium。
- **实测行为与文档不一致**：一律以代码为准，并在 `docs/01_引擎规格.md §14` 追加差异行；验收数字以 `docs/04_验收报告.md` 为准。
- **只想看看工具会做什么**：运行 `--plan <工程目录>`（只读、不写文件），或使用界面里的「扫描预览」。
- **只想跑测试不想构建**：`tests\run_e2e.ps1 -SkipBuild`（前提：已存在 `dist\L4D2LOD远景工坊.exe` 与可用的 Blender）。
- **UI 审计结果在哪看**：`--ui-audit` 输出目录中的 `audit.txt`（首部为环境信息，末部为汇总与失败明细）与 PNG 截图；
  关键截图会复制到 `docs\screenshots\main`。

## 维护约定

- 修改 Engine 的公开契约（`src/Engine/**Contracts*.cs`、`QcModels.cs`、`Contracts.cs`）时，要同步更新 `docs/01_引擎规格.md`。
- 修改 `src/Assets/blender_backend.py` 后必须重新构建（脚本按内容哈希释放，旧文件 7 天后自动清理）。
- 新增自测用例时，用 `[SelfTest("…")]` 写在所属模块的 `SelfTest*.cs` 里，不要改动共享文件。
- 代码与文档冲突时以代码为准，并在 `docs/01_引擎规格.md` 与 `docs/03_使用说明.md` 文末的差异表中登记。
- 验收证据不要写死临时绝对路径；引用写法遵循 `docs/04_验收报告.md §8`。
- 改动 UI 后跑 `--ui-audit`（要求 FAIL=0）并重新归档 `docs/screenshots/main`；改动引擎后跑 `--selftest` + `tests\run_e2e.ps1`。
- 启动性能可用 `L4D2LOD_PERF=1` 打点（日志在 `%TEMP%\L4D2LOD远景工坊\perf.log`）。

## 第三方资产

- 图标：本仓库手写的 24×24 矢量路径（无外部图标字体），由 `tools/make_icon.py` 生成多尺寸 `app.ico`。
- 字体：使用系统自带字体（Microsoft YaHei UI / Bahnschrift / Consolas），程序不随附字体文件。
- Blender 端脚本：本仓库自写（MIT 风格，随项目分发）。
- 代码风格：C# 12 / net48，无第三方依赖；所有面向用户的文案为中文，写入文件的数字一律使用 InvariantCulture。

---

## English

**Yuanjing Workshop (远景工坊)** is a Windows desktop tool that automatically generates
multi-level LODs for *Left 4 Dead 2* model projects. It decimates models with Blender into
`*_lodN.glb/smd`, then idempotently writes the `$lod` blocks back into QC files — with
automatic backups, atomic writes, and one-click restore. v7 is a **from-scratch rewrite**:
it contains no code from, and has no dependency on, the older v4.x / FIELD v5.x generations.

> Note: the application UI and the docs under `docs/` are currently Chinese-only.

- Distribution: a **single exe** (`dist\L4D2LOD远景工坊.exe`) — double-click and go, no installer
- Runtime: .NET Framework 4.8 — already included in Windows 10 1903+ / Windows 11
- External dependencies: Blender 4.0 / 4.1 (tested; a compatibility branch for 4.2+ exists but is not yet validated);
  `nekomdl.exe` (optional, only used for "trial compile after generation")
- UI: self-drawn WPF — light/dark dual themes, custom title bar, "Import Folder / Import Files" entries,
  in-place LOD value editing, and a CJK / Latin / monospace three-tier font fallback
- Engine: pure .NET with no UI dependencies; runs headless via `--selftest` / `--plan` / `--batch`
- Version: assembly 7.0.0, file version 7.0.0.0; © 2026 Yuanjing Workshop

### Quick start (users)

1. Build `dist\L4D2LOD远景工坊.exe` (see "Build" below), or download the distribution zip from Releases.
2. Open *Settings → Engine* and confirm the path to `blender.exe` (usually auto-detected).
3. Drag a project folder into the window and wait for the scan; click "Scan Preview" to review the plan,
   then click "Start Generation".
4. Back in your project directory, run your original `批量编译.bat` (batch-compile script) as before.
   If anything looks wrong, restore from the `.lodbak` backup sitting next to each QC.
5. Full manual and FAQ: `docs/03_使用说明.md` (Chinese).

### Build

No system-wide .NET SDK installation is required — the script locates a portable SDK automatically,
in the following order:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

- Compiler lookup order: `-DotNet` argument → `DOTNET_EXE` environment variable →
  `C:\Users\TPP\.tools\dotnet-sdk-8.0.404\dotnet.exe` → `%ProgramFiles%\dotnet\dotnet.exe` → `dotnet` on PATH
- Flags: `-Clean` (wipe `src\bin` / `src\obj` / `.build` first), `-NoDist` (build only), `-Debug`,
  `-Configuration <name>`, `-DotNet <path>`
- Target: `net48`, `LangVersion=latest` (modern C# via the SDK's Roslyn), `AnyCPU` / `Prefer32Bit=false`
- Output: `dist\L4D2LOD远景工坊.exe` — a single file with the embedded `Assets/blender_backend.py`
  and icon, carrying version resource 7.0.0.0
- The script also resets the exe's integrity level to **Medium** (artifacts built in restricted environments
  default to Low, and a Low-integrity process cannot write normal files), then prints
  size / SHA256 / FileVersion / integrity. It ends with `BUILD OK`, or `BUILD FAILED` plus exit code 1.
- No `app.config`, no `pdb`, and no third-party NuGet packages or side-by-side DLLs.

### Run

```powershell
dist\L4D2LOD远景工坊.exe                  # Open the GUI
dist\L4D2LOD远景工坊.exe <file|folder>...  # Open the GUI and queue the paths (single instance: handed to the open window)
dist\L4D2LOD远景工坊.exe --version        # Print version
dist\L4D2LOD远景工坊.exe --help           # Print usage
dist\L4D2LOD远景工坊.exe --selftest [--filter keyword] [--out dir]   # Engine self-test
dist\L4D2LOD远景工坊.exe --ui-audit --out <dir> [--shots-out <dir>]  # UI audit (screenshots / layout / contrast / theme round-trip / scaling)
dist\L4D2LOD远景工坊.exe --plan  <project dir> [options]  # Read-only plan (writes plan.json only)
dist\L4D2LOD远景工坊.exe --batch <project dir> [options]  # Headless batch run (batch.json / report / logs)
```

Maintenance entries (not for end users): `--icon-sheet [--out]` (icon sheet), `--widget-preview [--out]`
(control state matrix), `--settings-preview [--out]` (screenshots of the 5 settings pages, backup linkage,
diagnostics, and the skip-main picker). The options accepted by `--plan` / `--batch`
(`--preset` / `--dist` / `--ratio` / `--levels` / `--mode` / `--workers` / `--min-triangles` / `--force` /
`--compile` / `--facial` / `--skip-main` / `--no-backup` / `--fast` / `--blender` / `--nekomdl` /
`--report-dir` / `--out`) are documented in `docs/03_使用说明.md §14`.

Note: a WinExe does not print CLI output to the console automatically — redirect stdout in scripts,
or read `plan.json` / `batch.json` and the reports under the `--out` directory.

Test / portability environment variables:

- `L4D2LODSTUDIO_SETTINGS_DIR`: overrides the settings directory (self-test, E2E, and UI audit use it to
  isolate real user configuration).
- `L4D2LOD_SETTINGS_DIR`: overrides the legacy settings directory (read-only import; point it at an empty
  directory in tests).
- `DOTNET_EXE`: SDK path for `build.ps1` (equivalent to `-DotNet`).
- The temp directory is fixed at `%TEMP%\L4D2LOD远景工坊\` (task JSON, scripts, SMD intermediates,
  crash logs); stale files are cleaned up at startup.

### Tests

```powershell
# 1) Engine self-test (expect SELFTEST: N passed, 0 failed; stdout must be redirected for a WinExe,
#    and the result is also written to selftest.txt)
$env:L4D2LODSTUDIO_SETTINGS_DIR = "$env:TEMP\lodstudio_selftest"
& .\dist\L4D2LOD远景工坊.exe --selftest --out "$env:TEMP\lodstudio_selftest_out"
Get-Content "$env:TEMP\lodstudio_selftest_out\selftest.txt" -Tail 3

# 2) End-to-end (12 scenarios: real Blender + synthetic projects — include chains / GBK / hand-written
#    $lod / corrupt marker blocks / SMD / Chinese-space-long paths / wrapper QC / cancellation)
powershell -ExecutionPolicy Bypass -File tests\run_e2e.ps1 -Blender "D:\blender 4.1\blender.exe"
# Optional flags: -SkipBuild -KeepTemp -IncludeCancel -Levels 2 -Dist "20,50" -Ratio "60,25"

# 3) UI audit (state-matrix screenshots + layout audit + WCAG contrast + theme round-trip + three
#    scaling levels; passes only with FAIL=0)
& .\dist\L4D2LOD远景工坊.exe --ui-audit --out "$env:TEMP\ui-audit"
```

- All tests build their data in temp directories and **never touch real user projects or settings**
  (the settings directory can be overridden via `L4D2LODSTUDIO_SETTINGS_DIR`).
- Evidence locations: self-test results in `selftest.txt` under `--out` (the historical sampling directory
  `%TEMP%\selftest_probe` is for reference only); E2E results in `%TEMP%\lodstudio_e2e\e2e_report.txt`
  plus `logs\`; UI audit results in `<out>\audit.txt` plus PNGs, with key screenshots archived to
  `docs\screenshots\main\`.
- The acceptance report (environment, commands, assertion list, and every measured number) is
  `docs/04_验收报告.md` (§10–§17 record the acceptance/deployment of UI polish rounds 2–9).

### Repository layout

```
L4D2_LOD_Studio/
├─ build.ps1                 One-click build (SDK lookup / publish / dist / integrity fix / hashes)
├─ dist/                     Deliverables (exe)
├─ src/
│  ├─ L4D2LodStudio.csproj   net48 + WPF + embedded resources + version resource
│  ├─ app.manifest           PerMonitorV2 DPI / longPathAware / asInvoker / activeCodePage=UTF-8
│  ├─ Program.cs             Entry point (GUI / CLI dispatch / single instance / crash log)
│  ├─ AppInfo.cs             Version, settings dir, temp dir
│  ├─ SingleInstance.cs      Single-instance handoff (a second instance forwards paths to the open window)
│  ├─ MessageBar.cs          Minimal prompt for unhandled exceptions
│  ├─ Engine/                Engine (no UI dependencies)
│  │  ├─ Qc/                 QC encoding detection / lexer / parser / injection / backup
│  │  ├─ Model/              SMD / GLB codecs and conversion (bone weights included)
│  │  ├─ Blender/            Blender discovery / script extraction / process scheduling / watchdog / crash resume / cancel
│  │  ├─ Pipeline/           Scanning / nekomdl trial compile / vtx parsing / reports / diagnostics
│  │  ├─ Batch/              Planner / runner / plan & result models
│  │  └─ Platform/           Job Object / runtime context (admin / low integrity) / output locations
│  ├─ Controls/              Custom-drawn controls (icons, segmented, progress bars, ratio bar, toast, dialogs, stepped chart…)
│  ├─ Themes/                Dual-theme tokens + type ramp + control / overlay styles
│  ├─ ViewModels/            MVVM (Shell / queue / LOD plan / log / plan)
│  ├─ Views/                 Main window / settings window (5 pages) / dialogs / skip-main picker
│  ├─ Services/              Theme switching, off-screen capture, value converters
│  ├─ Cli/                   Self-test / UI audit / batch CLI / icon sheet / widget & settings previews
│  └─ Assets/                app.ico, blender_backend.py (embedded resources)
├─ tests/
│  ├─ gen_fixtures.py        Generates E2E synthetic projects with real Blender
│  └─ run_e2e.ps1            End-to-end regression (11 scenarios + optional cancel = 12)
├─ tools/
│  └─ make_icon.py           Icon source generator (variants → app.ico + sheet)
└─ docs/                     All documents are in Chinese:
   ├─ 00_方案_设计与决策.md   Requirements, technology choices, architecture, UI positioning, test & acceptance criteria
   ├─ 01_开发约定.md          Engineering conventions, self-test style, module ownership, acceptance gates
   ├─ 01_引擎规格.md          Engine implementation spec (modules / protocols / formats / invariants / API index)
   ├─ 02_UI设计规范.md        Design language, tokens, region specs, states, motion, acceptance
   ├─ 03_使用说明.md          User manual (UI / settings / shortcuts / CLI / FAQ)
   ├─ 04_验收报告.md          Acceptance evidence (build / self-test / E2E / UI audit / real projects / performance / packaging)
   ├─ 05_代码评审_引擎.md      Independent code review report (P0 / P1 / P2 / P3 findings and fixes)
   └─ screenshots/            Acceptance screenshots (icons / widgets / settings pages / main window)
```

### Design notes (why it works this way)

- **Safety first**: every QC modification follows "back up → write only after all backups succeed →
  atomic replace → roll back on failure". The `.lodbak` backups are never overwritten; the tool marker
  block is idempotent; a QC missing its end marker is refused; no failure ever deletes the only copy;
  GBK / UTF-8 / BOM / line endings are preserved byte-for-byte.
- **Global dedup**: a model referenced by multiple QCs or tasks is decimated only once; each output
  path is produced only once.
- **Predictable**: the scan preview / job plan tells you what will happen, what will be skipped, and why —
  before anything is touched.
- **Recoverable**: stopping only cancels pending work; LODs already generated are kept and QCs are left
  untouched; all child processes live in a Job Object, so no orphans survive the main process.
- **Single-exe delivery**: no installer, no runtime, no side-by-side files; the Blender script is extracted
  to the temp directory by content hash and reclaimed by startup cleanup.

### Status and known limitations

**Done and verified** (evidence in `docs/04_验收报告.md`):

- Single-file exe ≈ 854 KB, Medium integrity, FileVersion 7.0.0.0, zero third-party dependencies;
  `--version` answers in 75 ms;
- Engine self-test **77 passed / 0 failed** (including real Blender 4.0.2 / 4.1.1 and nekomdl cases);
- E2E **12/12 passed** (three write modes, force, corrupt-marker refusal, wrapper QC, SMD round-trip,
  GBK / Chinese / spaces / long paths, cancellation);
- Real projects: read-only plan over **646 QCs / 1065 models / 4196 tasks / 6.9M triangles in 13.1 s with
  0 errors**; a real sub-project batch **30/30 decimations succeeded, 132,350 → 33,331 triangles, and an
  idempotent re-run changed 0 bytes**; 18/18 real nekomdl trial compiles succeeded with vtx LOD counts
  read back;
- UI audit (reference, latest run) **PASS 80 / FAIL 0 / NOTE 4** (16 states × 2 themes, 36 layout groups,
  46 contrast checks, 0 theme round-trip diffs, three scaling levels, 8-level line height fixed at 40 /
  outline assertions); round 9 deliberately re-ran neither the self-test nor the audit, per user request.

**Known limitations** (intentional trade-offs, not defects; documented in `docs/03_使用说明.md`):

- Only `.glb` / `.smd` are supported; `.dmx` is not — same as the legacy version (the plan shows
  "skipped: not glb/smd" explicitly);
- Cold start takes 2.6–4.8 s (inherent cost of a rich WPF UI, see acceptance report §5); warm start ≈ 2.6 s;
- Read-only, encoding-broken, or marker-corrupted QCs are **refused** (data safety first, see manual §13);
- `innode` entries, small parts below the triangle threshold, and at most 4 weight influences per `$lod`
  line — all identical to the legacy behavior;
- Blender 4.2+, >260-char paths / UNC, real drag-and-drop, admin / low-integrity, multi-monitor, and
  Windows 10 have not been manually validated (see acceptance report §7).

### Documentation

Start with `docs/00_方案_设计与决策.md`; engineering conventions are in `docs/01_开发约定.md` and
`docs/01_引擎规格.md`; the UI spec is `docs/02_UI设计规范.md`; the user manual is
`docs/03_使用说明.md`; acceptance evidence is `docs/04_验收报告.md`; the independent code review is
`docs/05_代码评审_引擎.md`. (All in Chinese.)

### FAQ (selected)

- **Build fails with "dotnet not found"**: pass `-DotNet <path to dotnet.exe>` for a portable SDK, or set
  the `DOTNET_EXE` environment variable.
- **Every file write fails after double-clicking the exe**: check whether the title bar shows "Low integrity"
  (red dot); copy the exe to the desktop / unblock it, or run `icacls "<exe>" /setintegritylevel Medium`.
  The build script already tries to raise the dist exe to Medium.
- **Observed behavior differs from the docs**: the code wins; add a diff row in `docs/01_引擎规格.md §14`.
  For acceptance numbers, `docs/04_验收报告.md` is authoritative.
- **Just want to see what the tool would do**: run `--plan <project dir>` (read-only, writes nothing),
  or use "Scan Preview" in the UI.
- **Run tests without building**: `tests\run_e2e.ps1 -SkipBuild` (requires an existing
  `dist\L4D2LOD远景工坊.exe` and a real Blender).
- **Where to read the UI audit**: `audit.txt` in the `--ui-audit` output directory (environment at the top,
  summary and failure details at the bottom) plus the PNG screenshots; key screenshots are copied to
  `docs\screenshots\main`.

### Maintenance conventions

- Changing public Engine contracts (`src/Engine/**Contracts*.cs`, `QcModels.cs`, `Contracts.cs`) requires
  syncing `docs/01_引擎规格.md`.
- After changing `src/Assets/blender_backend.py`, rebuild (the script is extracted by content hash;
  stale copies are cleaned up after 7 days).
- New self-test cases go into the owning module's `SelfTest*.cs` via `[SelfTest("…")]`, never into shared
  files.
- On any code/doc conflict, the code wins and the difference is registered in the diff tables at the end
  of `docs/01_引擎规格.md` and `docs/03_使用说明.md`.
- Acceptance evidence must not hard-code temporary absolute paths; follow the citation style in
  `docs/04_验收报告.md §8`.
- After UI changes, run `--ui-audit` (FAIL=0 required) and re-archive `docs/screenshots/main`; after
  engine changes, run `--selftest` + `tests\run_e2e.ps1`.
- Startup performance can be profiled with `L4D2LOD_PERF=1` (log at `%TEMP%\L4D2LOD远景工坊\perf.log`).

### Third-party assets

- Icons: hand-written 24×24 vector paths in this repo (no external icon fonts); `tools/make_icon.py`
  renders the multi-size `app.ico`.
- Fonts: system fonts only (Microsoft YaHei UI / Bahnschrift / Consolas); no font files are distributed.
- Blender-side script: written in this repo (MIT-style, distributed with the project).
- Code style: C# 12 / net48, no third-party dependencies; all user-facing text is Chinese, and numbers
  written to files always use InvariantCulture.
