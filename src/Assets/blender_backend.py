# -*- coding: utf-8 -*-
"""远景工坊 · L4D2 自动 LOD 工具 —— Blender 端减面脚本（4.0 / 4.1 / 4.2+）

这是 Blender 后端的**唯一源码**：C# 侧以嵌入资源 LodStudio.blender_backend.py 编进 exe，
运行时按内容 SHA-1 释放到 %TEMP%\\L4D2LOD远景工坊\\backend\\blender_backend_<hash>.py 再交给 Blender。

启动（C# 构造，参数顺序不要改）：
    blender.exe --background --factory-startup --python-exit-code 2 --python "<本脚本>" -- "<task.json>"
（--python-exit-code 2 必须在 --python 之前：脚本未捕获异常 → 进程退出码 2）

任务 JSON（UTF-8 无 BOM）：
    {"quality": true,
     "jobs": [{"src": "D:\\p\\a.glb", "dst": "D:\\p\\a_lod1.glb", "ratio": 0.997,
               "shrinkInner": 0.0009, "minFaces": 12, "guardPct": 35, "quality": false}]}
    - quality：整批质量模式（缺省 true）；job 内 quality 可覆盖整批
    - minFaces / guardPct / shrinkInner = 0 都是合法值，不能用 `or 默认值` 吞掉
    - 额外字段忽略

stdout 协议（逐行、UTF-8、每行 flush，C# 实时解析）：
    JOBS=<n> QUALITY=<True|False> BLENDER=<version>      脚本已开始执行（C# 置 SawStart）
    BEGIN <i>                                           第 i 个任务开始（崩溃/超时时定位）
    RESULT {json}                                       第 i 个任务结束（C# 解析并回调）
    OK [i+1/n] … / FAIL [i+1/n] …                       人类可读
    WARN …                                              可恢复的异常（焊接/形态键/法线/平滑…）
    DONE ok=<n> fail=<m> 用时<X.X>s                      全部结束

结果旁路文件 <task.json>.jsonl：每完成一个任务追加一行同样的 JSON 并 flush+fsync，
进程崩溃/被杀导致 stdout 丢行时 C# 用它补齐（同一任务只登记一次，不会重复）。

算法要点（与旧版一致，但按新结构重写）：
  1) 每个任务先 read_factory_settings(use_empty=True)，再导入 glTF（merge_vertices=True，TypeError 退化）；
  2) 清理：多用户网格 copy → shape_key_clear() → 按包围盒 1e-6 焊接重合点（bmesh remove_doubles）
     → 清自定义法线并按 60° 平滑（4.0 用 use_auto_smooth+angle，4.1+ 用 shade_smooth_by_angle）；
  3) 质量模式按连通块分配面数预算（每块保底 minFaces，保底总额不超过预算的 guardPct%，超出从大块扣），
     separate(type='LOOSE') 后逐块 Decimate(COLLAPSE, triangulate) 再 join 回原对象（保留名字/修改器/父级）；
     速度模式 = 全局比例 Decimate，不拆分不分析连通块（LOD1 只降 0.3% 面时 C# 会传 quality=false）；
  4) 内层部件（对象名含 body）沿法线内缩 shrinkInner × 包围盒对角线（按世界缩放折算到局部坐标）；
  5) 导出 GLB（export_format='GLB', use_selection=False, export_apply=False），导出后校验文件存在且非空。
"""

import bpy
import bmesh
import sys
import os
import json
import math
import time
import traceback

# ───────────────────────────────── 控制台编码 ─────────────────────────────────
# 中文 print 在 GBK 控制台下会抛 UnicodeEncodeError，统一成 UTF-8 + replace（绝不因日志崩脚本）
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

# ───────────────────────────────── 常量 ─────────────────────────────────
DEFAULT_MIN_FACES = 12      # 每个连通块保底面数
DEFAULT_GUARD_PCT = 35.0    # 保底面数最多占用总预算的百分比
WARN_OVER = 1.5             # 实际面数超过目标 50% 以上时告警
AUTO_SMOOTH_DEG = 60.0      # 清自定义法线后的平滑角度

