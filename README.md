# 远景工坊 · L4D2 自动 LOD 工具（v7）

给《求生之路 2》模型工程自动生成多级 LOD 的 Windows 桌面工具：用 Blender 减面成
`*_lodN.glb/smd`，再把 `$lod` 幂等写回 QC（带备份、原子写、可一键还原）。**从零重写**，
不包含也不依赖旧版（v4.x / FIELD v5.x）源码。

- 交付形态：**单个 exe**（`dist\L4D2LOD远景工坊.exe`），双击即用
- 运行时：.NET Framework 4.8（Win10 1903+ / Win11 自带，无需安装运行时）
- 外部依赖：Blender 4.0/4.1（已实测；4.2+ 有兼容分支但尚未验收）；nekomdl.exe（可选，仅"生成后试编译"）
- 界面：WPF 自绘（浅色「日测」/ 暗色「夜测」双主题、自绘标题栏、「导入文件夹 / 导入文件」两入口、LOD 数值即时编辑、CJK/拉丁/等宽三级字体）
- 引擎：纯 .NET、无 UI 依赖，可 `--selftest` / `--plan` / `--batch` 无界面运行
- 版本：程序版本 7.0.0、文件版本 7.0.0.0；版权 2026 远景工坊

## 最短路径（使用者）

1. 构建出 `dist\L4D2LOD远景工坊.exe`（下面「构建」）。
2. 设置 → 引擎页指定 `blender.exe`（通常自动找到）。
3. 把工程文件夹拖进窗口 → 等自动扫描 → 点「扫描预览」确认计划 → 点「开始生成」。
4. 回到自己的工程目录，双击原来的 `批量编译.bat`；不放心就用 QC 旁的 `.lodbak` 还原。
5. 完整说明与 FAQ 见 `docs/03_使用说明.md`。

## 构建

本机没有 .NET SDK 安装包时，使用便携 SDK（默认路径）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

- 编译器查找顺序：参数 `-DotNet` → 环境变量 `DOTNET_EXE` → `C:\Users\TPP\.tools\dotnet-sdk-8.0.404\dotnet.exe`
  → `%ProgramFiles%\dotnet\dotnet.exe` → PATH 里的 `dotnet`
- 参数：`-Clean`（先删 `src\bin`/`src\obj`/`.build`）、`-NoDist`（只构建）、`-Debug`、`-Configuration <名称>`、`-DotNet <路径>`
- 目标框架 `net48`，`LangVersion=latest`（用 SDK 里的 Roslyn 编译现代 C#），`AnyCPU`/`Prefer32Bit=false`
- 产出：`dist\L4D2LOD远景工坊.exe`（单文件；内嵌 `Assets/blender_backend.py` 与图标；带 7.0.0.0 版本资源）
- `build.ps1` 会把 exe 完整性级别修正为 **Medium**（受限环境编译产物默认是 Low，Low 进程写不了普通文件），
  并在末尾打印体积 / SHA256 / FileVersion / integrity；成功末行 `BUILD OK`，失败 `BUILD FAILED` 并退出码 1
- 不生成 `app.config` / `pdb`；不引用任何第三方 NuGet 包或旁挂 DLL

## 运行

```powershell
dist\L4D2LOD远景工坊.exe                  # 打开界面
dist\L4D2LOD远景工坊.exe <文件|文件夹>...  # 打开界面并把路径加入队列（单实例：已有窗口则交给它）
dist\L4D2LOD远景工坊.exe --version        # 打印版本
dist\L4D2LOD远景工坊.exe --help           # 打印用法
dist\L4D2LOD远景工坊.exe --selftest [--filter 关键字] [--out 目录]   # 引擎自测
dist\L4D2LOD远景工坊.exe --ui-audit --out <目录> [--shots-out <目录>] # UI 验收（截图/布局/对比度/主题往返/缩放）
dist\L4D2LOD远景工坊.exe --plan  <工程目录> [选项] # 只读计划（plan.json）
dist\L4D2LOD远景工坊.exe --batch <工程目录> [选项] # 无界面执行一次批量（batch.json/报告/日志）
```

