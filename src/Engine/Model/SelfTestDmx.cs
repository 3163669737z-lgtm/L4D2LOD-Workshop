using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Datamodel;
using Datamodel.Codecs;
using DmxDocument = Datamodel.Datamodel;

namespace LodStudio.Engine
{
    internal static class SelfTestDmx
    {
        [SelfTest("DMX：批处理保留材质、蒙皮和表情模型，写出同格式 LOD")]
        public static void BatchRoundTrip(SelfTestContext ctx)
        {
            foreach (string encoding in new[] { "binary", "keyvalues2" })
            {
                string dir = ctx.NewDir("dmx_" + encoding);
                int version = encoding == "binary" ? 2 : 1;
                string source = Path.Combine(dir, "surface.dmx");
                string flex = Path.Combine(dir, "flex.dmx");
                WriteFixture(source, encoding, version, false);
                WriteFixture(flex, encoding, version, true);
                byte[] original = File.ReadAllBytes(source);
                byte[] facial = File.ReadAllBytes(flex);
                string qc = Path.Combine(dir, "main.qc");
                File.WriteAllText(qc,
                    "$modelname \"test/dmx.mdl\"\n$body \"surface\" \"surface.dmx\"\n" +
                    "$NekoModel \"facial\"\n{\n studio \"flex.dmx\"\n flex \"expression\"\n}\n");

                var options = new BatchRunOptions { Plan = new LodPlan(), AllowVertexUpdates = true, ReportDir = dir };
                options.Plan.Levels.Add(new LodLevel(60, 0.25));
                var plan = BatchPlanner.Build(new[] { new TaskRef(TaskKind.Qc, qc) }, dir, options,
                    NullObserver.Instance, CancellationToken.None);
                ctx.AssertEqual(2, plan.Models.Count, "普通 DMX 和 NekoModel 表情 DMX 都应被识别");
                ctx.AssertEqual(1, plan.JobCount, "只有普通 DMX 生成减面任务");
                ctx.Assert(plan.Models.Single(m => m.Path == flex).SkipReason.Contains("表情"), "保留表情增量");

                var outcome = BatchRunner.Run(plan, options, NullObserver.Instance, CancellationToken.None);
                ctx.Assert(outcome.Ok, string.Join("，", outcome.Errors));
                string lod = Path.Combine(dir, "surface_lod1.dmx");
                ctx.Assert(File.Exists(lod), "应生成 DMX LOD 文件");
                ctx.Assert(DmxLodSource.Read(lod).TriangleCount < DmxLodSource.Read(source).TriangleCount, "DMX 三角面应减少");
                ctx.AssertContains(File.ReadAllText(qc), "replacemodel \"surface.dmx\" \"surface_lod1.dmx\"", "QC 引用 DMX LOD");
                ctx.Assert(original.SequenceEqual(File.ReadAllBytes(source)), "源网格原样保留");
                ctx.Assert(facial.SequenceEqual(File.ReadAllBytes(flex)), "表情网格原样保留");
                using (var before = DmxDocument.Load(source, DeferredMode.Disabled))
                using (var after = DmxDocument.Load(lod, DeferredMode.Disabled))
                {
                    ctx.AssertEqual(encoding, after.Encoding, "保留 DMX 编码");
                    ctx.AssertEqual(version, after.EncodingVersion, "保留 DMX 编码版本");
                    Element vertices = before.AllElements.Single(e => e.ClassName == "DmeVertexData");
                    Element output = after.AllElements[vertices.ID];
                    ctx.Assert(vertices.Get<FloatArray>("jointWeights").SequenceEqual(output.Get<FloatArray>("jointWeights")),
                        "DMX 蒙皮权重原样保留");
                    ctx.Assert(vertices.Get<IntArray>("jointIndices").SequenceEqual(output.Get<IntArray>("jointIndices")),
                        "DMX 骨骼索引原样保留");
                    ctx.AssertEqual("fixture/material", after.AllElements.Single(e => e.ClassName == "DmeMaterial").Get<string>("mtlName"),
                        "DMX 材质原样保留");
                }
            }
        }

        private static void WriteFixture(string path, string encoding, int version, bool facial)
        {
            using (var document = new DmxDocument("model", 1))
            {
                const int columns = 9;
                var positions = new Vector3Array();
                var uvs = new Vector2Array();
                var weights = new FloatArray();
                var joints = new IntArray();
                for (int y = 0; y < columns; y++)
                    for (int x = 0; x < columns; x++)
                    {
                        positions.Add(new Vector3(x, y, 0));
                        uvs.Add(new Vector2(x / 8f, y / 8f));
                        weights.Add(0.73f);
                        weights.Add(0.27f);
                        joints.Add(0);
                        joints.Add(1);
                    }

                var faces = new IntArray();
                for (int y = 0; y < columns - 1; y++)
                    for (int x = 0; x < columns - 1; x++)
                    {
                        int a = y * columns + x;
                        int b = a + 1, c = b + columns, d = a + columns;
                        foreach (int index in new[] { a, b, c, -1, a, c, d, -1 }) faces.Add(index);
                    }

                var mesh = new Element(document, "surface", class_name: "DmeMesh");
                document.Root = mesh;
                var bind = new Element(document, "bind", class_name: "DmeVertexData");
                var set = new Element(document, "faces", class_name: "DmeFaceSet");
                var material = new Element(document, "material", class_name: "DmeMaterial");
                material["mtlName"] = "fixture/material";
                set["material"] = material;
                set["faces"] = faces;
                mesh["bindState"] = mesh["currentState"] = bind;
                mesh["baseStates"] = new ElementArray(new[] { bind });
                mesh["faceSets"] = new ElementArray(new[] { set });
                if (facial)
                    mesh["deltaStates"] = new ElementArray(new[] { new Element(document, "expression", class_name: "DmeVertexDeltaData") });

                bind["vertexFormat"] = new StringArray(new[] { "positions", "normals", "textureCoordinates", "jointWeights", "jointIndices" });
                bind["jointCount"] = 2;
                bind["positions"] = positions;
                bind["positionsIndices"] = new IntArray(Enumerable.Range(0, positions.Count));
                bind["normals"] = new Vector3Array(new[] { Vector3.UnitZ });
                bind["normalsIndices"] = new IntArray(Enumerable.Repeat(0, positions.Count));
                bind["textureCoordinates"] = uvs;
                bind["textureCoordinatesIndices"] = new IntArray(Enumerable.Range(0, uvs.Count));
                bind["jointWeights"] = weights;
                bind["jointIndices"] = joints;
                document.Save(path, encoding, version);
            }
        }
    }
}
