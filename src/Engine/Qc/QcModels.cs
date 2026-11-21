using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LodStudio.Engine
{
    // ══════════════════════════════════════════════════════════════════════════
    //  QC 子系统契约。实现见同目录下 QcTextFile.cs / QcLexer.cs / QcParser.cs /
    //  QcWriter.cs（由 QC 模块负责人创建；本文件只放共享数据模型）。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>QC 里的一个模型引用（$model / $body / $bodygroup studio）。</summary>
    public sealed class StudioRef
    {
        /// <summary>QC 里原样写的文件名（replacemodel 第一参数必须逐字用它）</summary>
        public string RawFile;
        /// <summary>文件名（含扩展名）</summary>
        public string Name;
        /// <summary>$bodygroup 名 / $body 的 bodypart 名 / "(主模型 xxx)"</summary>
        public string Bodygroup;
        /// <summary>bodypart 序号（整个人 include 链递增；$model 为 -1）</summary>
        public int BodygroupIndex;
        public bool IsMainModel;
        /// <summary>innode 参数（多子节点网格必需拆分；批量不支持 → 跳过）</summary>
        public string Innnode;
        /// <summary>$model 块里有 flexfile/flexcontroller/localvar → 加 nofacial 会丢表情</summary>
        public bool HasFacial;
        /// <summary>声明这个条目的文件（绝对路径，可能是 include 文件）</summary>
        public string DeclaredInQc;
        /// <summary>声明所在行（1 起）</summary>
        public int DeclaredLine;
        /// <summary>include 深度（0=根 QC）</summary>
        public int IncludeDepth;
        /// <summary>解析到的绝对路径；找不到为 null</summary>
        public string ResolvedPath;
        public bool Exists;
        /// <summary>正常路径都没找到、只按文件名递归找到同名文件：只提示不处理</summary>
        public bool Fallback;

        public override string ToString() { return RawFile; }
    }

    /// <summary>一个根 QC 的解析结果（含 $include 链、编码、写入安全信息）。</summary>
    public sealed class QcInfo
    {
        public string QcPath;
        public string QcDir;
        public string ModelName = "";
        public List<StudioRef> Studios = new List<StudioRef>();
        public List<string> Warnings = new List<string>();

        /// <summary>工具块以外的 $lod/$shadowlod 所在文件（可能是 include 文件）</summary>
        public List<string> ForeignLodFiles = new List<string>();
        /// <summary>含本工具标记块的文件</summary>
        public List<string> ToolMarkerFiles = new List<string>();
        /// <summary>有开始标记但缺结束标记的文件（禁止改这个 QC）</summary>
        public List<string> ToolBlockBrokenFiles = new List<string>();
        /// <summary>递归 include 读到的其他文件（绝对路径，不含根）</summary>
        public List<string> IncludedFiles = new List<string>();

        public bool ReadFailed;
        public bool IsStaticProp;
        public Encoding TextEncoding = new UTF8Encoding(false);
        public bool HasBom;
        public string NewLine = "\r\n";
        public int FilesRead;

        public bool HasForeignLod { get { return ForeignLodFiles.Count > 0; } }
        public bool HasToolMarker { get { return ToolMarkerFiles.Count > 0; } }
        public bool RootHasToolMarker
        {
            get
            {
                foreach (var f in ToolMarkerFiles)
                    if (string.Equals(f, QcPath, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }
        public bool ToolBlockBroken { get { return ToolBlockBrokenFiles.Count > 0; } }
        public List<StudioRef> Usable
        {
            get
            {
                var list = new List<StudioRef>();
                foreach (var s in Studios) if (s.Exists) list.Add(s);
                return list;
            }
        }
    }

    /// <summary>备份策略的落点解析结果（由调用方按设置算好，引擎只执行）。</summary>
    public sealed class QcBackupRequest
    {
        public bool Enabled = true;
        public string CentralBaseDir;   // null = 备份在原文件旁
        public string ProjectRoot;      // 集中备份时镜像相对路径用
    }
}
