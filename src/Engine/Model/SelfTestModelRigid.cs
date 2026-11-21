using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>评审修复回归：无 links 的刚性 SMD 骨骼绑定保真。</summary>
    internal static class SelfTestModelRigid
    {
        private static SmdModel MakeRigid()
        {
            var model = new SmdModel();
            model.Bones.Add(new SmdBone { Index = 0, Name = "root", Parent = -1 });
            model.Bones.Add(new SmdBone { Index = 1, Name = "spine", Parent = 0 });
            for (int i = 0; i < 4; i++)
            {
                var t = new SmdTriangle { Material = "body" };
                t.V0 = MakeV(i, 0, 0);
                t.V1 = MakeV(i, 1, 0);
                t.V2 = MakeV(i, 1, 1);
                model.Triangles.Add(t);
            }
            return model;
        }

        private static SmdVertex MakeV(float x, float y, float z)
        {
            // 没有 weights，但顶点主骨骼 = 1（spine）
            return new SmdVertex { X = x, Y = y, Z = z, Nx = 0, Ny = 0, Nz = 1, U = 0, V = 0, Bone = 1 };
        }

        [SelfTest("模型：无 links 的刚性 SMD 经 GLB 往返仍绑定原骨骼")]
        public static void RigidBonePreserved(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("rigid_smd");
            var model = MakeRigid();
            byte[] glb = SmdGlbCodec.SmdToGlb(model);
            var read = GlbCodec.ReadBytes(glb, "rigid.glb");
            ctx.Assert(read.TriangleCount == 4, "GLB 三角面数应为 4，实际 " + read.TriangleCount);
            var back = SmdGlbCodec.GlbToSmd(read, model);
            ctx.Assert(back.Triangles.Count == 4, "回写 SMD 三角面数");
            foreach (var tri in back.Triangles)
            {
                foreach (var v in new[] { tri.V0, tri.V1, tri.V2 })
                {
                    ctx.Assert(v.Weights.Count > 0, "回写顶点应有权重");
                    ctx.AssertEqual(1, v.Weights[0].BoneIndex, "回写顶点主骨骼应为 spine(1)");
                    ctx.AssertEqual(1, v.Bone, "回写顶点 Bone 字段应为 spine(1)");
                }
            }
        }

        [SelfTest("模型：深嵌套 JSON 不再栈溢出（有代数上限）")]
        public static void DeepJsonRejected(SelfTestContext ctx)
        {
            string dir = ctx.NewDir("deep_json");
            string path = Path.Combine(dir, "deep.glb");
            // 手工拼一个带深嵌套 JSON 的"GLB"：JSON 段是 200 层数组
            var sb = new StringBuilder();
            for (int i = 0; i < 200; i++) sb.Append('[');
            sb.Append('0');
            for (int i = 0; i < 200; i++) sb.Append(']');
            byte[] json = Encoding.UTF8.GetBytes(sb.ToString());
            int padded = (json.Length + 3) / 4 * 4;
            var bytes = new List<byte>();
            int total = 12 + 8 + padded;
            bytes.AddRange(BitConverter.GetBytes(0x46546C67));           // magic "glTF"
            bytes.AddRange(BitConverter.GetBytes(2));                     // version
            bytes.AddRange(BitConverter.GetBytes(total));
            bytes.AddRange(BitConverter.GetBytes(padded));                // chunk length
            bytes.AddRange(BitConverter.GetBytes(0x4E4F534A));            // "JSON"
            bytes.AddRange(json);
            for (int i = json.Length; i < padded; i++) bytes.Add(0x20);
            File.WriteAllBytes(path, bytes.ToArray());

            bool threw = false;
            try { GlbCodec.CountTriangles(path); }
            catch (Exception ex)
            {
                threw = true;
                ctx.Log("拒绝原因：" + ex.GetType().Name + ": " + ex.Message);
            }
            ctx.Assert(threw, "深嵌套 JSON 应抛可读异常而不是栈溢出");
        }
    }
}
