using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Datamodel;
using Datamodel.Codecs;
using DmxDocument = Datamodel.Datamodel;
using VertexKey = System.ValueTuple<System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector2,
    System.ValueTuple<ushort, ushort, ushort, ushort>, System.Numerics.Vector4>;

namespace LodStudio.Engine
{
    // The GLB is only a reduction proxy. DMX vertices, bindings and materials
    // remain in their native data model and only face-set indices are replaced.
    internal sealed class DmxLodSource
    {
        private sealed class FaceSet
        {
            public Guid Id;
            public int[] Corners;
            public Dictionary<VertexKey, int> SourceVertices;
        }

        private readonly byte[] sourceBytes;
        private readonly SmdModel proxy = new SmdModel();
        private readonly Dictionary<string, FaceSet> faceSets = new Dictionary<string, FaceSet>(StringComparer.Ordinal);
        public int TriangleCount => proxy.TriangleCount;
        internal double Extent => AutomaticLod.Extent(proxy);

        private DmxLodSource(byte[] sourceBytes)
        {
            this.sourceBytes = sourceBytes;
        }

        public static DmxLodSource Read(string path)
        {
            byte[] bytes = FileUtil.ReadAllBytesShared(path);
            using (var document = DmxDocument.Load(bytes, DeferredMode.Disabled))
            {
                if (!string.Equals(document.Format, "model", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("DMX 不是 model 格式");
                if (document.AllElements.Any(e => e.ClassName == "DmeVertexDeltaData"))
                    throw new NotSupportedException("DMX 含形态目标或表情增量");

                var source = new DmxLodSource(bytes);
                foreach (var mesh in document.AllElements.Where(e => e.ClassName == "DmeMesh"))
                    source.AddMesh(mesh);
                if (source.TriangleCount == 0)
                    throw new NotSupportedException("DMX 没有可处理的参考网格");
                return source;
            }
        }

        public byte[] CreateProxy()
        {
            byte[] bytes = SmdGlbCodec.SmdToGlb(proxy);
            foreach (var primitive in GlbCodec.ReadBytes(bytes, "DMX reduction proxy").Meshes.SelectMany(m => m.Primitives))
            {
                var faceSet = faceSets[primitive.MaterialName];
                if (primitive.Indices.Length != faceSet.Corners.Length)
                    throw new InvalidDataException("DMX 临时网格的索引数与源不一致");

                var vertices = new Dictionary<VertexKey, int>();
                for (int i = 0; i < primitive.Indices.Length; i++)
                    vertices[Key(primitive, primitive.Indices[i])] = faceSet.Corners[i];
                faceSet.SourceVertices = vertices;
            }
            return bytes;
        }

        internal static int CountReferenceTriangles(string path)
        {
            using (var document = DmxDocument.Load(path, DeferredMode.Disabled))
            {
                if (!string.Equals(document.Format, "model", StringComparison.OrdinalIgnoreCase)) return 0;
                int total = 0;
                foreach (var mesh in document.AllElements.Where(e => e.ClassName == "DmeMesh"))
                    foreach (var set in Get<ElementArray>(mesh, "faceSets") ?? new ElementArray())
                    {
                        if (set == null) continue;
                        var faces = Get<IntArray>(set, "faces");
                        if (faces == null) continue;
                        int corners = 0;
                        foreach (int index in faces)
                        {
                            if (index >= 0) corners++;
                            else { total += Math.Max(0, corners - 2); corners = 0; }
                        }
                        total += Math.Max(0, corners - 2);
                    }
                return total;
            }
        }

        public void WriteLod(string candidatePath, string destination)
        {
            var candidate = GlbCodec.Read(candidatePath);
            var reduced = candidate.Meshes.SelectMany(m => m.Primitives).ToDictionary(p => p.MaterialName, StringComparer.Ordinal);
            if (reduced.Count != faceSets.Count || candidate.TriangleCount >= TriangleCount)
                throw new InvalidDataException("DMX 减面候选缺失材质组或没有减少面数");

            using (var document = DmxDocument.Load(sourceBytes, DeferredMode.Disabled))
            {
                var elements = document.AllElements.Where(e => e.ClassName == "DmeFaceSet").ToDictionary(e => e.ID);
                foreach (var entry in faceSets)
                {
                    if (!reduced.TryGetValue(entry.Key, out var primitive) ||
                        primitive.Indices.Length == 0 || primitive.Indices.Length > entry.Value.Corners.Length)
                        throw new InvalidDataException("DMX 减面候选的面组不匹配");
                    if (primitive.Indices.Length == entry.Value.Corners.Length)
                        continue;

                    var faces = new IntArray(primitive.TriangleCount * 4);
                    for (int i = 0; i < primitive.Indices.Length; i++)
                    {
                        if (!entry.Value.SourceVertices.TryGetValue(Key(primitive, primitive.Indices[i]), out int corner))
                            throw new InvalidDataException("DMX 减面候选含无法映射回原网格的顶点");
                        faces.Add(corner);
                        if (i % 3 == 2) faces.Add(-1);
                    }
                    elements[entry.Value.Id]["faces"] = faces;
                }

                using (var stream = new MemoryStream())
                {
                    document.Save(stream, document.Encoding, document.EncodingVersion);
                    FileUtil.WriteAtomic(destination, stream.ToArray());
                }
            }
        }

        private void AddMesh(Element mesh)
        {
            if (Get<ElementArray>(mesh, "deltaStates")?.Count > 0)
                throw new NotSupportedException("DMX 含形态目标或表情增量");
            var bind = Get<Element>(mesh, "bindState");
            var states = Get<ElementArray>(mesh, "baseStates");
            var current = Get<Element>(mesh, "currentState");
            if (bind == null || (states != null && states.Any(s => s?.ID != bind.ID)) ||
                (current != null && current.ID != bind.ID))
                throw new NotSupportedException("DMX 缺少唯一的绑定网格");

            var format = Require<StringArray>(bind, "vertexFormat");
            string positions = format.Contains("positions") ? "positions" : "position$0";
            string normals = format.Contains("normals") ? "normals" : "normal$0";
            string uvs = format.Contains("textureCoordinates") ? "textureCoordinates" : "texcoord$0";
            string weights = format.Contains("jointWeights") ? "jointWeights" : "blendweights$0";
            string joints = format.Contains("jointIndices") ? "jointIndices" : "blendindices$0";
            if (format.Any(name => name != positions && name != normals && name != uvs &&
                                   name != weights && name != joints && name != "balance" && name != "balance$0"))
                throw new NotSupportedException("DMX 含当前减面器无法验证的顶点通道");

            var positionData = Require<Vector3Array>(bind, positions);
            var positionIndices = Require<IntArray>(bind, positions + "Indices");
            var normalData = Get<Vector3Array>(bind, normals);
            var normalIndices = Get<IntArray>(bind, normals + "Indices");
            var uvData = Get<Vector2Array>(bind, uvs);
            var uvIndices = Get<IntArray>(bind, uvs + "Indices");
            int jointCount = Get(bind, "jointCount", 0);
            var weightData = Get<FloatArray>(bind, weights);
            var jointData = Get<IntArray>(bind, joints);
            if (jointCount < 0 || jointCount > 4 ||
                (jointCount == 0 && (weightData?.Count > 0 || jointData?.Count > 0)) ||
                (jointCount > 0 && (weightData?.Count != (long)positionData.Count * jointCount ||
                                    jointData?.Count != weightData.Count)) ||
                ((normalData == null) != (normalIndices == null)) ||
                ((uvData == null) != (uvIndices == null)))
                throw new NotSupportedException("DMX 顶点通道或蒙皮布局暂未支持");

            var vertices = new SmdVertex[positionIndices.Count];
            for (int i = 0; i < vertices.Length; i++)
            {
                int positionIndex = positionIndices[i];
                Vector3 p = CheckedAt(positionData, positionIndex);
                Vector3 n = normalData == null ? Vector3.Zero : CheckedAt(normalData, CheckedAt(normalIndices, i));
                Vector2 uv = uvData == null ? Vector2.Zero : CheckedAt(uvData, CheckedAt(uvIndices, i));
                var vertex = new SmdVertex { X = p.X, Y = p.Y, Z = p.Z, Nx = n.X, Ny = n.Y, Nz = n.Z, U = uv.X, V = uv.Y };
                for (int j = 0; j < jointCount; j++)
                {
                    int index = positionIndex * jointCount + j;
                    float weight = weightData[index];
                    if (!Finite(weight) || weight < 0) throw new InvalidDataException("DMX 含无效蒙皮权重");
                    if (weight == 0) continue;
                    int joint = jointData[index];
                    if (joint < 0 || joint > ushort.MaxValue) throw new InvalidDataException("DMX 含无效骨骼索引");
                    while (proxy.Bones.Count <= joint)
                    {
                        int next = proxy.Bones.Count;
                        proxy.Bones.Add(new SmdBone { Index = next, Name = "joint_" + next, Parent = -1 });
                    }
                    vertex.Weights.Add(new SmdWeight { BoneIndex = joint, Weight = weight });
                }
                if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z) || !Finite(n.X) || !Finite(n.Y) || !Finite(n.Z) ||
                    !Finite(uv.X) || !Finite(uv.Y))
                    throw new InvalidDataException("DMX 含无效顶点数值");
                vertices[i] = vertex;
            }

            foreach (var set in Require<ElementArray>(mesh, "faceSets"))
            {
                if (set == null || faceSets.Values.Any(s => s.Id == set.ID))
                    throw new NotSupportedException("DMX 含共享或缺失的材质面组");
                int[] corners = Triangulate(Require<IntArray>(set, "faces"), vertices);
                if (corners.Length == 0) continue;

                string material = "dmx_faces_" + faceSets.Count.ToString(CultureInfo.InvariantCulture);
                var faceSet = new FaceSet { Id = set.ID, Corners = corners };
                faceSets.Add(material, faceSet);
                for (int i = 0; i < corners.Length; i += 3)
                    proxy.Triangles.Add(new SmdTriangle
                    {
                        Material = material, V0 = vertices[corners[i]], V1 = vertices[corners[i + 1]], V2 = vertices[corners[i + 2]]
                    });
            }
        }

