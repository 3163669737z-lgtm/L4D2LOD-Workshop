using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LodStudio.Engine
{
    internal static class AutomaticLod
    {
        internal const int ReferenceTriangles = 10000;
        internal const double MinimumSavings = 0.10;
        private const double HeightPixels = 1080;
        private const double FocalPixels = HeightPixels / 2 / 0.5773502691896257; // 60° vertical FOV

        internal static LodPlan Plan(long sourceTriangles)
        {
            var plan = new LodPlan();
            int count = Math.Min(LodPlan.MaxLevels, (int)Math.Ceiling(Math.Log(Math.Max(1, sourceTriangles / (double)ReferenceTriangles), 2)));
            if (count == 0) return plan;
            // Approach the reference budget in equal proportional steps, while
            // keeping successive transitions within a practical reduction range.
            double step = Math.Max(0.55, Math.Min(0.85, Math.Pow(ReferenceTriangles / (double)sourceTriangles, 1.0 / count)));
            for (int i = 0; i < count; ++i)
                plan.Levels.Add(new LodLevel(15 + 15 * i, Math.Pow(step, i + 1)));
            return plan;
        }

        internal static long CountVisible(BatchQcPlan qc, Func<PlannedModel, int> triangles)
        {
            var models = qc.Models.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
            var alternatives = new Dictionary<int, int>();
            long total = 0;
            foreach (var entry in qc.Info.Studios)
            {
                if (entry.ResolvedPath == null || !models.TryGetValue(entry.ResolvedPath, out var model)) continue;
                int count = entry.FixedTriangleCount ?? triangles(model);
                if (entry.BodygroupIndex < 0) total += count;
                else {
                    alternatives.TryGetValue(entry.BodygroupIndex, out int previous);
                    alternatives[entry.BodygroupIndex] = Math.Max(count, previous);
                }
            }
            return total + alternatives.Values.Sum(n => (long)n);
        }

        internal static int? NodeTriangles(GlbModel model, string name)
        {
            var stack = new Stack<int>();
            for (int i = 0; i < model.Nodes.Count; ++i)
                if (string.Equals(model.Nodes[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    stack.Push(i);
            if (stack.Count == 0) return null;

            var visited = new HashSet<int>();
            int count = 0;
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                if (!visited.Add(index)) continue;
                var node = model.Nodes[index];
                if (node.Mesh is int mesh && mesh >= 0 && mesh < model.Meshes.Count)
                    count += model.Meshes[mesh].Primitives.Sum(p => p.TriangleCount);
                foreach (int child in node.Children)
                    if (child >= 0 && child < model.Nodes.Count) stack.Push(child);
            }
            return count;
        }

        internal static double Extent(SmdModel model)
        {
            var bounds = new Extents();
            foreach (var t in model.Triangles)
            {
                bounds.Add(new Vector3(t.V0.X, t.V0.Y, t.V0.Z));
                bounds.Add(new Vector3(t.V1.X, t.V1.Y, t.V1.Z));
                bounds.Add(new Vector3(t.V2.X, t.V2.Y, t.V2.Z));
            }
            return bounds.Size;
        }

        internal static double GlbExtent(GlbModel model)
        {
            var bounds = new Extents();
            var parents = Enumerable.Repeat(-1, model.Nodes.Count).ToArray();
            for (int i = 0; i < model.Nodes.Count; ++i)
                foreach (int child in model.Nodes[i].Children)
                    if (child >= 0 && child < parents.Length) parents[child] = i;

            void AddMesh(GlbMesh mesh, Matrix4x4 transform)
            {
                foreach (var p in mesh.Primitives)
                    foreach (int index in p.Indices)
                    {
                        int j = index * 3;
                        bounds.Add(Vector3.Transform(new Vector3(p.Positions[j], p.Positions[j + 1], p.Positions[j + 2]), transform));
                    }
            }

            bool hasInstances = false;
            for (int i = 0; i < model.Nodes.Count; ++i)
            {
                int mesh = model.Nodes[i].Mesh ?? -1;
                if (mesh < 0 || mesh >= model.Meshes.Count) continue;
                hasInstances = true;
                var transform = Matrix4x4.Identity;
                int depth = 0;
                for (int node = i; node >= 0; node = parents[node])
                {
                    if (++depth > parents.Length) throw new System.IO.InvalidDataException("GLB 节点存在循环引用");
                    transform *= LocalTransform(model.Nodes[node]);
                }
                AddMesh(model.Meshes[mesh], transform);
            }
            if (!hasInstances)
                foreach (var mesh in model.Meshes) AddMesh(mesh, Matrix4x4.Identity);
            return bounds.Size;
        }

        private static Matrix4x4 LocalTransform(GlbNode node)
        {
            var m = node.Matrix;
            if (m != null) return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                                                 m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            var s = node.Scale;
            var r = node.Rotation;
            var t = node.Translation;
            return (s == null ? Matrix4x4.Identity : Matrix4x4.CreateScale(s[0], s[1], s[2])) *
                   (r == null ? Matrix4x4.Identity : Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3]))) *
                   (t == null ? Matrix4x4.Identity : Matrix4x4.CreateTranslation(t[0], t[1], t[2]));
        }

        internal static void Estimate(AutomaticLodResult level, double extentHu, long sourceTriangles, double previousSwitch)
        {
            // Budget estimate until the reducer exposes measured surface and animation error.
            double reduction = 1 - level.Triangles / (double)sourceTriangles;
            double errorHu = Math.Max(1, extentHu) * (0.005 + 0.015 * reduction);
            double distance = Math.Max(150, Math.Min(1500, errorHu * FocalPixels / 2)); // two pixels at reference resolution
            // Source's switch point is the inverse projected size metric for a unit sphere.
            level.SwitchPoint = Math.Round(Math.Max(previousSwitch * 1.35, 100 * distance / (2 * FocalPixels)), 3);
            level.EstimatedDistanceHu = Math.Round(level.SwitchPoint * 2 * FocalPixels / 100, 1);
        }

        private sealed class Extents
        {
            private Vector3 low = new Vector3(float.PositiveInfinity);
            private Vector3 high = new Vector3(float.NegativeInfinity);
            internal void Add(Vector3 p) { low = Vector3.Min(low, p); high = Vector3.Max(high, p); }
            internal double Size
            {
                get
                {
                    double size = (high - low).Length();
                    return double.IsNaN(size) || double.IsInfinity(size) ? 0 : size;
                }
            }
        }
    }
}
