using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// 模型编解码模块自测（SMD / GLB / SMD↔GLB）。
    ///
    /// 运行：<c>L4D2LOD远景工坊.exe --selftest --filter 模型</c>
    /// 所有临时文件只写 ctx.NewDir(...) 返回的目录。
    /// </summary>
    internal static class SelfTestModel
    {
        private const double Geo = 1e-4;          // 位置/法线/UV 容差
        private const double WeightTolerance = 1e-4;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ══════════════════════════════════════════════════════════════════
        //  1. SMD 文本往返（多骨骼 / 多材质 / 权重）
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：SMD 文本往返（多骨骼/多材质/权重）")]
        public static void SmdTextRoundTrip(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("smd_roundtrip");
            var model = BuildSkinned();

            byte[] bytes = SmdCodec.WriteBytes(model);
            ctx.Assert(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                       "SMD 写出不应带 UTF-8 BOM");
            var parsed = SmdCodec.ParseBytes(bytes, Path.Combine(dir, "model.smd"));
            AssertSmdEquivalent(ctx, model, parsed, "WriteBytes→ParseBytes");
            ctx.AssertEqual(model.TriangleCount, parsed.TriangleCount, "TriangleCount 属性");
            ctx.AssertEqual(0, parsed.Warnings.Count, "合法 SMD 往返不应产生解析警告");

            // 文本格式：四段齐全、CRLF、至少 6 位小数、links 数量显式写出（含 0）
            string text = Encoding.UTF8.GetString(bytes);
            ctx.AssertContains(text, "version 1", "version 行");
            ctx.AssertContains(text, "nodes\r\n", "nodes 段");
            ctx.AssertContains(text, "skeleton\r\ntime 0\r\n", "skeleton 段带 time 0");
            ctx.AssertContains(text, "triangles\r\n", "triangles 段");
            ctx.AssertContains(text, "\r\nend\r\n", "段结束行");
            ctx.AssertContains(text, "0.000000", "浮点至少 6 位小数");
            ctx.AssertContains(text,
                "0 0.000000 0.000000 0.000000 0.000000 0.000000 1.000000 0.000000 0.000000 1 0 1.000000",
                "顶点行格式（主骨/坐标/法线/UV/links 数量/权重）；links 数量为 0 时也要写");
            ctx.Assert(parsed.HasSkinWeights, "全绑 bone 0 的顶点权重必须保留（旧版 A1 缺陷回归）");
            ctx.AssertEqual(model.SkeletonPose.Count, parsed.SkeletonPose.Count, "skeleton 姿态行数透传");

            // 文件写入 + Parse(path)
            string path = Path.Combine(dir, "model.smd");
            SmdCodec.Write(path, model);
            ctx.Assert(File.Exists(path), "SmdCodec.Write 应写出文件");
            var fromFile = SmdCodec.Parse(path);
            AssertSmdEquivalent(ctx, model, fromFile, "Write→Parse(path)");

            // 重新写出应逐字节稳定（幂等）
            byte[] again = SmdCodec.WriteBytes(fromFile);
            ctx.AssertEqual(bytes.Length, again.Length, "SMD 写出幂等（字节长度）");
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != again[i])
                {
                    ctx.Assert(false, "SMD 写出幂等失败：第 " + i + " 字节不一致");
                    break;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  2. SMD 解析容错
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：SMD 解析容错（9 字段/空白/坏行/CRLF/LF）")]
        public static void SmdTolerance(SelfTestContext ctx)
        {
            // A) LF + 9 字段顶点（无 links 数量）+ Tab/多余空格 + triangles 在 nodes 前
            string lf = "version 1\n" +
                        "triangles\n" +
                        "models/a\n" +
                        " 0\t0 0 0\t0 0 1\t0 0\n" +
                        "0   1 0 0  0 0 1   1 0\n" +
                        "0 0 1 0 0 0 1 0 1\n" +
                        "end\n" +
                        "nodes\n" +
                        "0 \"root\" -1\n" +
                        "end\n";
            var a = SmdCodec.ParseBytes(Encoding.UTF8.GetBytes(lf), "lf.smd");
            ctx.AssertEqual(1, a.TriangleCount, "LF/9 字段顶点：三角面数");
            ctx.AssertEqual(1, a.Bones.Count, "LF：骨骼数");
            ctx.AssertEqual(0, a.Warnings.Count, "LF/9 字段顶点不应产生警告");
            ctx.AssertEqual(0, a.Triangles[0].V0.Weights.Count, "9 字段顶点应解析为无权重");
            ctx.AssertEqual(1.0, a.Triangles[0].V1.X, Geo, "9 字段顶点坐标");

            // B) CRLF + 坏骨骼行 + 材质与顶点之间夹空行/注释 + links 声明 2 但只给 1 组
            string crlf = "version 1\r\n" +
                          "nodes\r\n" +
                          "0 \"root\" -1\r\n" +
                          "this line is broken\r\n" +
                          "2 \"arm\" 0\r\n" +
                          "end\r\n" +
                          "skeleton\r\n" +
                          "time 0\r\n" +
                          "0 0 0 0 0 0\r\n" +
                          "end\r\n" +
                          "triangles\r\n" +
                          "models/a\r\n" +
                          "\r\n" +
                          "// 注释：材质与顶点之间\r\n" +
                          "  2 0 0 0 0 0 1 0 0 2 0 0.5\r\n" +
                          "  2 1 0 0 0 0 1 1 0\r\n" +
                          "  2 0 1 0 0 0 1 0 1 0\r\n" +
                          "models/b\r\n" +
                          "  2 5 5 5 0 0 1 0 0 1\r\n" +
                          "  2 6 5 5 0 0 1 1 0 1\r\n" +
                          "  2 5 6 5 0 0 1 0 1 1\r\n" +
                          "end\r\n";
            var b = SmdCodec.ParseBytes(Encoding.UTF8.GetBytes(crlf), "crlf.smd");
            ctx.AssertEqual(2, b.Bones.Count, "坏骨骼行应被跳过，其余骨骼保留");
            ctx.AssertEqual(2, b.TriangleCount, "坏行不应连累后续三角形");
            ctx.Assert(b.Warnings.Count >= 2, "坏行/links 不足应记录警告（实际 " + b.Warnings.Count + " 条）");
            ctx.AssertContains(string.Join(" | ", b.Warnings.ToArray()), "格式错误", "nodes 坏行警告");
            ctx.AssertContains(string.Join(" | ", b.Warnings.ToArray()), "权重", "links 数量不足警告");
            ctx.AssertEqual(1, b.Triangles[0].V0.Weights.Count, "links 声明 2 实际 1 组：应读取 1 组");
            ctx.AssertEqual(0.5, b.Triangles[0].V0.Weights[0].Weight, WeightTolerance, "links 不足时权重值");
            ctx.AssertEqual(0, b.Triangles[0].V1.Weights.Count, "9 字段顶点权重为空");
            ctx.AssertEqual(2, b.Bones[1].Index, "骨骼 Index 保留");

            // C) 坏顶点行（字段不足）+ 不完整三角形：跳过并警告，后续三角形仍能解析
            string badVertices = "version 1\r\n" +
                                 "nodes\r\n0 \"root\" -1\r\nend\r\n" +
                                 "triangles\r\n" +
                                 "models/c\r\n" +
                                 "2 0 0 0 0 0 1 0\r\n" +
                                 "2 1 0 0 0 0 1 1 0\r\n" +
                                 "2 0 1 0 0 0 1 0 1\r\n" +
                                 "models/d\r\n" +
                                 "2 5 5 5 0 0 1 0 0\r\n" +
                                 "2 6 5 5 0 0 1 1 0\r\n" +
                                 "2 5 6 5 0 0 1 0 1\r\n" +
                                 "end\r\n";
            var c = SmdCodec.ParseBytes(Encoding.UTF8.GetBytes(badVertices), "badverts.smd");
            ctx.AssertEqual(1, c.TriangleCount, "坏顶点行后应继续解析出完整三角形");
            string cwarn = string.Join(" | ", c.Warnings.ToArray());
            ctx.AssertContains(cwarn, "顶点格式错误", "坏顶点行警告");
            ctx.AssertContains(cwarn, "不完整", "不完整三角形警告");

            // D) nodes 段缺 end：遇到 triangles 段头应自动恢复，不吞掉三角形
            string missingEnd = "version 1\r\n" +
                                "nodes\r\n" +
                                "0 \"root\" -1\r\n" +
                                "triangles\r\n" +
                                "models/e\r\n" +
                                "2 0 0 0 0 0 1 0 0\r\n" +
                                "2 1 0 0 0 0 1 1 0\r\n" +
                                "2 0 1 0 0 0 1 0 1\r\n" +
                                "end\r\n";
            var d = SmdCodec.ParseBytes(Encoding.UTF8.GetBytes(missingEnd), "missingend.smd");
            ctx.AssertEqual(1, d.TriangleCount, "缺 end 时遇到段头应自动恢复并解析出三角形");
            ctx.AssertEqual(1, d.Bones.Count, "缺 end 时骨骼仍解析");
            ctx.AssertContains(string.Join(" | ", d.Warnings.ToArray()), "缺少 end", "缺 end 警告");

            // E) UTF-8 BOM 与 GBK（ANSI）两种编码 + 中文材质/骨骼名
            const string cn = "version 1\r\nnodes\r\n0 \"骨骼根\" -1\r\nend\r\ntriangles\r\n材质/贴图\r\n" +
                              "0 0 0 0 0 0 1 0 0 0\r\n0 1 0 0 0 0 1 1 0 0\r\n0 0 1 0 0 0 1 0 1 0\r\nend\r\n";
            byte[] utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] bomBytes = new byte[utf8Bom.Length + Encoding.UTF8.GetByteCount(cn)];
            Array.Copy(utf8Bom, bomBytes, 3);
            Array.Copy(Encoding.UTF8.GetBytes(cn), 0, bomBytes, 3, bomBytes.Length - 3);
            var bomModel = SmdCodec.ParseBytes(bomBytes, "bom.smd");
            ctx.AssertEqual("骨骼根", bomModel.Bones[0].Name, "UTF-8 BOM：中文骨骼名");
            ctx.AssertEqual("材质/贴图", bomModel.Triangles[0].Material, "UTF-8 BOM：中文材质名");
            byte[] gbkBytes = Encoding.GetEncoding(936).GetBytes(cn);
            var gbkModel = SmdCodec.ParseBytes(gbkBytes, "gbk.smd");
            ctx.AssertEqual("骨骼根", gbkModel.Bones[0].Name, "GBK(936)：中文骨骼名");
            ctx.AssertEqual("材质/贴图", gbkModel.Triangles[0].Material, "GBK(936)：中文材质名");
        }

        // ══════════════════════════════════════════════════════════════════
        //  3. GLB 容器结构与自校验
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：GLB 容器结构与自校验（魔数/版本/对齐/CountTriangles）")]
        public static void GlbStructure(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("glb_structure");
            var model = BuildSkinned();
            byte[] glb = GlbCodec.WriteFromSmd(model);

            ctx.AssertEqual((int)0x46546C67, (int)BitConverter.ToUInt32(glb, 0), "GLB magic 'glTF'");
            ctx.AssertEqual(2, BitConverter.ToInt32(glb, 4), "GLB version 2");
            ctx.AssertEqual(glb.Length, (int)BitConverter.ToUInt32(glb, 8), "GLB totalLength 与实际长度一致");

            int jsonLength = BitConverter.ToInt32(glb, 12);
            ctx.AssertEqual(0, jsonLength % 4, "JSON chunk 长度 4 字节对齐");
            ctx.AssertEqual((int)0x4E4F534A, (int)BitConverter.ToUInt32(glb, 16), "JSON chunk 类型");
            int binHeader = 20 + jsonLength;
            int binLength = BitConverter.ToInt32(glb, binHeader);
            ctx.AssertEqual(0, binLength % 4, "BIN chunk 长度 4 字节对齐");
            ctx.AssertEqual((int)0x004E4942, (int)BitConverter.ToUInt32(glb, binHeader + 4), "BIN chunk 类型");

            // JSON 结构断言
            var container = GlbJson.SplitGlb(glb, "selftest");
            var root = container.Root;
            ctx.AssertEqual("2.0", GlbJson.Str(GlbJson.Obj(root, "asset"), "version"), "asset.version");
            var buffers = GlbJson.Arr(root, "buffers");
            ctx.Assert(buffers != null && buffers.Count == 1, "应只有 1 个 buffer");
            ctx.AssertEqual(binLength, GlbJson.Int(buffers[0] as Dictionary<string, object>, "byteLength", -1),
                            "buffer.byteLength 与 BIN chunk 长度一致");

            var accessors = GlbJson.Arr(root, "accessors");
            var meshes = GlbJson.Arr(root, "meshes");
            ctx.Assert(meshes != null && meshes.Count == 1, "应只有 1 个 mesh");
            var primitives = GlbJson.Arr(GlbJson.ObjOf(meshes[0]), "primitives");
            ctx.AssertEqual(2, primitives.Count, "按材质分 2 个 primitive");

            int materialCount = 0;
            foreach (var primitive in primitives)
            {
                var prim = GlbJson.ObjOf(primitive);
                var attributes = GlbJson.Obj(prim, "attributes");
                int positionIndex = GlbJson.Int(attributes, "POSITION", -1);
                ctx.Assert(positionIndex >= 0, "primitive 必须有 POSITION");
                var positionAccessor = GlbJson.ObjOf(accessors[positionIndex]);
                ctx.Assert(GlbJson.Arr(positionAccessor, "min") != null &&
                           GlbJson.Arr(positionAccessor, "max") != null,
                           "POSITION accessor 必须写 min/max（glTF 规范强制）");
                ctx.AssertEqual(3, GlbJson.Arr(positionAccessor, "min").Count, "POSITION min 是 VEC3");
                int jointsIndex = GlbJson.Int(attributes, "JOINTS_0", -1);
                int weightsIndex = GlbJson.Int(attributes, "WEIGHTS_0", -1);
                ctx.Assert(jointsIndex >= 0 && weightsIndex >= 0, "蒙皮模型必须写 JOINTS_0/WEIGHTS_0");
                ctx.AssertEqual(5123, GlbJson.Int(GlbJson.ObjOf(accessors[jointsIndex]), "componentType", -1),
                                "JOINTS_0 用 UNSIGNED_SHORT(5123)");
                ctx.AssertEqual(5126, GlbJson.Int(GlbJson.ObjOf(accessors[weightsIndex]), "componentType", -1),
                                "WEIGHTS_0 用 FLOAT(5126)");
                int materialIndex = GlbJson.Int(prim, "material", -1);
                ctx.Assert(materialIndex >= 0, "primitive 必须引用材质名");
                materialCount++;
            }
            ctx.AssertEqual(2, materialCount, "材质数");

            var skins = GlbJson.Arr(root, "skins");
            ctx.Assert(skins != null && skins.Count == 1, "蒙皮模型应有 1 个 skin");
            var skin = GlbJson.ObjOf(skins[0]);
            var skinJoints = GlbJson.IntArray(skin, "joints", "selftest");
            ctx.Assert(skinJoints != null && skinJoints.Length == model.Bones.Count, "skin.joints 数量");
            for (int i = 0; i < skinJoints.Length; i++)
                ctx.AssertEqual(i, skinJoints[i], "skin.joints 应指向骨骼节点（列表位置 " + i + "）");
            int ibmIndex = GlbJson.Int(skin, "inverseBindMatrices", -1);
            ctx.Assert(ibmIndex >= 0, "应写 inverseBindMatrices");
            ctx.AssertEqual("MAT4", GlbJson.Str(GlbJson.ObjOf(accessors[ibmIndex]), "type"), "IBM accessor 类型");
            ctx.AssertEqual(model.Bones.Count, GlbJson.Int(GlbJson.ObjOf(accessors[ibmIndex]), "count", -1),
                            "IBM 矩阵数量 = 骨骼数");

            // 写出后自校验（WriteFromSmd 内部已做）+ 显式读回
            var readBack = GlbCodec.ReadBytes(glb, "selftest");
            ctx.AssertEqual(model.TriangleCount, readBack.TriangleCount, "读回三角面数");
            ctx.AssertEqual(1, readBack.Skins.Count, "读回 skin 数");
            ctx.AssertEqual(model.Bones.Count, readBack.Skins[0].Joints.Length, "读回 joints 数");
            ctx.Assert(readBack.Skins[0].InverseBindMatrices != null, "读回 IBM");
            ctx.Assert(readBack.Meshes[0].Primitives[0].Joints != null, "读回 JOINTS_0");
            ctx.Assert(readBack.Meshes[0].Primitives[0].Weights != null, "读回 WEIGHTS_0");
            ctx.AssertEqual(0, readBack.Warnings.Count, "读取自己写出的 GLB 不应有警告");

            // 落盘 → Read(path) / CountTriangles(path)
            string path = Path.Combine(dir, "model.glb");
            File.WriteAllBytes(path, glb);
            var fromFile = GlbCodec.Read(path);
            ctx.AssertEqual(model.TriangleCount, fromFile.TriangleCount, "Read(path) 三角面数");
            ctx.AssertEqual(model.TriangleCount, GlbCodec.CountTriangles(path), "CountTriangles 与展开后一致");

            // 静态模型：无 skin / 无 JOINTS_0，CountTriangles 正确
            var staticModel = BuildStatic();
            byte[] staticGlb = GlbCodec.WriteFromSmd(staticModel);
            string staticPath = Path.Combine(dir, "static.glb");
            File.WriteAllBytes(staticPath, staticGlb);
            var staticRead = GlbCodec.Read(staticPath);
            ctx.AssertEqual(0, staticRead.Skins.Count, "静态模型不应有 skin");
            ctx.Assert(staticRead.Meshes[0].Primitives[0].Joints == null, "静态模型不应写 JOINTS_0");
            ctx.AssertEqual(1, GlbCodec.CountTriangles(staticPath), "静态模型 CountTriangles");
        }

        // ══════════════════════════════════════════════════════════════════
        //  4. SMD → GLB → SMD 往返（几何 / UV / 权重 / 骨骼索引）
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：SMD→GLB→SMD 往返（几何/UV/权重/骨骼索引）")]
        public static void SmdGlbRoundTrip(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("smd_glb_roundtrip");
            var original = BuildSkinned();

            byte[] glb = GlbCodec.WriteFromSmd(original);
            var read = GlbCodec.ReadBytes(glb, "roundtrip.glb");
            var back = SmdGlbCodec.GlbToSmd(read, original);

            AssertBonesEquivalent(ctx, original, back);
            ctx.AssertEqual(original.TriangleCount, back.TriangleCount, "往返三角面数");
            for (int t = 0; t < original.TriangleCount; t++)
            {
                ctx.AssertEqual(original.Triangles[t].Material, back.Triangles[t].Material, "三角形[" + t + "]材质");
                AssertVertexGeometry(ctx, original.Triangles[t].V0, back.Triangles[t].V0, "三角形[" + t + "].V0");
                AssertVertexGeometry(ctx, original.Triangles[t].V1, back.Triangles[t].V1, "三角形[" + t + "].V1");
                AssertVertexGeometry(ctx, original.Triangles[t].V2, back.Triangles[t].V2, "三角形[" + t + "].V2");
                AssertWeightsEquivalent(ctx, original.Triangles[t].V0, back.Triangles[t].V0, "三角形[" + t + "].V0 权重");
                AssertWeightsEquivalent(ctx, original.Triangles[t].V1, back.Triangles[t].V1, "三角形[" + t + "].V1 权重");
                AssertWeightsEquivalent(ctx, original.Triangles[t].V2, back.Triangles[t].V2, "三角形[" + t + "].V2 权重");
            }
            ctx.Assert(back.HasSkinWeights, "全绑 bone 0 的蒙皮模型往返后必须仍识别为蒙皮");
            AssertWeightsNormalized(ctx, back, "往返结果");

            // 再把结果写成 SMD 并重新解析（验证 SMD 写出精度与 links 格式）
            string backPath = Path.Combine(dir, "back.smd");
            SmdCodec.Write(backPath, back);
            var reparsed = SmdCodec.Parse(backPath);
            ctx.AssertEqual(original.TriangleCount, reparsed.TriangleCount, "回写 SMD 后三角面数");
            AssertBonesEquivalent(ctx, original, reparsed);
            for (int t = 0; t < original.TriangleCount; t++)
            {
                AssertVertexGeometry(ctx, original.Triangles[t].V0, reparsed.Triangles[t].V0, "回写 SMD 三角形[" + t + "].V0");
                AssertWeightsEquivalent(ctx, original.Triangles[t].V0, reparsed.Triangles[t].V0, "回写 SMD 三角形[" + t + "].V0 权重");
            }

            // 静态模型往返：无权重、骨骼表保留
            var staticModel = BuildStatic();
            var staticRead = GlbCodec.ReadBytes(GlbCodec.WriteFromSmd(staticModel), "static.glb");
            var staticBack = SmdGlbCodec.GlbToSmd(staticRead, staticModel);
            AssertBonesEquivalent(ctx, staticModel, staticBack);
            ctx.AssertEqual(staticModel.TriangleCount, staticBack.TriangleCount, "静态模型往返三角数");
            ctx.Assert(!staticBack.HasSkinWeights, "静态模型往返不应产生权重");
            AssertVertexGeometry(ctx, staticModel.Triangles[0].V0, staticBack.Triangles[0].V0, "静态模型 V0");

            // 蒙皮模型中“无权重但有主骨骼字段”的顶点：GLB 里按主骨骼绑定（权重 1），保证 Blender 不丢绑定
            var mixed = BuildSkinned();
            mixed.Triangles[0].V0.Weights.Clear();
            mixed.Triangles[0].V0.Bone = 2;
            var mixedBack = SmdGlbCodec.GlbToSmd(
                GlbCodec.ReadBytes(GlbCodec.WriteFromSmd(mixed), "mixed.glb"), mixed);
            var mixedVertex = mixedBack.Triangles[0].V0;
            ctx.AssertEqual(1, mixedVertex.Weights.Count, "无权重顶点在 GLB 中应按主骨骼绑定");
            ctx.AssertEqual(2, mixedVertex.Weights[0].BoneIndex, "无权重顶点绑定到顶点行第一个字段的骨骼");
            ctx.AssertEqual(1.0, mixedVertex.Weights[0].Weight, WeightTolerance, "无权重顶点绑定权重为 1");

            // 超过 4 个影响：截断到 4 并重新归一化
            var six = BuildSixInfluence();
            var sixBack = SmdGlbCodec.GlbToSmd(GlbCodec.ReadBytes(GlbCodec.WriteFromSmd(six), "six.glb"), six);
            var vertex = sixBack.Triangles[0].V0;
            ctx.Assert(vertex.Weights.Count > 0 && vertex.Weights.Count <= 4,
                       "每顶点最多 4 个影响（实际 " + vertex.Weights.Count + "）");
            double sum = 0;
            foreach (var w in vertex.Weights) sum += w.Weight;
            ctx.AssertEqual(1.0, sum, WeightTolerance, "权重和归一化到 1");
            foreach (var w in vertex.Weights)
                ctx.Assert(w.BoneIndex < 4, "只保留权重最大的 4 个影响（不应出现骨骼 " + w.BoneIndex + "）");
        }

        // ══════════════════════════════════════════════════════════════════
        //  5. 损坏输入 → 可读异常
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：损坏输入抛出可读异常")]
        public static void CorruptedInputs(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("corrupted");
            AssertThrows<InvalidDataException>(() => SmdCodec.ParseBytes(new byte[0], "empty.smd"),
                                               "空 SMD", ctx, "空");
            AssertThrows<InvalidDataException>(() => SmdCodec.ParseBytes(Encoding.UTF8.GetBytes("hello world"), "text.smd"),
                                               "非 SMD 文本", ctx, "triangles");
            AssertThrows<InvalidDataException>(() => SmdCodec.Parse(Path.Combine(dir, "missing.smd")),
                                               "不存在的 SMD", ctx, "不存在");

            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(new byte[0], "empty.glb"),
                                               "空 GLB", ctx, "空");
            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(new byte[] { 1, 2, 3, 4, 5 }, "tiny.glb"),
                                               "过短 GLB", ctx, "GLB");
            AssertThrows<InvalidDataException>(
                () => GlbCodec.ReadBytes(Encoding.UTF8.GetBytes("this is definitely not a glb file at all"), "text.glb"),
                "非 GLB 文件", ctx, "GLB");
            AssertThrows<InvalidDataException>(() => GlbCodec.Read(Path.Combine(dir, "missing.glb")),
                                               "不存在的 GLB", ctx, "不存在");

            byte[] valid = GlbCodec.WriteFromSmd(BuildSkinned());
            byte[] truncated = new byte[valid.Length / 2];
            Array.Copy(valid, truncated, truncated.Length);
            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(truncated, "truncated.glb"),
                                               "截断 GLB", ctx, "截断");

            byte[] versionOne = (byte[])valid.Clone();
            GlbJson.WriteU32(versionOne, 4, 1);
            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(versionOne, "v1.glb"),
                                               "GLB v1", ctx, "版本");

            byte[] badJson = GlbJson.PackGlb(Encoding.UTF8.GetBytes("{ this is not json"), new byte[0]);
            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(badJson, "badjson.glb"),
                                               "JSON 非法", ctx, "JSON");

            // sparse accessor 明确不支持
            string sparseJson = "{\"asset\":{\"version\":\"2.0\"},\"accessors\":[" +
                                "{\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"," +
                                "\"sparse\":{\"count\":1,\"indices\":{\"bufferView\":0,\"componentType\":5123}," +
                                "\"values\":{\"bufferView\":1}}}]," +
                                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":6}," +
                                "{\"buffer\":0,\"byteOffset\":8,\"byteLength\":12}]," +
                                "\"buffers\":[{\"byteLength\":20}]," +
                                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]," +
                                "\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
            AssertThrows<InvalidDataException>(
                () => GlbCodec.ReadBytes(GlbJson.PackGlb(Encoding.UTF8.GetBytes(sparseJson), new byte[20]), "sparse.glb"),
                "sparse accessor", ctx, "sparse");

            // 非三角图元（mode=1）
            string lineJson = "{\"asset\":{\"version\":\"2.0\"},\"accessors\":[" +
                              "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}]," +
                              "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}]," +
                              "\"buffers\":[{\"byteLength\":36}]," +
                              "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"mode\":1}]}]}";
            var lineBytes = GlbJson.PackGlb(Encoding.UTF8.GetBytes(lineJson), new byte[36]);
            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(lineBytes, "lines.glb"),
                                               "非三角图元", ctx, "mode");

            // 索引数不是 3 的倍数
            string badIndicesJson = "{\"asset\":{\"version\":\"2.0\"},\"accessors\":[" +
                                    "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}," +
                                    "{\"bufferView\":1,\"componentType\":5123,\"count\":4,\"type\":\"SCALAR\"}]," +
                                    "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}," +
                                    "{\"buffer\":0,\"byteOffset\":40,\"byteLength\":8}]," +
                                    "\"buffers\":[{\"byteLength\":48}]," +
                                    "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]}";
            var badIndicesBytes = GlbJson.PackGlb(Encoding.UTF8.GetBytes(badIndicesJson), new byte[48]);
            AssertThrows<InvalidDataException>(() => GlbCodec.ReadBytes(badIndicesBytes, "badindices.glb"),
                                               "索引数不是 3 的倍数", ctx, "3 的倍数");

            // Draco 压缩 / 量化网格：明确不支持，给可读错误而不是读出错误数据
            string dracoJson = "{\"asset\":{\"version\":\"2.0\"},\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]," +
                               "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]}";
            AssertThrows<InvalidDataException>(
                () => GlbCodec.ReadBytes(GlbJson.PackGlb(Encoding.UTF8.GetBytes(dracoJson), new byte[0]), "draco.glb"),
                "Draco 压缩", ctx, "Draco");

            // CountTriangles 对非 GLB 也要抛可读异常
            string textPath = Path.Combine(dir, "not_glb.txt");
            File.WriteAllText(textPath, "not a glb");
            AssertThrows<InvalidDataException>(() => GlbCodec.CountTriangles(textPath), "CountTriangles 非 GLB", ctx, "GLB");
        }

        // ══════════════════════════════════════════════════════════════════
        //  6. GLB 读取：byteOffset / byteStride / normalized
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：GLB 读取支持 byteOffset/byteStride/normalized")]
        public static void GlbReadInterleaved(SelfTestContext ctx)
        {
            byte[] bin = BuildInterleavedBin();
            string json = BuildInterleavedJson();
            byte[] glb = GlbJson.PackGlb(Encoding.UTF8.GetBytes(json), bin);

            var glbModel = GlbCodec.ReadBytes(glb, "interleaved.glb");
            ctx.AssertEqual(1, glbModel.Meshes.Count, "mesh 数");
            var primitive = glbModel.Meshes[0].Primitives[0];
            ctx.AssertEqual(3, primitive.VertexCount, "顶点数");
            ctx.AssertEqual(1, primitive.TriangleCount, "三角面数");
            ctx.AssertEqual(0.0, primitive.Positions[0], Geo, "POSITION[0].x（byteStride=16）");
            ctx.AssertEqual(1.0, primitive.Positions[3], Geo, "POSITION[1].x（byteStride=16）");
            ctx.AssertEqual(1.0, primitive.Positions[7], Geo, "POSITION[2].y（byteStride=16）");
            ctx.AssertEqual(1.0f, primitive.Normals[2], Geo, "NORMAL[0].z");
            ctx.AssertEqual(1.0, primitive.Uvs[2], Geo, "TEXCOORD_0[1].u");
            ctx.AssertEqual(0, primitive.Joints[0], "JOINTS_0（ubyte）");
            ctx.Assert(primitive.Weights != null && primitive.Weights.Length == 12, "WEIGHTS_0 读取长度");
            ctx.AssertEqual(1.0, primitive.Weights[0], WeightTolerance, "normalized ushort 权重 65535 → 1.0");
            ctx.AssertEqual(0.5, primitive.Weights[4], 1e-3, "normalized ushort 权重 32768 → ≈0.5");
            ctx.AssertEqual(0.5, primitive.Weights[5], 1e-3, "normalized ushort 权重 32767 → ≈0.5");
            ctx.AssertEqual("mat", primitive.MaterialName, "材质名");

            // 通过 skin.joints → node 名 → 原始骨骼 Index 还原到 SMD
            var original = new SmdModel();
            original.Bones.Add(new SmdBone { Index = 3, Name = "root", Parent = -1 });
            var smd = SmdGlbCodec.GlbToSmd(glbModel, original);
            ctx.AssertEqual(1, smd.TriangleCount, "转 SMD 三角数");
            ctx.AssertEqual(3, smd.Triangles[0].V0.Weights[0].BoneIndex, "骨骼索引按名字还原（node root → Index 3）");
            ctx.AssertEqual(1.0, smd.Triangles[0].V0.Weights[0].Weight, WeightTolerance, "权重值保留");
            ctx.AssertEqual(0, smd.Triangles[0].V2.Weights.Count, "全 0 权重顶点按无骨骼顶点写出");
        }

        // ══════════════════════════════════════════════════════════════════
        //  7. GlbToSmd 骨骼名映射（大小写 / Blender 后缀 / 回落 0）
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：GlbToSmd 骨骼名映射（大小写/Blender 后缀/回落 0）")]
        public static void GlbToSmdBoneMapping(SelfTestContext ctx)
        {
            var original = new SmdModel();
            original.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            original.Bones.Add(new SmdBone { Index = 4, Name = "arm", Parent = 0 });

            var glb = new GlbModel();
            glb.Nodes.Add(new GlbNode { Name = "root" });        // 0 精确匹配
            glb.Nodes.Add(new GlbNode { Name = "arm.001" });     // 1 Blender 重命名后缀
            glb.Nodes.Add(new GlbNode { Name = "unknown" });     // 2 找不到 → 回落 0
            glb.Nodes.Add(new GlbNode { Name = "ROOT" });        // 3 大小写不敏感
            glb.Nodes.Add(new GlbNode { Name = "arm" });         // 4 精确匹配
            glb.Nodes.Add(new GlbNode { Name = "MeshNode", Mesh = 0, Skin = 0 });
            glb.Skins.Add(new GlbSkin { Joints = new[] { 0, 1, 2, 3, 4 } });

            var primitive = new GlbPrimitive
            {
                MaterialName = "mat_x",
                Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0, 2, 0, 0, 2, 1, 0 },
                Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                Uvs = new float[] { 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0 },
                Indices = new[] { 0, 1, 2, 3, 4, 5 },
                Joints = new ushort[]
                {
                    0, 0, 0, 0,   // v0 → root
                    1, 0, 0, 0,   // v1 → arm.001（去后缀）
                    2, 0, 0, 0,   // v2 → unknown（回落 0）
                    3, 0, 0, 0,   // v3 → ROOT（大小写不敏感）
                    0, 0, 0, 0,   // v4 → 全 0 权重
                    4, 0, 0, 0    // v5 → arm
                },
                Weights = new float[]
                {
                    1, 0, 0, 0,
                    1, 0, 0, 0,
                    1, 0, 0, 0,
                    1, 0, 0, 0,
                    0, 0, 0, 0,
                    1, 0, 0, 0
                }
            };
            glb.Meshes.Add(new GlbMesh { Primitives = { primitive } });

            var result = SmdGlbCodec.GlbToSmd(glb, original);
            ctx.AssertEqual(2, result.Bones.Count, "骨骼表来自原始 SMD");
            ctx.AssertEqual(2, result.TriangleCount, "三角面数");
            ctx.AssertEqual("mat_x", result.Triangles[0].Material, "材质名保留");

            var t0 = result.Triangles[0];
            var t1 = result.Triangles[1];
            ctx.AssertEqual(0, t0.V0.Weights[0].BoneIndex, "root 精确匹配 → Index 0");
            ctx.AssertEqual(4, t0.V1.Weights[0].BoneIndex, "arm.001 去后缀 → Index 4");
            ctx.AssertEqual(0, t0.V2.Weights[0].BoneIndex, "unknown 回落骨骼 0");
            ctx.AssertEqual(0, t1.V0.Weights[0].BoneIndex, "ROOT 大小写不敏感 → Index 0");
            ctx.AssertEqual(0, t1.V1.Weights.Count, "全 0 权重顶点不写权重");
            ctx.AssertEqual(4, t1.V2.Weights[0].BoneIndex, "arm 精确匹配 → Index 4");

            string warnings = string.Join(" | ", result.Warnings.ToArray());
            ctx.AssertContains(warnings, "找不到", "未匹配骨骼名应记录警告");
            ctx.AssertContains(warnings, "回落", "警告里应说明回落到骨骼 0");
            ctx.AssertContains(warnings, "unknown", "警告里应给出示例骨骼名");
            ctx.AssertContains(warnings, "后缀", "去掉 Blender 后缀应记录警告");
            ctx.AssertContains(warnings, "没有任何有效权重", "无有效权重顶点应记录警告");

            // 没有 skin 但有 JOINTS_0/WEIGHTS_0：按静态处理并警告
            var noSkin = new GlbModel();
            noSkin.Nodes.Add(new GlbNode { Name = "root" });
            noSkin.Nodes.Add(new GlbNode { Name = "MeshNode", Mesh = 0 });
            noSkin.Meshes.Add(new GlbMesh
            {
                Primitives =
                {
                    new GlbPrimitive
                    {
                        Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                        Indices = new[] { 0, 1, 2 },
                        Joints = new ushort[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                        Weights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }
                    }
                }
            });
            var noSkinResult = SmdGlbCodec.GlbToSmd(noSkin, original);
            ctx.Assert(!noSkinResult.HasSkinWeights, "无 skin 时按静态处理");
            ctx.AssertContains(string.Join(" | ", noSkinResult.Warnings.ToArray()), "skin", "无 skin 警告");
        }

        // ══════════════════════════════════════════════════════════════════
        //  10. 大文件健壮性（5 万面）
        // ══════════════════════════════════════════════════════════════════

        [SelfTest("模型：5 万面大模型 SMD/GLB 往返")]
        public static void LargeModelRoundTrip(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("large");
            var model = BuildLarge(50000);
            ctx.AssertEqual(50000, model.TriangleCount, "构造的三角面数");

            var timer = Stopwatch.StartNew();
            byte[] glb = GlbCodec.WriteFromSmd(model);
            long writeMs = timer.ElapsedMilliseconds;
            string glbPath = Path.Combine(dir, "large.glb");
            File.WriteAllBytes(glbPath, glb);
            ctx.AssertEqual(50000, GlbCodec.CountTriangles(glbPath), "CountTriangles(5 万面)");

            timer.Restart();
            var read = GlbCodec.Read(glbPath);
            var back = SmdGlbCodec.GlbToSmd(read, model);
            long convertMs = timer.ElapsedMilliseconds;
            ctx.AssertEqual(50000, back.TriangleCount, "GLB 往返三角面数");
            ctx.Assert(back.HasSkinWeights, "大模型应保留蒙皮");

            timer.Restart();
            byte[] smdBytes = SmdCodec.WriteBytes(model);
            var reparsed = SmdCodec.ParseBytes(smdBytes, Path.Combine(dir, "large.smd"));
            long smdMs = timer.ElapsedMilliseconds;
            ctx.AssertEqual(50000, reparsed.TriangleCount, "SMD 往返三角面数");

            ctx.Assert(writeMs < 120000, "5 万面 GLB 写出耗时过长（" + writeMs + "ms）");
            ctx.Assert(convertMs < 120000, "5 万面读回+转 SMD 耗时过长（" + convertMs + "ms）");
            ctx.Assert(smdMs < 120000, "5 万面 SMD 往返耗时过长（" + smdMs + "ms）");
            ctx.Log("5 万面：GLB " + glb.Length + " 字节，写出(含自校验) " + writeMs +
                    "ms，读回+转 SMD " + convertMs + "ms，SMD 往返 " + smdMs + "ms");
        }

        // ══════════════════════════════════════════════════════════════════
        //  断言辅助
        // ══════════════════════════════════════════════════════════════════

        private static void AssertThrows<T>(Action action, string what, SelfTestContext ctx,
                                            params string[] messageFragments) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                foreach (string fragment in messageFragments)
                    ctx.AssertContains(ex.Message, fragment, what + "（异常信息）");
                ctx.Log(what + " → 抛 " + typeof(T).Name + "：" + ex.Message);
                return;
            }
            catch (Exception ex)
            {
                throw new SelfTestFailureException(what + "：期望抛 " + typeof(T).Name + "，实际抛 " +
                                                   ex.GetType().Name + ": " + ex.Message);
            }
            throw new SelfTestFailureException(what + "：期望抛 " + typeof(T).Name + "，实际没有抛异常");
        }

        private static void AssertBonesEquivalent(SelfTestContext ctx, SmdModel expected, SmdModel actual)
        {
            ctx.AssertEqual(expected.Bones.Count, actual.Bones.Count, "骨骼数");
            for (int i = 0; i < expected.Bones.Count; i++)
            {
                ctx.AssertEqual(expected.Bones[i].Index, actual.Bones[i].Index, "骨骼[" + i + "].Index");
                ctx.AssertEqual(expected.Bones[i].Name, actual.Bones[i].Name, "骨骼[" + i + "].Name");
                ctx.AssertEqual(expected.Bones[i].Parent, actual.Bones[i].Parent, "骨骼[" + i + "].Parent");
            }
        }

        private static void AssertSmdEquivalent(SelfTestContext ctx, SmdModel expected, SmdModel actual, string tag)
        {
            AssertBonesEquivalent(ctx, expected, actual);
            ctx.AssertEqual(expected.TriangleCount, actual.TriangleCount, tag + "：三角面数");
            for (int t = 0; t < expected.TriangleCount; t++)
            {
                ctx.AssertEqual(expected.Triangles[t].Material, actual.Triangles[t].Material,
                                tag + "：三角形[" + t + "]材质");
                AssertVertexFull(ctx, expected.Triangles[t].V0, actual.Triangles[t].V0, tag + "：[ " + t + "].V0");
                AssertVertexFull(ctx, expected.Triangles[t].V1, actual.Triangles[t].V1, tag + "：[ " + t + "].V1");
                AssertVertexFull(ctx, expected.Triangles[t].V2, actual.Triangles[t].V2, tag + "：[ " + t + "].V2");
            }
        }

        /// <summary>SMD 文本往返用：几何 + 权重顺序都要一致。</summary>
        private static void AssertVertexFull(SelfTestContext ctx, SmdVertex expected, SmdVertex actual, string what)
        {
            AssertVertexGeometry(ctx, expected, actual, what);
            ctx.AssertEqual(expected.Weights.Count, actual.Weights.Count, what + "：权重数量");
            for (int k = 0; k < expected.Weights.Count; k++)
            {
                ctx.AssertEqual(expected.Weights[k].BoneIndex, actual.Weights[k].BoneIndex, what + "：权重骨骼[" + k + "]");
                ctx.AssertEqual(expected.Weights[k].Weight, actual.Weights[k].Weight, WeightTolerance,
                                what + "：权重值[" + k + "]");
            }
        }

        private static void AssertVertexGeometry(SelfTestContext ctx, SmdVertex expected, SmdVertex actual, string what)
        {
            ctx.Assert(expected != null && actual != null, what + "：顶点为空");
            ctx.AssertEqual(expected.X, actual.X, Geo, what + ".X");
            ctx.AssertEqual(expected.Y, actual.Y, Geo, what + ".Y");
            ctx.AssertEqual(expected.Z, actual.Z, Geo, what + ".Z");
            ctx.AssertEqual(expected.Nx, actual.Nx, Geo, what + ".Nx");
            ctx.AssertEqual(expected.Ny, actual.Ny, Geo, what + ".Ny");
            ctx.AssertEqual(expected.Nz, actual.Nz, Geo, what + ".Nz");
            ctx.AssertEqual(expected.U, actual.U, Geo, what + ".U");
            ctx.AssertEqual(expected.V, actual.V, Geo, what + ".V");
        }

        /// <summary>权重按骨骼索引做成映射比较（顺序可能因 Blender 排序而变化）。</summary>
        private static void AssertWeightsEquivalent(SelfTestContext ctx, SmdVertex expected, SmdVertex actual, string what)
        {
            var expectedMap = WeightMap(expected);
            var actualMap = WeightMap(actual);
            ctx.AssertEqual(expectedMap.Count, actualMap.Count, what + "：影响骨骼数");
            foreach (var pair in expectedMap)
            {
                ctx.Assert(actualMap.ContainsKey(pair.Key), what + "：缺少骨骼 " + pair.Key);
                ctx.AssertEqual(pair.Value, actualMap[pair.Key], WeightTolerance, what + "：骨骼 " + pair.Key + " 权重");
            }
        }

        private static Dictionary<int, double> WeightMap(SmdVertex vertex)
        {
            var map = new Dictionary<int, double>();
            if (vertex == null || vertex.Weights == null) return map;
            foreach (var weight in vertex.Weights)
            {
                if (weight == null) continue;
                double old;
                map.TryGetValue(weight.BoneIndex, out old);
                map[weight.BoneIndex] = old + weight.Weight;
            }
            return map;
        }

        private static void AssertWeightsNormalized(SelfTestContext ctx, SmdModel model, string tag)
        {
            foreach (var triangle in model.Triangles)
            {
                foreach (var vertex in new[] { triangle.V0, triangle.V1, triangle.V2 })
                {
                    if (vertex == null || vertex.Weights == null || vertex.Weights.Count == 0) continue;
                    double sum = 0;
                    foreach (var weight in vertex.Weights) sum += weight.Weight;
                    ctx.AssertEqual(1.0, sum, WeightTolerance, tag + "：权重和应为 1");
                }
            }
        }

        private static SmdModel BuildSkinned()
        {
            var model = new SmdModel();
            model.Bones.Add(new SmdBone { Index = 5, Name = "hand", Parent = 2 });
            model.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            model.Bones.Add(new SmdBone { Index = 2, Name = "Arm_Upper", Parent = 0 });
            model.Bones.Add(new SmdBone { Index = 7, Name = "spine", Parent = -1 });

            AddTriangle(model, "models/props/test/body",
                V(0, 0, 0, 0, 0, 1, 0, 0, 0, 1f),
                V(100, 0, 0, 0, 0, 1, 1, 0, 0, 1f),
                V(0, 50, 0, 0, 0, 1, 0, 1, 0, 1f));
            AddTriangle(model, "models/props/test/body",
                V(0, 0, 200, 0, 0, 1, 0, 0, 2, 0.5f, 5, 0.5f),
                V(100, 0, 200, 0, 0, 1, 1, 0, 2, 0.5f, 5, 0.5f),
                V(0, 50, 200, 0, 0, 1, 0, 1, 2, 0.5f, 5, 0.5f));
            AddTriangle(model, "models/props/test/head",
                V(10, 10, 10, 1, 0, 0, 0, 0, 0, 0.25f, 2, 0.25f, 5, 0.5f),
                V(110, 10, 10, 1, 0, 0, 1, 0, 0, 0.25f, 2, 0.25f, 5, 0.5f),
                V(10, 60, 10, 1, 0, 0, 0, 1, 0, 0.25f, 2, 0.25f, 5, 0.5f));
            AddTriangle(model, "models/props/test/head",
                V(10, 20, 200, 0, 1, 0, 0.25f, 0.25f, 7, 1f),
                V(110, 20, 200, 0, 1, 0, 0.75f, 0.25f, 7, 1f),
                V(10, 70, 200, 0, 1, 0, 0.25f, 0.75f, 7, 1f));

            for (int i = 0; i < model.Bones.Count; i++) model.SkeletonPose.Add("0 0 0 0 0 0");
            return model;
        }

        private static SmdModel BuildStatic()
        {
            var model = new SmdModel();
            model.Bones.Add(new SmdBone { Index = 0, Name = "static_prop", Parent = -1 });
            AddTriangle(model, "models/props/test/static",
                V(0, 0, 0, 0, 0, 1, 0, 0),
                V(10, 0, 0, 0, 0, 1, 1, 0),
                V(0, 10, 0, 0, 0, 1, 0, 1));
            return model;
        }

        private static SmdModel BuildSixInfluence()
        {
            var model = new SmdModel();
            for (int i = 0; i < 6; i++) model.Bones.Add(new SmdBone { Index = i, Name = "bone" + i, Parent = -1 });
            AddTriangle(model, "mat",
                V(0, 0, 0, 0, 0, 1, 0, 0,
                    0, 0.40f, 1, 0.20f, 2, 0.15f, 3, 0.10f, 4, 0.10f, 5, 0.05f),
                V(1, 0, 0, 0, 0, 1, 1, 0,
                    0, 0.40f, 1, 0.20f, 2, 0.15f, 3, 0.10f, 4, 0.10f, 5, 0.05f),
                V(0, 1, 0, 0, 0, 1, 0, 1,
                    0, 0.40f, 1, 0.20f, 2, 0.15f, 3, 0.10f, 4, 0.10f, 5, 0.05f));
            return model;
        }

        private static SmdModel BuildLarge(int triangleCount)
        {
            var model = new SmdModel();
            model.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            model.Bones.Add(new SmdBone { Index = 1, Name = "child", Parent = 0 });
            var random = new Random(12345);
            for (int i = 0; i < triangleCount; i++)
            {
                float x = (float)(random.NextDouble() * 1000 - 500);
                float y = (float)(random.NextDouble() * 1000 - 500);
                float z = (float)(random.NextDouble() * 1000 - 500);
                object[] weights = i % 2 == 0 ? new object[] { 0, 1f } : new object[] { 0, 0.5f, 1, 0.5f };
                AddTriangle(model, i % 3 == 0 ? "mat_a" : "mat_b",
                    V(x, y, z, 0, 0, 1, 0, 0, weights),
                    V(x + 1, y, z, 0, 0, 1, 1, 0, weights),
                    V(x, y + 1, z, 0, 0, 1, 0, 1, weights));
            }
            return model;
        }

        private static void AddTriangle(SmdModel model, string material, SmdVertex a, SmdVertex b, SmdVertex c)
        {
            model.Triangles.Add(new SmdTriangle { Material = material, V0 = a, V1 = b, V2 = c });
        }

        /// <summary>建顶点：位置/法线/UV + 可选 (骨骼, 权重) 对。</summary>
        private static SmdVertex V(float x, float y, float z, float nx, float ny, float nz, float u, float v,
                                   params object[] weights)
        {
            var vertex = new SmdVertex { X = x, Y = y, Z = z, Nx = nx, Ny = ny, Nz = nz, U = u, V = v };
            if (weights != null)
            {
                for (int i = 0; i + 1 < weights.Length; i += 2)
                {
                    vertex.Weights.Add(new SmdWeight
                    {
                        BoneIndex = Convert.ToInt32(weights[i], Inv),
                        Weight = Convert.ToSingle(weights[i + 1], Inv)
                    });
                }
            }
            return vertex;
        }

        // ── 交错 / normalized 的合成 GLB（验证 byteStride / byteOffset / normalized） ──

        /// <summary>回归防护：内缩后不允许出现任何「没有被节点引用」的 mesh（旧实现克隆后会留下孤儿）。</summary>
        private static byte[] BuildInterleavedBin()
        {
            // layout:
            //   view0 POSITION   offset 0,   byteLength 48, byteStride 16；accessor byteOffset=4（每顶点 12 字节 + 4 字节填充）
            //   view1 NORMAL     offset 48,  3 * 12 = 36   (48..83)
            //   view2 TEXCOORD_0 offset 84,  3 * 8  = 24   (84..107)
            //   view3 JOINTS_0   offset 108, 3 * 4  = 12   (108..119) ubyte VEC4
            //   view4 WEIGHTS_0  offset 120, 3 * 8  = 24   (120..143) ushort VEC4 normalized
            //   view5 indices    offset 144, 3 * 2  = 6    (144..149) ushort
            var bin = new byte[152];
            float[] positions = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            for (int i = 0; i < 3; i++)
                for (int c = 0; c < 3; c++)
                    Array.Copy(BitConverter.GetBytes(positions[i * 3 + c]), 0, bin, 4 + i * 16 + c * 4, 4);
            for (int i = 0; i < 3; i++)
            {
                Array.Copy(BitConverter.GetBytes(0f), 0, bin, 48 + i * 12, 4);
                Array.Copy(BitConverter.GetBytes(0f), 0, bin, 48 + i * 12 + 4, 4);
                Array.Copy(BitConverter.GetBytes(1f), 0, bin, 48 + i * 12 + 8, 4);
            }
            int[] uvOffsets = { 84, 84 + 8, 84 + 16 };
            float[] uvs = { 0, 0, 1, 0, 0, 1 };
            for (int i = 0; i < 3; i++)
            {
                Array.Copy(BitConverter.GetBytes(uvs[i * 2]), 0, bin, uvOffsets[i], 4);
                Array.Copy(BitConverter.GetBytes(uvs[i * 2 + 1]), 0, bin, uvOffsets[i] + 4, 4);
            }
            for (int i = 0; i < 12; i++) bin[108 + i] = 0;   // JOINTS_0 全 0（bone 0）
            ushort[] weights = { 65535, 0, 0, 0, 32768, 32767, 0, 0, 0, 0, 0, 0 };
            for (int i = 0; i < weights.Length; i++)
            {
                bin[120 + i * 2] = (byte)weights[i];
                bin[120 + i * 2 + 1] = (byte)(weights[i] >> 8);
            }
            ushort[] indices = { 0, 1, 2 };
            for (int i = 0; i < 3; i++)
            {
                bin[144 + i * 2] = (byte)indices[i];
                bin[144 + i * 2 + 1] = (byte)(indices[i] >> 8);
            }
            return bin;
        }

        private static string BuildInterleavedJson()
        {
            return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0," +
                   "\"scenes\":[{\"nodes\":[1]}]," +
                   "\"nodes\":[{\"name\":\"root\"},{\"name\":\"MeshNode\",\"mesh\":0,\"skin\":0}]," +
                   "\"skins\":[{\"joints\":[0]}]," +
                   "\"materials\":[{\"name\":\"mat\"}]," +
                   "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1," +
                   "\"TEXCOORD_0\":2,\"JOINTS_0\":3,\"WEIGHTS_0\":4},\"indices\":5,\"material\":0}]}]," +
                   "\"accessors\":[" +
                   "{\"bufferView\":0,\"byteOffset\":4,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"," +
                   "\"min\":[0,0,0],\"max\":[1,1,0]}," +
                   "{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}," +
                   "{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"}," +
                   "{\"bufferView\":3,\"componentType\":5121,\"count\":3,\"type\":\"VEC4\"}," +
                   "{\"bufferView\":4,\"componentType\":5123,\"count\":3,\"type\":\"VEC4\",\"normalized\":true}," +
                   "{\"bufferView\":5,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]," +
                   "\"bufferViews\":[" +
                   "{\"buffer\":0,\"byteOffset\":0,\"byteLength\":48,\"byteStride\":16}," +
                   "{\"buffer\":0,\"byteOffset\":48,\"byteLength\":36}," +
                   "{\"buffer\":0,\"byteOffset\":84,\"byteLength\":24}," +
                   "{\"buffer\":0,\"byteOffset\":108,\"byteLength\":12}," +
                   "{\"buffer\":0,\"byteOffset\":120,\"byteLength\":24}," +
                   "{\"buffer\":0,\"byteOffset\":144,\"byteLength\":6}]," +
                   "\"buffers\":[{\"byteLength\":152}]}";
        }
    }

}