# 仅供自动化测试：源路径包含该子串时模拟 Blender 原生崩溃（os.abort()），
# 用于验证 C# 侧「单任务崩溃只失败它自己，剩余任务换新进程续跑」。
_TEST_CRASH = os.environ.get('L4D2LOD_TEST_CRASH') or ''
# 仅供自动化测试：L4D2LOD_TEST_SLEEP=<秒> 且源路径含 sleepme 时先卡住指定秒数，
# 用于验证 C# 侧看门狗的空闲超时（只失败卡住的任务，剩余任务换新进程续跑）。
_TEST_SLEEP = 0.0
try:
    _TEST_SLEEP = float(os.environ.get('L4D2LOD_TEST_SLEEP') or 0)
except Exception:
    _TEST_SLEEP = 0.0

_BLENDER_VERSION = tuple(getattr(bpy.app, 'version', (0, 0, 0)))
_IS_41_PLUS = _BLENDER_VERSION >= (4, 1, 0)   # 4.1 移除了 mesh.use_auto_smooth


def warn(msg):
    """可恢复异常：只提示，不中断任务（C# 会把 WARN 开头的行转发到日志）"""
    print("WARN " + msg, flush=True)


def version_text():
    try:
        return bpy.app.version_string
    except Exception:
        return "%d.%d.%d" % (_BLENDER_VERSION + (0, 0, 0))[:3]


# ───────────────────────────────── 任务参数 ─────────────────────────────────


def _helper_collection_names():
    """glTF 导入器为骨骼显示生成的辅助集合名（Blender 约定以 glTF_not_exported 开头）。"""
    names = set()
    try:
        for c in bpy.data.collections:
            if c.name.startswith('glTF_not_exported'):
                names.add(c.name)
    except Exception:
        pass
    return names


def scene_mesh_objects():
    """场景里的真实网格对象：排除 glTF 导入器的辅助集合（否则会多算/多处理骨骼显示用的 Icosphere）。"""
    helpers = _helper_collection_names()
    out = []
    try:
        objects = list(bpy.context.scene.objects)
    except Exception:
        return out
    for o in objects:
        if o.type != 'MESH':
            continue
        if helpers:
            skip = False
            try:
                for c in o.users_collection:
                    if c.name in helpers:
                        skip = True
                        break
            except Exception:
                pass
            if skip:
                continue
        out.append(o)
    return out


def job_value(job, key, default, conv):
    """显式判 None：0 是合法值，不能被 `or DEFAULT` 吞掉"""
    v = job.get(key)
    if v is None:
        return default
    try:
        return conv(v)
    except Exception:
        return default


# ───────────────────────────────── 几何工具 ─────────────────────────────────

def is_inner_wrapped(name):
    """内层部件判定：对象名含 body（Body / body_01 / InnerBody…）"""
    return 'body' in (name or '').lower()


def bbox_diagonal(mw, me):
    """世界坐标包围盒对角线长度（用于内缩量与焊接阈值）"""
    mn = [1e18, 1e18, 1e18]
    mx = [-1e18, -1e18, -1e18]
    for v in me.vertices:
        co = mw @ v.co
        for k in range(3):
            if co[k] < mn[k]:
                mn[k] = co[k]
            if co[k] > mx[k]:
                mx[k] = co[k]
    if mn[0] > 1e17:
        return 0.0
    dx = mx[0] - mn[0]
    dy = mx[1] - mn[1]
    dz = mx[2] - mn[2]
    return math.sqrt(dx * dx + dy * dy + dz * dz)


def world_to_local_scale(obj):
    """世界距离换算到局部坐标所需的除数（matrix_world 的平均缩放）"""
    try:
        s = obj.matrix_world.to_scale()
        avg = (abs(s[0]) + abs(s[1]) + abs(s[2])) / 3.0
        return avg if avg > 1e-12 else 1.0
    except Exception:
        return 1.0


