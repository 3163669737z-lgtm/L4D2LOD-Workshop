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

算法要点：
  0) 优先路径（7.0.2+）：若环境变量 L4D2LOD_GLTFPACK 指向内置 gltfpack，先用
     `gltfpack -si <形状优先比例> -se <按LOD的误差上限> -slb -noq -kn -km` 对源 GLB 做拓扑感知减面，
     再把结果导入 Blender；只折叠原网格已有的边、不按空间位置焊接顶点，左右袜筒等互不连通部件不会
     被桥接。7.0.3 起 `-si` 按源面数自适应：高面数部件按目标减，低面数部件自动少减；`-se` 卡住形状，
     面数达不到目标也可以接受。gltfpack 不可用/失败时才回退旧路径，并打印 WARN 说明原因。
  1) 旧路径：每个任务先 read_factory_settings(use_empty=True)，再导入 glTF（merge_vertices=True，TypeError 退化）；
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
import re
import struct
import uuid
import tempfile
import subprocess
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
DEFAULT_GUARD_PCT = 35.0    # 保底/保形面数最多超出总预算的百分比
WARN_OVER = 1.5             # 实际面数超过目标 50% 以上时告警
AUTO_SMOOTH_DEG = 60.0      # 清自定义法线后的平滑角度

SMALL_PART_PCT = 0.05       # 面数 <= 全对象 5% 的连通块视为「小部件」
SMALL_PART_KEEP = 0.40      # 小部件至少保留 40% 的面（避免 60 面蕾丝 ×16% = 10 面塌成平板）
LOCK_BOUNDARY = True        # 质量模式下硬锁开放边界顶点（蕾丝/飘带/口袋沿不再塌成刀片）
LOCK_CONCAVE_DEG = 40.0     # 主部件上硬锁凹折痕（保住手指缝、衣褶，避免糊成一片）
LOCK_CONCAVE_DOMINANT = 0.5 # 只对 >= 全对象 50% 面的主部件启用凹折痕保护（避免发片/装饰过保）
LOCK_MAX_PROTECT_FRAC = 0.35  # 被锁顶点超过该比例就放弃硬锁（发片/蕾丝几乎全是边界，全锁会减不动）
LOCK_OVER_TOL = 1.30        # 硬锁后实际面数超过目标该倍数，就退回无锁版重算，保证 LOD 比例不失控

# ── 7.0.2：内置 gltfpack（meshoptimizer，MIT）拓扑感知减面 ──
GLTFPACK_ENV = 'L4D2LOD_GLTFPACK'
GLTFPACK_TIMEOUT = 1800     # 单任务上限 30 分钟；正常模型远小于这个值
GLTFPACK_SE = '0.01'        # 默认误差上限：结果偏差约 <=1% 包围盒（实测 65% 目标下约 0.5%）
GLTFPACK_TAGS = ['-slb', '-noq', '-kn', '-km', '-kv']  # 锁边界 / 关量化 / 保留节点名、材质名与 UV 等顶点属性

