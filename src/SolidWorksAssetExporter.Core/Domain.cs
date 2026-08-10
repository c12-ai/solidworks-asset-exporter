using System;
using System.Collections.Generic;

namespace SolidWorksAssetExporter.Core
{
    public enum DocumentKind { Part, Assembly, Drawing, Unknown }
    public enum ScanClassification { AssetBoundary, ContainsAsset, NoAsset }
    public enum ExportNodeKind { Group, Asset, Project }
    public enum ProjectMeshFormat { Step, Stl }

    public sealed class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }
    }

    public sealed class ModelDescriptor
    {
        public ModelDescriptor()
        {
            FileProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ConfigurationProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string FullPath { get; set; }
        public string FileName { get; set; }
        public string InternalCreationTime { get; set; }
        public string Configuration { get; set; }
        public string DisplayState { get; set; }
        public DocumentKind DocumentKind { get; set; }
        public bool IsSaved { get; set; }
        public bool IsDirty { get; set; }
        public IDictionary<string, string> FileProperties { get; set; }
        public IDictionary<string, string> ConfigurationProperties { get; set; }
    }

    public interface ICadNode
    {
        string InstanceId { get; }
        string Name { get; }
        string InstancePath { get; }
        bool IsVisible { get; }
        bool IsSuppressed { get; }
        bool IsEnvelope { get; }
        bool IsFixed { get; }
        ModelDescriptor Model { get; }
        Matrix4 WorldTransform { get; }
        IEnumerable<ICadNode> GetChildren();
    }

    public sealed class ScanNode
    {
        public ScanNode()
        {
            Children = new List<ScanNode>();
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public ICadNode Source { get; set; }
        public ScanClassification Classification { get; set; }
        public IDictionary<string, string> Properties { get; set; }
        public int? AssetVersion { get; set; }
        public IList<ScanNode> Children { get; private set; }
    }

    public sealed class ExportNode
    {
        public ExportNode()
        {
            Children = new List<ExportNode>();
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Id { get; set; }
        public string ParentId { get; set; }
        public string Name { get; set; }
        public ExportNodeKind Kind { get; set; }
        public Pose Pose { get; set; }
        public ICadNode Source { get; set; }
        public string GeometryUuid { get; set; }
        public string AssetId { get; set; }
        public string MeshFile { get; set; }
        public IDictionary<string, string> Properties { get; set; }
        public IList<ExportNode> Children { get; private set; }
    }

    public sealed class AssemblyExportPlan
    {
        public AssemblyExportPlan() { Roots = new List<ExportNode>(); }
        public string AssemblyUuid { get; set; }
        public int AssemblyVersion { get; set; }
        public ProjectMeshFormat MeshFormat { get; set; }
        public IList<ExportNode> Roots { get; private set; }
    }

    public struct Quaternion
    {
        public Quaternion(double x, double y, double z, double w)
        { X = x; Y = y; Z = z; W = w; }
        public double X, Y, Z, W;
    }

    public struct Pose
    {
        public Pose(double tx, double ty, double tz, Quaternion rotation)
        { Tx = tx; Ty = ty; Tz = tz; Rotation = rotation; }
        public double Tx, Ty, Tz;
        public Quaternion Rotation;
        public static Pose FromTransform(Matrix4 transform)
        {
            return new Pose(transform[0, 3], transform[1, 3], transform[2, 3], transform.ToQuaternion());
        }
    }

    public sealed class Matrix4
    {
        private readonly double[] _m;

        public Matrix4(double[] rowMajor)
        {
            if (rowMajor == null || rowMajor.Length != 16) throw new ArgumentException("Matrix must contain 16 values.");
            _m = (double[])rowMajor.Clone();
        }

        public double this[int row, int column] { get { return _m[(row * 4) + column]; } }

        public static Matrix4 Identity
        {
            get { return new Matrix4(new[] { 1d, 0d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 0d, 1d }); }
        }

        public Matrix4 Multiply(Matrix4 other)
        {
            var result = new double[16];
            for (var r = 0; r < 4; r++)
                for (var c = 0; c < 4; c++)
                    for (var k = 0; k < 4; k++)
                        result[(r * 4) + c] += this[r, k] * other[k, c];
            return new Matrix4(result);
        }

        public Matrix4 InverseRigid()
        {
            var result = new double[16];
            result[15] = 1d;
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    result[(r * 4) + c] = this[c, r];
            for (var r = 0; r < 3; r++)
                result[(r * 4) + 3] = -(result[(r * 4)] * this[0, 3] + result[(r * 4) + 1] * this[1, 3] + result[(r * 4) + 2] * this[2, 3]);
            return new Matrix4(result);
        }

        public Quaternion ToQuaternion()
        {
            double x, y, z, w;
            var trace = this[0, 0] + this[1, 1] + this[2, 2];
            if (trace > 0d)
            {
                var s = Math.Sqrt(trace + 1d) * 2d;
                w = 0.25d * s;
                x = (this[2, 1] - this[1, 2]) / s;
                y = (this[0, 2] - this[2, 0]) / s;
                z = (this[1, 0] - this[0, 1]) / s;
            }
            else if (this[0, 0] > this[1, 1] && this[0, 0] > this[2, 2])
            {
                var s = Math.Sqrt(1d + this[0, 0] - this[1, 1] - this[2, 2]) * 2d;
                w = (this[2, 1] - this[1, 2]) / s;
                x = 0.25d * s;
                y = (this[0, 1] + this[1, 0]) / s;
                z = (this[0, 2] + this[2, 0]) / s;
            }
            else if (this[1, 1] > this[2, 2])
            {
                var s = Math.Sqrt(1d + this[1, 1] - this[0, 0] - this[2, 2]) * 2d;
                w = (this[0, 2] - this[2, 0]) / s;
                x = (this[0, 1] + this[1, 0]) / s;
                y = 0.25d * s;
                z = (this[1, 2] + this[2, 1]) / s;
            }
            else
            {
                var s = Math.Sqrt(1d + this[2, 2] - this[0, 0] - this[1, 1]) * 2d;
                w = (this[1, 0] - this[0, 1]) / s;
                x = (this[0, 2] + this[2, 0]) / s;
                y = (this[1, 2] + this[2, 1]) / s;
                z = 0.25d * s;
            }
            var length = Math.Sqrt(x * x + y * y + z * z + w * w);
            return new Quaternion(x / length, y / length, z / length, w / length);
        }
    }
}