def shrink_along_normal(obj, amount):
    """沿顶点法线内缩（amount 已是局部坐标距离）"""
    if amount <= 0:
        return
    me = obj.data
    for v in me.vertices:
        n = v.normal
        v.co[0] -= n[0] * amount
        v.co[1] -= n[1] * amount
        v.co[2] -= n[2] * amount
    me.update()


# ───────────────────────────────── 网格清理 ─────────────────────────────────

def make_active(obj):
    """让 obj 成为唯一选中且激活的对象（很多 bpy.ops 依赖上下文）"""
    try:
        bpy.ops.object.select_all(action='DESELECT')
    except Exception:
        pass
    try:
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
    except Exception:
        pass


def weld_by_position(obj):
    """按位置焊接重合顶点，返回焊接掉的顶点数。

    glTF 导入的 merge_vertices 只合并「位置+法线」都相同的点，平直着色/硬边处仍是断开的
   （实测 flat icosphere 11120 面 = 33360 点 = 11120 个假连通块），这些假块会让按块分配失效，
    Decimate 还会沿断开处撕出裂缝。UV/法线是 loop 数据，焊接后仍保留，导出时按需自动拆分。
    """
    me = obj.data
    n = len(me.vertices)
    if n == 0:
        return 0
    try:
        mn = [1e18] * 3
        mx = [-1e18] * 3
        for v in me.vertices:
            c = v.co
            for k in range(3):
                if c[k] < mn[k]:
                    mn[k] = c[k]
                if c[k] > mx[k]:
                    mx[k] = c[k]
        diag = math.sqrt(sum((mx[k] - mn[k]) ** 2 for k in range(3)))
        dist = max(diag * 1e-6, 1e-8)
        bm = bmesh.new()
        try:
            bm.from_mesh(me)
            before = len(bm.verts)
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=dist)
            after = len(bm.verts)
            if after != before:
                bm.to_mesh(me)
                me.update()
        finally:
            bm.free()
        return max(0, n - len(me.vertices))
    except Exception as e:
        warn("焊接 %s 失败（已跳过）: %s" % (obj.name, e))
        return 0


def mark_sharp_edges_by_angle(obj, deg):
    """按面夹角手工标硬边（4.1+ 的 shade_smooth_by_angle 不可用时的回退）。

    4.1 起「自动平滑」改为按 sharp edge 计算法线，因此标硬边等价于按角度平滑。
    """
    try:
        bm = bmesh.new()
        try:
            bm.from_mesh(obj.data)
            cos_thr = math.cos(math.radians(deg))
            for e in bm.edges:
                faces = e.link_faces
                if len(faces) != 2:
                    e.smooth = False            # 边界/非流形边永远硬
                    continue
                e.smooth = faces[0].normal.dot(faces[1].normal) > cos_thr
            bm.to_mesh(obj.data)
        finally:
            bm.free()
        obj.data.update()
    except Exception as e:
        warn("按角度标硬边 %s 失败: %s" % (obj.name, e))


def _has_custom_normals(obj):
    try:
        return bool(obj.data.has_custom_normals)
    except Exception:
        return False