# ── 7.0.3：形状优先（shape-first）──
# 不再死磕 LOD 目标比例：面数多的部件按目标减，面数少的部件自动少减；
# 同时用 gltfpack 的 -se 误差上限卡住形状，面数达不到目标也可以接受。
SHAPE_FIRST = True
SHAPE_REF_FACES = 4000.0    # >= 4000 面的部件按原目标比例减；低于此值按 sqrt 曲线减轻
SHAPE_MIN_WEIGHT = 0.15     # 最小减面权重，避免几十面的小件完全不减
SHAPE_ERROR_FINE = '0.01'    # LOD1/2：误差 <= 约 1% 包围盒
SHAPE_ERROR_MID = '0.015'    # LOD3：略放宽
SHAPE_ERROR_COARSE = '0.02'  # LOD4：远景级，误差 <= 约 2% 包围盒

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
    """按「位置 + 蒙皮权重签名」焊接重合顶点，返回焊接掉的顶点数。

    旧实现是无差别 remove_doubles：只要坐标重合就焊，会把两个本来独立的部件在接缝处
    焊成一块（LOD 时一起被折叠，看起来“粘”在一起），并丢掉其中一侧的骨骼权重。
    现在只有位置相同 **且** 顶点组/权重完全一致的顶点才合并：
    - 同一张连续曲面在 UV 接缝/硬边处被拆开的点，权重一致 → 正常焊回，不撕裂缝；
    - 独立部件即便坐标恰好重合，只要骨骼权重不同 → 不焊，保留部件边界与权重。
    UV/法线是 loop 数据，焊接后仍保留，导出时按需自动拆分。

    平直着色模型（每个三角面法线不同）仍能靠权重一致焊回，假连通块问题不受影响。
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

        # 顶点签名：顶点组索引 + 权重（排序后比较，避免顺序差异）
        sig_cache = []
        for v in me.vertices:
            sig_cache.append(tuple(sorted(
                (g.group, round(float(g.weight), 4)) for g in v.groups)))

        decimals = 5
        clusters = {}
        for i, v in enumerate(me.vertices):
            key = (round(v.co.x, decimals), round(v.co.y, decimals),
                   round(v.co.z, decimals), sig_cache[i])
            clusters.setdefault(key, []).append(i)

        merged = [(c, c[0]) for c in clusters.values() if len(c) > 1]
        if not merged:
            return 0

        bm = bmesh.new()
        try:
            bm.from_mesh(me)
            bm.verts.ensure_lookup_table()
            targetmap = {}
            for cluster, keep in merged:
                for vi in cluster:
                    if vi != keep:
                        targetmap[bm.verts[vi]] = bm.verts[keep]
            before = len(bm.verts)
            try:
                bmesh.ops.weld_verts(bm, targetmap=targetmap)
            except Exception:
                warn("weld_verts 不可用，退化为全局 remove_doubles（%s）" % obj.name)
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
    """按连通块分配目标面数（保形优先）。

    全局比例会把小连通块压成平板：60 面的蕾丝 ×16% = 10 面、202 面的口袋 = 33 面，
    减完就成了插进衣服里的薄片（视觉上“粘”在主体上）。旧实现只保底 12 面，救不回来。

    分配规则：
    1) 每块先按比例得 base；
    2) 小块（<= 全对象 SMALL_PART_PCT）再按 SMALL_PART_KEEP 保形；
    3) 保底 + 保形多出来的面最多超出预算 guard_pct%（默认 35%），超出时按比例削“保形”部分；
    4) 仍超时只从大块扣，且不低于其比例目标的 60%（也不再低于 min_faces），绝不拿大块硬填。
    """
    total = sum(sizes)
    budget = max(1, int(round(total * ratio)))
    n = len(sizes)
    small_limit = total * SMALL_PART_PCT

    base = []
    hard = []
    soft = []
    for s in sizes:
        b = max(1, min(int(round(s * ratio)), s))
        base.append(b)
        # 硬保底：至少 min_faces（块本身更小则保持原样）
        h = (s - b) if s <= min_faces else max(0, min_faces - b)
        hard.append(h)
        # 软保形：小部件至少保留 SMALL_PART_KEEP
        if s <= small_limit:
            keep = min(s, int(round(s * SMALL_PART_KEEP)))
            soft.append(max(0, keep - (b + h)))
        else:
            soft.append(0)

    targets = [base[i] + hard[i] + soft[i] for i in range(n)]

    cap = budget + int(round(budget * guard_pct / 100.0))
    over = sum(targets) - cap
    if over > 0:
        total_soft = sum(soft)
        if total_soft > 0:
            f = max(0.0, 1.0 - over / float(total_soft))
            for i in range(n):
                targets[i] = base[i] + hard[i] + int(soft[i] * f)
            over = sum(targets) - cap

    if over > 0:
        floors = []
        for i, s in enumerate(sizes):
            if s > small_limit:
                floors.append(max(min(min_faces, s), int(round(base[i] * 0.6))))
            else:
                floors.append(targets[i])
        loop = 0
        while over > 0 and loop < 200000:
            loop += 1
            deducted = False
            for i in range(n):
                if targets[i] > floors[i]:
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


def build_protect_group(obj, boundary, concave_deg):
    """把「开放边界顶点 + 凹折痕顶点」写进临时顶点组，供 Decimate 硬保护。

    边界 = 只被 1 个面使用的边；凹折痕 = 二面角为负且角度超过 concave_deg 的边。
    Decimate 的顶点组 + invert=True 语义实测为「组内 weight=1 的顶点不参与折叠」。
    返回 (组名, 是否新建, 被锁顶点占比)；没有可保护顶点或已存在同名组时返回 (None, False, 0.0)。
    """
    if not boundary and concave_deg <= 0:
        return None, False, 0.0
    me = obj.data
    bm = bmesh.new()
    keep = set()
    try:
        bm.from_mesh(me)
        if boundary:
            for e in bm.edges:
                if len(e.link_faces) == 1:
                    for v in e.verts:
                        keep.add(v.index)
        if concave_deg > 0:
            limit = -math.radians(concave_deg)
            for e in bm.edges:
                if len(e.link_faces) != 2:
                    continue
                try:
                    ang = e.calc_face_angle_signed()
                except Exception:
                    continue
                if ang < limit:
                    for v in e.verts:
                        keep.add(v.index)
    finally:
        bm.free()

    if not keep:
        return None, False, 0.0
    name = "__LOD_PROTECT__"
    existing = obj.vertex_groups.get(name)
    if existing is not None:
        # 源模型自带同名组时不覆盖用户数据
        return None, False, 0.0
    vg = obj.vertex_groups.new(name=name)
    vg.add(sorted(keep), 1.0, 'REPLACE')
    nv = max(1, len(me.vertices))
    return name, True, len(keep) / float(nv)


def drop_protect_group(obj, group, created):
    if not group or not created:
        return
    try:
        vg = obj.vertex_groups.get(group)
        if vg is not None:
            obj.vertex_groups.remove(vg)
    except Exception:
        pass


# ───────────────────────────────── Decimate ─────────────────────────────────

def decimate_single(obj, tgt_faces, protect_group=None):
    """对单个对象做一个全局 Decimate(COLLAPSE, triangulate) 到目标面数"""
    cur = len(obj.data.polygons)
    if cur == 0 or tgt_faces >= cur:
        return False
    mod = obj.modifiers.new(name="LOD", type='DECIMATE')
    mod.decimate_type = 'COLLAPSE'
    mod.ratio = tgt_faces / float(cur)
    mod.use_collapse_triangulate = True
    if protect_group:
        mod.vertex_group = protect_group
        mod.invert_vertex_group = True   # 组内 weight=1 的顶点硬锁，不参与折叠
        mod.vertex_group_factor = 1.0
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


def decimate_guarded(obj, tgt_faces, boundary, concave_deg):
    """带保护组 + 超限回退的 Decimate。

    1) 按 boundary/concave 建临时保护组；被锁顶点占比 > LOCK_MAX_PROTECT_FRAC 时放弃
       （发片/蕾丝几乎全是边界顶点，全锁会完全减不动）。
    2) 锁定后实际面数仍超过目标 LOCK_OVER_TOL 倍时，回滚到备份网格并用无锁版重算，
       保证 LOD 面数比例不因保形而失控。
    """
    pg = pg_new = None
    backup = None
    try:
        pg, pg_new, frac = build_protect_group(obj, boundary, concave_deg)
        if pg and frac > LOCK_MAX_PROTECT_FRAC:
            drop_protect_group(obj, pg, pg_new)
            pg, pg_new = None, False
        if pg:
            backup = obj.data.copy()
        applied = decimate_single(obj, tgt_faces, pg)
        if pg and len(obj.data.polygons) > tgt_faces * LOCK_OVER_TOL + 4:
            old = obj.data
            obj.data = backup
            backup = None
            try:
                bpy.data.meshes.remove(old)
            except Exception:
                pass
            applied = decimate_single(obj, tgt_faces, None)
        return applied
    finally:
        if backup is not None:
            try:
                bpy.data.meshes.remove(backup)
            except Exception:
                pass
        drop_protect_group(obj, pg, pg_new)


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
        conc = LOCK_CONCAVE_DEG if (nblocks == 1 and LOCK_CONCAVE_DEG > 0) else 0.0
        decimate_guarded(obj, tgt, LOCK_BOUNDARY, conc)
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
        # 质量模式：锁开放边界；主部件再加锁凹折痕（保手指缝/衣褶，避免糊成一片）
        conc = LOCK_CONCAVE_DEG if (LOCK_CONCAVE_DEG > 0 and cur >= total * LOCK_CONCAVE_DOMINANT) else 0.0
        if decimate_guarded(part, want, LOCK_BOUNDARY, conc):
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


# ───────────────────────────────── 7.0.3：形状优先比例 ─────────────────────────────────

def shape_first_ratio(before, requested_ratio):
    """把用户设定的 LOD 比例改成「按面数自适应」的目标比例。

    面数 >= SHAPE_REF_FACES 的部件：完全按原目标减（高面数减得多）。
    面数越少，减面权重按 sqrt(before/REF) 下降（低面数减得少）。
    例如请求 0.65：
      20000 面 -> 0.650   5000 面 -> 0.650
      2784 面  -> 0.708   1000 面 -> 0.825
       500 面  -> 0.876    200 面 -> 0.922
    面数允许达不到目标；形状优先。
    """
    try:
        b = float(before)
        r = float(requested_ratio)
    except Exception:
        return requested_ratio
    if b <= 0 or r >= 0.995 or r <= 0.0:
        return r
    w = math.sqrt(b / SHAPE_REF_FACES)
    w = min(1.0, max(SHAPE_MIN_WEIGHT, w))
    eff = 1.0 - (1.0 - r) * w
    return min(1.0, max(r, eff))


def shape_error_for_ratio(requested_ratio):
    """按 LOD 级别选择误差上限：近景严、远景略宽，但最大不超过 2%。"""
    try:
        r = float(requested_ratio)
    except Exception:
        return GLTFPACK_SE
    if r <= 0.35:
        return SHAPE_ERROR_COARSE
    if r <= 0.50:
        return SHAPE_ERROR_MID
    return SHAPE_ERROR_FINE


# ───────────────────────────────── 7.0.2：gltfpack 拓扑感知减面 ─────────────────────────────────

def find_gltfpack():
    """返回 C# 释放的内置 gltfpack 路径；没有则返回 None（旧路径回退）。"""
    p = (os.environ.get(GLTFPACK_ENV) or '').strip()
    if p and os.path.isfile(p):
        return p
    return None


