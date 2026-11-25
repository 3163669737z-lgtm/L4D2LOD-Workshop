using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LodStudio.Engine
{
    public sealed class BackupScanResult
    {
        public List<string> BackupFiles = new List<string>();
        public List<string> OrphanLodFiles = new List<string>();
        public List<string> SkippedReasons = new List<string>();
    }

    public static class QcBackupManager
    {
        private const string BackupSuffix = ".lodbak";
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private static readonly Regex LodName = new Regex(@"^.+_(?:lod[1-7]|shadowlod).*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly object Gate = new object();
        // A mesh path alone cannot identify the project tree that might reference it.
        private static readonly HashSet<string> ScannedRoots = new HashSet<string>(Paths);

        public static BackupScanResult Scan(IEnumerable<string> rootDirsOrQcPaths)
        {
            lock (Gate)
            {
                var result = new BackupScanResult();
                var roots = new HashSet<string>(Paths);
                foreach (string input in rootDirsOrQcPaths ?? Enumerable.Empty<string>())
                {
                    try
                    {
                        string path = Path.GetFullPath(input);
                        if (!Directory.Exists(path) && path.EndsWith(".qc", StringComparison.OrdinalIgnoreCase))
                            path = Path.GetDirectoryName(path);
                        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
                        roots.Add(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                    }
                    catch (Exception ex) { result.SkippedReasons.Add(input + "： " + ex.Message); }
                }
                ScannedRoots.UnionWith(roots);
                ScanTrees(roots, result);
                return result;
            }
        }

        public static int RestoreOriginalQcs(IEnumerable<string> backupFiles, out List<string> errors)
        {
            lock (Gate)
            {
                errors = new List<string>();
                int count = 0;
                var seen = new HashSet<string>(Paths);
                foreach (string input in backupFiles ?? Enumerable.Empty<string>())
                {
                    try
                    {
                        string backup = Path.GetFullPath(input);
                        if (!seen.Add(backup)) continue;
                        if (!IsOriginalBackup(backup)) throw new IOException("需要 .qc.lodbak 原始备份。");
                        CheckPath(backup);
                        string qc = backup.Substring(0, backup.Length - BackupSuffix.Length);
                        CheckPath(qc, allowMissing: true);
                        FileUtil.CopyAtomic(backup, qc);
                        count++;
                    }
                    catch (Exception ex) { errors.Add(input + "： " + ex.Message); }
                }
                return count;
            }
        }

        public static int DeleteOrphanLodFiles(IEnumerable<string> orphanFiles, out List<string> errors)
        {
            lock (Gate)
            {
                errors = new List<string>();
                var requested = new HashSet<string>(Paths);
                foreach (string input in orphanFiles ?? Enumerable.Empty<string>())
                {
                    try
                    {
                        string path = Path.GetFullPath(input);
                        if (!IsGeneratedMesh(path)) throw new IOException("需要生成的 LOD 网格。");
                        if (!ScannedRoots.Any(root => IsUnder(root, path)))
                            throw new IOException("请先扫描该网格所在的工程目录。");
                        requested.Add(path);
                    }
                    catch (Exception ex) { errors.Add(input + "： " + ex.Message); }
                }
                if (requested.Count == 0) return 0;

                // Re-read the project before deletion, including QC files added since the scan.
                var current = new BackupScanResult();
                ScanTrees(ScannedRoots, current);
                var verified = new HashSet<string>(current.OrphanLodFiles, Paths);
                int count = 0;
                foreach (string path in requested)
                {
                    try
                    {
                        if (!verified.Contains(path))
                            throw new IOException("无法确认网格未被 QC 引用。");
                        if ((CheckPath(path) & FileAttributes.Directory) != 0)
                            throw new IOException("需要 LOD 网格文件。");
                        File.Delete(path);
                        count++;
                    }
                    catch (Exception ex) { errors.Add(path + "： " + ex.Message); }
                }
                return count;
            }
        }

        private static void ScanTrees(HashSet<string> roots, BackupScanResult result)
        {
            var backups = new HashSet<string>(Paths);
            var meshes = new HashSet<string>(Paths);
            var referenced = new HashSet<string>(Paths);
            var unresolvedNames = new HashSet<string>(Paths);
            var protectedRoots = new List<string>();

            foreach (string root in roots.Where(r => !roots.Any(other => !Paths.Equals(r, other) && IsUnder(other, r))))
            {
                var qcs = new List<string>();
                var directories = new Stack<string>();
                directories.Push(root);
                bool failed = false;
                void Protect(string path, Exception error)
                {
                    failed = true;
                    result.SkippedReasons.Add(root + "： " + path + "： " + error.Message);
                }
                while (directories.Count > 0)
                {
                    string dir = directories.Pop();
                    try
                    {
                        CheckPath(dir);
                        foreach (string path in Directory.EnumerateFileSystemEntries(dir))
                        {
                            FileAttributes attrs = File.GetAttributes(path);
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                            {
                                if ((attrs & FileAttributes.Directory) != 0 || path.EndsWith(".qc", StringComparison.OrdinalIgnoreCase))
                                    Protect(path, new IOException("跳过重解析点。"));
                                continue;
                            }
                            if ((attrs & FileAttributes.Directory) != 0) directories.Push(path);
                            else if (IsOriginalBackup(path)) backups.Add(path);
                            else if (path.EndsWith(".qc", StringComparison.OrdinalIgnoreCase)) qcs.Add(path);
                            else if (IsGeneratedMesh(path)) meshes.Add(path);
                        }
                    }
                    catch (Exception ex) { Protect(dir, ex); }
                }
                foreach (string qc in qcs)
                {
                    try { new ReferenceScan(qc, referenced, unresolvedNames).Read(); }
                    catch (Exception ex) { Protect(qc, ex); }
                }
                if (failed) protectedRoots.Add(root);
            }
            result.BackupFiles.AddRange(backups.OrderBy(p => p, Paths));
            result.OrphanLodFiles.AddRange(meshes.Where(p => !referenced.Contains(p) &&
                !unresolvedNames.Contains(Path.GetFileName(p)) &&
                !unresolvedNames.Contains(Path.GetFileNameWithoutExtension(p)) &&
                !protectedRoots.Any(root => IsUnder(root, p))).OrderBy(p => p, Paths));
        }

        private sealed class ReferenceScan
        {
            private readonly string rootQc;
            private readonly string rootDir;
            private readonly HashSet<string> referenced;
            private readonly HashSet<string> unresolvedNames;
            private readonly HashSet<string> activeIncludes = new HashSet<string>(Paths);
            private readonly Stack<string> directoryStack = new Stack<string>();
            private string currentDir;

            public ReferenceScan(string qc, HashSet<string> referenced, HashSet<string> unresolvedNames)
            {
                rootQc = qc;
                rootDir = currentDir = Path.GetDirectoryName(qc);
                this.referenced = referenced;
                this.unresolvedNames = unresolvedNames;
            }

            public void Read() => ReadFile(rootQc, 0);

            private void ReadFile(string file, int depth)
            {
                if (depth > QcParser.MaxIncludeDepth || !activeIncludes.Add(file))
                    throw new IOException("无法展开 $include：" + file);

                CheckPath(file);
                string dir = Path.GetDirectoryName(file);
                var tokens = ReadTokens(file);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    if (token.IsBrace) continue;
                    string Argument(int offset)
                    {
                        if (i + offset >= tokens.Count || tokens[i + offset].IsBrace ||
                            (!tokens[i + offset].Quoted && tokens[i + offset].Text.StartsWith("$", StringComparison.Ordinal)) ||
                            tokens[i + offset].Text.Length == 0)
                            throw new InvalidDataException(file + " 缺少 " + token.Text + " 参数。");
                        return tokens[i + offset].Text;
                    }
                    void RequireBlock(int offset)
                    {
                        if (i + offset >= tokens.Count || !tokens[i + offset].IsWord("{"))
                            throw new InvalidDataException(file + " 缺少 " + token.Text + " 块。");
                    }

                    switch (token.Quoted ? "" : token.Text.ToLowerInvariant())
                    {
                        case "$include":
                            string rawInclude = Argument(1);
                            string include = ResolveInclude(rawInclude, dir);
                            if (include == null) throw new FileNotFoundException(file + " 无法解析 $include " + rawInclude);
                            ReadFile(include, depth + 1);
                            i++;
                            continue;
                        case "$pushd":
                            directoryStack.Push(currentDir);
                            goto case "$cd";
                        case "$cd":
                            currentDir = Path.GetFullPath(Path.Combine(currentDir, Argument(1)));
                            i++;
                            continue;
                        case "$popd":
                            if (directoryStack.Count > 0) currentDir = directoryStack.Pop();
                            continue;
                        case "$modelname": Argument(1); break;
                        case "$model":
                        case "$body":
                        case "replacemodel": Argument(1); Argument(2); break;
                        case "$bodygroup":
                        case "$nekomodel": Argument(1); RequireBlock(2); break;
                        case "$lod":
                            if (!double.TryParse(Argument(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) ||
                                double.IsNaN(distance) || double.IsInfinity(distance))
                                throw new InvalidDataException(file + " 的 $lod 距离无效。");
                            RequireBlock(2);
                            break;
                        case "$shadowlod": RequireBlock(1); break;
                    }

                    // Inspect all path-shaped tokens, including replacements in the tool block
                    // and model references in QC directives outside the planning parser.
                    RecordReference(token.Text, dir);
                }
                activeIncludes.Remove(file);
            }

            private string ResolveInclude(string raw, string dir)
            {
                foreach (string folder in new[] { currentDir, rootDir, dir })
                {
                    string path = Path.GetFullPath(Path.Combine(folder, raw));
                    if (File.Exists(path)) return path;
                }
                string fallback = Path.Combine(rootDir, Path.GetFileName(raw));
                return File.Exists(fallback) ? fallback : null;
            }

            private void RecordReference(string raw, string dir)
            {
                if (raw.IndexOf("_lod", StringComparison.OrdinalIgnoreCase) < 0 &&
                    raw.IndexOf("_shadowlod", StringComparison.OrdinalIgnoreCase) < 0) return;
                string name = Path.GetFileName(raw);
                string extension = Path.GetExtension(name);
                if (!LodName.IsMatch(Path.GetFileNameWithoutExtension(name))) return;
                bool extensionless = extension.Length == 0;
                if (!extensionless && !QcPathResolver.IsSupportedModelExtension(extension)) return;

                string[] suffixes = extensionless ? new[] { ".glb", ".smd", ".dmx" } : new[] { "" };
                bool found = false;
                foreach (string folder in new[] { currentDir, rootDir, dir, Path.GetDirectoryName(rootDir) }.Distinct(Paths))
                {
                    if (folder == null) continue;
                    foreach (string suffix in suffixes)
                    {
                        string path = Path.GetFullPath(Path.Combine(folder, raw + suffix));
                        if (File.Exists(path)) { referenced.Add(path); found = true; }
                    }
                }
                if (!found) unresolvedNames.Add(name);
            }
        }

        private static List<QcToken> ReadTokens(string file)
        {
            string raw = QcParser.ReadTextSmart(file);
            QcLexer.MaskToolBlocks(raw, out bool broken);
            if (broken || raw.IndexOf('\0') >= 0) throw new InvalidDataException(file + " 的 QC 内容不完整。");
            string masked = QcLexer.MaskComments(raw);
            for (int i = 0; i + 1 < raw.Length; i++)
            {
                if (raw[i] != '/' || masked[i] != ' ') continue;
                if (raw[i + 1] == '/')
                {
                    i = raw.IndexOf('\n', i + 2);
                    if (i < 0) break;
                }
                else if (raw[i + 1] == '*')
                {
                    int close = raw.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0) throw new InvalidDataException(file + " 的注释块未闭合。");
                    i = close + 1;
                }
            }

            var tokens = QcLexer.Tokenize(masked);
            int depth = 0;
            foreach (var token in tokens)
            {
                int end = token.Pos + token.Text.Length + 1;
                if (token.Quoted && (end >= masked.Length || masked[end] != '"'))
                    throw new InvalidDataException(file + " 的引号未闭合。");
                if (token.IsWord("{")) depth++;
                if (token.IsWord("}") && --depth < 0)
                    throw new InvalidDataException(file + " 的括号不匹配。");
            }
            if (depth != 0) throw new InvalidDataException(file + " 的括号不匹配。");
            return tokens;
        }

        private static bool IsUnder(string root, string path) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);

        private static bool IsOriginalBackup(string path) => path.EndsWith(".qc" + BackupSuffix, StringComparison.OrdinalIgnoreCase);

        private static bool IsGeneratedMesh(string path) => QcPathResolver.IsSupportedModelExtension(Path.GetExtension(path)) &&
            LodName.IsMatch(Path.GetFileNameWithoutExtension(path));

        private static FileAttributes CheckPath(string path, bool allowMissing = false)
        {
            for (string dir = Path.GetDirectoryName(path); dir != null; dir = Path.GetDirectoryName(dir))
                RejectLink(dir, File.GetAttributes(dir));
            FileAttributes attrs;
            try { attrs = File.GetAttributes(path); }
            catch (FileNotFoundException) when (allowMissing) { return 0; }
            RejectLink(path, attrs);
            return attrs;
        }

        private static void RejectLink(string path, FileAttributes attrs)
        {
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("跳过重解析点：" + path);
        }
    }
}