维护用入口（不面向普通用户）：`--icon-sheet [--out]`（图标总表）、`--widget-preview [--out]`（控件状态矩阵）、
`--settings-preview [--out]`（设置 5 页/备份联动/诊断/跳过主模型选择器截图）。
`--plan/--batch` 的选项（`--preset/--dist/--ratio/--levels/--mode/--workers/--min-triangles/--force/--compile/--facial/
--skip-main/--no-backup/--fast/--blender/--nekomdl/--report-dir/--out`）见 `docs/03_使用说明.md §14`。

WinExe 不会自动把 CLI 输出打到控制台：脚本里请重定向 stdout，或读 `--out` 目录里的 `plan.json` / `batch.json` / 报告。

测试/便携相关环境变量：

- `L4D2LODSTUDIO_SETTINGS_DIR`：覆盖设置目录（自测、E2E、UI 审计都用它隔离用户配置）。
- `L4D2LOD_SETTINGS_DIR`：覆盖旧版设置目录（只读导入用；测试里可指向空目录）。
- `DOTNET_EXE`：`build.ps1` 的 SDK 路径（等价于 `-DotNet`）。
- 临时目录固定为 `%TEMP%\L4D2LOD远景工坊\`（任务 JSON/脚本/SMD 中间文件/崩溃日志），启动时清理陈旧文件。

## 测试

```powershell
# 1) 引擎自测（要求 SELFTEST: N passed, 0 failed；WinExe 需重定向 stdout，结果也会写 selftest.txt）
$env:L4D2LODSTUDIO_SETTINGS_DIR = "$env:TEMP\lodstudio_selftest"
& .\dist\L4D2LOD远景工坊.exe --selftest --out "$env:TEMP\lodstudio_selftest_out"
Get-Content "$env:TEMP\lodstudio_selftest_out\selftest.txt" -Tail 3

# 2) 端到端（12 场景：真实 Blender + 合成工程：include 链 / GBK / 手写 $lod / 坏标记块 / SMD / 中文空格长路径 / 包装 QC / 取消）
powershell -ExecutionPolicy Bypass -File tests\run_e2e.ps1 -Blender "D:\blender 4.1\blender.exe"
# 可选参数：-SkipBuild -KeepTemp -IncludeCancel -Levels 2 -Dist "20,50" -Ratio "60,25"