def count_glb_triangles(path):
    """不依赖 Blender 直接读 GLB 的三角形数，作为 before 的权威值（与 C# 统计口径一致）。"""
    try:
        with open(path, 'rb') as f:
            data = f.read()
        if len(data) < 20 or data[:4] != b'glTF':
            return -1
        pos = 12
        doc = None
        while pos + 8 <= len(data):
            length, ctype = struct.unpack_from('<II', data, pos)
            pos += 8
            if length < 0 or pos + length > len(data):
                return -1
            chunk = data[pos:pos + length]
            pos += length
            if ctype == 0x4E4F534A:      # JSON
                doc = json.loads(chunk.decode('utf-8'))
                break
        if doc is None:
            return -1
        accessors = doc.get('accessors') or []
        tris = 0
        for mesh in (doc.get('meshes') or []):
            for prim in (mesh.get('primitives') or []):
                if int(prim.get('mode', 4)) != 4:
                    continue
                idx = prim.get('indices')
                if idx is not None:
                    acc = accessors[int(idx)] if 0 <= int(idx) < len(accessors) else None
                else:
                    pos_acc = (prim.get('attributes') or {}).get('POSITION')
                    acc = accessors[int(pos_acc)] if pos_acc is not None and 0 <= int(pos_acc) < len(accessors) else None
                if acc is None:
                    continue
                tris += int(acc.get('count', 0)) // 3
        return tris
    except Exception:
        return -1


