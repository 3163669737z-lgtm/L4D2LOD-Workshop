#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
远景工坊 · E2E 测试数据生成器
==============================
用真实 Blender 生成 GLB 测试模型，并造出若干"像真实工程一样"的合成项目：
  proj_main     include 链 + $model(带 flexfile) + $bodygroup/studio + GBK QC + 低于门槛模型
  proj_loose    只有散落 glb（测试"没有 QC 时自动建包装 QC"）
  proj_smd      SMD 模型工程（SMD→GLB→减面→SMD，权重/骨骼保真）
  proj_hand     已手写 $lod（测试跳过 / --force 注释停用）
  proj_broken   工具标记块缺结束标记（测试拒绝修改）
  proj_empty    空文件夹
  proj_paths    中文/空格/超长路径的工程副本

用法：
  python gen_fixtures.py --blender "D:\\blender 4.1\\blender.exe" --out <dir> [--force]

只读约束：只写 <out> 目录；不碰用户任何真实工程。
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import textwrap

BLENDER_SCRIPT = r'''
import bpy, bmesh, math, os, sys

OUT = sys.argv[sys.argv.index("--") + 1]

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)

def uv_unwrap(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    try:
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.02)
        bpy.ops.object.mode_set(mode='OBJECT')
    except Exception as e:
        print("UV warn:", e)

def add_subdiv_cube(name, size, cuts, mat_color, inner=None):
    reset()
    bpy.ops.mesh.primitive_cube_add(size=size)
    obj = bpy.context.active_object
    obj.name = name
    if cuts > 0:
        bm = bmesh.new(); bm.from_mesh(obj.data)
        bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=cuts, use_grid_fill=True)
        bm.to_mesh(obj.data); bm.free()
    mat = bpy.data.materials.new("Mat_" + name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf: bsdf.inputs["Base Color"].default_value = mat_color
    obj.data.materials.append(mat)
    uv_unwrap(obj)
    if inner:
        bpy.ops.mesh.primitive_uv_sphere_add(segments=20, ring_count=10, radius=size * 0.42)
        inner_obj = bpy.context.active_object
        inner_obj.name = inner
        imat = bpy.data.materials.new("Mat_inner")
        imat.use_nodes = True
        inner_obj.data.materials.append(imat)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "_tmp.blend"))
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, name + ".glb"), export_format='GLB', use_selection=False)

def add_skinned(name, segments, rings):
    reset()
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings, radius=18)
    obj = bpy.context.active_object
    obj.name = name
    mat = bpy.data.materials.new("Mat_skin"); mat.use_nodes = True
    obj.data.materials.append(mat)
    uv_unwrap(obj)
    # 骨架：两根骨头
    bpy.ops.object.armature_add(enter_editmode=True, location=(0,0,0))
    arm = bpy.context.active_object
    arm.name = "Rig"
    eb = arm.data.edit_bones[0]
    eb.name = "root"
    eb.head = (0,0,-30); eb.tail = (0,0,0)
    b2 = arm.data.edit_bones.new("spine")
    b2.head = (0,0,0); b2.tail = (0,0,30)
    b2.parent = eb
    bpy.ops.object.mode_set(mode='OBJECT')
    obj.parent = arm
    mod = obj.modifiers.new("Armature", 'ARMATURE'); mod.object = arm
    vg_root = obj.vertex_groups.new(name="root")
    vg_spine = obj.vertex_groups.new(name="spine")
    for v in obj.data.vertices:
        w = 1.0 if v.co.z < 0 else 0.0
        vg_root.add([v.index], w, 'REPLACE')
        vg_spine.add([v.index], 1.0 - w, 'REPLACE')
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True); arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, name + ".glb"), export_format='GLB',
                              use_selection=True, export_skins=True)

try:
    # 1) 大箱子（多材质 + UV + 大量面）
    add_subdiv_cube("crate", 40.0, 9, (0.55, 0.35, 0.2, 1.0))
    # 2) 角色体（外包一层 + 内层 Body_inner，测试内缩/多对象）
    add_subdiv_cube("char_body", 36.0, 8, (0.8, 0.7, 0.6, 1.0), inner="Body_inner")
    # 3) 小于面数门槛的小件
    add_subdiv_cube("crate_small", 10.0, 1, (0.3, 0.5, 0.7, 1.0))
    # 4) 带骨骼/权重的模型（Blender 侧健壮性）
    add_skinned("armored", 32, 16)
    print("GLB_OK")
except Exception as e:
    import traceback; traceback.print_exc()
    print("GLB_FAIL", e)
    sys.exit(1)
'''


