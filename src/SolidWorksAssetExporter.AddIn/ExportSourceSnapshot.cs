using System;
using System.IO;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class ExportSourceFileSnapshot
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; }

        public static ExportSourceFileSnapshot Capture(string path, Func<string, string> hashFile)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new ValidationException("预览源文件不存在：" + fullPath);
            var before = new FileInfo(fullPath);
            var hash = hashFile == null ? string.Empty : hashFile(fullPath);
            var after = new FileInfo(fullPath);
            if (before.Length != after.Length || before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks)
                throw new ValidationException("建立预览快照期间文件发生变化，请保存完成后重新预览：" + fullPath);
            return new ExportSourceFileSnapshot
            {
                Path = fullPath,
                Size = after.Length,
                LastWriteUtcTicks = after.LastWriteTimeUtc.Ticks,
                Sha256 = hash
            };
        }

        public void ValidateUnchanged()
        {
            if (string.IsNullOrWhiteSpace(Path) || !File.Exists(Path))
                throw new ValidationException("预览后源文件已被删除或移动，请重新预览：" + Path);
            var current = new FileInfo(Path);
            if (current.Length != Size || current.LastWriteTimeUtc.Ticks != LastWriteUtcTicks)
                throw new ValidationException("预览后源文件已发生变化，请重新预览：" + Path);
        }
    }
}
