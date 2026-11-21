using System;
using System.Collections.Generic;
using System.IO;

namespace LodStudio.Engine
{
    /// <summary>
    /// SMD ↔ GLB 转换：
    ///  - <see cref="SmdToGlb"/>：给 Blender 导入用的临时 GLB（实现即 <see cref="GlbCodec.WriteFromSmd"/>）；
    ///  - <see cref="GlbToSmd"/>：把 Blender 减面后输出的 GLB 转回 SMD，用原始 SMD 的骨骼表还原骨骼索引与权重。
    ///
    /// 权重还原链（与旧版一致，但把静默失败变成可计数警告）：
    ///   JOINTS_0 值 → skin.joints[值] → node 下标 → node 名 → 原始 SMD 同名骨骼的 Index。
    /// 名字比较大小写不敏感；找不到时回落骨骼 0 并在 <see cref="SmdModel.Warnings"/> 里给出数量；
    /// 另外兼容 Blender 对重名骨骼追加的 “.001/.002” 后缀（去掉后缀再匹配一次）。
    /// 几何/UV 直接取 GLB 属性（Blender 导出的 GLB 与导入时同一坐标空间，不做任何轴系/缩放换算）。
    /// </summary>
    public static class SmdGlbCodec
    {
        /// <summary>每顶点最大影响数（glTF JOINTS_0/WEIGHTS_0 为 VEC4；SMD 侧按 SMD 允许的最大影响数截断）。</summary>
        internal const int MaxInfluences = 4;

        /// <summary>SMD → GLB 字节（单 mesh、按材质分 primitive、骨骼节点树 + skin + 权重）。</summary>
        public static byte[] SmdToGlb(SmdModel model)
        {
            if (model == null) throw new ArgumentNullException("model");
            return GlbCodec.WriteFromSmd(model);
        }

        /// <summary>
        /// GLB（Blender 减面后的结果）→ SMD。original 提供“骨骼名 → 骨骼 Index/父子关系”的还原依据，
        /// 输出 SMD 的 nodes 段完全来自 original（与 Blender 输出无关）；original 为空时骨骼表为空。
        /// </summary>
        public static SmdModel GlbToSmd(GlbModel glb, SmdModel original)
        {
            if (glb == null) throw new ArgumentNullException("glb");

            var result = new SmdModel { SourcePath = glb.SourcePath ?? "" };
            if (original != null)
            {
                if (original.Bones != null)
                {
                    foreach (var bone in original.Bones)
                    {
                        if (bone == null) continue;
                        result.Bones.Add(new SmdBone { Index = bone.Index, Name = bone.Name ?? "", Parent = bone.Parent });
                    }
                }
                if (original.SkeletonPose != null) result.SkeletonPose.AddRange(original.SkeletonPose);
            }
            else
            {
                AddWarning(result, "没有提供原始 SMD 骨骼表，无法还原骨骼索引（所有权重影响将回落到骨骼 0）");
            }

            if (glb.Warnings != null)
            {
                foreach (var warning in glb.Warnings) AddWarning(result, "[GLB] " + warning);
            }

            // 骨骼名（大小写不敏感）→ 原始 SMD bone.Index
            var nameToBone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var bone in result.Bones)
            {
                if (!nameToBone.ContainsKey(bone.Name)) nameToBone[bone.Name] = bone.Index;
            }

            string[] nodeNames = new string[glb.Nodes == null ? 0 : glb.Nodes.Count];
            for (int i = 0; i < nodeNames.Length; i++)
            {
                var node = glb.Nodes[i];
                nodeNames[i] = node == null ? "" : (node.Name ?? "");
            }

            var stats = new Stats();
            int ambiguousSkinMeshes = 0;
            if (glb.Meshes != null)
            {
                for (int meshIndex = 0; meshIndex < glb.Meshes.Count; meshIndex++)
                {
                    var mesh = glb.Meshes[meshIndex];
                    if (mesh == null || mesh.Primitives == null) continue;

                    bool ambiguous;
                    GlbSkin skin = ResolveSkin(glb, meshIndex, out ambiguous);
                    if (ambiguous) ambiguousSkinMeshes++;
                    bool skinUsable = skin != null && skin.Joints != null && skin.Joints.Length > 0;

                    foreach (var primitive in mesh.Primitives)
                    {
                        if (primitive == null || primitive.Positions == null || primitive.Positions.Length < 3)
                        {
                            AddWarning(result, "GLB 有 primitive 缺少 POSITION，已跳过");
                            continue;
                        }
                        int vertexCount = primitive.VertexCount;
                        var indices = primitive.Indices;
                        if (indices == null)
                        {
                            indices = new int[vertexCount];
                            for (int i = 0; i < vertexCount; i++) indices[i] = i;
                        }

                        bool primitiveSkinned = skinUsable && primitive.Joints != null && primitive.Weights != null &&
                                                primitive.Joints.Length >= vertexCount * MaxInfluences &&
                                                primitive.Weights.Length >= vertexCount * MaxInfluences;
                        if (skinUsable && !primitiveSkinned && (primitive.Joints != null || primitive.Weights != null))
                            stats.IncompleteWeightPrimitives++;
                        if (!skinUsable && primitive.Joints != null && primitive.Weights != null)
                            stats.NoSkinPrimitives++;

                        string material = string.IsNullOrEmpty(primitive.MaterialName) ? "material0" : primitive.MaterialName;
                        if (string.IsNullOrEmpty(primitive.MaterialName)) stats.MissingMaterialPrimitives++;

                        for (int t = 0; t + 2 < indices.Length; t += 3)
                        {
                            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                            if (i0 < 0 || i0 >= vertexCount || i1 < 0 || i1 >= vertexCount || i2 < 0 || i2 >= vertexCount)
                                throw new InvalidDataException("GLB 索引越界，无法转换为 SMD（" + glb.SourcePath + "）");
                            result.Triangles.Add(new SmdTriangle
                            {
                                Material = material,
                                V0 = BuildVertex(primitive, i0, primitiveSkinned, skin, nodeNames, nameToBone, stats),
                                V1 = BuildVertex(primitive, i1, primitiveSkinned, skin, nodeNames, nameToBone, stats),
                                V2 = BuildVertex(primitive, i2, primitiveSkinned, skin, nodeNames, nameToBone, stats)
                            });
                        }
                    }
                }
            }

