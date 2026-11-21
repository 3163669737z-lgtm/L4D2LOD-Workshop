using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// .dx90.vtx（Source 引擎编译产物）解析：读回真实 LOD 级数 / 切换点 / 每级三角形与顶点数。
    ///
    /// 二进制布局（与旧工具实测一致，字段偏移固定）：
    ///   header：numLods @20、numBodyParts @28、bodyPartOffset @32（int32 小端）
    ///   bodyPart（8 字节）：numModels、modelOffset（相对 bodyPart 起点）
    ///   model   （8 字节）：numLods、lodOffset（相对 model 起点）
    ///   lod     （12 字节）：numMeshes、meshOffset（相对 lod 起点）、switchPoint（float）
    ///   mesh    （9 字节）：numStripGroups、stripGroupOffset（相对 mesh 起点）
    ///   stripGroup（25 字节）：numVertices @0、numIndices @8（三角形数 = numIndices / 3）
    ///
    /// 任何字段异常 / 越界 / 文件过小 → Valid=false + 中文 Error（绝不抛异常）：
    /// 试编译是"附加校验"，不能因为一个坏 vtx 让整批运行崩溃。
    /// </summary>
    public static class VtxInspector
    {
        private const int MinHeaderBytes = 36;
        private const int MaxLods = 20;
        private const int MaxBodyParts = 200;
        private const int MaxModels = 4096;
        private const int MaxMeshes = 4096;
        private const int MaxStripGroups = 65536;

        /// <summary>解析 .dx90.vtx（二进制）；失败返回 Valid=false + Error。</summary>
        public static VtxInfo Read(string vtxPath)
        {
            var r = new VtxInfo();
            try
            {
                if (string.IsNullOrEmpty(vtxPath))
                {
                    r.Error = "vtx 路径为空";
                    return r;
                }
                byte[] b;
                try { b = FileUtil.ReadAllBytesShared(vtxPath); }
                catch (Exception ex)
                {
                    r.Error = "读取 vtx 失败: " + ex.Message;
                    return r;
                }
                if (b == null || b.Length < MinHeaderBytes)
                {
                    r.Error = "文件过小（" + (b == null ? 0 : b.Length) + " 字节，至少需要 " + MinHeaderBytes + " 字节）";
                    return r;
                }

                int numLods, numBodyParts, bodyPartOffset;
                if (!TryInt32(b, 20, out numLods) ||
                    !TryInt32(b, 28, out numBodyParts) ||
                    !TryInt32(b, 32, out bodyPartOffset))
                {
                    r.Error = "头部字段读取失败（文件被截断？）";
                    return r;
                }
                if (numLods < 0 || numLods > MaxLods)
                {
                    r.Error = "头部字段异常：LOD 级数 " + numLods + "（合理范围 0~" + MaxLods + "）";
                    return r;
                }
                if (numBodyParts < 0 || numBodyParts > MaxBodyParts)
                {
                    r.Error = "头部字段异常：bodyPart 数 " + numBodyParts + "（合理范围 0~" + MaxBodyParts + "）";
                    return r;
                }
                if (bodyPartOffset < MinHeaderBytes || (long)bodyPartOffset + (long)numBodyParts * 8 > b.Length)
                {
                    r.Error = "头部字段异常：bodyPart 偏移 " + bodyPartOffset +
                              " 越界（文件 " + b.Length + " 字节）";
                    return r;
                }

                var swByLi = new Dictionary<int, double>();
                var triByLi = new Dictionary<int, int>();
                var vtxByLi = new Dictionary<int, int>();

                for (int bp = 0; bp < numBodyParts; bp++)
                {
                    int bpBase = bodyPartOffset + bp * 8;
                    int numModels, modelOffset;
                    if (!TryInt32(b, bpBase, out numModels) || !TryInt32(b, bpBase + 4, out modelOffset))
                        return Fail(r, "bodyPart[" + bp + "] 字段越界");
                    if (numModels < 0 || numModels > MaxModels)
                        return Fail(r, "bodyPart[" + bp + "] 的 model 数异常：" + numModels);

                    int modelBase = AddChecked(bpBase, modelOffset);
                    if (modelBase < 0)
                        return Fail(r, "bodyPart[" + bp + "] 的 model 偏移越界");

                    for (int mi = 0; mi < numModels; mi++)
                    {
                        int mBase = modelBase + mi * 8;
                        int numLodEntries, lodOffset;
                        if (!TryInt32(b, mBase, out numLodEntries) || !TryInt32(b, mBase + 4, out lodOffset))
                            return Fail(r, "model[" + bp + "." + mi + "] 字段越界");
                        if (numLodEntries < 0 || numLodEntries > MaxLods)
                            return Fail(r, "model[" + bp + "." + mi + "] 的 LOD 数异常：" + numLodEntries);

                        int lodBase = AddChecked(mBase, lodOffset);
                        if (lodBase < 0)
                            return Fail(r, "model[" + bp + "." + mi + "] 的 lod 偏移越界");

                        for (int li = 0; li < numLodEntries; li++)
                        {
                            int lb = lodBase + li * 12;
                            int nMesh, meshOffset;
                            float sw;
                            if (!TryInt32(b, lb, out nMesh) || !TryInt32(b, lb + 4, out meshOffset) ||
                                !TrySingle(b, lb + 8, out sw))
                                return Fail(r, "LOD[" + bp + "." + mi + "." + li + "] 字段越界");
                            if (nMesh < 0 || nMesh > MaxMeshes)
                                return Fail(r, "LOD[" + bp + "." + mi + "." + li + "] 的 mesh 数异常：" + nMesh);

                            int meshBase = AddChecked(lb, meshOffset);
                            if (meshBase < 0)
                                return Fail(r, "LOD[" + bp + "." + mi + "." + li + "] 的 mesh 偏移越界");

                            int tris = 0, verts = 0;
                            for (int k = 0; k < nMesh; k++)
                            {
                                int mb = meshBase + k * 9;
                                int numSg, sgOffset;
                                if (!TryInt32(b, mb, out numSg) || !TryInt32(b, mb + 4, out sgOffset))
                                    return Fail(r, "mesh[" + bp + "." + mi + "." + li + "." + k + "] 字段越界");
                                if (numSg < 0 || numSg > MaxStripGroups)
                                    return Fail(r, "mesh[" + bp + "." + mi + "." + li + "." + k + "] 的 stripGroup 数异常：" + numSg);

                                int sgBase = AddChecked(mb, sgOffset);
                                if (sgBase < 0)
                                    return Fail(r, "mesh[" + bp + "." + mi + "." + li + "." + k + "] 的 stripGroup 偏移越界");

                                for (int sg = 0; sg < numSg; sg++)
                                {
                                    int gb = sgBase + sg * 25;
                                    int v, idx;
                                    if (!TryInt32(b, gb, out v) || !TryInt32(b, gb + 8, out idx))
                                        return Fail(r, "stripGroup[" + bp + "." + mi + "." + li + "." + k + "." + sg + "] 字段越界");
                                    if (v < 0 || idx < 0)
                                        return Fail(r, "stripGroup[" + bp + "." + mi + "." + li + "." + k + "." + sg + "] 计数字段为负");
                                    verts += v;
                                    tris += idx / 3;
                                }
                            }

                            double prev;
                            swByLi[li] = swByLi.TryGetValue(li, out prev) ? Math.Min(prev, (double)sw) : (double)sw;
                            triByLi[li] = (triByLi.ContainsKey(li) ? triByLi[li] : 0) + tris;
                            vtxByLi[li] = (vtxByLi.ContainsKey(li) ? vtxByLi[li] : 0) + verts;
                        }
                    }
                }

                r.NumLods = numLods;
                var keys = new List<int>(swByLi.Keys);
                keys.Sort();
                foreach (int li in keys)
                {
                    r.SwitchPoints.Add(swByLi[li]);
                    r.Triangles.Add(triByLi.ContainsKey(li) ? triByLi[li] : 0);
                    r.Vertices.Add(vtxByLi.ContainsKey(li) ? vtxByLi[li] : 0);
                }
                r.Valid = true;
            }
            catch (Exception ex)
            {
                r.Error = "解析 vtx 失败: " + ex.GetType().Name + ": " + ex.Message;
                r.Valid = false;
            }
            return r;
        }

        /// <summary>试编译日志用的一行摘要（失败时返回错误说明）。</summary>
        public static string Describe(VtxInfo vtx)
        {
            if (vtx == null) return "没有 vtx 信息";
            if (!vtx.Valid) return "vtx 无效：" + (vtx.Error ?? "未知原因");
            var sb = new StringBuilder();
            sb.Append("LOD").Append(vtx.NumLods.ToString(CultureInfo.InvariantCulture));
            if (vtx.Triangles.Count > 0)
            {
                sb.Append("  面数 ");
                for (int i = 0; i < vtx.Triangles.Count; i++)
                {
                    if (i > 0) sb.Append(" → ");
                    sb.Append(vtx.Triangles[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        // ───────────────────────────── 私有实现 ─────────────────────────────

        private static VtxInfo Fail(VtxInfo r, string message)
        {
            r.Error = message;
            r.Valid = false;
            return r;
        }

        private static bool TryInt32(byte[] b, int offset, out int value)
        {
            value = 0;
            if (b == null || offset < 0 || (long)offset + 4 > b.Length) return false;
            value = BitConverter.ToInt32(b, offset);
            return true;
        }

        private static bool TrySingle(byte[] b, int offset, out float value)
        {
            value = 0;
            if (b == null || offset < 0 || (long)offset + 4 > b.Length) return false;
            value = BitConverter.ToSingle(b, offset);
            return true;
        }

        /// <summary>base + rel（int 溢出防护）；越界返回 -1（由调用方给出中文错误）。</summary>
        private static int AddChecked(int baseOffset, int relative)
        {
            long sum = (long)baseOffset + relative;
            if (sum < 0 || sum > int.MaxValue) return -1;
            return (int)sum;
        }
    }
}