def run_gltfpack(src, dst, ratio, se=None):
    """调用内置 gltfpack 生成一个简化 GLB；返回其 stdout/stderr 文本。

    关键参数：
      -si ratio : 目标三角形比例（7.0.3 由 shape_first_ratio 按面数自适应）
      -se E     : 误差上限（7.0.3 默认 1%/1.5%/2%，按 LOD 级别），形状优先时
                  可能达不到目标面数，这是预期行为
      -slb      : 锁边界，袜口/开口不会被拉塌
      -noq      : 关闭量化，输出普通 float GLB，Blender/SMD 可直接读
      -kn -km   : 保留节点名/材质名，避免下游按名字找部件失败
    不使用 -sa（aggressive/sloppy）和 -sp（permissive）：它们会跨属性缝/近距离合并。
    """
    exe = find_gltfpack()
    if not exe:
        raise RuntimeError("未找到内置 gltfpack（环境变量 %s 为空或路径不存在）" % GLTFPACK_ENV)
    ratio_text = ('%.6f' % float(ratio)).rstrip('0').rstrip('.')
    se_text = str(se if se is not None else GLTFPACK_SE)
    cmd = [exe, '-i', src, '-o', dst, '-si', ratio_text, '-se', se_text] + list(GLTFPACK_TAGS) + ['-v']
    creationflags = 0x08000000 if os.name == 'nt' else 0   # CREATE_NO_WINDOW
    try:
        proc = subprocess.run(
            cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=GLTFPACK_TIMEOUT, creationflags=creationflags,
            text=True, encoding='utf-8', errors='replace')
    except subprocess.TimeoutExpired:
        raise RuntimeError("gltfpack 超时（%d 秒）" % GLTFPACK_TIMEOUT)
    out = (proc.stdout or '') + (proc.stderr or '')
    if proc.returncode != 0:
        tail = out.strip()[-800:]
        raise RuntimeError("gltfpack 失败（exit=%d）：%s" % (proc.returncode, tail or '(无输出)'))
    if not os.path.isfile(dst):
        raise RuntimeError("gltfpack 未生成输出文件：%s" % dst)
    if os.path.getsize(dst) <= 20:
        raise RuntimeError("gltfpack 输出文件异常（%d 字节）：%s" % (os.path.getsize(dst), dst))
    return out