            if (result.Triangles.Count == 0) AddWarning(result, "GLB 里没有可转换的三角形，输出 SMD 为空网格");
            if (ambiguousSkinMeshes > 0)
                AddWarning(result, ambiguousSkinMeshes + " 个 mesh 无法确定使用哪个 skin（有多个 skin 且没有节点关联），已按静态网格处理");
            if (stats.NoSkinPrimitives > 0)
                AddWarning(result, stats.NoSkinPrimitives + " 个 primitive 带 JOINTS_0/WEIGHTS_0 但没有可用的 skin，权重已丢失（按静态网格处理）");
            if (stats.IncompleteWeightPrimitives > 0)
                AddWarning(result, stats.IncompleteWeightPrimitives + " 个 primitive 的 JOINTS_0/WEIGHTS_0 数据不完整，权重已丢失");
            if (stats.UnmappedNames > 0)
                AddWarning(result, stats.UnmappedNames + " 个权重影响的骨骼名在原始 SMD 骨骼表中找不到，已回落到骨骼 0" +
                                  BadNamesSuffix(stats));
            if (stats.UnmappedJoints > 0)
                AddWarning(result, stats.UnmappedJoints + " 个权重影响的 joint 索引越界（skin.joints / nodes），已忽略");
            if (stats.SuffixResolved > 0)
                AddWarning(result, stats.SuffixResolved + " 个权重影响通过去除 Blender 后缀（如 \".001\"）匹配到原始骨骼");
            if (stats.TruncatedVertices > 0)
                AddWarning(result, stats.TruncatedVertices + " 个顶点的权重影响超过 " + MaxInfluences +
                                  " 个，已按权重降序截断并重新归一化");
            if (stats.WeightlessVertices > 0)
                AddWarning(result, stats.WeightlessVertices + " 个顶点没有任何有效权重，已按无骨骼顶点写出");
            if (stats.MissingMaterialPrimitives > 0)
                AddWarning(result, stats.MissingMaterialPrimitives + " 个 primitive 没有材质名，已使用 \"material0\"");

            return result;
        }

        // ──────────────────────────────────────────────────────────────────
        //  内部实现
        // ──────────────────────────────────────────────────────────────────

        private sealed class Stats
        {
            internal int UnmappedNames;
            internal int UnmappedJoints;
            internal int SuffixResolved;
            internal int WeightlessVertices;
            internal int TruncatedVertices;
            internal int NoSkinPrimitives;
            internal int IncompleteWeightPrimitives;
            internal int MissingMaterialPrimitives;
            internal readonly List<string> BadNames = new List<string>();
        }

        /// <summary>按 node.skin 选 skin；没有节点引用该 mesh 时，只有 1 个 skin 才敢用，否则返回 null 并标记歧义。</summary>
        private static GlbSkin ResolveSkin(GlbModel glb, int meshIndex, out bool ambiguous)
        {
            ambiguous = false;
            if (glb.Skins == null || glb.Skins.Count == 0) return null;
            if (glb.Nodes != null)
            {
                foreach (var node in glb.Nodes)
                {
                    if (node == null || !node.Mesh.HasValue || node.Mesh.Value != meshIndex || !node.Skin.HasValue) continue;
                    int skinIndex = node.Skin.Value;
                    if (skinIndex >= 0 && skinIndex < glb.Skins.Count) return glb.Skins[skinIndex];
                }
            }
            if (glb.Skins.Count == 1) return glb.Skins[0];
            ambiguous = true;
            return null;
        }

