using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>Body 内缩结果。Data 在未发生修改时引用原始字节，避免无意义的 GLB 往返。</summary>
    public sealed class GlbInnerShrinkResult
    {
        public byte[] Data { get; internal set; }
        public bool Changed { get; internal set; }
        public int BodyMeshes { get; internal set; }
        public int ModifiedVertices { get; internal set; }
        public double WorldDiagonal { get; internal set; }
        public double ShrinkDistance { get; internal set; }
    }

    /// <summary>
    /// 在原始 glTF JSON + BIN 上实现 Body 沿法线内缩。
    /// 独占 mesh（只被 Body 节点引用、POSITION accessor 未被其它 mesh 引用）直接原位写回顶点，
    /// 不新增 mesh/accessor/bufferView，因此不会产生孤儿 mesh、也不会让文件里的几何翻倍；
    /// 只有与非 Body 实例共享 mesh 时才克隆出独立 POSITION 数据（原 mesh 仍被非 Body 引用）。
    /// 蒙皮、UV、材质、图片、动画及其它 JSON 字段不做模型级往返。
    /// </summary>
    public static class GlbInnerShrink
    {
        public const double DefaultRatio = 0.03;

        private sealed class MeshUse
        {
            internal int NodeIndex;
            internal int MeshIndex;
            internal string Name;
            internal Matrix World;
            internal bool IsBody;
        }

        private sealed class Matrix
        {
            // glTF 矩阵为 column-major：m[列 * 4 + 行]。
            internal readonly double[] M = new double[16];

            internal static Matrix Identity()
            {
                var matrix = new Matrix();
                matrix.M[0] = matrix.M[5] = matrix.M[10] = matrix.M[15] = 1.0;
                return matrix;
            }
        }

        public static GlbInnerShrinkResult Apply(byte[] source, double ratio, string displayName)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 0)
                throw new ArgumentOutOfRangeException("ratio", "Body 内缩比例必须是非负有限数");

            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var result = new GlbInnerShrinkResult { Data = source };
            if (ratio == 0) return result;

            var container = GlbJson.SplitGlb(source, name);
            var root = container.Root;
            var nodes = GlbJson.Arr(root, "nodes");
            var meshes = GlbJson.Arr(root, "meshes");
            if (nodes == null || meshes == null) return result;

            var uses = new List<MeshUse>();
            CollectMeshUses(root, nodes, meshes, uses, name);
            foreach (var use in uses) if (use.IsBody) result.BodyMeshes++;
            if (result.BodyMeshes == 0) return result;

            // 与 Blender 后端一致：取所有活动场景网格对象中最大的世界坐标包围盒对角线。
            double diagonal = 0;
            foreach (var use in uses)
            {
                double current = ComputeWorldDiagonal(root, container.Bin, use, name);
                if (current > diagonal) diagonal = current;
            }
            result.WorldDiagonal = diagonal;
            result.ShrinkDistance = diagonal * ratio;
            if (diagonal <= 0 || result.ShrinkDistance <= 0) return result;

            var accessors = GlbJson.Arr(root, "accessors");
            var views = GlbJson.Arr(root, "bufferViews");
            var buffers = GlbJson.Arr(root, "buffers");
            if (accessors == null || views == null || buffers == null || buffers.Count != 1)
                throw new InvalidDataException("Body 内缩需要恰好一个内嵌 buffer：" + name);

            byte[] bin = container.Bin;

            // 按 mesh 分组处理：独占 mesh 原位写回，共享 mesh 才克隆。
            var positionOwners = BuildPositionAccessorOwners(meshes);
            var groups = new SortedDictionary<int, List<MeshUse>>();
            foreach (var use in uses)
            {
                List<MeshUse> group;
                if (!groups.TryGetValue(use.MeshIndex, out group))
                {
                    group = new List<MeshUse>();
                    groups.Add(use.MeshIndex, group);
                }
                group.Add(use);
            }

            foreach (var pair in groups)
            {
                List<MeshUse> group = pair.Value;
                bool allBody = true;
                foreach (var use in group)
                {
                    if (!use.IsBody) { allBody = false; break; }
                }

                // 独占 mesh：该 mesh 的全部实例都是 Body，且 POSITION accessor 没有被其它 mesh 的
                // primitive 引用 —— 原地内缩即安全（所有看这份顶点的实例都需要内缩）。
                if (allBody && ArePositionAccessorsExclusive(meshes, pair.Key, positionOwners))
                {
                    // 同一 mesh 被多个 Body 节点引用时只写一遍。世界缩放不同则本地内缩量不同，
                    // 取最大值保证每个实例都向内缩（差异是亚毫米级，且避免为每实例克隆而留下孤儿 mesh）。
                    double inPlaceDistance = 0;
                    foreach (var use in group)
                    {
                        double candidate = result.ShrinkDistance / AverageWorldScale(use.World);
                        if (candidate > inPlaceDistance) inPlaceDistance = candidate;
                    }
                    if (TryShrinkMeshInPlace(root, bin, meshes, pair.Key, inPlaceDistance, result, name))
                        continue;
                }

                // 与非 Body 实例共享 mesh（或数据形态不适合原位写）时克隆独立 mesh；
                // 原 mesh 仍被非 Body 实例引用，不会成为孤儿。
                foreach (var use in group)
                {
                    if (!use.IsBody) continue;
                    double scale = AverageWorldScale(use.World);
                    double localDistance = result.ShrinkDistance / scale;
                    CloneBodyMeshAndShrink(root, nodes, meshes, accessors, views, ref bin,
                                           use, localDistance, result, name);
                }
            }

            if (result.ModifiedVertices == 0) return result;
            var buffer = GlbJson.ObjOf(buffers[0]);
            if (buffer == null) throw new InvalidDataException("GLB buffers[0] 不是对象：" + name);
            buffer["byteLength"] = (double)bin.Length;
            result.Data = GlbJson.PackGlb(Encoding.UTF8.GetBytes(SerializeJson(root)), bin);
            result.Changed = true;
            return result;
        }

        private static void CollectMeshUses(Dictionary<string, object> root, List<object> nodes,
            List<object> meshes, List<MeshUse> output, string name)
        {
            var scenes = GlbJson.Arr(root, "scenes");
            if (scenes == null || scenes.Count == 0) return;
            int sceneIndex = GlbJson.Int(root, "scene", 0);
            if (sceneIndex < 0 || sceneIndex >= scenes.Count)
                throw new InvalidDataException("GLB 活动 scene 索引越界（" + sceneIndex + "）：" + name);
            var scene = GlbJson.ObjOf(scenes[sceneIndex]);
            var roots = GlbJson.Arr(scene, "nodes");
            if (scene == null || roots == null) return;

            var visiting = new HashSet<int>();
            var visited = new HashSet<int>();
            foreach (object item in roots)
                TraverseNode(ConvertIndex(item, "scene.nodes", name), nodes, meshes, Matrix.Identity(),
                             output, visiting, visited, name);
        }

        private static void TraverseNode(int index, List<object> nodes, List<object> meshes, Matrix parent,
            List<MeshUse> output, HashSet<int> visiting, HashSet<int> visited, string name)
        {
            if (index < 0 || index >= nodes.Count)
                throw new InvalidDataException("GLB 节点索引越界（" + index + "）：" + name);
            if (visited.Contains(index)) return;
            if (!visiting.Add(index)) throw new InvalidDataException("GLB 节点层级存在环（node=" + index + "）：" + name);

            var node = GlbJson.ObjOf(nodes[index]);
            if (node == null) throw new InvalidDataException("GLB nodes[" + index + "] 不是对象：" + name);
            Matrix world = Multiply(parent, ReadNodeMatrix(node, name));
            int meshIndex = GlbJson.Int(node, "mesh", -1);
            if (meshIndex >= 0)
            {
                if (meshIndex >= meshes.Count)
                    throw new InvalidDataException("GLB mesh 索引越界（" + meshIndex + "）：" + name);
                string nodeName = GlbJson.Str(node, "name");
                string meshName = GlbJson.Str(GlbJson.ObjOf(meshes[meshIndex]), "name");
                string objectName = !string.IsNullOrEmpty(nodeName) ? nodeName : (meshName ?? "");
                output.Add(new MeshUse
                {
                    NodeIndex = index,
                    MeshIndex = meshIndex,
                    Name = objectName,
                    World = world,
                    IsBody = objectName.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0
                });
            }

            var children = GlbJson.Arr(node, "children");
            if (children != null)
                foreach (object child in children)
                    TraverseNode(ConvertIndex(child, "node.children", name), nodes, meshes, world,
                                 output, visiting, visited, name);
            visiting.Remove(index);
            visited.Add(index);
        }

        private static int ConvertIndex(object value, string what, string name)
        {
            if (value is double) return (int)Math.Round((double)value);
            if (value is long) return (int)(long)value;
            throw new InvalidDataException("GLB " + what + " 含非数字索引：" + name);
        }

        private static Matrix ReadNodeMatrix(Dictionary<string, object> node, string name)
        {
            var rawMatrix = GlbJson.Arr(node, "matrix");
            if (rawMatrix != null)
            {
                if (rawMatrix.Count != 16) throw new InvalidDataException("GLB node.matrix 必须为 16 个数：" + name);
                var matrix = new Matrix();
                for (int i = 0; i < 16; i++) matrix.M[i] = Number(rawMatrix[i], "node.matrix", name);
                return matrix;
            }

            double[] t = ReadVector(node, "translation", 3, 0, name);
            double[] r = ReadVector(node, "rotation", 4, 0, name);
            double[] s = ReadVector(node, "scale", 3, 1, name);
            if (GlbJson.Arr(node, "rotation") == null) r[3] = 1;
            double x = r[0], y = r[1], z = r[2], w = r[3];
            double length = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (length <= 1e-20) { x = y = z = 0; w = 1; }
            else { x /= length; y /= length; z /= length; w /= length; }

            var result = Matrix.Identity();
            result.M[0] = 1 - 2 * (y * y + z * z);
            result.M[1] = 2 * (x * y + w * z);
            result.M[2] = 2 * (x * z - w * y);
            result.M[4] = 2 * (x * y - w * z);
            result.M[5] = 1 - 2 * (x * x + z * z);
            result.M[6] = 2 * (y * z + w * x);
            result.M[8] = 2 * (x * z + w * y);
            result.M[9] = 2 * (y * z - w * x);
            result.M[10] = 1 - 2 * (x * x + y * y);
            for (int row = 0; row < 3; row++)
            {
                result.M[row] *= s[0];
                result.M[4 + row] *= s[1];
                result.M[8 + row] *= s[2];
            }
            result.M[12] = t[0]; result.M[13] = t[1]; result.M[14] = t[2];
            return result;
        }

        private static double[] ReadVector(Dictionary<string, object> parent, string key, int count,
            double fallback, string name)
        {
            var values = new double[count];
            for (int i = 0; i < count; i++) values[i] = fallback;
            var raw = GlbJson.Arr(parent, key);
            if (raw == null) return values;
            if (raw.Count != count) throw new InvalidDataException("GLB node." + key + " 分量数错误：" + name);
            for (int i = 0; i < count; i++) values[i] = Number(raw[i], "node." + key, name);
            return values;
        }

        private static double Number(object value, string what, string name)
        {
            if (value is double) return (double)value;
            if (value is long) return (long)value;
            throw new InvalidDataException("GLB " + what + " 含非数字值：" + name);
        }

        private static Matrix Multiply(Matrix a, Matrix b)
        {
            var result = new Matrix();
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    for (int k = 0; k < 4; k++)
                        result.M[col * 4 + row] += a.M[k * 4 + row] * b.M[col * 4 + k];
            return result;
        }

        private static void TransformPoint(Matrix matrix, float[] positions, int index, out double x, out double y, out double z)
        {
            var m = matrix.M;
            double px = positions[index], py = positions[index + 1], pz = positions[index + 2];
            x = m[0] * px + m[4] * py + m[8] * pz + m[12];
            y = m[1] * px + m[5] * py + m[9] * pz + m[13];
            z = m[2] * px + m[6] * py + m[10] * pz + m[14];
        }

        private static double AverageWorldScale(Matrix matrix)
        {
            var m = matrix.M;
            double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
            double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
            double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
            double average = (Math.Abs(sx) + Math.Abs(sy) + Math.Abs(sz)) / 3.0;
            return average > 1e-12 ? average : 1.0;
        }

        private static double ComputeWorldDiagonal(Dictionary<string, object> root, byte[] bin, MeshUse use, string name)
        {
            var meshes = GlbJson.Arr(root, "meshes");
            var primitives = GlbJson.Arr(GlbJson.ObjOf(meshes[use.MeshIndex]), "primitives");
            if (primitives == null) return 0;
            double[] min = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] max = { double.MinValue, double.MinValue, double.MinValue };
            bool any = false;
            foreach (object item in primitives)
            {
                var attributes = GlbJson.Obj(GlbJson.ObjOf(item), "attributes");
                int position = GlbJson.Int(attributes, "POSITION", -1);
                if (position < 0) continue;
                float[] positions = GlbJson.ReadFloats(root, bin, position, name, 3, "POSITION");
                for (int i = 0; i + 2 < positions.Length; i += 3)
                {
                    double x, y, z;
                    TransformPoint(use.World, positions, i, out x, out y, out z);
                    double[] point = { x, y, z };
                    for (int c = 0; c < 3; c++)
                    {
                        if (point[c] < min[c]) min[c] = point[c];
                        if (point[c] > max[c]) max[c] = point[c];
                    }
                    any = true;
                }
            }
            if (!any) return 0;
            double dx = max[0] - min[0], dy = max[1] - min[1], dz = max[2] - min[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>POSITION accessor → 引用它的 mesh 索引集合（判断能否原位写回；只看 POSITION，其它属性不写入）。</summary>
        private static Dictionary<int, HashSet<int>> BuildPositionAccessorOwners(List<object> meshes)
        {
            var owners = new Dictionary<int, HashSet<int>>();
            for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                var mesh = GlbJson.ObjOf(meshes[meshIndex]);
                if (mesh == null) continue;
                var primitives = GlbJson.Arr(mesh, "primitives");
                if (primitives == null) continue;
                foreach (object item in primitives)
                {
                    var primitive = GlbJson.ObjOf(item);
                    if (primitive == null) continue;
                    var attributes = GlbJson.Obj(primitive, "attributes");
                    int position = GlbJson.Int(attributes, "POSITION", -1);
                    if (position < 0) continue;
                    HashSet<int> set;
                    if (!owners.TryGetValue(position, out set))
                    {
                        set = new HashSet<int>();
                        owners.Add(position, set);
                    }
                    set.Add(meshIndex);
                }
            }
            return owners;
        }

        /// <summary>该 mesh 的所有 POSITION accessor 是否都没有被其它 mesh 引用。</summary>
        private static bool ArePositionAccessorsExclusive(List<object> meshes, int meshIndex,
            Dictionary<int, HashSet<int>> owners)
        {
            var mesh = GlbJson.ObjOf(meshes[meshIndex]);
            var primitives = GlbJson.Arr(mesh, "primitives");
            if (primitives == null) return false;
            foreach (object item in primitives)
            {
                var primitive = GlbJson.ObjOf(item);
                if (primitive == null) continue;
                var attributes = GlbJson.Obj(primitive, "attributes");
                int position = GlbJson.Int(attributes, "POSITION", -1);
                if (position < 0) continue;
                HashSet<int> set;
                if (owners.TryGetValue(position, out set) && set.Count > 1) return false;
            }
            return true;
        }

        /// <summary>
        /// 把独占 mesh 的 POSITION 原地沿法线内缩：写回原 bufferView 区域并更新 accessor 的 min/max，
        /// 不新增 mesh/accessor/bufferView。返回 false 表示数据形态不适合原位写（调用方回退克隆路径）。
        /// </summary>
        private static bool TryShrinkMeshInPlace(Dictionary<string, object> root, byte[] bin, List<object> meshes,
            int meshIndex, double localDistance, GlbInnerShrinkResult result, string name)
        {
            var mesh = GlbJson.ObjOf(meshes[meshIndex]);
            if (mesh == null) throw new InvalidDataException("Body mesh 不是对象：" + name);
            var primitives = GlbJson.Arr(mesh, "primitives");
            if (primitives == null) return false;

            // 先完成全部校验再写入，避免改到一半才发现要回退。
            var pairs = new List<int[]>();
            var seenPositions = new HashSet<int>();
            foreach (object item in primitives)
            {
                var primitive = GlbJson.ObjOf(item);
                if (primitive == null) throw new InvalidDataException("Body mesh 含非对象 primitive：" + name);
                var attributes = GlbJson.Obj(primitive, "attributes");
                int positionIndex = GlbJson.Int(attributes, "POSITION", -1);
                if (positionIndex < 0) continue;
                var targets = GlbJson.Arr(primitive, "targets");
                if (targets != null && targets.Count > 0)
                    throw new InvalidDataException("Body 含 morph target，当前无 Blender 内缩不支持，请使用 Blender 后端：" + name);
                var positionInfo = GlbJson.ReadAccessorInfo(root, positionIndex, name);
                if (positionInfo.ComponentType != GlbJson.ComponentFloat || positionInfo.Components != 3)
                    return false;
                if (positionInfo.Count == 0 || positionInfo.BufferView < 0) continue;
                int normalIndex = GlbJson.Int(attributes, "NORMAL", -1);
                if (normalIndex < 0) return false;
                var normalInfo = GlbJson.ReadAccessorInfo(root, normalIndex, name);
                if (normalInfo.ComponentType != GlbJson.ComponentFloat || normalInfo.Components != 3 ||
                    normalInfo.Count != positionInfo.Count)
                    return false;
                // 同一 POSITION accessor 被同 mesh 多个 primitive 共用时只缩一次（首个 NORMAL 为准）。
                if (!seenPositions.Add(positionIndex)) continue;
                pairs.Add(new[] { positionIndex, normalIndex });
            }

            var accessors = GlbJson.Arr(root, "accessors");
            foreach (var pair in pairs)
                ShrinkPositionInPlace(root, bin, accessors, pair[0], pair[1], localDistance, result, name);
            return true;
        }

        /// <summary>读 POSITION/NORMAL，沿单位法线内缩后原地写回 BIN 并刷新 accessor min/max。</summary>
        private static void ShrinkPositionInPlace(Dictionary<string, object> root, byte[] bin, List<object> accessors,
            int positionIndex, int normalIndex, double amount, GlbInnerShrinkResult result, string name)
        {
            var positionInfo = GlbJson.ReadAccessorInfo(root, positionIndex, name);
            var view = GlbJson.ReadViewInfo(root, positionInfo.BufferView, name);
            float[] positions = GlbJson.ReadFloats(root, bin, positionIndex, name, 3, "POSITION");
            float[] normals = GlbJson.ReadFloats(root, bin, normalIndex, name, 3, "NORMAL");

            const int elementSize = 12;   // FLOAT VEC3：3 × 4 字节（调用前已校验）
            int stride = view.ByteStride > 0 ? view.ByteStride : elementSize;
            int offset = view.Offset + positionInfo.ByteOffset;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i + 2 < positions.Length; i += 3)
            {
                double nx = normals[i], ny = normals[i + 1], nz = normals[i + 2];
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 1e-20) { nx /= length; ny /= length; nz /= length; }
                else { nx = ny = nz = 0; }
                float x = (float)(positions[i] - nx * amount);
                float y = (float)(positions[i + 1] - ny * amount);
                float z = (float)(positions[i + 2] - nz * amount);
                int row = offset + (i / 3) * stride;
                Array.Copy(BitConverter.GetBytes(x), 0, bin, row, 4);
                Array.Copy(BitConverter.GetBytes(y), 0, bin, row + 4, 4);
                Array.Copy(BitConverter.GetBytes(z), 0, bin, row + 8, 4);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            var accessor = GlbJson.ObjOf(accessors[positionIndex]);
            if (accessor == null) throw new InvalidDataException("GLB accessor #" + positionIndex + " 不是对象：" + name);
            accessor["min"] = NumberList(new[] { minX, minY, minZ });
            accessor["max"] = NumberList(new[] { maxX, maxY, maxZ });
            result.ModifiedVertices += positionInfo.Count;
        }

        private static void CloneBodyMeshAndShrink(Dictionary<string, object> root, List<object> nodes,
            List<object> meshes, List<object> accessors, List<object> views, ref byte[] bin, MeshUse use,
            double localDistance, GlbInnerShrinkResult result, string name)
        {
            var originalMesh = GlbJson.ObjOf(meshes[use.MeshIndex]);
            var originalPrimitives = GlbJson.Arr(originalMesh, "primitives");
            if (originalMesh == null || originalPrimitives == null)
                throw new InvalidDataException("Body mesh 缺少 primitives：" + use.Name + "：" + name);

            var clonedMesh = new Dictionary<string, object>(originalMesh);
            var clonedPrimitives = new List<object>();
            var clonedByPair = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (object item in originalPrimitives)
            {
                var primitive = GlbJson.ObjOf(item);
                if (primitive == null) throw new InvalidDataException("Body mesh 含非对象 primitive：" + name);
                var clonedPrimitive = new Dictionary<string, object>(primitive);
                var attributes = GlbJson.Obj(primitive, "attributes");
                var clonedAttributes = attributes == null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object>(attributes);
                clonedPrimitive["attributes"] = clonedAttributes;

                int positionIndex = GlbJson.Int(attributes, "POSITION", -1);
                if (positionIndex >= 0)
                {
                    var targets = GlbJson.Arr(primitive, "targets");
                    if (targets != null && targets.Count > 0)
                        throw new InvalidDataException("Body 含 morph target，当前无 Blender 内缩不支持，请使用 Blender 后端：" + name);
                    var positionInfo = GlbJson.ReadAccessorInfo(root, positionIndex, name);
                    if (positionInfo.ComponentType != GlbJson.ComponentFloat || positionInfo.Components != 3)
                        throw new InvalidDataException("Body POSITION 必须是 FLOAT VEC3：" + name);
                    if (positionInfo.Count > 0)
                    {
                        int normalIndex = GlbJson.Int(attributes, "NORMAL", -1);
                        if (normalIndex < 0)
                            throw new InvalidDataException("Body 缺少 NORMAL，无法确定内缩方向，请使用 Blender 后端：" + name);
                        var normalInfo = GlbJson.ReadAccessorInfo(root, normalIndex, name);
                        if (normalInfo.ComponentType != GlbJson.ComponentFloat || normalInfo.Components != 3)
                            throw new InvalidDataException("Body NORMAL 必须是 FLOAT VEC3：" + name);
                        if (normalInfo.Count != positionInfo.Count)
                            throw new InvalidDataException("Body POSITION 与 NORMAL 数量不一致：" + name);

                        string key = positionIndex + ":" + normalIndex;
                        int clonedAccessor;
                        if (!clonedByPair.TryGetValue(key, out clonedAccessor))
                        {
                            float[] positions = GlbJson.ReadFloats(root, bin, positionIndex, name, 3, "POSITION");
                            float[] normals = GlbJson.ReadFloats(root, bin, normalIndex, name, 3, "NORMAL");
                            clonedAccessor = AppendShrunkPositions(accessors, views, ref bin, positions, normals,
                                                                  localDistance, positionIndex, name);
                            clonedByPair.Add(key, clonedAccessor);
                            result.ModifiedVertices += positionInfo.Count;
                        }
                        clonedAttributes["POSITION"] = (double)clonedAccessor;
                    }
                }
                clonedPrimitives.Add(clonedPrimitive);
            }

            clonedMesh["primitives"] = clonedPrimitives;
            meshes.Add(clonedMesh);
            var node = GlbJson.ObjOf(nodes[use.NodeIndex]);
            node["mesh"] = (double)(meshes.Count - 1);
        }

        private static int AppendShrunkPositions(List<object> accessors, List<object> views, ref byte[] bin,
            float[] positions, float[] normals, double amount, int originalAccessorIndex, string name)
        {
            var shrunk = new float[positions.Length];
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < positions.Length; i += 3)
            {
                double nx = normals[i], ny = normals[i + 1], nz = normals[i + 2];
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 1e-20) { nx /= length; ny /= length; nz /= length; }
                else { nx = ny = nz = 0; }
                shrunk[i] = (float)(positions[i] - nx * amount);
                shrunk[i + 1] = (float)(positions[i + 1] - ny * amount);
                shrunk[i + 2] = (float)(positions[i + 2] - nz * amount);
                for (int c = 0; c < 3; c++)
                {
                    if (shrunk[i + c] < min[c]) min[c] = shrunk[i + c];
                    if (shrunk[i + c] > max[c]) max[c] = shrunk[i + c];
                }
            }

            int padding = (4 - (bin.Length % 4)) % 4;
            int offset = checked(bin.Length + padding);
            int rawLength = checked(shrunk.Length * 4);
            var expanded = new byte[checked(offset + rawLength)];
            Array.Copy(bin, expanded, bin.Length);
            for (int i = 0; i < shrunk.Length; i++)
                Array.Copy(BitConverter.GetBytes(shrunk[i]), 0, expanded, offset + i * 4, 4);
            bin = expanded;

            int viewIndex = views.Count;
            views.Add(new Dictionary<string, object>
            {
                { "buffer", 0.0 },
                { "byteOffset", (double)offset },
                { "byteLength", (double)rawLength },
                { "target", (double)GlbJson.TargetArrayBuffer }
            });

            var original = GlbJson.ObjOf(accessors[originalAccessorIndex]);
            var accessor = original == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(original);
            accessor["bufferView"] = (double)viewIndex;
            accessor["byteOffset"] = 0.0;
            accessor["componentType"] = (double)GlbJson.ComponentFloat;
            accessor["count"] = (double)(shrunk.Length / 3);
            accessor["type"] = "VEC3";
            accessor.Remove("normalized");
            accessor["min"] = NumberList(min);
            accessor["max"] = NumberList(max);
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        private static List<object> NumberList(float[] values)
        {
            var result = new List<object>();
            foreach (float value in values) result.Add((double)value);
            return result;
        }

        private static string SerializeJson(Dictionary<string, object> root)
        {
            var builder = new StringBuilder(16384);
            AppendJson(builder, root);
            return builder.ToString();
        }

        private static void AppendJson(StringBuilder builder, object value)
        {
            if (value == null) { builder.Append("null"); return; }
            var obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                builder.Append('{');
                bool first = true;
                foreach (var pair in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(Json.Escape(pair.Key)).Append(':');
                    AppendJson(builder, pair.Value);
                }
                builder.Append('}');
                return;
            }
            var array = value as List<object>;
            if (array != null)
            {
                builder.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    AppendJson(builder, array[i]);
                }
                builder.Append(']');
                return;
            }
            if (value is string) { builder.Append(Json.Escape((string)value)); return; }
            if (value is bool) { builder.Append((bool)value ? "true" : "false"); return; }
            if (value is double)
            {
                double number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number)) number = 0;
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is long) { builder.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is int) { builder.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is float) { builder.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            throw new InvalidDataException("无法序列化 glTF JSON 类型：" + value.GetType().FullName);
        }
    }
}
