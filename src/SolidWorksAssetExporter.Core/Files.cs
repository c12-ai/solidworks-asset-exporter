using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SolidWorksAssetExporter.Core
{
    public static class FileHash
    {
        public static string Sha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var hash = SHA256.Create())
                return ToHex(hash.ComputeHash(stream));
        }

        public static string Sha256Text(string value)
        {
            using (var hash = SHA256.Create())
                return ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
        }

        public static string PackageFingerprint(IEnumerable<string> files, string baseDirectory)
        {
            if (files == null) throw new ArgumentNullException("files");
            var root = Path.GetFullPath(baseDirectory ?? string.Empty);
            var entries = files.Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Canonical.Join(PathPolicy.RelativeTo(root, path), Sha256(path)));
            return Sha256Text(string.Join("\n", entries));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }

    public static class PathPolicy
    {
        public static string CombineUnderRoot(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("根目录不能为空。", "root");
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("相对路径不能为空。", "relativePath");
            if (Path.IsPathRooted(relativePath)) throw new ValidationException("清单文件路径必须是相对路径: [" + relativePath + "]。");
            var fullRoot = EnsureSeparator(Path.GetFullPath(root));
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("路径越出导出根目录: [" + relativePath + "]。");
            return candidate;
        }

        public static string RelativeTo(string root, string path)
        {
            var fullRoot = EnsureSeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("文件不在指定根目录内: [" + fullPath + "]。");
            return fullPath.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string EnsureSeparator(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                return path + Path.DirectorySeparatorChar;
            return path;
        }
    }

    public sealed class DirectoryTransaction : IDisposable
    {
        private bool _committed;

        public DirectoryTransaction(string finalDirectory)
        {
            if (string.IsNullOrWhiteSpace(finalDirectory)) throw new ArgumentException("最终目录不能为空。", "finalDirectory");
            FinalDirectory = Path.GetFullPath(finalDirectory);
            var parent = Path.GetDirectoryName(FinalDirectory);
            if (string.IsNullOrWhiteSpace(parent)) throw new ValidationException("最终目录没有有效父目录。");
            Directory.CreateDirectory(parent);
            StagingDirectory = Path.Combine(parent, ".staging-" + Path.GetFileName(FinalDirectory) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StagingDirectory);
        }

        public string StagingDirectory { get; private set; }
        public string FinalDirectory { get; private set; }

        public void Commit()
        {
            if (_committed) throw new InvalidOperationException("事务已经提交。");
            if (Directory.Exists(FinalDirectory) || File.Exists(FinalDirectory))
                throw new ValidationException("目标版本目录已经存在，禁止覆盖: [" + FinalDirectory + "]。");
            Directory.Move(StagingDirectory, FinalDirectory);
            _committed = true;
        }

        public void Dispose()
        {
            if (!_committed && Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, true);
        }
    }
}