def gltfpack_temp_path():
    d = os.path.join(tempfile.gettempdir(), 'L4D2LOD远景工坊', 'gltfpack')
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, 'g_' + uuid.uuid4().hex + '.glb')


# ───────────────────────────────── 导入 / 导出 ─────────────────────────────────

def import_gltf(src, merge_vertices=True):
    """导入 glTF/GLB。

    merge_vertices=True：旧减面路径需要合并接缝顶点后再按连通块处理；
    gltfpack 路径必须传 False：避免 Blender 导入阶段把左右袜筒在重合处再次焊死。
    老版本没有该参数 → TypeError 退化（默认不合并，正好符合 gltfpack 路径）。
    """
    try:
        bpy.ops.import_scene.gltf(filepath=src, merge_vertices=merge_vertices)
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
        tmp_glb = None

        try:
            requested_ratio = float(job['ratio'])
            shrink_ratio = job_value(job, 'shrinkInner', 0.0, float)
            min_faces = job_value(job, 'minFaces', DEFAULT_MIN_FACES, int)
            guard_pct = job_value(job, 'guardPct', DEFAULT_GUARD_PCT, float)
            job_quality = job.get('quality')
            if job_quality is None:
                job_quality = quality
            job_quality = bool(job_quality)
            rec['ratio_requested'] = requested_ratio

            if _TEST_CRASH and _TEST_CRASH in (src or ''):
                print("TEST: 模拟崩溃", flush=True)
                os.abort()

            if _TEST_SLEEP > 0 and 'sleepme' in (src or ''):
                print("TEST: 模拟卡住 %.1fs" % _TEST_SLEEP, flush=True)
                time.sleep(_TEST_SLEEP)

            if not src or not os.path.isfile(src):
                raise RuntimeError("源文件不存在：%s" % (src or '(空)'))

            # 7.0.3 形状优先：先按源面数决定实际减面目标。
            # 高面数部件按用户目标减；低面数部件自动放宽比例，宁可不达标也不硬减。
            before_hint = count_glb_triangles(src)
            if SHAPE_FIRST:
                ratio = shape_first_ratio(before_hint, requested_ratio)
            else:
                ratio = requested_ratio
            se_cap = shape_error_for_ratio(requested_ratio)
            rec['ratio_eff'] = ratio
            rec['shape_error'] = float(se_cap)
            rec['shape_first'] = bool(SHAPE_FIRST)

            # 优先走内置 gltfpack：拓扑感知减面，不做空间焊接。
            # 只有 gltfpack 缺失/执行失败才回退旧路径，并明确打印 WARN，避免静默回到旧算法。
            used_gltfpack = False
            gp_stats = ''
            gp = find_gltfpack()
            if gp:
                try:
                    tmp_glb = gltfpack_temp_path()
                    gp_stats = run_gltfpack(src, tmp_glb, ratio, se_cap)
                    bpy.ops.wm.read_factory_settings(use_empty=True)
                    import_gltf(tmp_glb, merge_vertices=False)
                    used_gltfpack = True
                    last = [ln for ln in gp_stats.splitlines() if ln.strip().startswith('output:')]
                    print("GLTFPACK req=%.4f eff=%.4f se=%s %s" % (
                        requested_ratio, ratio, se_cap,
                        (last[-1].strip() if last else 'ok')), flush=True)
                    try:
                        os.remove(tmp_glb)
                        tmp_glb = None
                    except Exception:
                        pass
                except Exception as e:
                    warn("gltfpack 失败，回退旧减面（%s）" % e)
                    used_gltfpack = False
                    if tmp_glb:
                        try:
                            os.remove(tmp_glb)
                        except Exception:
                            pass
                        tmp_glb = None
                    bpy.ops.wm.read_factory_settings(use_empty=True)
                    import_gltf(src)
            else:
                bpy.ops.wm.read_factory_settings(use_empty=True)
                import_gltf(src)

            mesh_objs = scene_mesh_objects()
            if not mesh_objs:
                raise RuntimeError("导入后没有网格对象（源文件可能不是有效的 GLB）")
            if used_gltfpack:
                before = count_glb_triangles(src)
                if before <= 0:
                    before = sum(len(o.data.polygons) for o in mesh_objs)
            else:
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
            if used_gltfpack:
                # gltfpack 已在源 GLB 上完成减面；这里只统计连通块供 UI/日志显示。
                target_total = int(round(before * ratio))
                for ob in mesh_objs:
                    sizes = analyze_blocks(ob)
                    block_total += len(sizes)
                    applied_total += sum(1 for s in sizes if s > min_faces)
            else:
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
            rec['method'] = 'gltfpack' if used_gltfpack else 'blender'
            rec['before'] = before
            rec['after'] = after
            rec['target'] = target_total
            rec['blocks'] = block_total
            rec['applied'] = applied_total
            if used_gltfpack and ratio < 0.99 and after >= before:
                rec['warn'] = "gltfpack 未减面：模型可能为硬边/分离面（每面独立顶点），已保持原面数且未使用旧焊接算法"
            if target_total > 0 and after > target_total * WARN_OVER:
                if used_gltfpack:
                    rec['warn'] = "实际面数 %d 超出目标 %d 达 %.0f%%（gltfpack 受边界/拓扑约束提前停止）" % (
                        after, target_total, (after / float(target_total) - 1.0) * 100.0)
                else:
                    rec['warn'] = "实际面数 %d 超出目标 %d 达 %.0f%%（%d 个小块因差值<2 未简化）" % (
                        after, target_total, (after / float(target_total) - 1.0) * 100.0, skipped_total)
            n_ok += 1
            print("OK [%d/%d] %s  %d -> %d (目标%d, 形状比%.3f/se%s)  %d块(简%d)%s%s" % (
                i + 1, len(jobs), os.path.basename(src or ''), before, after, target_total,
                ratio, se_cap, block_total, applied_total,
                ("  内缩%.4f" % shrink_dist) if shrink_dist > 0 else "",
                ("  警告:" + rec['warn']) if 'warn' in rec else ""), flush=True)

        except Exception as e:
            if tmp_glb:
                try:
                    os.remove(tmp_glb)
                except Exception:
                    pass
                tmp_glb = None
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
