using System;
using System.IO;
using System.Text;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class WanxiangUploadLog
    {
        private readonly object _sync = new object();

        private WanxiangUploadLog(string path)
        {
            Path = path;
        }

        public string Path { get; private set; }

        public static WanxiangUploadLog Create()
        {
            return CreateInDirectory(LogDirectory);
        }

        internal static WanxiangUploadLog CreateInDirectory(string directory)
        {
            directory = System.IO.Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "wanxiang-upload-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
            return new WanxiangUploadLog(path);
        }

        public static string FindLatest()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return string.Empty;
                string latest = null;
                DateTime latestTime = DateTime.MinValue;
                foreach (var file in Directory.EnumerateFiles(LogDirectory, "wanxiang-upload-*.log",
                    SearchOption.TopDirectoryOnly))
                {
                    var time = File.GetLastWriteTimeUtc(file);
                    if (latest == null || time > latestTime)
                    {
                        latest = file;
                        latestTime = time;
                    }
                }
                return latest ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Write(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " +
                OneLine(message) + Environment.NewLine;
            lock (_sync)
            {
                File.AppendAllText(Path, line, new UTF8Encoding(false));
            }
        }

        public static string SummarizeResponse(string value)
        {
            var compact = OneLine(value);
            const int limit = 800;
            return compact.Length <= limit ? compact : compact.Substring(0, limit) + "... [truncated]";
        }

        private static string LogDirectory
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "SolidWorksAssetExporter", "uploads");
            }
        }

        private static string OneLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "<empty>";
            var builder = new StringBuilder(value.Length);
            var previousWasWhitespace = false;
            foreach (var character in value.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasWhitespace) builder.Append(' ');
                    previousWasWhitespace = true;
                }
                else
                {
                    builder.Append(character);
                    previousWasWhitespace = false;
                }
            }
            return builder.ToString();
        }
    }
}