        private static int[] Triangulate(IntArray faces, SmdVertex[] vertices)
        {
            var triangles = new List<int>();
            int start = 0;
            for (int end = 0; end <= faces.Count; end++)
            {
                if (end < faces.Count && faces[end] >= 0)
                {
                    if (faces[end] >= vertices.Length) throw new InvalidDataException("DMX 面索引越界");
                    continue;
                }
                if (end < faces.Count && faces[end] != -1)
                    throw new InvalidDataException("DMX 含无效面分隔符");

                int count = end - start;
                if (count != 0 && count < 3) throw new InvalidDataException("DMX 面少于三个顶点");
                if (count > 3)
                {
                    Vector3 Position(int at)
                    {
                        var v = vertices[faces[start + at % count]];
                        return new Vector3(v.X, v.Y, v.Z);
                    }
                    var normal = Vector3.Cross(Position(1) - Position(0), Position(2) - Position(0));
                    if (normal.LengthSquared() == 0) throw new NotSupportedException("DMX 含退化多边形");
                    for (int i = 1; i < count; i++)
                    {
                        var turn = Vector3.Cross(Position(i + 1) - Position(i), Position(i + 2) - Position(i));
                        if (Vector3.Dot(normal, turn) < 0)
                            throw new NotSupportedException("DMX 含凹多边形，需先三角化");
                    }
                }
                for (int j = start + 1; j + 1 < end; j++)
                {
                    triangles.Add(faces[start]);
                    triangles.Add(faces[j]);
                    triangles.Add(faces[j + 1]);
                }
                start = end + 1;
            }
            return triangles.ToArray();
        }

