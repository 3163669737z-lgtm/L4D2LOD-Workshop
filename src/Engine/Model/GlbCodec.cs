using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// GLB（glTF 2.0 二进制容器）编解码。
    ///
    /// 读取：JSON chunk + BIN chunk；bufferViews/accessors（byteOffset / byteStride / componentType /
    /// normalized / min-max），POSITION / NORMAL / TEXCOORD_0 / indices / JOINTS_0 / WEIGHTS_0，
    /// nodes（层级 + TRS/matrix）、skins（joints + 可选 inverseBindMatrices）、materials 名称。
    /// sparse accessor、外部 buffer、非三角图元、损坏文件都抛可读中文 <see cref="InvalidDataException"/>。
    ///
    /// 写出（<see cref="WriteFromSmd"/>）：单 mesh、按材质分 primitive、三角 soup、
    /// POSITION(含 min/max) / NORMAL / TEXCOORD_0 / indices / JOINTS_0(ushort) / WEIGHTS_0(float, 归一化)，
    /// 骨骼节点树（节点名 = SMD 骨骼名，父子按 SmdBone.Parent）+ skin（joints + 单位 IBM），
    /// BIN chunk 4 字节对齐；写出后立即自校验（读回三角面数必须一致）。
    ///
    /// 坐标/单位：SMD 与 glTF 都按原坐标写，不做任何缩放或轴系换算（与旧版管线一致；
    /// Blender glTF 导入/导出互为逆变换，减面后回写的数值仍在同一坐标空间）。
    /// </summary>
    public static class GlbCodec
    {
        private const string MeshNodeName = "LODMesh";
        private const string Generator = "Farsight LOD Studio";
        private const int MaxSmdInfluences = 4;

        // ══════════════════════════════════════════════════════════════════
        //  读取
        // ══════════════════════════════════════════════════════════════════

        /// <summary>读取 GLB 文件（共享读，允许文件正被 Blender 打开）；损坏时抛可读异常。</summary>
        public static GlbModel Read(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("GLB 路径为空", "path");
            byte[] data;
            try
            {
                data = FileUtil.ReadAllBytesShared(path);
            }
            catch (FileNotFoundException ex) { throw new InvalidDataException("GLB 文件不存在：" + path, ex); }
            catch (DirectoryNotFoundException ex) { throw new InvalidDataException("GLB 目录不存在：" + path, ex); }
            catch (Exception ex) { throw new InvalidDataException("读取 GLB 失败：" + path + " —— " + ex.Message, ex); }
            return ReadBytes(data, path);
        }

        /// <summary>从字节解析 GLB。displayName 只用于错误/警告文案（传路径即可）。</summary>
        public static GlbModel ReadBytes(byte[] data, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var container = GlbJson.SplitGlb(data, name);
            var root = container.Root;
            var bin = container.Bin;
            var model = new GlbModel { SourcePath = name };

            // ── asset ──
            var asset = GlbJson.Obj(root, "asset");
            string version = GlbJson.Str(asset, "version");
            if (string.IsNullOrEmpty(version) || !version.StartsWith("2", StringComparison.Ordinal))
                throw new InvalidDataException("GLB 不是 glTF 2.0（asset.version=" + (version ?? "(缺失)") + "）：" + name);

            // 明确不支持的压缩/量化扩展：与其读出错误数据，不如给可读错误
            var required = GlbJson.Arr(root, "extensionsRequired");
            if (required != null)
            {
                foreach (var extension in required)
                {
                    string extensionName = extension as string;
                    if (string.Equals(extensionName, "KHR_draco_mesh_compression", StringComparison.Ordinal))
                        throw new InvalidDataException("不支持 Draco 压缩的 GLB（KHR_draco_mesh_compression）：" + name);
                    if (string.Equals(extensionName, "KHR_mesh_quantization", StringComparison.Ordinal))
                        throw new InvalidDataException("不支持量化网格的 GLB（KHR_mesh_quantization）：" + name);
                }
            }

            // ── materials ──
            var materialNames = new List<string>();
            var materials = GlbJson.Arr(root, "materials");
            if (materials != null)
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    var material = materials[i] as Dictionary<string, object>;
                    if (material == null) { materialNames.Add(""); continue; }
                    materialNames.Add(GlbJson.Str(material, "name") ?? "");
                }
            }

            // ── nodes ──
            var nodeArray = GlbJson.Arr(root, "nodes");
            if (nodeArray != null)
            {
                for (int i = 0; i < nodeArray.Count; i++)
                {
                    var nodeObj = nodeArray[i] as Dictionary<string, object>;
                    if (nodeObj == null)
                        throw new InvalidDataException("GLB nodes[" + i + "] 不是对象：" + name);
                    var node = new GlbNode();
                    node.Name = GlbJson.Str(nodeObj, "name") ?? "";
                    int mesh = GlbJson.Int(nodeObj, "mesh", -1);
                    if (mesh >= 0) node.Mesh = mesh;
                    int skin = GlbJson.Int(nodeObj, "skin", -1);
                    if (skin >= 0) node.Skin = skin;
                    node.Children = GlbJson.IntArray(nodeObj, "children", name) ?? new int[0];

                    var matrix = GlbJson.FloatArray(nodeObj, "matrix", name);
                    if (matrix != null)
                    {
                        if (matrix.Length != 16)
                            throw new InvalidDataException("GLB nodes[" + i + "] 的 matrix 不是 16 个数：" + name);
                        node.Matrix = matrix;
                    }
                    var translation = GlbJson.FloatArray(nodeObj, "translation", name);
                    if (translation != null)
                    {
                        if (translation.Length != 3)
                            throw new InvalidDataException("GLB nodes[" + i + "] 的 translation 不是 3 个数：" + name);
                        node.Translation = translation;
                    }
                    var rotation = GlbJson.FloatArray(nodeObj, "rotation", name);
                    if (rotation != null)
                    {
                        if (rotation.Length != 4)
                            throw new InvalidDataException("GLB nodes[" + i + "] 的 rotation 不是 4 个数：" + name);
                        node.Rotation = rotation;
                    }
                    var scale = GlbJson.FloatArray(nodeObj, "scale", name);
                    if (scale != null)
                    {
                        if (scale.Length != 3)
                            throw new InvalidDataException("GLB nodes[" + i + "] 的 scale 不是 3 个数：" + name);
                        node.Scale = scale;
                    }
                    model.Nodes.Add(node);
                }
            }

            // ── skins ──
            var skinArray = GlbJson.Arr(root, "skins");
            if (skinArray != null)
            {
                for (int i = 0; i < skinArray.Count; i++)
                {
                    var skinObj = skinArray[i] as Dictionary<string, object>;
                    if (skinObj == null)
                        throw new InvalidDataException("GLB skins[" + i + "] 不是对象：" + name);
                    var skin = new GlbSkin { Name = GlbJson.Str(skinObj, "name") ?? "" };
                    var joints = GlbJson.IntArray(skinObj, "joints", name);
                    if (joints == null)
                    {
                        GlbJson.Warn(model.Warnings, "skin[" + i + "] 缺少 joints 数组");
                        joints = new int[0];
                    }
                    skin.Joints = joints;
                    int ibmAccessor = GlbJson.Int(skinObj, "inverseBindMatrices", -1);
                    if (ibmAccessor >= 0)
                    {
                        float[] ibm = GlbJson.ReadFloats(root, bin, ibmAccessor, name, 16, "inverseBindMatrices");
                        int matrices = ibm.Length / 16;
                        if (matrices != skin.Joints.Length)
                            GlbJson.Warn(model.Warnings, "skin[" + i + "] 的 inverseBindMatrices 数量（" + matrices +
                                                          "）与 joints 数量（" + skin.Joints.Length + "）不一致");
                        skin.InverseBindMatrices = ibm;
                    }
                    model.Skins.Add(skin);
                }
            }

            // ── meshes / primitives ──
            var meshArray = GlbJson.Arr(root, "meshes");
            if (meshArray != null)
            {
                for (int mi = 0; mi < meshArray.Count; mi++)
                {
                    var meshObj = meshArray[mi] as Dictionary<string, object>;
                    if (meshObj == null)
                        throw new InvalidDataException("GLB meshes[" + mi + "] 不是对象：" + name);
                    var mesh = new GlbMesh { Name = GlbJson.Str(meshObj, "name") ?? "" };
                    var primitives = GlbJson.Arr(meshObj, "primitives");
                    if (primitives == null || primitives.Count == 0)
                    {
                        GlbJson.Warn(model.Warnings, "mesh[" + mi + "] 没有 primitive");
                        model.Meshes.Add(mesh);
                        continue;
                    }
                    for (int pi = 0; pi < primitives.Count; pi++)
                    {
                        var primObj = primitives[pi] as Dictionary<string, object>;
                        if (primObj == null)
                            throw new InvalidDataException("GLB meshes[" + mi + "].primitives[" + pi + "] 不是对象：" + name);
                        var primitive = ReadPrimitive(root, bin, primObj, mi, pi, materialNames, model, name);
                        if (primitive != null) mesh.Primitives.Add(primitive);
                    }
                    model.Meshes.Add(mesh);
                }
            }

            // ── 引用校验（越界的 mesh/skin/children 只警告并剔除，避免下游崩） ──
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var node = model.Nodes[i];
                if (node.Mesh.HasValue && (node.Mesh.Value < 0 || node.Mesh.Value >= model.Meshes.Count))
                {
                    GlbJson.Warn(model.Warnings, "nodes[" + i + "] 引用了不存在的 mesh[" + node.Mesh.Value + "]");
                    node.Mesh = null;
                }
                if (node.Skin.HasValue && (node.Skin.Value < 0 || node.Skin.Value >= model.Skins.Count))
                {
                    GlbJson.Warn(model.Warnings, "nodes[" + i + "] 引用了不存在的 skin[" + node.Skin.Value + "]");
                    node.Skin = null;
                }
                if (node.Children != null && node.Children.Length > 0)
                {
                    var validChildren = new List<int>(node.Children.Length);
                    foreach (int child in node.Children)
                    {
                        if (child >= 0 && child < model.Nodes.Count) validChildren.Add(child);
                        else GlbJson.Warn(model.Warnings, "nodes[" + i + "] 的 children 引用了不存在的节点[" + child + "]");
                    }
                    node.Children = validChildren.ToArray();
                }
            }
            for (int i = 0; i < model.Skins.Count; i++)
            {
                var skin = model.Skins[i];
                foreach (int joint in skin.Joints)
                {
                    if (joint < 0 || joint >= model.Nodes.Count)
                    {
                        GlbJson.Warn(model.Warnings, "skin[" + i + "] 的 joints 引用了不存在的节点[" + joint + "]");
                        break;
                    }
                }
            }

            bool hasGeometry = false;
            foreach (var mesh in model.Meshes)
                foreach (var primitive in mesh.Primitives)
                    if (primitive.Positions != null && primitive.Positions.Length >= 3) hasGeometry = true;
            if (!hasGeometry)
                throw new InvalidDataException("GLB 里没有任何可用的网格几何（所有 primitive 都缺少 POSITION）：" + name);

            return model;
        }

        private static GlbPrimitive ReadPrimitive(Dictionary<string, object> root, byte[] bin,
                                                  Dictionary<string, object> primObj,
                                                  int meshIndex, int primIndex, List<string> materialNames,
                                                  GlbModel model, string displayName)
        {
            string where = "mesh[" + meshIndex + "].primitives[" + primIndex + "]";

            int mode = GlbJson.Int(primObj, "mode", 4);
            if (mode != 4)
                throw new InvalidDataException("GLB 只支持三角图元（mode=4），" + where +
                                               " 的 mode=" + mode + "：" + displayName);

            var attributes = GlbJson.Obj(primObj, "attributes");
            if (attributes == null)
                throw new InvalidDataException("GLB " + where + " 缺少 attributes：" + displayName);

            int positionAccessor = GlbJson.Int(attributes, "POSITION", -1);
            if (positionAccessor < 0)
            {
                GlbJson.Warn(model.Warnings, where + " 缺少 POSITION，已跳过该 primitive");
                return null;
            }

            var primitive = new GlbPrimitive();
            primitive.Positions = GlbJson.ReadFloats(root, bin, positionAccessor, displayName, 3, "POSITION");
            int vertexCount = primitive.Positions.Length / 3;
            if (vertexCount == 0)
            {
                GlbJson.Warn(model.Warnings, where + " 的 POSITION 为空，已跳过该 primitive");
                return null;
            }

            int normalAccessor = GlbJson.Int(attributes, "NORMAL", -1);
            if (normalAccessor >= 0)
            {
                primitive.Normals = GlbJson.ReadFloats(root, bin, normalAccessor, displayName, 3, "NORMAL");
                if (primitive.Normals.Length != primitive.Positions.Length)
                {
                    GlbJson.Warn(model.Warnings, where + " 的 NORMAL 顶点数与 POSITION 不一致，已忽略法线");
                    primitive.Normals = null;
                }
            }
            else
            {
                GlbJson.Warn(model.Warnings, where + " 没有 NORMAL，法线按 0 处理");
            }

            int uvAccessor = GlbJson.Int(attributes, "TEXCOORD_0", -1);
            if (uvAccessor >= 0)
            {
                primitive.Uvs = GlbJson.ReadFloats(root, bin, uvAccessor, displayName, 2, "TEXCOORD_0");
                if (primitive.Uvs.Length != vertexCount * 2)
                {
                    GlbJson.Warn(model.Warnings, where + " 的 TEXCOORD_0 顶点数与 POSITION 不一致，已忽略 UV");
                    primitive.Uvs = null;
                }
            }
            else
            {
                GlbJson.Warn(model.Warnings, where + " 没有 TEXCOORD_0，UV 按 0 处理");
            }

            int jointsAccessor = GlbJson.Int(attributes, "JOINTS_0", -1);
            int weightsAccessor = GlbJson.Int(attributes, "WEIGHTS_0", -1);
            if (jointsAccessor >= 0 && weightsAccessor >= 0)
            {
                int[] joints = GlbJson.ReadInts(root, bin, jointsAccessor, displayName, 4, "JOINTS_0");
                float[] weights = GlbJson.ReadFloats(root, bin, weightsAccessor, displayName, 4, "WEIGHTS_0");
                if (joints.Length != vertexCount * 4 || weights.Length != vertexCount * 4)
                {
                    GlbJson.Warn(model.Warnings, where + " 的 JOINTS_0/WEIGHTS_0 顶点数与 POSITION 不一致，已忽略骨骼权重");
                }
                else
                {
                    primitive.Joints = ConvertToUShort(joints, where, model);
                    primitive.Weights = weights;
                }
            }
            else if (jointsAccessor >= 0 || weightsAccessor >= 0)
            {
                GlbJson.Warn(model.Warnings, where + " 只有 JOINTS_0/WEIGHTS_0 之一，已忽略骨骼权重");
            }

            int indicesAccessor = GlbJson.Int(primObj, "indices", -1);
            if (indicesAccessor >= 0)
            {
                primitive.Indices = GlbJson.ReadInts(root, bin, indicesAccessor, displayName, 1, "indices");
                if (primitive.Indices.Length % 3 != 0)
                    throw new InvalidDataException("GLB " + where + " 的索引数（" + primitive.Indices.Length +
                                                   "）不是 3 的倍数，不是三角网格：" + displayName);
                foreach (int index in primitive.Indices)
                {
                    if (index < 0 || index >= vertexCount)
                        throw new InvalidDataException("GLB " + where + " 的索引越界（" + index + "，顶点数 " +
                                                       vertexCount + "）：" + displayName);
                }
            }
            else
            {
                // 规范允许省略 indices（隐式 0..count-1）
                primitive.Indices = new int[vertexCount];
                for (int i = 0; i < vertexCount; i++) primitive.Indices[i] = i;
                if (primitive.Indices.Length % 3 != 0)
                    throw new InvalidDataException("GLB " + where + " 没有 indices 且顶点数不是 3 的倍数：" + displayName);
            }

            int materialIndex = GlbJson.Int(primObj, "material", -1);
            if (materialIndex >= 0 && materialIndex < materialNames.Count)
            {
                primitive.MaterialName = materialNames[materialIndex] ?? "";
            }
            else if (materialIndex >= 0)
            {
                GlbJson.Warn(model.Warnings, where + " 引用了不存在的 material[" + materialIndex + "]");
                primitive.MaterialName = "";
            }
            else
            {
                primitive.MaterialName = "";
            }
            return primitive;
        }

        private static ushort[] ConvertToUShort(int[] joints, string where, GlbModel model)
        {
            var result = new ushort[joints.Length];
            for (int i = 0; i < joints.Length; i++)
            {
                int value = joints[i];
                if (value < 0 || value > ushort.MaxValue)
                {
                    GlbJson.Warn(model.Warnings, where + " 的 JOINTS_0 值超出 ushort 范围（" + value + "），已置 0");
                    value = 0;
                }
                result[i] = (ushort)value;
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  面数统计（只读 JSON，不展开顶点）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 快速统计三角面数：只解析 GLB 的 JSON chunk，对每个 primitive 取 indices accessor 的 count / 3
        /// （没有 indices 时用 POSITION count / 3），不读取 BIN 顶点数据。文件不是 GLB 时抛可读异常。
        /// </summary>
        public static int CountTriangles(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("GLB 路径为空", "path");
            byte[] data;
            try
            {
                data = FileUtil.ReadAllBytesShared(path);
            }
            catch (FileNotFoundException ex) { throw new InvalidDataException("GLB 文件不存在：" + path, ex); }
            catch (Exception ex) { throw new InvalidDataException("读取 GLB 失败：" + path + " —— " + ex.Message, ex); }
            return CountTriangles(data, path);
        }

        /// <summary>从字节快速统计三角面数（只解析 JSON chunk）。</summary>
        internal static int CountTriangles(byte[] data, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var container = GlbJson.SplitGlb(data, name);
            var root = container.Root;
            var accessors = GlbJson.Arr(root, "accessors");
            var meshes = GlbJson.Arr(root, "meshes");
            long total = 0;
            if (meshes != null)
            {
                for (int mi = 0; mi < meshes.Count; mi++)
                {
                    var meshObj = meshes[mi] as Dictionary<string, object>;
                    if (meshObj == null) continue;
                    var primitives = GlbJson.Arr(meshObj, "primitives");
                    if (primitives == null) continue;
                    for (int pi = 0; pi < primitives.Count; pi++)
                    {
                        var primObj = primitives[pi] as Dictionary<string, object>;
                        if (primObj == null) continue;
                        int mode = GlbJson.Int(primObj, "mode", 4);
                        if (mode != 4) continue;    // 只统计三角图元
                        int count;
                        int indices = GlbJson.Int(primObj, "indices", -1);
                        if (indices >= 0)
                        {
                            count = AccessorCount(accessors, indices, mi, pi, name);
                        }
                        else
                        {
                            var attributes = GlbJson.Obj(primObj, "attributes");
                            int position = GlbJson.Int(attributes, "POSITION", -1);
                            if (position < 0) continue;
                            count = AccessorCount(accessors, position, mi, pi, name);
                        }
                        total += count / 3;
                    }
                }
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        private static int AccessorCount(List<object> accessors, int accessorIndex, int meshIndex, int primIndex, string displayName)
        {
            if (accessors == null || accessorIndex < 0 || accessorIndex >= accessors.Count)
                throw new InvalidDataException("GLB mesh[" + meshIndex + "].primitives[" + primIndex +
                                               "] 引用了不存在的 accessor[" + accessorIndex + "]：" + displayName);
            var accessor = accessors[accessorIndex] as Dictionary<string, object>;
            if (accessor == null)
                throw new InvalidDataException("GLB accessor[" + accessorIndex + "] 不是对象：" + displayName);
            int count = GlbJson.Int(accessor, "count", -1);
            if (count < 0)
                throw new InvalidDataException("GLB accessor[" + accessorIndex + "] 缺少合法的 count：" + displayName);
            return count;
        }

        // ══════════════════════════════════════════════════════════════════
        //  SMD → GLB
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 由 SMD 生成给 Blender 导入用的 GLB（单 mesh、按材质分 primitive、骨骼节点树 + skin）。
        /// 写出后立刻用 <see cref="ReadBytes"/> 自校验三角面数，失败抛可读异常。
        /// </summary>
        public static byte[] WriteFromSmd(SmdModel model)
        {
            if (model == null) throw new ArgumentNullException("model");
            var triangles = model.Triangles ?? new List<SmdTriangle>();
            if (triangles.Count == 0)
                throw new InvalidDataException("SMD 没有三角形，无法生成 GLB（空网格会导致 Blender 导入失败）");

            var bones = new List<SmdBone>();
            if (model.Bones != null) foreach (var bone in model.Bones) if (bone != null) bones.Add(bone);

            // SMD 骨骼 Index → 骨骼列表位置（= glTF node 下标 = skin.joints 数组下标）
            var bonePosition = new Dictionary<int, int>();
            for (int i = 0; i < bones.Count; i++)
                if (!bonePosition.ContainsKey(bones[i].Index)) bonePosition[bones[i].Index] = i;

            // 蒙皮判定：有显式权重，或有顶点明确写了非 0 的主骨骼（旧版把后者）当静态，回写时绑定会全部落到骨骼 0）。
            // 真·静态模型（骨骼全 0 且无权重）保持静态，不额外生成骨架。
            bool anyRigidBone = false;
            foreach (var triangle in triangles)
            {
                if (triangle == null) continue;
                if ((triangle.V0 != null && triangle.V0.Bone != 0) ||
                    (triangle.V1 != null && triangle.V1.Bone != 0) ||
                    (triangle.V2 != null && triangle.V2.Bone != 0))
                {
                    anyRigidBone = true;
                    break;
                }
            }
            bool skinned = bones.Count > 0 && (model.HasSkinWeights || anyRigidBone);
            if (!skinned && bones.Count == 0 && model.HasSkinWeights)
                throw new InvalidDataException("SMD 有顶点骨骼权重但 nodes 段没有骨骼，无法生成蒙皮 GLB");

            // 按材质分组（顺序稳定：首次出现顺序）
            var materialOrder = new List<string>();
            var materialIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var groups = new List<PrimitiveBuffer>();
            int appendedTriangles = 0;
            foreach (var triangle in triangles)
            {
                if (triangle == null) continue;
                string material = NormalizeMaterial(triangle.Material);
                int index;
                if (!materialIndex.TryGetValue(material, out index))
                {
                    index = groups.Count;
                    materialIndex[material] = index;
                    materialOrder.Add(material);
                    groups.Add(new PrimitiveBuffer());
                }
                AppendTriangle(groups[index], triangle, bonePosition, skinned);
                appendedTriangles++;
            }
            if (appendedTriangles == 0)
                throw new InvalidDataException("SMD 没有有效三角形，无法生成 GLB");

            var writer = new GlbBinWriter();
            var primitiveJson = new List<string>(groups.Count);
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                int vertexCount = group.VertexCount;
                if (vertexCount == 0) continue;

                float[] min, max;
                ComputeBounds(group.Positions, out min, out max);
                int positionAccessor = writer.AddFloats(group.Positions.ToArray(), 3, "VEC3",
                                                        GlbJson.TargetArrayBuffer, min, max);
                int normalAccessor = writer.AddFloats(group.Normals.ToArray(), 3, "VEC3",
                                                      GlbJson.TargetArrayBuffer);
                int uvAccessor = writer.AddFloats(group.Uvs.ToArray(), 2, "VEC2",
                                                  GlbJson.TargetArrayBuffer);
                int jointsAccessor = -1, weightsAccessor = -1;
                if (skinned)
                {
                    jointsAccessor = writer.AddUShorts(group.Joints.ToArray(), 4, "VEC4",
                                                       GlbJson.TargetArrayBuffer);
                    weightsAccessor = writer.AddFloats(group.Weights.ToArray(), 4, "VEC4",
                                                       GlbJson.TargetArrayBuffer);
                }

                int indicesAccessor;
                if (vertexCount <= ushort.MaxValue)
                {
                    var indices = new ushort[vertexCount];
                    for (int i = 0; i < vertexCount; i++) indices[i] = (ushort)i;
                    indicesAccessor = writer.AddUShorts(indices, 1, "SCALAR", GlbJson.TargetElementArrayBuffer);
                }
                else
                {
                    var indices = new int[vertexCount];
                    for (int i = 0; i < vertexCount; i++) indices[i] = i;
                    indicesAccessor = writer.AddUInts(indices, 1, "SCALAR", GlbJson.TargetElementArrayBuffer);
                }

                var sb = new StringBuilder(256);
                sb.Append("{\"attributes\":{\"POSITION\":").Append(positionAccessor)
                  .Append(",\"NORMAL\":").Append(normalAccessor)
                  .Append(",\"TEXCOORD_0\":").Append(uvAccessor);
                if (skinned)
                    sb.Append(",\"JOINTS_0\":").Append(jointsAccessor)
                      .Append(",\"WEIGHTS_0\":").Append(weightsAccessor);
                sb.Append("},\"indices\":").Append(indicesAccessor)
                  .Append(",\"material\":").Append(gi)
                  .Append(",\"mode\":4}");
                primitiveJson.Add(sb.ToString());
            }
            if (primitiveJson.Count == 0)
                throw new InvalidDataException("SMD 没有可写出的三角形（分组后为空），无法生成 GLB");

            // skin 的 inverseBindMatrices：节点全部单位变换、顶点已在 bind pose，故单位矩阵
            int ibmAccessor = -1;
            if (skinned) ibmAccessor = writer.AddIdentityMat4(bones.Count);

            int boneCount = bones.Count;
            int meshNodeIndex = boneCount;
            var children = BuildChildren(bones, bonePosition);

            var json = new StringBuilder(4096);
            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"").Append(Generator).Append("\"},");
            json.Append("\"scene\":0,\"scenes\":[{\"nodes\":[").Append(meshNodeIndex).Append("]}],");
            json.Append("\"nodes\":[");
            for (int i = 0; i < boneCount; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"name\":").Append(Json.Escape(bones[i].Name ?? ""));
                if (children[i].Count > 0)
                {
                    json.Append(",\"children\":[");
                    for (int c = 0; c < children[i].Count; c++)
                    {
                        if (c > 0) json.Append(',');
                        json.Append(children[i][c].ToString(GlbJson.Inv));
                    }
                    json.Append(']');
                }
                json.Append('}');
            }
            if (boneCount > 0) json.Append(',');
            json.Append("{\"name\":\"").Append(MeshNodeName).Append("\",\"mesh\":0");
            if (skinned) json.Append(",\"skin\":0");
            json.Append("}]");

            if (skinned)
            {
                json.Append(",\"skins\":[{\"name\":\"SMD_Skeleton\",\"joints\":[");
                for (int i = 0; i < boneCount; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append(i.ToString(GlbJson.Inv));
                }
                json.Append(']');
                if (ibmAccessor >= 0) json.Append(",\"inverseBindMatrices\":").Append(ibmAccessor);
                json.Append("}]");
            }

            json.Append(",\"materials\":[");
            for (int i = 0; i < materialOrder.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"name\":").Append(Json.Escape(materialOrder[i])).Append('}');
            }
            json.Append(']');

            json.Append(",\"meshes\":[{\"name\":\"").Append(MeshNodeName).Append("\",\"primitives\":[");
            for (int i = 0; i < primitiveJson.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(primitiveJson[i]);
            }
            json.Append("]}]");

            byte[] bin = writer.BinBytes();
            json.Append(",\"accessors\":").Append(writer.AccessorsJson());
            json.Append(",\"bufferViews\":").Append(writer.ViewsJson());
            json.Append(",\"buffers\":[{\"byteLength\":").Append(bin.Length.ToString(GlbJson.Inv)).Append("}]}");

            byte[] glb = GlbJson.PackGlb(Encoding.UTF8.GetBytes(json.ToString()), bin);

            // 写出后自校验：必须能读回且三角面数一致
            GlbModel check;
            try
            {
                check = ReadBytes(glb, "(GLB 自校验)");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("GLB 自校验失败：写出的文件无法读回 —— " + ex.Message, ex);
            }
            if (check.TriangleCount != appendedTriangles)
                throw new InvalidDataException("GLB 自校验失败：三角面数不一致（输入 " + appendedTriangles +
                                               "，读回 " + check.TriangleCount + "）");

            return glb;
        }

        // ──────────────────────────────────────────────────────────────────
        //  写出辅助
        // ──────────────────────────────────────────────────────────────────

        private sealed class PrimitiveBuffer
        {
            internal readonly List<float> Positions = new List<float>();
            internal readonly List<float> Normals = new List<float>();
            internal readonly List<float> Uvs = new List<float>();
            internal readonly List<ushort> Joints = new List<ushort>();
            internal readonly List<float> Weights = new List<float>();
            internal int VertexCount { get { return Positions.Count / 3; } }
        }

        private static void AppendTriangle(PrimitiveBuffer group, SmdTriangle triangle,
                                           Dictionary<int, int> bonePosition, bool skinned)
        {
            AppendVertex(group, triangle.V0, bonePosition, skinned);
            AppendVertex(group, triangle.V1, bonePosition, skinned);
            AppendVertex(group, triangle.V2, bonePosition, skinned);
        }

        private static void AppendVertex(PrimitiveBuffer group, SmdVertex vertex,
                                         Dictionary<int, int> bonePosition, bool skinned)
        {
            float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, u = 0, v = 0;
            if (vertex != null)
            {
                x = Safe(vertex.X); y = Safe(vertex.Y); z = Safe(vertex.Z);
                nx = Safe(vertex.Nx); ny = Safe(vertex.Ny); nz = Safe(vertex.Nz);
                u = Safe(vertex.U); v = Safe(vertex.V);
            }
            group.Positions.Add(x);
            group.Positions.Add(y);
            group.Positions.Add(z);
            group.Normals.Add(nx);
            group.Normals.Add(ny);
            group.Normals.Add(nz);
            group.Uvs.Add(u);
            group.Uvs.Add(v);

            if (!skinned) return;

            // 权重：按骨骼列表位置合并同名影响 → 按权重降序取前 4 → 归一化
            var accumulated = new Dictionary<int, float>();
            if (vertex != null && vertex.Weights != null)
            {
                foreach (var weight in vertex.Weights)
                {
                    if (weight == null) continue;
                    float value = weight.Weight;
                    if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) continue;
                    int position;
                    if (!bonePosition.TryGetValue(weight.BoneIndex, out position)) position = 0; // 未知骨骼回落到 joints[0]
                    float old;
                    accumulated.TryGetValue(position, out old);
                    accumulated[position] = old + value;
                }
            }

            var influences = new List<KeyValuePair<int, float>>(accumulated);
            influences.Sort(delegate (KeyValuePair<int, float> a, KeyValuePair<int, float> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return a.Key.CompareTo(b.Key);
            });
            if (influences.Count > MaxSmdInfluences)
                influences.RemoveRange(MaxSmdInfluences, influences.Count - MaxSmdInfluences);

            float sum = 0;
            foreach (var pair in influences) sum += pair.Value;
            if (influences.Count == 0 || sum <= 0f)
            {
                // 蒙皮模型里没有任何权重的顶点：glTF 要求权重和为 1，绑定到它的主骨骼（SMD 顶点行第一个字段），
                // 没有主骨骼时回落骨骼 0；这样 Blender 导入不会出现无 vertex group 的顶点。
                int fallback = 0;
                if (vertex != null && bonePosition.TryGetValue(vertex.Bone, out fallback))
                {
                    // fallback 已是 joints 数组位置
                }
                else
                {
                    fallback = 0;
                }
                influences.Clear();
                influences.Add(new KeyValuePair<int, float>(fallback, 1f));
                sum = 1f;
            }

            for (int k = 0; k < MaxSmdInfluences; k++)
            {
                if (k < influences.Count)
                {
                    group.Joints.Add((ushort)influences[k].Key);
                    group.Weights.Add(influences[k].Value / sum);
                }
                else
                {
                    group.Joints.Add(0);
                    group.Weights.Add(0f);
                }
            }
        }

        private static List<int>[] BuildChildren(List<SmdBone> bones, Dictionary<int, int> bonePosition)
        {
            int count = bones.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++)
            {
                parent[i] = -1;
                int parentIndex = bones[i].Parent;
                if (parentIndex < 0) continue;
                int parentPosition;
                if (bonePosition.TryGetValue(parentIndex, out parentPosition) && parentPosition != i)
                    parent[i] = parentPosition;
            }

            // 打断环（glTF 节点不能成环，坏 SMD 也不能让 Blender 卡死）
            for (int i = 0; i < count; i++)
            {
                int guard = 0;
                int p = parent[i];
                while (p >= 0 && guard <= count)
                {
                    p = parent[p];
                    guard++;
                }
                if (p >= 0) parent[i] = -1;
            }

            var children = new List<int>[count];
            for (int i = 0; i < count; i++) children[i] = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int p = parent[i];
                if (p >= 0 && p != i) children[p].Add(i);
            }
            return children;
        }

        private static void ComputeBounds(List<float> positions, out float[] min, out float[] max)
        {
            min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i + 2 < positions.Count; i += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    float value = positions[i + c];
                    if (value < min[c]) min[c] = value;
                    if (value > max[c]) max[c] = value;
                }
            }
            if (min[0] > max[0]) { min[0] = min[1] = min[2] = 0; max[0] = max[1] = max[2] = 0; }
        }

        private static float Safe(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private static string NormalizeMaterial(string material)
        {
            if (string.IsNullOrEmpty(material)) return "material0";
            string trimmed = material.Trim();
            return trimmed.Length == 0 ? "material0" : trimmed;
        }
    }
}