def clear_custom_normals_and_smooth(obj):
    """清自定义法线 → 按 60° 平滑。

    自定义法线在 Decimate 时会被插值坏（着色斑块），所以 LOD 阶段清掉、改用按角度平滑。
    4.0.x：mesh.use_auto_smooth / auto_smooth_angle（4.1 已移除这两个属性）
    4.1+ ：bpy.ops.object.shade_smooth_by_angle(angle=)，失败退 shade_auto_smooth(angle=)，
           再失败退回「手工按夹角标硬边」——保证 4.2/4.3 改名时也不会静默失去平滑。
    """
    make_active(obj)
    try:
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    except Exception as e:
        warn("清自定义法线 %s 失败（继续）: %s" % (obj.name, e))

    # 所有面先设为平滑，再按角度打硬边；否则清完法线后可能是全平面着色
    try:
        bpy.ops.object.shade_smooth()
    except Exception:
        try:
            for p in obj.data.polygons:
                p.use_smooth = True
        except Exception:
            pass

    angle = math.radians(AUTO_SMOOTH_DEG)
    if not _IS_41_PLUS:
        try:
            obj.data.use_auto_smooth = True
            obj.data.auto_smooth_angle = angle
        except Exception as e:
            warn("auto_smooth %s 失败: %s" % (obj.name, e))
        return

    done = False
    try:
        bpy.ops.object.shade_smooth_by_angle(angle=angle)
        done = True
    except Exception:
        pass
    if not done:
        try:
            bpy.ops.object.shade_auto_smooth(angle=angle)
            done = True
        except Exception:
            pass
    if not done:
        warn("shade_smooth_by_angle / shade_auto_smooth 均不可用（Blender %s），改用按角度标硬边"
             % version_text())
        mark_sharp_edges_by_angle(obj, AUTO_SMOOTH_DEG)


def prepare_mesh(obj):
    """Decimate 前的清理：多用户网格数据、形态键、焊接、自定义法线/平滑"""
    # 多个对象共用一份网格数据时 modifier_apply 会失败
    try:
        if obj.data.users > 1:
            obj.data = obj.data.copy()
    except Exception:
        pass

    # 形态键：LOD 不需要表情；不清掉 modifier_apply 会抛
    # "Modifier cannot be applied to a mesh with shape keys"
    try:
        if obj.data.shape_keys:
            obj.shape_key_clear()
    except Exception as e:
        warn("清形态键 %s 失败: %s" % (obj.name, e))

    weld_by_position(obj)

    if _has_custom_normals(obj):
        clear_custom_normals_and_smooth(obj)


# ───────────────────────────────── 连通块预算 ─────────────────────────────────

def analyze_blocks(obj):
    """bmesh + 并查集分析连通块，返回按面数降序的块大小列表"""
    me = obj.data
    bm = bmesh.new()
    try:
        bm.from_mesh(me)
        bm.verts.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        parent = list(range(len(bm.verts)))

        def find(x):
            while parent[x] != x:
                parent[x] = parent[parent[x]]
                x = parent[x]
            return x

        for f in bm.faces:
            vs = [v.index for v in f.verts]
            for i in range(1, len(vs)):
                ra, rb = find(vs[0]), find(vs[i])
                if ra != rb:
                    parent[rb] = ra

        sizes = {}
        for f in bm.faces:
            r = find(f.verts[0].index)
            sizes[r] = sizes.get(r, 0) + 1
    finally:
        bm.free()
    return sorted(sizes.values(), reverse=True)


def allocate(sizes, ratio, min_faces, guard_pct):
    """按连通块分配目标面数。

    全局比例会把小连通块压成平板（60 面的蕾丝 × 16% = 10 面 → 塌成平面，多层糊在一起）。
    改为：每块先按比例，再保底 min_faces；保底总开销不超过预算的 guard_pct%，超出按比例削减；
    若总目标仍超预算，从「可扣的大块」逐个扣 1，直到不超（不扣到保底线以下）。
    """
    total = sum(sizes)
    budget = max(1, int(round(total * ratio)))
    n = len(sizes)

    base = []
    for s in sizes:
        w = int(round(s * ratio))
        base.append(max(1, min(w, s)))

    deficits = []
    for i, s in enumerate(sizes):
        target = base[i]
        if s >= min_faces and base[i] < min_faces:
            target = min_faces
        if target > s:
            target = s
        deficits.append(target - base[i])

    total_deficit = sum(d for d in deficits if d > 0)
    guard = int(budget * guard_pct / 100.0)

    if total_deficit > guard and total_deficit > 0:
        scale = guard / float(total_deficit)
        for i in range(n):
            if deficits[i] > 0:
                deficits[i] = int(deficits[i] * scale)

    targets = [base[i] + deficits[i] for i in range(n)]

    over = sum(targets) - budget
    if over > 0:
        loop = 0
        while over > 0 and loop < 200000:
            loop += 1
            deducted = False
            for i in range(n):
                floor_i = min(min_faces, sizes[i])
                if targets[i] > floor_i and sizes[i] > min_faces:
                    targets[i] -= 1
                    over -= 1
                    deducted = True
                    if over <= 0:
                        break
            if not deducted:
                break

    for i in range(n):
        targets[i] = max(1, min(targets[i], sizes[i]))
    return targets


