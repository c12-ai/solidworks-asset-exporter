using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SolidWorksAssetExporter.Core
{
    // A comparison key for already-published geometry. STL headers, normals,
    // facet order and winding are not part of the shape being compared.
    public static class StlGeometryFingerprint
    {
        public static string Compute(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("STL 路径不能为空。", "path");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var triangles = IsBinary(stream) ? ReadBinary(stream) : ReadAscii(stream);
                if (triangles.Count == 0) throw new InvalidDataException("STL 没有可比较的三角面：" + path);
                triangles.Sort(StringComparer.Ordinal);
                return FileHash.Sha256Text(string.Join("\n", triangles));
            }
        }

        private static bool IsBinary(Stream stream)
        {
            if (stream.Length < 84) return false;
            stream.Position = 80;
            using (var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, true))
            {
                var count = reader.ReadUInt32();
                stream.Position = 0;
                return count > 0 && 84L + 50L * count == stream.Length;
            }
        }

        private static List<string> ReadBinary(Stream stream)
        {
            var triangles = new List<string>();
            stream.Position = 80;
            using (var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, true))
            {
                var count = reader.ReadUInt32();
                if (count > int.MaxValue) throw new InvalidDataException("STL 三角面数量过大。");
                for (var index = 0; index < count; index++)
                {
                    reader.ReadBytes(12); // Normal is exporter metadata, not shape.
                    var vertices = new string[3];
                    for (var vertex = 0; vertex < 3; vertex++)
                        vertices[vertex] = Vertex(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    reader.ReadUInt16(); // Attribute byte count.
                    Array.Sort(vertices, StringComparer.Ordinal);
                    triangles.Add(string.Join(";", vertices));
                }
            }
            return triangles;
        }

        private static List<string> ReadAscii(Stream stream)
        {
            var triangles = new List<string>();
            var vertices = new List<string>(3);
            stream.Position = 0;
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !string.Equals(parts[0], "vertex", StringComparison.OrdinalIgnoreCase)) continue;
                    if (parts.Length != 4) throw new InvalidDataException("ASCII STL 顶点格式无效。");
                    vertices.Add(Vertex(float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture),
                        float.Parse(parts[3], CultureInfo.InvariantCulture)));
                    if (vertices.Count != 3) continue;
                    vertices.Sort(StringComparer.Ordinal);
                    triangles.Add(string.Join(";", vertices));
                    vertices.Clear();
                }
            }
            if (vertices.Count != 0) throw new InvalidDataException("ASCII STL 三角面不完整。");
            return triangles;
        }

        private static string Vertex(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) ||
                float.IsNaN(z) || float.IsInfinity(z)) throw new InvalidDataException("STL 顶点包含非有限坐标。");
            if (x == 0f) x = 0f;
            if (y == 0f) y = 0f;
            if (z == 0f) z = 0f;
            return x.ToString("R", CultureInfo.InvariantCulture) + "," +
                y.ToString("R", CultureInfo.InvariantCulture) + "," +
                z.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