# 3) UI 审计（状态矩阵截图 + 布局审计 + WCAG 对比度 + 主题往返 + 三档缩放；FAIL=0 才通过）
& .\dist\L4D2LOD远景工坊.exe --ui-audit --out "$env:TEMP\ui-audit"
```

- 测试全部在临时目录造数据，**不碰用户真实工程与配置**（`L4D2LODSTUDIO_SETTINGS_DIR` 可覆盖设置目录）。
- 证据位置：自测结果写在 `--out` 目录的 `selftest.txt`（历史采样目录 `%TEMP%\selftest_probe` 仅作参考）；
  E2E `%TEMP%\lodstudio_e2e\e2e_report.txt` + `logs\`；UI 审计 `<out>\audit.txt` + PNG，
  关键截图归档到 `docs\screenshots\main\`。
- 验收报告（环境/命令/断言清单/全部实测数字）：`docs/04_验收报告.md`（§10–§17 为第二至九轮 UI 打磨的验收/部署记录）。

## 目录

```
L4D2_LOD_Studio/
├─ build.ps1                 一键构建（SDK 定位 / publish / dist / 完整性 / 哈希）
├─ dist/                     交付产物（exe）
├─ src/
│  ├─ L4D2LodStudio.csproj   net48 + WPF + 嵌入资源 + 版本资源
│  ├─ app.manifest           PerMonitorV2 DPI / longPathAware / asInvoker / activeCodePage=UTF-8
│  ├─ Program.cs             入口（GUI / CLI 分发 / 单实例 / 崩溃日志）
│  ├─ AppInfo.cs             版本、设置目录、临时目录
│  ├─ SingleInstance.cs      单实例投递（第二个实例把路径交给已打开窗口）
│  ├─ MessageBar.cs          未处理异常的最小提示
│  ├─ Engine/                引擎（无 UI 依赖）
│  │  ├─ Qc/                 QC 编码探测/词法/解析/注入/备份
│  │  ├─ Model/              SMD / GLB 编解码与互转（骨骼权重）
│  │  ├─ Blender/            Blender 定位/脚本释放/进程调度/看门狗/崩溃续跑/取消
│  │  ├─ Pipeline/           扫描 / nekomdl 试编译 / vtx 解析 / 报告 / 诊断
│  │  ├─ Batch/              计划器 / 执行器 / 计划与结果模型
│  │  └─ Platform/           Job Object / 运行环境（管理员/低完整性）/ 输出位置
│  ├─ Controls/              自绘控件（图标、分段、进度条、比例条、Toast、对话框、阶梯图控件…）
│  ├─ Themes/                双主题令牌 + 字体阶梯 + 控件/浮层样式
│  ├─ ViewModels/            MVVM（Shell / 队列 / LOD 方案 / 日志 / 计划）
│  ├─ Views/                 主窗口 / 设置窗口（5 页）/ 对话框 / 跳过主模型选择器
│  ├─ Services/              主题切换、离屏截图、值转换器
│  ├─ Cli/                   自测 / UI 审计 / 批量命令行 / 图标表 / 控件与设置预览
│  └─ Assets/                app.ico、blender_backend.py（嵌入资源）
├─ tests/
│  ├─ gen_fixtures.py        用真实 Blender 生成 E2E 合成工程
│  └─ run_e2e.ps1            端到端回归（11 场景 + 可选取消 = 12 场景）
├─ tools/
│  └─ make_icon.py           图标源生成（多方案 → app.ico + 总表）
└─ docs/
   ├─ 00_方案_设计与决策.md   需求、选型、架构、UI 定位、测试与验收标准
   ├─ 01_开发约定.md          工程约定、自测写法、模块分工、验收硬指标
   ├─ 01_引擎规格.md          引擎实现规格（模块/协议/格式/不变量/API 索引）
   ├─ 02_UI设计规范.md        设计语言、令牌、区域规格、状态、动效、验收
   ├─ 03_使用说明.md          用户手册（界面/设置/快捷键/CLI/FAQ）
   ├─ 04_验收报告.md          验收证据（构建/自测/E2E/UI 审计/真实工程/性能/打包）
   ├─ 05_代码评审_引擎.md      独立代码评审报告（P0/P1/P2/P3 清单与修复对照）
   └─ screenshots/            图标/控件/设置/主界面验收截图（main、widgets、设置页 PNG）