def run_blender(blender, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    script = os.path.join(out_dir, "_create_models.py")
    with open(script, "w", encoding="utf-8") as f:
        f.write(BLENDER_SCRIPT)
    cmd = [blender, "--background", "--factory-startup", "--python-exit-code", "2",
           "--python", script, "--", out_dir]
    print("[gen] running blender:", " ".join(cmd))
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
    out = p.stdout.decode("utf-8", "replace")
    tail = "\n".join(out.splitlines()[-15:])
    print(tail)
    if p.returncode != 0 or "GLB_OK" not in out:
        raise SystemExit("Blender 生成模型失败，退出码 %s" % p.returncode)
    for name in ("crate", "char_body", "crate_small", "armored"):
        path = os.path.join(out_dir, name + ".glb")
        if not os.path.isfile(path) or os.path.getsize(path) < 200:
            raise SystemExit("缺少模型或文件过小: " + path)
    for tmp in os.listdir(out_dir):
        if tmp.startswith("_tmp."):
            try:
                os.remove(os.path.join(out_dir, tmp))
            except OSError:
                pass


def write(path, text, encoding="utf-8", bom=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    data = text.replace("\r\n", "\n").replace("\n", "\r\n")
    raw = data.encode(encoding)
    if bom and encoding == "utf-8":
        raw = b"\xef\xbb\xbf" + raw
    with open(path, "wb") as f:
        f.write(raw)


def write_smd(path, tris):
    """生成带两个骨骼和混合权重的 SMD。"""
    lines = ["version 1", "nodes", '0 "root" -1', '1 "spine" 0', "end", "skeleton", "time 0",
             "0 0 0 0 0 0 0", "1 0 10 0 0 0 0", "end", "triangles"]
    lines.extend(tris)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("\n".join(lines) + "\n")


def plane_triangles(nx, ny):
    """生成带线性 UV 和均匀权重的平面，内部网格可以无损简化。"""
    out = []

    def tri(*points):
        out.append("body")
        for i, j in points:
            out.append(f"0 {i} {j} 0 0 0 1 {i / nx:.6f} {j / ny:.6f} 2 0 0.65 1 0.35")

    for i in range(nx):
        for j in range(ny):
            a, b, c, d = (i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)
            tri(a, b, c)
            tri(a, c, d)
    return out

def build_projects(out):
    glb = os.path.join(out, "models")
    proj = os.path.join(out, "projects")

    # ── proj_main：include 链 + $model(flexfile) + $bodygroup/studio + GBK QC ──
    main = os.path.join(proj, "proj_main")
    os.makedirs(os.path.join(main, "qc"), exist_ok=True)
    os.makedirs(os.path.join(main, "models", "SMD"), exist_ok=True)
    for name in ("crate", "char_body", "crate_small", "armored"):
        shutil.copy2(os.path.join(glb, name + ".glb"), os.path.join(main, "models", "SMD", name + ".glb"))
    write(os.path.join(main, "qc", "actor_main.qc"), textwrap.dedent('''\
        // 合成工程：主模型 + 衣服 + 道具（include 链测试）
        $modelname "models/actor.mdl"
        $cd "..\\models"

        $model "actor_body" "SMD/char_body.glb"
        {
            flexfile "SMD/flex.vta"
            flexcontroller eyes "close" 0 1
        }

        $bodygroup "clothes"
        {
            studio "SMD/armored"
        }

        $bodygroup "props"
        {
            studio "SMD/crate.glb"
        }
        '''))
    write(os.path.join(main, "qc", "actor.qc"), textwrap.dedent('''\
        // 根 QC：只 include，不直接写 studio（真实工程常见布局）
        $include "actor_main.qc"
        $surfaceprop "flesh"
        '''))
    write(os.path.join(main, "qc", "weapon.qc"), textwrap.dedent('''\
        $modelname "models/weapon.mdl"
        $cd "..\\models"

        $bodygroup "gun"
        {
            studio "SMD/crate"
        }
        $sequence "idle" { "SMD/crate.glb" fps 30 }
        '''))
    # GBK 编码 QC（测试编码/换行保留）
    write(os.path.join(main, "qc", "gbk_model.qc"), textwrap.dedent('''\
        // 中文注释：GBK 编码的 QC，写回后不应乱码
        $modelname "models/中文模型.mdl"
        $cd "..\\models"
        $bodygroup "箱子"
        {
            studio "SMD/crate.glb"
        }
        '''), encoding="gbk")
    write(os.path.join(main, "批量编译.bat"), textwrap.dedent('''\
        @echo off
        rem 合成工程：批量编译（测试 .bat 解析）
        "%~dp0..\\nekomdl.exe" "%~dp0qc\\actor.qc"
        "%~dp0..\\nekomdl.exe" "%~dp0qc\\weapon.qc"
        "%~dp0..\\nekomdl.exe" "%~dp0qc\\gbk_model.qc"
        '''))
    # 低于门槛的小件（放在 SMD 目录但不被 QC 引用）
    shutil.copy2(os.path.join(glb, "crate_small.glb"), os.path.join(main, "models", "SMD", "crate_small.glb"))

    # ── proj_loose：只有散落模型（自动包装 QC）──
    loose = os.path.join(proj, "proj_loose")
    os.makedirs(os.path.join(loose, "props"), exist_ok=True)
    shutil.copy2(os.path.join(glb, "crate.glb"), os.path.join(loose, "props", "crate.glb"))
    shutil.copy2(os.path.join(glb, "crate_small.glb"), os.path.join(loose, "props", "crate_small.glb"))

    # ── proj_smd：SMD 工程 ──
    smd = os.path.join(proj, "proj_smd")
    os.makedirs(os.path.join(smd, "smd"), exist_ok=True)
    tris = plane_triangles(16, 16)
    write_smd(os.path.join(smd, "smd", "char_src.smd"), tris)
    write(os.path.join(smd, "smd", "char.qc"), textwrap.dedent('''\
        $modelname "models/smd_char.mdl"
        $cd "."
        $bodygroup "mesh"
        {
            studio "char_src.smd"
        }
        '''))
    write(os.path.join(smd, "批量编译.bat"), '@echo off\r\n"%~dp0..\\..\\nekomdl.exe" "%~dp0smd\\char.qc"\r\n')

    # ── proj_hand：手写 $lod ──
    hand = os.path.join(proj, "proj_hand")
    os.makedirs(hand, exist_ok=True)
    shutil.copy2(os.path.join(glb, "crate.glb"), os.path.join(hand, "hand_body.glb"))
    write(os.path.join(hand, "hand.qc"), textwrap.dedent('''\
        // 已有玩家手写的 $lod（测试默认跳过；--force 时注释停用）
        $modelname "models/hand.mdl"
        $bodygroup "mesh"
        {
            studio "hand_body.glb"
        }

        $lod 40
        {
            replacemodel "hand_body.glb" "hand_body_lod1.glb"
        }
        '''))
    write(os.path.join(hand, "批量编译.bat"), '@echo off\r\nnekomdl.exe "%~dp0hand.qc"\r\n')

    # ── proj_broken：工具块缺结束标记 ──
    broken = os.path.join(proj, "proj_broken")
    os.makedirs(broken, exist_ok=True)
    shutil.copy2(os.path.join(glb, "crate.glb"), os.path.join(broken, "broken.glb"))
    write(os.path.join(broken, "broken.qc"), textwrap.dedent('''\
        $modelname "models/broken.mdl"
        $bodygroup "mesh"
        {
            studio "broken.glb"
        }

        //>>> L4D2 自动 LOD 工具 生成块 开始 >>>
        $lod 30
        {
            replacemodel "broken.glb" "broken_lod1.glb"
        }
        ''') + "\n")
    write(os.path.join(broken, "批量编译.bat"), '@echo off\r\nnekomdl.exe "%~dp0broken.qc"\r\n')

    # ── proj_empty ──
    os.makedirs(os.path.join(proj, "proj_empty"), exist_ok=True)

    # ── proj_paths：中文/空格/超长路径副本 ──
    long_name = "工程 含空格_0123456789_0123456789_0123456789_0123456789_0123456789"
    paths = os.path.join(proj, "proj_paths", long_name)
    os.makedirs(paths, exist_ok=True)
    shutil.copy2(os.path.join(glb, "crate.glb"), os.path.join(paths, "crate.glb"))
    write(os.path.join(paths, "中文工程.qc"), textwrap.dedent('''\
        $modelname "models/paths.mdl"
        $bodygroup "mesh"
        {
            studio "crate.glb"
        }
        '''))
    write(os.path.join(paths, "批量编译.bat"), '@echo off\r\nnekomdl.exe "%~dp0中文工程.qc"\r\n')

    # 元信息（e2e 脚本读取）
    meta = {
        "glbDir": glb,
        "projects": {
            "main": main,
            "loose": loose,
            "smd": smd,
            "hand": hand,
            "broken": broken,
            "empty": os.path.join(proj, "proj_empty"),
            "paths": paths,
        },
        "expected": {
            "mainRootQcs": 3,
            "mainModels": 3,
            "smdTris": len(tris) // 4,
        },
    }
    with open(os.path.join(out, "fixtures.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)
    return meta


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--blender", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()

    out = os.path.abspath(args.out)
    if os.path.isdir(out) and args.force:
        shutil.rmtree(out)
    os.makedirs(out, exist_ok=True)
    if not os.path.isfile(args.blender):
        raise SystemExit("Blender 不存在: " + args.blender)

    run_blender(args.blender, os.path.join(out, "models"))
    meta = build_projects(out)
    print(json.dumps(meta, ensure_ascii=False, indent=2))
    print("FIXTURES_OK")


if __name__ == "__main__":
    main()
