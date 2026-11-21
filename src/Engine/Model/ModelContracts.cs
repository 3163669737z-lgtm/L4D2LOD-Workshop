using System;
using System.Collections.Generic;

namespace LodStudio.Engine
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SMD / GLB 数据模型与编解码契约。
    //  实现负责人：模型编解码模块。契约不可变（Pipeline 依赖这些成员）。
    // ══════════════════════════════════════════════════════════════════════════

    public sealed class SmdBone
    {
        public int Index;
        public string Name;
        public int Parent = -1;
        public override string ToString() { return Index + ":" + Name; }
    }

    public sealed class SmdWeight
    {
        public int BoneIndex;
        public float Weight;
    }

    /// <summary>SMD 三角形里的一个顶点：位置/法线/UV + 骨骼权重（0~N 个）。</summary>
    public sealed class SmdVertex
    {
        /// <summary>
        /// 顶点行第一个字段（主骨骼 SMD 索引）。旧参考网格里无 links 的顶点靠它绑定骨骼；
        /// 写出时若 <see cref="Weights"/> 非空则用第一个权重的骨骼，否则用本字段。
        /// </summary>
        public int Bone;
        public float X, Y, Z;
        public float Nx, Ny, Nz;
        public float U, V;
        public List<SmdWeight> Weights = new List<SmdWeight>();
    }

    public sealed class SmdTriangle
    {
        public string Material = "";
        public SmdVertex V0, V1, V2;
    }

    public sealed class SmdModel
    {
        public string SourcePath;
        public List<SmdBone> Bones = new List<SmdBone>();
        public List<SmdTriangle> Triangles = new List<SmdTriangle>();

        /// <summary>
        /// 解析/转换过程中发现、但不致命的问题（可读中文，按发现顺序）。
        /// 例：坏行已跳过、骨骼重名、权重超过 4 个被截断、骨骼名无法映射回原表。
        /// </summary>
        public List<string> Warnings = new List<string>();

        /// <summary>
        /// 源 SMD skeleton 段的姿态行（原样透传，格式“x y z rx ry rz”）；为空表示只有 time 0。
        /// 只用于回写 SMD 时保留原始姿态，不参与减面。
        /// </summary>
        public List<string> SkeletonPose = new List<string>();

        public int TriangleCount { get { return Triangles.Count; } }

        /// <summary>是否有任何顶点带骨骼权重（全绑 bone 0 的蒙皮也算；线性扫描 triangles）。</summary>
        public bool HasSkinWeights
        {
            get
            {
                if (Triangles == null) return false;
                foreach (var t in Triangles)
                {
                    if (t == null) continue;
                    if (HasWeight(t.V0) || HasWeight(t.V1) || HasWeight(t.V2)) return true;
                }
                return false;
            }
        }

        private static bool HasWeight(SmdVertex v)
        {
            return v != null && v.Weights != null && v.Weights.Count > 0;
        }
    }

    // ── GLB 数据模型（最小必要：网格 + 骨骼 + skin） ──

    public sealed class GlbNode
    {
        public string Name = "";
        public int? Mesh;
        public int? Skin;
        public int[] Children = new int[0];
        public float[] Matrix;          // 4x4 列主序；null 表示用 TRS
        // TRS（Matrix 非 null 时按规范以 Matrix 为准；这里保留原值便于诊断/后续使用）
        public float[] Translation;     // xyz
        public float[] Rotation;        // xyzw
        public float[] Scale;           // xyz
    }

    public sealed class GlbSkin
    {
        public string Name = "";
        public int[] Joints = new int[0];
        /// <summary>4x4 列主序矩阵（每 joint 16 个 float）；可能为 null（无 IBM）</summary>
        public float[] InverseBindMatrices;
    }

    /// <summary>一个 primitive 的展开顶点属性（数组长度 = 顶点数；Joints/Weights 为 4 分量）。</summary>
    public sealed class GlbPrimitive
    {
        public string MaterialName = "";
        public float[] Positions;   // xyz
        public float[] Normals;     // xyz（可能 null）
        public float[] Uvs;         // uv
        public int[] Indices;       // 三角形索引
        public ushort[] Joints;     // 每顶点 4 个（可能 null）
        public float[] Weights;     // 每顶点 4 个（可能 null）
        public int VertexCount { get { return Positions == null ? 0 : Positions.Length / 3; } }
        public int TriangleCount { get { return Indices == null ? 0 : Indices.Length / 3; } }
    }

    public sealed class GlbMesh
    {
        public string Name = "";
        public List<GlbPrimitive> Primitives = new List<GlbPrimitive>();
    }

    public sealed class GlbModel
    {
        public string SourcePath;
        public List<GlbMesh> Meshes = new List<GlbMesh>();
        public List<GlbNode> Nodes = new List<GlbNode>();
        public List<GlbSkin> Skins = new List<GlbSkin>();
        public List<string> Warnings = new List<string>();

        public int TriangleCount
        {
            get
            {
                int n = 0;
                foreach (var m in Meshes) foreach (var p in m.Primitives) n += p.TriangleCount;
                return n;
            }
        }
    }

    // 编解码器实现位于同目录：
    //   SmdCodec.cs      —— SMD 文本读写
    //   GlbJson.cs       —— GLB 容器 / accessor / 二进制写入内部辅助
    //   GlbCodec.cs      —— GLB 读写、面数统计、SMD→GLB 写出
    //   SmdGlbCodec.cs   —— SMD↔GLB 转换与权重还原
    //   SelfTestModel.cs —— 本模块自测
}