```

## 设计要点（为什么这么做）

- **安全第一**：QC 修改全部走"备份→全部备份成功才写→原子替换→失败回滚"；备份 `.lodbak` 永不覆盖；
  工具标记块幂等；缺结束标记拒绝改；任何失败都不删除唯一副本；GBK/UTF-8/BOM/换行原样保留。
- **全局去重**：同一个模型被多个 QC/多个任务引用只减面一次；同一输出路径只跑一次。
- **可预期**：扫描预览/作业计划先告诉你"会做什么、哪些跳过、为什么"，再动手。
- **可恢复**：停止只停后续，已生成的 LOD 保留、QC 不再改；子进程进 Job Object，主程序退出不留孤儿。
- **单 exe 交付**：无安装、无运行库、无旁挂文件；Blender 脚本按内容哈希释放到临时目录并由启动清理。

## 当前状态与已知限制

**已完成并实测**（证据见 `docs/04_验收报告.md`）：

- 单文件 exe 约 854 KB、完整性 Medium、FileVersion 7.0.0.0、无第三方依赖；`--version` 75 ms；
- 引擎自测 **77 passed / 0 failed**（含真实 Blender 4.0.2/4.1.1、nekomdl 用例）；
- 端到端 **12/12 通过**（三种写入方式、force、坏标记块拒绝、包装 QC、SMD 往返、GBK/中文/空格/长路径、取消）；
- 真实工程：只读计划 **646 QC / 1065 模型 / 4196 任务 / 6.9M 面 / 13.1 s / 0 错误**；
  真实子工程批量 **30/30 减面成功、132,350 → 33,331 面、幂等复跑 0 字节变化**；真实 nekomdl 试编译 18/18 成功并读回 vtx LOD 面数；
- UI 审计（参考，最近一次）**PASS 80 / FAIL 0 / NOTE 4**（16 状态×双主题、36 组布局、46 项对比度、主题往返 0 差异、三档缩放、8 级行高固定 40/外框边线断言）；第九轮按用户要求未重跑自检/审计。

**已知限制**（不是缺陷，已在 `docs/03_使用说明.md` 说明）：

- 只支持 `.glb` / `.smd`；`.dmx` 与旧版一致不支持（计划里明确显示"跳过：不是 glb/smd"）；
- 冷启动 2.6~4.8 s（WPF 富界面固有开销，见验收报告 §5）；热启动 2.6 s；
- 只读 / 编码异常 / 工具标记块损坏的 QC 会被**拒绝修改**（数据安全优先，见使用说明 §13）；
- `innode` 条目、面数低于门槛的小部件、`$lod` 每条最多 4 个权重影响 —— 与旧版行为一致；
- Blender 4.2+、>260 长路径/UNC、真实拖放、管理员/低完整性、多屏与 Win10 未做手工验收（见验收报告 §7）。

## 文档

从「[docs/00_方案_设计与决策.md](docs/00_方案_设计与决策.md)」开始；实现约定看
「[docs/01_开发约定.md](docs/01_开发约定.md)」与「[docs/01_引擎规格.md](docs/01_引擎规格.md)」；
界面规范见「[docs/02_UI设计规范.md](docs/02_UI设计规范.md)」；用户请看
「[docs/03_使用说明.md](docs/03_使用说明.md)」；验收证据见「[docs/04_验收报告.md](docs/04_验收报告.md)」，
独立代码评审见「[docs/05_代码评审_引擎.md](docs/05_代码评审_引擎.md)」。

## 常见问题（精选）

- **构建报 `找不到 dotnet`**：用 `-DotNet <dotnet.exe 路径>` 指定便携 SDK，或设置环境变量 `DOTNET_EXE`。
- **双击后写文件全失败**：看顶栏权限是否为"低完整性"（红点）；把 exe 复制到桌面/解除锁定，
  或 `icacls "<exe>" /setintegritylevel Medium`。构建脚本已尽量把 dist exe 提升到 Medium。
- **测出来的行为与文档不一致**：以代码为准，在 `docs/01_引擎规格.md §14` 追加差异行；验收数字以 `docs/04_验收报告.md` 为准。
- **只想看工具会做什么**：用 `--plan <工程目录>`（只读、不写文件），或界面里的「扫描预览」。
- **只想跑测试不构建**：`tests\run_e2e.ps1 -SkipBuild`（需要已存在 `dist\L4D2LOD远景工坊.exe` 与真实 Blender）。
- **UI 审计在哪看**：`--ui-audit` 输出目录里的 `audit.txt`（首部是环境、末部是汇总与失败明细）、PNG 截图；
  关键截图会复制到 `docs\screenshots\main`。

## 维护约定

- 改 Engine 公开契约（`src/Engine/**Contracts*.cs`、`QcModels.cs`、`Contracts.cs`）要同步 `docs/01_引擎规格.md`。
- 改 `src/Assets/blender_backend.py` 后必须重新构建（脚本按内容哈希释放，旧文件 7 天后清理）。
- 新增自测用例用 `[SelfTest("…")]` 写在所属模块的 `SelfTest*.cs` 里，不改共享文件。
- 任何代码/文档冲突以代码为准，并在 `docs/01_引擎规格.md` 与 `docs/03_使用说明.md` 文末的差异表登记。
- 验收证据不要写死临时绝对路径；按 `docs/04_验收报告.md §8` 的写法引用。
- 改 UI 后跑 `--ui-audit`（要求 FAIL=0）并重新归档 `docs/screenshots/main`；改引擎后跑 `--selftest` + `tests\run_e2e.ps1`。
- 启动性能可用 `L4D2LOD_PERF=1` 打点（`%TEMP%\L4D2LOD远景工坊\perf.log`）。

## 第三方资产

- 图标：本仓库手写 24×24 矢量路径（无外部图标字体），由 `tools/make_icon.py` 生成多尺寸 `app.ico`
- 字体：使用系统自带（Microsoft YaHei UI / Bahnschrift / Consolas），不随程序分发字体文件
- Blender 端脚本：本仓库自写（MIT 风格，随项目分发）
- 代码风格：C# 12 / net48，无第三方依赖；所有面向用户的文案为中文，写入文件的数字使用 InvariantCulture。