# ───────────────────────────────── Decimate ─────────────────────────────────

def decimate_single(obj, tgt_faces):
    """对单个对象做一个全局 Decimate(COLLAPSE, triangulate) 到目标面数"""
    cur = len(obj.data.polygons)
    if cur == 0 or tgt_faces >= cur:
        return False
    mod = obj.modifiers.new(name="LOD", type='DECIMATE')
    mod.decimate_type = 'COLLAPSE'
    mod.ratio = tgt_faces / float(cur)
    mod.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = obj
    # 有骨骼修改器时把 Decimate 移到最前，否则 apply 会报「不是第一个修改器」
    if len(obj.modifiers) > 1:
        try:
            bpy.ops.object.modifier_move_to_index(modifier=mod.name, index=0)
        except Exception:
            pass
    try:
        bpy.ops.object.modifier_apply(modifier=mod.name)
    except Exception:
        # 极少数情况下 apply 失败（上下文/形态键残留）：移除修改器，让调用方按原面数处理
        try:
            obj.modifiers.remove(mod)
        except Exception:
            pass
        raise
    return True


def decimate_object(obj, ratio, min_faces, guard_pct, quality):
    """返回 (原始面数, 现面数, 连通块数, 实际简化块数, 目标面数, 因差值<2 跳过的块数)"""
    total = len(obj.data.polygons)
    if total == 0:
        return 0, 0, 0, 0, 0, 0

    prepare_mesh(obj)

    if not quality:
        # 速度模式：纯全局比例（不分析连通块，LOD1 只降 0.3% 面时快很多）
        tgt = max(1, min(total, int(round(total * ratio))))
        decimate_single(obj, tgt)
        return total, len(obj.data.polygons), 1, 1, tgt, 0

    sizes = analyze_blocks(obj)
    targets = allocate(sizes, ratio, min_faces, guard_pct)
    nblocks = len(sizes)
    target_sum = sum(targets) if targets else total

    need_work = sum(1 for i in range(min(len(sizes), len(targets)))
                    if targets[i] < sizes[i])

    # 没什么可动的块：直接全局处理（避免 separate/join 的开销）
    if nblocks <= 1 or need_work == 0:
        tgt = max(1, min(target_sum, total))
        decimate_single(obj, tgt)
        return total, len(obj.data.polygons), nblocks, 0, tgt, 0

    orig_name = obj.name
    orig_data_name = obj.data.name
    scene = bpy.context.scene
    # 记录 separate 前的对象集合：新增的才是零件。
    # 按名字前缀收集会把别的原始对象（Body 与 Body.001）一起吞进来。
    before_set = set(o.name for o in scene.objects)

    make_active(obj)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='LOOSE')
    bpy.ops.object.mode_set(mode='OBJECT')

    parts = [obj] + [o for o in scene.objects
                     if o.name not in before_set and o.type == 'MESH']
    parts_sorted = sorted(parts, key=lambda o: -len(o.data.polygons))

    applied = 0
    skipped = 0
    for i, part in enumerate(parts_sorted):
        cur = len(part.data.polygons)
        if cur == 0 or i >= len(targets):
            continue
        want = targets[i]
        if want >= cur:
            continue
        if (cur - want) < 2:
            skipped += 1
            continue
        if decimate_single(part, want):
            applied += 1

    if len(parts) > 1:
        bpy.ops.object.select_all(action='DESELECT')
        for p in parts:
            p.select_set(True)
        # 以原对象为 active：保留它的名字、数据块、修改器（骨骼）与父子关系
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.join()

    try:
        obj.name = orig_name
        obj.data.name = orig_data_name
    except Exception:
        pass
    return total, len(obj.data.polygons), nblocks, applied, target_sum, skipped


