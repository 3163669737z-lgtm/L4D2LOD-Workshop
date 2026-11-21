using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    /// <summary>
    /// GLB / glTF 2.0 内部辅助层（不对其它模块公开）：
    ///  - GLB 容器拆装（magic/version/总长度/chunk 遍历，JSON+BIN，4 字节对齐）；
    ///  - glTF JSON 取值（对象/数组/数字/字符串，全部容错为可读异常）；
    ///  - accessor 解码：bufferView / byteOffset / byteStride / componentType / normalized / 越界校验，
    ///    sparse 明确不支持并抛可读错误；
    ///  - 二进制写入器 <see cref="GlbBinWriter"/>：按 4 字节对齐累积 bufferView/accessor 并生成对应 JSON 片段。
    /// </summary>
    internal static class GlbJson
    {
        internal const uint Magic = 0x46546C67;          // "glTF"
        internal const uint ChunkJson = 0x4E4F534A;      // "JSON"
        internal const uint ChunkBin = 0x004E4942;       // "BIN\0"

        internal const int ComponentByte = 5120;
        internal const int ComponentUByte = 5121;
        internal const int ComponentShort = 5122;
        internal const int ComponentUShort = 5123;
        internal const int ComponentUInt = 5125;
        internal const int ComponentFloat = 5126;

        internal const int TargetArrayBuffer = 34962;
        internal const int TargetElementArrayBuffer = 34963;

        internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
        private static readonly HashSet<string> KnownTypeNames =
            new HashSet<string>(new[] { "SCALAR", "VEC2", "VEC3", "VEC4", "MAT2", "MAT3", "MAT4" }, StringComparer.Ordinal);

        // ──────────────────────────────────────────────────────────────────
        //  GLB 容器
        // ──────────────────────────────────────────────────────────────────

        internal sealed class Container
        {
            internal Dictionary<string, object> Root;
            internal byte[] Bin;
            internal string JsonText;
        }

        /// <summary>拆 GLB：校验 magic/version/totalLength，遍历 chunk 取 JSON 与 BIN；任何损坏都抛可读 <see cref="InvalidDataException"/>。</summary>
        internal static Container SplitGlb(byte[] data, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length == 0) throw new InvalidDataException("GLB 文件为空：" + name);
            if (data.Length < 20)
                throw new InvalidDataException("不是有效的 GLB：文件只有 " + data.Length + " 字节（至少 20 字节）：" + name);

            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != Magic)
                throw new InvalidDataException("不是 GLB 文件（magic=0x" + magic.ToString("X8", Inv) + "，应为 0x46546C67）：" + name);
            uint version = BitConverter.ToUInt32(data, 4);
            if (version != 2)
                throw new InvalidDataException("不支持的 GLB 版本 " + version + "（本工具只支持 glTF 2.0）：" + name);
            uint totalLength = BitConverter.ToUInt32(data, 8);
            if (totalLength < 20)
                throw new InvalidDataException("GLB 头部声明的总长度无效（" + totalLength + " 字节）：" + name);
            if (totalLength > (uint)data.Length)
                throw new InvalidDataException("GLB 文件被截断：头部声明 " + totalLength + " 字节，实际只有 " +
                                               data.Length + " 字节：" + name);

            var container = new Container();
            int limit = (int)totalLength;
            int offset = 12;
            bool sawJson = false, sawBin = false;
            while (offset + 8 <= limit)
            {
                uint chunkLength = BitConverter.ToUInt32(data, offset);
                uint chunkType = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;
                if (chunkLength > (uint)(limit - offset))
                    throw new InvalidDataException("GLB 分块越界（声明 " + chunkLength + " 字节，剩余 " +
                                                   (limit - offset) + " 字节）：" + name);
                int length = (int)chunkLength;
                if (chunkType == ChunkJson && !sawJson)
                {
                    try { container.JsonText = Utf8Strict.GetString(data, offset, length); }
                    catch (DecoderFallbackException)
                    {
                        throw new InvalidDataException("GLB 的 JSON 分块不是合法 UTF-8：" + name);
                    }
                    // 规范用空格补齐；个别导出器用 0 补齐，这里一并容忍
                    container.JsonText = container.JsonText.TrimEnd(' ', '\0');
                    sawJson = true;
                }
                else if (chunkType == ChunkBin && !sawBin)
                {
                    container.Bin = new byte[length];
                    Array.Copy(data, offset, container.Bin, 0, length);
                    sawBin = true;
                }
                // 其它 chunk 类型按规范忽略
                offset += length;
            }

            if (!sawJson) throw new InvalidDataException("GLB 缺少 JSON 分块：" + name);
            if (container.Bin == null) container.Bin = new byte[0];

            object parsed;
            try { parsed = Json.Parse(container.JsonText); }
            catch (Json.JsonException ex)
            {
                throw new InvalidDataException("GLB 的 JSON 段非法：" + ex.Message + "：" + name, ex);
            }
            container.Root = parsed as Dictionary<string, object>;
            if (container.Root == null)
                throw new InvalidDataException("GLB 的 JSON 根节点不是对象：" + name);
            return container;
        }

        /// <summary>打包 GLB（json/bin 均补齐到 4 字节：JSON 用空格、BIN 用 0）。</summary>
        internal static byte[] PackGlb(byte[] jsonBytes, byte[] binBytes)
        {
            byte[] json = Pad(jsonBytes, (byte)' ');
            byte[] bin = Pad(binBytes, 0);
            int total = 12 + 8 + json.Length + 8 + bin.Length;
            var buffer = new byte[total];
            WriteU32(buffer, 0, Magic);
            WriteU32(buffer, 4, 2);
            WriteU32(buffer, 8, (uint)total);
            WriteU32(buffer, 12, (uint)json.Length);
            WriteU32(buffer, 16, ChunkJson);
            Array.Copy(json, 0, buffer, 20, json.Length);
            int binHeader = 20 + json.Length;
            WriteU32(buffer, binHeader, (uint)bin.Length);
            WriteU32(buffer, binHeader + 4, ChunkBin);
            Array.Copy(bin, 0, buffer, binHeader + 8, bin.Length);
            return buffer;
        }

        internal static byte[] Pad(byte[] data, byte fill)
        {
            if (data == null) data = new byte[0];
            int pad = (4 - (data.Length % 4)) % 4;
            if (pad == 0) return data;
            var result = new byte[data.Length + pad];
            Array.Copy(data, result, data.Length);
            for (int i = data.Length; i < result.Length; i++) result[i] = fill;
            return result;
        }

        internal static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        // ──────────────────────────────────────────────────────────────────
        //  JSON 取值
        // ──────────────────────────────────────────────────────────────────

        internal static Dictionary<string, object> Obj(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent != null && parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static List<object> Arr(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent != null && parent.TryGetValue(key, out value) ? value as List<object> : null;
        }

        internal static List<object> ArrOf(object value)
        {
            return value as List<object>;
        }

        internal static Dictionary<string, object> ObjOf(object value)
        {
            return value as Dictionary<string, object>;
        }

        internal static string Str(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? value.ToString();
        }

        internal static bool Has(Dictionary<string, object> parent, string key)
        {
            return parent != null && parent.ContainsKey(key) && parent[key] != null;
        }

        internal static int Int(Dictionary<string, object> parent, string key, int fallback)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || value == null) return fallback;
            if (value is double) return (int)Math.Round((double)value);
            if (value is long) return (int)(long)value;
            if (value is string)
            {
                int parsed;
                return int.TryParse((string)value, NumberStyles.Integer, Inv, out parsed) ? parsed : fallback;
            }
            return fallback;
        }

        internal static double Dbl(Dictionary<string, object> parent, string key, double fallback)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || value == null) return fallback;
            if (value is double) return (double)value;
            if (value is long) return (long)value;
            return fallback;
        }

        internal static bool Bool(Dictionary<string, object> parent, string key, bool fallback)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            return fallback;
        }

        internal static int[] IntArray(Dictionary<string, object> parent, string key, string displayName)
        {
            var array = Arr(parent, key);
            if (array == null) return null;
            var result = new int[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                object value = array[i];
                if (value is double) result[i] = (int)Math.Round((double)value);
                else if (value is long) result[i] = (int)(long)value;
                else
                    throw new InvalidDataException("GLB 的 " + key + " 数组第 " + i + " 项不是数字：" + displayName);
            }
            return result;
        }

        internal static float[] FloatArray(Dictionary<string, object> parent, string key, string displayName)
        {
            var array = Arr(parent, key);
            if (array == null) return null;
            var result = new float[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                object value = array[i];
                if (value is double) result[i] = (float)(double)value;
                else if (value is long) result[i] = (long)value;
                else
                    throw new InvalidDataException("GLB 的 " + key + " 数组第 " + i + " 项不是数字：" + displayName);
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────────────
        //  accessor 解码
        // ──────────────────────────────────────────────────────────────────

        internal sealed class AccessorInfo
        {
            internal int BufferView = -1;
            internal int ByteOffset;
            internal int ComponentType;
            internal int Count;
            internal int Components;
            internal string Type;
            internal bool Normalized;
        }

        internal sealed class ViewInfo
        {
            internal int Buffer;
            internal int Offset;
            internal int ByteLength;
            internal int ByteStride;
            internal int Target;
        }

        internal static int ComponentsOf(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT2": return 4;
                case "MAT3": return 9;
                case "MAT4": return 16;
                default: return 0;
            }
        }

        internal static string TypeNameOf(int components)
        {
            switch (components)
            {
                case 1: return "SCALAR";
                case 2: return "VEC2";
                case 3: return "VEC3";
                case 4: return "VEC4";
                case 16: return "MAT4";
                default: return components + " 分量";
            }
        }

        internal static int ComponentSizeOf(int componentType)
        {
            switch (componentType)
            {
                case ComponentByte:
                case ComponentUByte: return 1;
                case ComponentShort:
                case ComponentUShort: return 2;
                case ComponentUInt:
                case ComponentFloat: return 4;
                default: return 0;
            }
        }

        internal static AccessorInfo ReadAccessorInfo(Dictionary<string, object> root, int accessorIndex, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var accessors = Arr(root, "accessors");
            if (accessors == null || accessorIndex < 0 || accessorIndex >= accessors.Count)
                throw new InvalidDataException("GLB accessor 索引越界（" + accessorIndex + "）：" + name);

            var obj = accessors[accessorIndex] as Dictionary<string, object>;
            if (obj == null) throw new InvalidDataException("GLB accessor #" + accessorIndex + " 不是对象：" + name);

            var info = new AccessorInfo();
            object bufferView;
            if (obj.TryGetValue("bufferView", out bufferView) && bufferView is double)
                info.BufferView = (int)(double)bufferView;
            else
                info.BufferView = -1;
            info.ByteOffset = Int(obj, "byteOffset", 0);
            info.ComponentType = Int(obj, "componentType", 0);
            info.Count = Int(obj, "count", -1);
            info.Type = Str(obj, "type");
            info.Normalized = Bool(obj, "normalized", false);

            if (info.Count < 0)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 缺少合法的 count：" + name);
            if (info.ByteOffset < 0)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 的 byteOffset 为负数：" + name);
            if (!KnownTypeNames.Contains(info.Type ?? ""))
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 的 type 非法（" + (info.Type ?? "(缺失)") + "）：" + name);
            info.Components = ComponentsOf(info.Type);
            if (ComponentSizeOf(info.ComponentType) == 0)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 的 componentType 不支持（" +
                                               info.ComponentType + "）：" + name);

            object sparse;
            if (obj.TryGetValue("sparse", out sparse) && sparse != null)
                throw new InvalidDataException("不支持 sparse accessor（accessor #" + accessorIndex + "）：" + name);
            return info;
        }

        internal static ViewInfo ReadViewInfo(Dictionary<string, object> root, int viewIndex, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var views = Arr(root, "bufferViews");
            if (views == null || viewIndex < 0 || viewIndex >= views.Count)
                throw new InvalidDataException("GLB bufferView 索引越界（" + viewIndex + "）：" + name);
            var obj = views[viewIndex] as Dictionary<string, object>;
            if (obj == null) throw new InvalidDataException("GLB bufferView #" + viewIndex + " 不是对象：" + name);

            var info = new ViewInfo
            {
                Buffer = Int(obj, "buffer", 0),
                Offset = Int(obj, "byteOffset", 0),
                ByteLength = Int(obj, "byteLength", -1),
                ByteStride = Int(obj, "byteStride", 0),
                Target = Int(obj, "target", 0)
            };
            if (info.ByteLength < 0)
                throw new InvalidDataException("GLB bufferView #" + viewIndex + " 缺少合法的 byteLength：" + name);
            if (info.Offset < 0)
                throw new InvalidDataException("GLB bufferView #" + viewIndex + " 的 byteOffset 为负数：" + name);
            if (info.ByteStride < 0)
                throw new InvalidDataException("GLB bufferView #" + viewIndex + " 的 byteStride 为负数：" + name);
            if (info.Buffer != 0)
                throw new InvalidDataException("GLB 只支持单个内嵌 buffer（bufferView 引用了 buffer=" + info.Buffer + "）：" + name);

            var buffers = Arr(root, "buffers");
            if (buffers != null && buffers.Count > 1)
                throw new InvalidDataException("GLB 只支持单个 buffer（实际 " + buffers.Count + " 个）：" + name);
            if (buffers != null && buffers.Count == 1)
            {
                var bufferObj = buffers[0] as Dictionary<string, object>;
                string uri = Str(bufferObj, "uri");
                if (!string.IsNullOrEmpty(uri))
                    throw new InvalidDataException("不支持外部 buffer（uri=" + uri + "）：" + name);
            }
            return info;
        }

        /// <summary>读 accessor 为 float 数组（长度 = count * components）。expectedComponents &gt; 0 时校验分量数。</summary>
        internal static float[] ReadFloats(Dictionary<string, object> root, byte[] bin, int accessorIndex,
                                          string displayName, int expectedComponents, string what)
        {
            var info = ReadAccessorInfo(root, accessorIndex, displayName);
            if (expectedComponents > 0 && info.Components != expectedComponents)
                throw new InvalidDataException("GLB 的 " + (what ?? "accessor") + " 类型应为 " +
                                               TypeNameOf(expectedComponents) + "（实际 " + info.Type + "）：" + displayName);
            return ReadFloatsCore(root, bin, info, accessorIndex, displayName);
        }

        private static float[] ReadFloatsCore(Dictionary<string, object> root, byte[] bin, AccessorInfo info,
                                              int accessorIndex, string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var result = new float[checked(info.Count * info.Components)];
            if (info.BufferView < 0 || info.Count == 0) return result;   // 没有 bufferView 视为全 0（规范允许）

            var view = ReadViewInfo(root, info.BufferView, displayName);
            int componentSize = ComponentSizeOf(info.ComponentType);
            int elementSize = info.Components * componentSize;
            int stride = view.ByteStride > 0 ? view.ByteStride : elementSize;
            if (view.ByteStride > 0 && stride < elementSize)
                throw new InvalidDataException("GLB bufferView #" + info.BufferView + " 的 byteStride (" + stride +
                                               ") 小于元素大小 (" + elementSize + ")：" + name);

            long required = (long)info.ByteOffset + (long)(info.Count - 1) * stride + elementSize;
            if (required > view.ByteLength)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 数据超出 bufferView 长度（需要 " +
                                               required + " 字节，bufferView 只有 " + view.ByteLength + " 字节）：" + name);
            long end = (long)view.Offset + required;
            if (end > bin.Length)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 数据超出 BIN 分块长度（需要到 " +
                                               end + " 字节，BIN 只有 " + bin.Length + " 字节）：" + name);

            int offsetBase = (int)view.Offset + info.ByteOffset;
            for (int i = 0; i < info.Count; i++)
            {
                int rowOffset = offsetBase + i * stride;
                for (int c = 0; c < info.Components; c++)
                    result[i * info.Components + c] = ReadFloatComponent(bin, rowOffset + c * componentSize,
                                                                        info.ComponentType, info.Normalized);
            }
            return result;
        }

        /// <summary>读 accessor 为 int 数组（索引 / JOINTS_0）。FLOAT 类型视为非法。</summary>
        internal static int[] ReadInts(Dictionary<string, object> root, byte[] bin, int accessorIndex,
                                       string displayName, int expectedComponents, string what)
        {
            string name = string.IsNullOrEmpty(displayName) ? "(GLB)" : displayName;
            var info = ReadAccessorInfo(root, accessorIndex, displayName);
            if (expectedComponents > 0 && info.Components != expectedComponents)
                throw new InvalidDataException("GLB 的 " + (what ?? "accessor") + " 类型应为 " +
                                               TypeNameOf(expectedComponents) + "（实际 " + info.Type + "）：" + name);
            if (info.ComponentType == ComponentFloat)
                throw new InvalidDataException("GLB 的 " + (what ?? "accessor") + " 不能是 FLOAT 类型：" + name);

            var result = new int[checked(info.Count * info.Components)];
            if (info.BufferView < 0 || info.Count == 0) return result;

            var view = ReadViewInfo(root, info.BufferView, displayName);
            int componentSize = ComponentSizeOf(info.ComponentType);
            int elementSize = info.Components * componentSize;
            int stride = view.ByteStride > 0 ? view.ByteStride : elementSize;
            if (view.ByteStride > 0 && stride < elementSize)
                throw new InvalidDataException("GLB bufferView #" + info.BufferView + " 的 byteStride (" + stride +
                                               ") 小于元素大小 (" + elementSize + ")：" + name);

            long required = (long)info.ByteOffset + (long)(info.Count - 1) * stride + elementSize;
            if (required > view.ByteLength)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 数据超出 bufferView 长度（需要 " +
                                               required + " 字节，bufferView 只有 " + view.ByteLength + " 字节）：" + name);
            long end = (long)view.Offset + required;
            if (end > bin.Length)
                throw new InvalidDataException("GLB accessor #" + accessorIndex + " 数据超出 BIN 分块长度：" + name);

            int offsetBase = (int)view.Offset + info.ByteOffset;
            for (int i = 0; i < info.Count; i++)
            {
                int rowOffset = offsetBase + i * stride;
                for (int c = 0; c < info.Components; c++)
                    result[i * info.Components + c] = ReadIntComponent(bin, rowOffset + c * componentSize,
                                                                      info.ComponentType, name);
            }
            return result;
        }

        private static float ReadFloatComponent(byte[] bin, int offset, int componentType, bool normalized)
        {
            switch (componentType)
            {
                case ComponentFloat: return BitConverter.ToSingle(bin, offset);
                case ComponentUByte: return normalized ? bin[offset] / 255f : bin[offset];
                case ComponentUShort: return normalized ? BitConverter.ToUInt16(bin, offset) / 65535f : BitConverter.ToUInt16(bin, offset);
                case ComponentByte: { sbyte v = (sbyte)bin[offset]; return normalized ? v / 127f : v; }
                case ComponentShort: { short v = BitConverter.ToInt16(bin, offset); return normalized ? v / 32767f : v; }
                case ComponentUInt:
                    {
                        uint v = BitConverter.ToUInt32(bin, offset);
                        return normalized ? (float)(v / 4294967295.0) : v;
                    }
                default: return 0f;
            }
        }

        private static int ReadIntComponent(byte[] bin, int offset, int componentType, string displayName)
        {
            switch (componentType)
            {
                case ComponentUByte: return bin[offset];
                case ComponentUShort: return BitConverter.ToUInt16(bin, offset);
                case ComponentByte: return (sbyte)bin[offset];
                case ComponentShort: return BitConverter.ToInt16(bin, offset);
                case ComponentUInt:
                    {
                        uint v = BitConverter.ToUInt32(bin, offset);
                        if (v > int.MaxValue)
                            throw new InvalidDataException("GLB 无符号整数超出 Int32 范围：" + displayName);
                        return (int)v;
                    }
                default:
                    throw new InvalidDataException("GLB 索引/关节 accessor 的 componentType 不支持：" + componentType + "：" + displayName);
            }
        }

        /// <summary>警告列表（上限 200 条，避免损坏文件刷爆日志）。</summary>
        internal static void Warn(List<string> warnings, string message)
        {
            if (warnings == null) return;
            if (warnings.Count >= 200)
            {
                if (warnings.Count == 200) warnings.Add("（警告过多，后续省略）");
                return;
            }
            warnings.Add(message);
        }
    }

    /// <summary>GLB 二进制写入器：按 4 字节对齐累积 bufferView + accessor，并生成对应 JSON 片段。</summary>
    internal sealed class GlbBinWriter
    {
        private readonly MemoryStream _bin = new MemoryStream();
        private readonly List<string> _views = new List<string>();
        private readonly List<string> _accessors = new List<string>();

        internal long BinLength { get { return _bin.Length; } }

        internal int AddFloats(float[] data, int components, string type, int? target,
                               float[] min = null, float[] max = null)
        {
            if (data == null) data = new float[0];
            if (components <= 0 || data.Length % components != 0)
                throw new ArgumentException("二进制数据长度不是分量数的整数倍");
            var raw = new byte[data.Length * 4];
            for (int i = 0; i < data.Length; i++)
            {
                float value = data[i];
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                Array.Copy(BitConverter.GetBytes(value), 0, raw, i * 4, 4);
            }
            int view = AddView(raw, target);
            return AddAccessor(view, GlbJson.ComponentFloat, data.Length / components, type, false, min, max);
        }

        internal int AddUShorts(ushort[] data, int components, string type, int? target)
        {
            if (data == null) data = new ushort[0];
            if (components <= 0 || data.Length % components != 0)
                throw new ArgumentException("二进制数据长度不是分量数的整数倍");
            var raw = new byte[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                raw[i * 2] = (byte)data[i];
                raw[i * 2 + 1] = (byte)(data[i] >> 8);
            }
            int view = AddView(raw, target);
            return AddAccessor(view, GlbJson.ComponentUShort, data.Length / components, type, false, null, null);
        }

        internal int AddUInts(int[] data, int components, string type, int? target)
        {
            if (data == null) data = new int[0];
            if (components <= 0 || data.Length % components != 0)
                throw new ArgumentException("二进制数据长度不是分量数的整数倍");
            var raw = new byte[data.Length * 4];
            for (int i = 0; i < data.Length; i++) GlbJson.WriteU32(raw, i * 4, (uint)data[i]);
            int view = AddView(raw, target);
            return AddAccessor(view, GlbJson.ComponentUInt, data.Length / components, type, false, null, null);
        }

        /// <summary>写 count 个 4x4 单位矩阵（inverseBindMatrices 默认值）。</summary>
        internal int AddIdentityMat4(int count)
        {
            var data = new float[count * 16];
            for (int i = 0; i < count; i++)
            {
                data[i * 16 + 0] = 1f;
                data[i * 16 + 5] = 1f;
                data[i * 16 + 10] = 1f;
                data[i * 16 + 15] = 1f;
            }
            return AddFloats(data, 16, "MAT4", null);
        }

        internal byte[] BinBytes()
        {
            Align4();
            return _bin.ToArray();
        }

        internal string AccessorsJson() { return "[" + string.Join(",", _accessors.ToArray()) + "]"; }

        internal string ViewsJson() { return "[" + string.Join(",", _views.ToArray()) + "]"; }

        private int AddView(byte[] raw, int? target)
        {
            Align4();
            int offset = (int)_bin.Length;
            _bin.Write(raw, 0, raw.Length);
            var sb = new StringBuilder();
            sb.Append("{\"buffer\":0,\"byteOffset\":").Append(offset.ToString(GlbJson.Inv))
              .Append(",\"byteLength\":").Append(raw.Length.ToString(GlbJson.Inv));
            if (target.HasValue) sb.Append(",\"target\":").Append(target.Value.ToString(GlbJson.Inv));
            sb.Append('}');
            _views.Add(sb.ToString());
            return _views.Count - 1;
        }

        private int AddAccessor(int bufferView, int componentType, int count, string type, bool normalized,
                                float[] min, float[] max)
        {
            var sb = new StringBuilder();
            sb.Append("{\"bufferView\":").Append(bufferView.ToString(GlbJson.Inv))
              .Append(",\"componentType\":").Append(componentType.ToString(GlbJson.Inv))
              .Append(",\"count\":").Append(count.ToString(GlbJson.Inv))
              .Append(",\"type\":\"").Append(type).Append('"');
            if (normalized) sb.Append(",\"normalized\":true");
            if (min != null && min.Length > 0)
            {
                sb.Append(",\"min\":[");
                for (int i = 0; i < min.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Num(min[i]));
                }
                sb.Append(']');
            }
            if (max != null && max.Length > 0)
            {
                sb.Append(",\"max\":[");
                for (int i = 0; i < max.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Num(max[i]));
                }
                sb.Append(']');
            }
            sb.Append('}');
            _accessors.Add(sb.ToString());
            return _accessors.Count - 1;
        }

        private void Align4()
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        }

        /// <summary>JSON 数字格式：float 用 "R" 保证读回与二进制值一致（min/max 校验需要）。</summary>
        internal static string Num(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            return value.ToString("R", GlbJson.Inv);
        }
    }
}
