# 远景工坊 · 求生之路 2 自动 LOD

Windows 10/11 x64 人物模组工具。支持工程文件夹、QC、GLB、参考 SMD 和 DMX。GUI 和 CLI 共用内置 meshoptimizer 后端，解压后可离线运行。

[下载 Windows 分发包和源码包](https://github.com/3163669737z-lgtm/L4D2LOD-Workshop/releases/latest)

## GUI

双击 `L4D2LOD远景工坊.exe`，将工程文件夹、QC 或模型拖入窗口，也可通过队列中的「导入」选择文件或文件夹。将「LOD 方案」设为「自动」，点「扫描预览」查看候选档位、写入方式和跳过原因。点「按计划生成」执行当前预览。改动队列或有效参数后，先重新扫描。生成后可从底栏打开报告和产物目录。实际面数、估算距离和写入的 Source 阈值见报告。

自动模式下，导入 QC 或包含 QC 的文件夹会处理工程引用，导入独立模型只在模型旁写对应格式的 LOD 文件。处理多个模型时可一次导入多个文件。

## CLI

```powershell
.\L4D2LOD-cli.exe --help
.\L4D2LOD-cli.exe --plan 'D:\工程\survivor.qc' --out 'D:\LOD报告\预览'
.\L4D2LOD-cli.exe --batch 'D:\工程\survivor.qc' --out 'D:\LOD报告\生成'
.\L4D2LOD-cli.exe --batch 'D:\模型\角色.glb' 'D:\模型\配件.smd'
```

- `--plan` 只读取工程，并将计划写入 `plan.json`。`--batch` 另外写入 `batch.json`、运行报告及日志。省略 `--out` 时输出到临时目录。
- `--workers N` 调整并行任务数，`--keep-models 'SMD\Face.glb|SMD\Hair.glb'` 指定需要沿用原网格的部件。
- 批处理退出码为 0（完成，可能包含跳过项）、1（存在失败）、2（参数或实例冲突）、3（程序异常）。命令行批处理与 GUI 共用单实例锁。
- `--preset 4` 显式选择自动。固定预设和手工参数可通过 `--preset 0` 至 `--preset 3` 或成对的 `--dist` 与 `--ratio` 使用。

## 自动规划与写回

- 根据每个 QC 的可见组合估算原始面数。同一 BodyGroup 的互斥选项取三角面数最大的选项。超过 10,000 面时按对数生成候选，LOD0 加自动 LOD 一共最多 8 级。
- 每档以 LOD0 为基础，目标保留比例根据原始面数和候选级数估算。共享网格按引用它的最高面数方案生成一次。模型会依据几何、轮廓和属性检查保留更多面，并舍弃相对上一个有效档节省不足 10% 的候选。
- 切换阈值根据参考尺寸、减面比例、1080p 高度和垂直视角 60° 估算。报告分别列出估算的 Hammer 单位和 `$lod` 阈值。QC 模型使用工程坐标和 `$scale`，独立 GLB 按 glTF 米制估算。[Valve Source SDK 的 LODMetric](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/studio.h#L1510)
- 自动模式先生成模型，再备份 `.qc` 并原子写回。已有手写 `$lod` 或 `$shadowlod` 的工程会跳过。多个 QC 共享模型声明时，工具可能将自动块写入该 include，结果会标出实际写入的文件。重复执行更新同一个工具块。
- `.lodbak` 保留首次修改前的 QC。随后的修改使用时间戳备份。生成的模型名带内容摘要，回滚旧 QC 后仍可引用原来的 LOD。

## 支持范围

- GLB 的表情图元与 QC 中声明表情的 SMD/VTA 继续使用源拓扑。DMX 支持二进制和 KeyValues2 的普通参考网格，当前验证的组合为 `model 1`、`binary 2` 或 `keyvalues2 1`。含 Flex 的 DMX 引用沿用原网格。
- 减面质量校验依据绑定姿势中的表面、投影轮廓及蒙皮属性采样。游戏动画、动态附件穿插和表情播放仍需在 HLMV++ 或游戏内验收。带 Morph 的独立 GLB 可以出几何 LOD，实际减面量取决于可安全简化的图元。
- 自动生成流程输出模型源、QC 和报告。游戏编译可沿用工程自己的 NekoMDL 或批处理流程。

## 从源码构建

需要 .NET SDK 和 .NET Framework 4.8 引用程序集。运行 `pwsh -File ./build.ps1`，在 `dist` 获取两种入口。重建原生减面组件请运行 `tools/build-gltfpack.ps1`，使用 x64 MinGW、CMake 和 Ninja。

```powershell
.\dist\L4D2LOD-cli.exe --selftest --out "$env:TEMP\lod-selftest"
.\dist\L4D2LOD-cli.exe --ui-audit --probe automatic 'D:\工程\survivor.qc' --out "$env:TEMP\lod-ui"
```

第三方依赖见 `THIRD_PARTY_NOTICES.md`。