# ───────────────────────────────── 导入 / 导出 ─────────────────────────────────

def import_gltf(src):
    """导入 glTF/GLB。

    merge_vertices=True：不合并时 UV 接缝/硬边会被当成独立连通块，separate 后各块独立
    Decimate 会产生裂缝；合并后接缝仍以 UV seam 保留在 loop 数据中，导出时自动拆分。
    老版本没有该参数 → TypeError 退化。
    """
    try:
        bpy.ops.import_scene.gltf(filepath=src, merge_vertices=True)
    except TypeError:
        bpy.ops.import_scene.gltf(filepath=src)


def export_glb(dst, src):
    """导出 GLB 并校验文件存在且非空。

    export_apply=False：不把骨骼/修改器烘进静态网格（骨骼蒙皮由 glTF 的 skin 表达）。
    """
    d = os.path.dirname(dst)
    if d:
        os.makedirs(d, exist_ok=True)
    bpy.ops.export_scene.gltf(
        filepath=dst,
        export_format='GLB',
        use_selection=False,
        export_apply=False,
    )
    if not os.path.isfile(dst):
        raise RuntimeError("导出 GLB 失败：文件未生成（%s）" % dst)
    size = os.path.getsize(dst)
    if size <= 0:
        raise RuntimeError("导出 GLB 失败：文件大小为 0（%s）" % dst)
    if size < 20:
        raise RuntimeError("导出 GLB 失败：文件内容异常（%d 字节，%s）" % (size, dst))


# ───────────────────────────────── 结果输出 ─────────────────────────────────

def emit_result(rec, jsonl_path):
    """一个任务的结果：先追加 jsonl（flush+fsync 兜底），再打印 RESULT 行"""
    line = json.dumps(rec, ensure_ascii=True)
    try:
        with open(jsonl_path, 'a', encoding='utf-8') as jf:
            jf.write(line + '\n')
            jf.flush()
            os.fsync(jf.fileno())
    except Exception as e:
        warn("写 jsonl 失败（stdout 仍可用）: %s" % e)
    print("RESULT " + line, flush=True)


# ───────────────────────────────── 主流程 ─────────────────────────────────

def parse_task_path(argv):
    """取 `--` 之后的第一个参数作为任务 JSON 路径（必须带 -- 分隔符）"""
    if "--" in argv:
        tail = argv[argv.index("--") + 1:]
    else:
        tail = []
    return tail[0] if tail else None