        private static SmdVertex BuildVertex(GlbPrimitive primitive, int vertexIndex, bool skinned, GlbSkin skin,
                                             string[] nodeNames, Dictionary<string, int> nameToBone, Stats stats)
        {
            int p = vertexIndex * 3;
            var vertex = new SmdVertex
            {
                X = primitive.Positions[p],
                Y = primitive.Positions[p + 1],
                Z = primitive.Positions[p + 2],
                Nx = primitive.Normals != null && primitive.Normals.Length > p + 2 ? primitive.Normals[p] : 0f,
                Ny = primitive.Normals != null && primitive.Normals.Length > p + 2 ? primitive.Normals[p + 1] : 0f,
                Nz = primitive.Normals != null && primitive.Normals.Length > p + 2 ? primitive.Normals[p + 2] : 0f,
                U = primitive.Uvs != null && primitive.Uvs.Length > vertexIndex * 2 + 1 ? primitive.Uvs[vertexIndex * 2] : 0f,
                V = primitive.Uvs != null && primitive.Uvs.Length > vertexIndex * 2 + 1 ? primitive.Uvs[vertexIndex * 2 + 1] : 0f
            };
            if (!skinned) return vertex;

            var accumulated = new Dictionary<int, float>();
            for (int k = 0; k < MaxInfluences; k++)
            {
                int jointPosition = primitive.Joints[vertexIndex * MaxInfluences + k];
                float weight = primitive.Weights[vertexIndex * MaxInfluences + k];
                if (float.IsNaN(weight) || float.IsInfinity(weight) || weight <= 0f) continue;

                if (skin.Joints == null || jointPosition < 0 || jointPosition >= skin.Joints.Length)
                {
                    stats.UnmappedJoints++;
                    continue;
                }
                int nodeIndex = skin.Joints[jointPosition];
                if (nodeIndex < 0 || nodeIndex >= nodeNames.Length)
                {
                    stats.UnmappedJoints++;
                    continue;
                }
                string nodeName = nodeNames[nodeIndex];
                if (string.IsNullOrEmpty(nodeName))
                {
                    stats.UnmappedNames++;
                    TrackBadName(stats, "(空节点名)");
                    continue;
                }

                int boneIndex;
                if (!nameToBone.TryGetValue(nodeName, out boneIndex))
                {
                    string baseName;
                    if (TryStripBlenderSuffix(nodeName, out baseName) &&
                        nameToBone.TryGetValue(baseName, out boneIndex))
                    {
                        stats.SuffixResolved++;
                    }
                    else
                    {
                        stats.UnmappedNames++;
                        TrackBadName(stats, nodeName);
                        boneIndex = 0;   // 回落骨骼 0（保留权重，不静默丢影响）
                    }
                }
                float old;
                accumulated.TryGetValue(boneIndex, out old);
                accumulated[boneIndex] = old + weight;
            }

            if (accumulated.Count == 0)
            {
                stats.WeightlessVertices++;
                return vertex;
            }

            var influences = new List<KeyValuePair<int, float>>(accumulated);
            influences.Sort(delegate (KeyValuePair<int, float> a, KeyValuePair<int, float> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return a.Key.CompareTo(b.Key);
            });
            if (influences.Count > MaxInfluences)
            {
                influences.RemoveRange(MaxInfluences, influences.Count - MaxInfluences);
                stats.TruncatedVertices++;
            }

            float sum = 0f;
            foreach (var pair in influences) sum += pair.Value;
            if (sum <= 0f)
            {
                stats.WeightlessVertices++;
                return vertex;
            }

            vertex.Weights = new List<SmdWeight>(influences.Count);
            foreach (var pair in influences)
                vertex.Weights.Add(new SmdWeight { BoneIndex = pair.Key, Weight = pair.Value / sum });
            // 主骨骼字段语义：权重最大的骨骼（SMD 写出时若有权重会用第一个权重的骨骼，两者一致）
            vertex.Bone = influences[0].Key;
            return vertex;
        }

        /// <summary>兼容 Blender 对重名骨骼追加的 “.001/.002” 后缀（只去掉末尾 3 位数字）。</summary>
        private static bool TryStripBlenderSuffix(string name, out string baseName)
        {
            baseName = null;
            if (string.IsNullOrEmpty(name) || name.Length < 5) return false;
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || name.Length - dot != 4) return false;
            for (int i = dot + 1; i < name.Length; i++)
            {
                char c = name[i];
                if (c < '0' || c > '9') return false;
            }
            baseName = name.Substring(0, dot);
            return true;
        }

        private static void TrackBadName(Stats stats, string name)
        {
            if (stats.BadNames.Count < 3 && !stats.BadNames.Contains(name)) stats.BadNames.Add(name);
        }

        private static string BadNamesSuffix(Stats stats)
        {
            if (stats.BadNames.Count == 0) return "";
            return "（例如 \"" + string.Join("\", \"", stats.BadNames.ToArray()) + "\"）";
        }

        private static void AddWarning(SmdModel model, string message)
        {
            if (model == null || model.Warnings == null) return;
            if (model.Warnings.Count >= 200)
            {
                if (model.Warnings.Count == 200) model.Warnings.Add("（警告过多，后续省略）");
                return;
            }
            model.Warnings.Add(message);
        }
    }
}