        private static VertexKey Key(GlbPrimitive p, int i)
        {
            int a = i * 3, b = i * 2, c = i * 4;
            return new VertexKey(
                new Vector3(p.Positions[a], p.Positions[a + 1], p.Positions[a + 2]),
                p.Normals == null ? Vector3.Zero : new Vector3(p.Normals[a], p.Normals[a + 1], p.Normals[a + 2]),
                new Vector2(p.Uvs[b], p.Uvs[b + 1]),
                p.Joints == null ? default : (p.Joints[c], p.Joints[c + 1], p.Joints[c + 2], p.Joints[c + 3]),
                p.Weights == null ? Vector4.Zero : new Vector4(p.Weights[c], p.Weights[c + 1], p.Weights[c + 2], p.Weights[c + 3]));
        }

        private static T CheckedAt<T>(IList<T> values, int index) =>
            index >= 0 && index < values.Count ? values[index] : throw new InvalidDataException("DMX 顶点索引越界");

        private static T Require<T>(Element element, string name) where T : class =>
            Get<T>(element, name) ?? throw new InvalidDataException("DMX 缺少 " + name + " 数据");

        private static T Get<T>(Element element, string name, T fallback = default) =>
            element.TryGetValue(name, out var value) && value is T typed ? typed : fallback;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