def main(argv):
    task_path = parse_task_path(argv)
    if not task_path:
        raise RuntimeError("缺少任务文件参数：调用方式应为 blender ... --python <本脚本> -- <task.json>")

    with open(task_path, 'r', encoding='utf-8') as f:
        task = json.load(f)

    jobs = task.get('jobs') or []
    quality = bool(task.get('quality', True))     # True=按块保形，False=只用全局比例（快）
    jsonl_path = task_path + '.jsonl'

    print("JOBS=%d QUALITY=%s BLENDER=%s" % (len(jobs), quality, version_text()), flush=True)

    t_start = time.time()
    n_ok = 0
    n_fail = 0
    for i, job in enumerate(jobs):
        src = job.get('src')
        dst = job.get('dst')
        rec = {'idx': i, 'src': src, 'dst': dst, 'ok': False}
        t0 = time.time()
        print("BEGIN %d" % i, flush=True)   # C# 据此判断崩溃/超时时是哪个任务在处理

        try:
            ratio = float(job['ratio'])
            shrink_ratio = job_value(job, 'shrinkInner', 0.0, float)
            min_faces = job_value(job, 'minFaces', DEFAULT_MIN_FACES, int)
            guard_pct = job_value(job, 'guardPct', DEFAULT_GUARD_PCT, float)
            job_quality = job.get('quality')
            if job_quality is None:
                job_quality = quality
            job_quality = bool(job_quality)
            rec['ratio'] = ratio

            if _TEST_CRASH and _TEST_CRASH in (src or ''):
                print("TEST: 模拟崩溃", flush=True)
                os.abort()

            if _TEST_SLEEP > 0 and 'sleepme' in (src or ''):
                print("TEST: 模拟卡住 %.1fs" % _TEST_SLEEP, flush=True)
                time.sleep(_TEST_SLEEP)

            if not src or not os.path.isfile(src):
                raise RuntimeError("源文件不存在：%s" % (src or '(空)'))

            bpy.ops.wm.read_factory_settings(use_empty=True)
            import_gltf(src)

            mesh_objs = scene_mesh_objects()
            if not mesh_objs:
                raise RuntimeError("导入后没有网格对象（源文件可能不是有效的 GLB）")
            before = sum(len(o.data.polygons) for o in mesh_objs)
            if before == 0:
                raise RuntimeError("导入后网格面数为 0")
            orig_names = [o.name for o in mesh_objs]

            # 内缩量按整批最大包围盒对角线算（世界坐标）
            diag = 0.0
            for ob in mesh_objs:
                dd = bbox_diagonal(ob.matrix_world, ob.data)
                if dd > diag:
                    diag = dd
            shrink_dist = diag * shrink_ratio if shrink_ratio > 0 else 0.0

            block_total = 0
            applied_total = 0
            target_total = 0
            skipped_total = 0
            for name in orig_names:
                ob = bpy.context.scene.objects.get(name)
                if ob is None or ob.type != 'MESH':
                    continue
                _b, _a, blocks, applied, tgt, skipped = decimate_object(
                    ob, ratio, min_faces, guard_pct, job_quality)
                block_total += blocks
                applied_total += applied
                target_total += tgt
                skipped_total += skipped

            if shrink_dist > 0:
                for ob in scene_mesh_objects():
                    if len(ob.data.polygons) == 0:
                        continue
                    if is_inner_wrapped(ob.name):
                        # 距离按世界对角线算，顶点坐标是局部的 → 除以世界缩放
                        shrink_along_normal(ob, shrink_dist / world_to_local_scale(ob))

            after = sum(len(o.data.polygons) for o in scene_mesh_objects())

            export_glb(dst, src)

            rec['ok'] = True
            rec['before'] = before
            rec['after'] = after
            rec['target'] = target_total
            rec['blocks'] = block_total
            rec['applied'] = applied_total
            if target_total > 0 and after > target_total * WARN_OVER:
                rec['warn'] = "实际面数 %d 超出目标 %d 达 %.0f%%（%d 个小块因差值<2 未简化）" % (
                    after, target_total, (after / float(target_total) - 1.0) * 100.0, skipped_total)
            n_ok += 1
            print("OK [%d/%d] %s  %d -> %d (目标%d)  %d块(简%d)%s%s" % (
                i + 1, len(jobs), os.path.basename(src or ''), before, after, target_total,
                block_total, applied_total,
                ("  内缩%.4f" % shrink_dist) if shrink_dist > 0 else "",
                ("  警告:" + rec['warn']) if 'warn' in rec else ""), flush=True)

        except Exception as e:
            rec['error'] = str(e) or e.__class__.__name__
            n_fail += 1
            print("FAIL [%d/%d] %s: %s" % (i + 1, len(jobs), os.path.basename(src or ''), e), flush=True)
            traceback.print_exc()

        rec['seconds'] = round(time.time() - t0, 2)
        emit_result(rec, jsonl_path)

    print("DONE ok=%d fail=%d 用时%.1fs" % (n_ok, n_fail, time.time() - t_start), flush=True)
    return 0


if __name__ == "__main__":
    main(sys.argv)
