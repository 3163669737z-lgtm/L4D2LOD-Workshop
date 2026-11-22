# 远景工坊 · L4D2 自动 LOD 工具

给《求生之路 2》的模型工程自动生成多级 LOD。拖进工程、看一眼计划、点开始，剩下交给它。

**v0.2.0** · Windows 10 / 11 · 单文件 exe · [下载最新版](https://github.com/3163669737z-lgtm/L4D2LOD-Workshop/releases/latest) · [English ↓](#english)

## 它帮你做什么

- **找 QC**：从工程里找出真正会参与编译的 QC，展开 `$include`，把模型引用收齐。
- **做 LOD**：按距离生成 `*_lod1.glb`、`*_lod2.glb`……默认 4 级。
- **写回 QC**：把 `$lod { replacemodel … }` 幂等写进去，重复跑不会叠加。
- **留后路**：改过的 QC 旁都有 `.lodbak`，想还原改回 `.qc` 就行。

它不碰你的 `.bat`、材质、贴图和模型源文件，也不替你编译。生成完照旧运行你自己的 `批量编译.bat`。

## 两种减面后端

| 后端 | 说明 | 适合 |
|---|---|---|
| **Blender**（默认） | 逐连通块精细减面，质量优先；需要安装 Blender 4.0 / 4.1 / 4.2 | 机器上有 Blender，想要更好的细节 |
| **内置 gltfpack**（v0.2.0 新增，实验） | 不用装 Blender，速度约快一个量级；Body 内缩同样生效 | 想开箱即用、快速出 LOD |

在「设置 → 引擎 → 减面后端」里切换。选「内置 gltfpack」时，Blender 仍可保留作回退，不会偷偷切换。

## 五分钟上手

1. 从 [Releases](https://github.com/3163669737z-lgtm/L4D2LOD-Workshop/releases/latest) 下载分发包，解压到一个**有写权限**的目录。桌面、D 盘都行，别放 `C:\Program Files`。
2. 双击 `L4D2LOD远景工坊.exe`。第一次先到「设置 → 引擎」选后端：有 Blender 就用默认，没装就选「内置 gltfpack」。
3. 把工程文件夹拖进窗口，或点「导入文件夹」。工具会先只读扫描，不会改任何文件。
4. 点「扫描预览」，看看哪些 QC 会处理、哪些会跳过、为什么。没问题就点「开始生成」。
5. 生成完，回到自己的工程按平时流程编译。想还原，就把 `.lodbak` 改回 `.qc`。

> 第一次建议拿**工程副本**试跑。默认 4 级 LOD（10 / 15 / 25 / 50 HU）和官方角色参数一致，不确定就用默认。

## 下载与系统要求

- 分发包：`farsight-l4d2-lod-studio-v0.2.0-release.zip`（exe + 完整使用说明 + 第三方声明）
- 源码包：`farsight-l4d2-lod-studio-v0.2.0-source.zip`
- Windows 10 1903+ / Windows 11；系统自带 .NET Framework 4.8，不需要 Python，也不需要命令行
- Blender 只在选 Blender 后端时需要；`nekomdl.exe` 只在开「生成后试编译」时需要
- 内置 gltfpack 来自 meshoptimizer（MIT）

## 安全与还原

- 改 QC 前先备份，所有备份成功后才写入；写入用原子替换，失败会回滚。
- `.lodbak` 不会被覆盖；之后每次修改会另存带时间戳的备份。
- QC 只读、编码异常或工具标记块损坏时，直接拒绝修改，不会硬写。
- 同一个模型被多个 QC 引用时，只减面一次。

## 命令行（可选）

```powershell
L4D2LOD远景工坊.exe --plan  <工程目录>   # 只读预览，输出 plan.json
L4D2LOD远景工坊.exe --batch <工程目录>   # 无界面批量执行
L4D2LOD远景工坊.exe --backend gltfpack --batch <工程目录>
L4D2LOD远景工坊.exe --selftest           # 引擎自测
```

`--plan` 不写模型和 QC；`--batch` 的报告和 `batch.json` 默认写到临时目录，可用 `--out` / `--report-dir` 指定。

## 常见问题

**没有 Blender 能用吗？**  
能。选「内置 gltfpack」后端，全程不需要 Blender。

**减面后部件会粘在一起吗？**  
不会按空间位置把不相连的部件焊在一起；gltfpack 只折叠原网格已有的边。

**为什么面数没降到设定比例？**  
形状优先。面数少的部件会自动少减，误差到上限也会提前停。目标比例是上限参考，不是硬指标。

**中文 / GBK 的 QC 会乱码吗？**  
不会。写回沿用原 QC 的编码、BOM 和换行。

**杀毒软件拦截写文件？**  
把工具加入信任区，或右键「以管理员身份运行」。管理员模式收不到拖放，请用「导入」按钮。

## 从源码构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

需要 .NET SDK 8；脚本会自动查找便携版 SDK。产物在 `dist\L4D2LOD远景工坊.exe`。

完整使用说明在分发包的 `README.md` 里；设计与验收资料在仓库 `docs/`。

## 版本

- **0.2.0**：新增「内置 gltfpack」减面后端，不需要 Blender；保留 Blender 后端作为精细 / 回退方案。
- 更早版本记录见 Releases。

---

## English

**Yuanjing Workshop** is a Windows app that generates multi-level LODs for *Left 4 Dead 2* model projects. Drop in a project, check the plan, press Start — it does the rest.

**v0.2.0** · Windows 10 / 11 · single exe · [Download the latest release](https://github.com/3163669737z-lgtm/L4D2LOD-Workshop/releases/latest)

### What it does

- **Finds the QCs** that are actually compiled, expands `$include` chains, and collects model references.
- **Builds the LODs**: generates `*_lod1.glb`, `*_lod2.glb`, and so on at the distances you set (4 levels by default).
- **Writes them back**: inserts the `$lod { replacemodel … }` blocks idempotently, so repeated runs do not stack up.
- **Keeps a way back**: every modified QC gets a `.lodbak` file. Rename it back to `.qc` to restore.

It does not touch your `.bat`, materials, textures, or source models, and it does not compile for you. Run your usual build script afterwards.

### Two decimation backends

| Backend | Details | Best for |
|---|---|---|
| **Blender** (default) | Per-connected-component decimation, quality first. Requires Blender 4.0 / 4.1 / 4.2. | You have Blender and care about detail. |
| **Built-in gltfpack** (new in v0.2.0, experimental) | No Blender needed. About 10× faster. Body shrinking still works. | Quick setup and fast LODs. |

Switch it in *Settings → Engine → Decimation backend*. With the built-in gltfpack backend, Blender stays optional as a fallback; the app never switches behind your back.

### Quick start

1. Download the release package and unzip it to a **writable** folder (Desktop or D: is fine; avoid `C:\Program Files`).
2. Run `L4D2LOD远景工坊.exe`. On first launch, open *Settings → Engine* and pick a backend: keep Blender if you have it, or choose built-in gltfpack.
3. Drag a project folder into the window, or click *Import Folder*. The first scan is read-only.
4. Click *Scan Preview* to see what will be processed, what will be skipped, and why. If it looks right, click *Start Generation*.
5. Build your models with your usual pipeline. To undo, rename the `.lodbak` file back to `.qc`.

> Start with a **copy of the project** the first time. The default 4 levels (10 / 15 / 25 / 50 HU) match the official character settings.

### Requirements

- Windows 10 1903+ / Windows 11. .NET Framework 4.8 is already included.
- No Python, no installer, no command line required.
- Blender only for the Blender backend. `nekomdl.exe` only for the optional "trial compile" step.
- The app UI and the bundled manual are currently Chinese-only.

### Safety and restore

- Backs up every QC before writing; writes only after all backups succeed, using atomic replace with rollback.
- `.lodbak` files are never overwritten. Later edits get timestamped backups.
- Read-only, badly encoded, or corrupted QCs are refused instead of force-written.
- A model referenced by several QCs is decimated only once.

### Command line (optional)

```powershell
L4D2LOD远景工坊.exe --plan  <project>   # read-only plan, writes plan.json
L4D2LOD远景工坊.exe --batch <project>   # headless batch run
L4D2LOD远景工坊.exe --backend gltfpack --batch <project>
L4D2LOD远景工坊.exe --selftest          # engine self-test
```

`--plan` does not write models or QCs. `--batch` writes its report and `batch.json` to the temp directory by default; use `--out` / `--report-dir` to change that.

### FAQ

**Can I use it without Blender?**  
Yes. Choose the built-in gltfpack backend; Blender is not needed at all.

**Will separate parts get welded together?**  
No. Parts are not merged by spatial position; gltfpack only collapses edges that already exist in the mesh.

**Why is the triangle count above the target ratio?**  
Shape comes first. Low-poly parts are decimated less, and the error limit can stop decimation early. The target is a reference ceiling, not a hard guarantee.

**What about Chinese / GBK QCs?**  
They are preserved. Writes keep the original encoding, BOM, and line endings.

### Build from source

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

Requires .NET SDK 8; the script can locate a portable SDK automatically. The output is `dist\L4D2LOD远景工坊.exe`.

The full manual is the `README.md` inside the release package. Design and acceptance notes live under `docs/`.

### Version

- **0.2.0**: added the built-in gltfpack decimation backend (no Blender required); the Blender backend remains for fine decimation or fallback.
- See Releases for older builds.
