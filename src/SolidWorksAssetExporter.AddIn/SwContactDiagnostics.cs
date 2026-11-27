using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal static class SwContactDiagnostics
    {
        private const int ExportLimit = 24;
        private const int BindingLimit = 8;

        internal static IList<ContactDiagnostic> ForExport(SldWorks app, ModelDoc2 doc, IEnumerable<ExportNode> assets, AssemblyRelations graph)
        {
            var byId = assets.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var pairs = graph.Relations.Select(relation => Tuple.Create(byId[relation.ParentNodeId], byId[relation.ChildNodeId], relation.Pose));
            return Check(app, doc, pairs, ExportLimit, TimeSpan.FromSeconds(4));
        }

        internal static IList<ContactDiagnostic> ForBinding(SldWorks app, ModelDoc2 doc, ExportNode parent, IEnumerable<ExportNode> children)
        {
            var parentTransform = ReadWorldTransform(parent);
            var parentInverse = parentTransform == null ? null : parentTransform.InverseRigid();
            var pairs = children.Select(child => Tuple.Create(parent, child, ReadRelativePose(parentInverse, child)));
            return Check(app, doc, pairs, BindingLimit, TimeSpan.FromSeconds(2));
        }

        private static IList<ContactDiagnostic> Check(SldWorks app, ModelDoc2 doc,
            IEnumerable<Tuple<ExportNode, ExportNode, RelationPose>> pairs, int limit, TimeSpan duration)
        {
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var candidates = pairs.Select(pair => new ContactCandidate(pair.Item1, pair.Item2,
                Canonical.Join(EndpointKey(keys, pair.Item1), EndpointKey(keys, pair.Item2)), pair.Item3)).ToArray();
            if (candidates.Length == 0) return new List<ContactDiagnostic>();
            var clock = Stopwatch.StartNew();
            var measurer = new NativeContactMeasurer(app, doc, () => clock.Elapsed <= duration);
            return ContactDiagnostics.Analyze(candidates, measurer.Measure, measurer.HasTime, limit);
        }

        private static RelationPose ReadRelativePose(Matrix4 parentInverse, ExportNode child)
        {
            if (parentInverse == null) return null;
            var childTransform = ReadWorldTransform(child);
            return childTransform == null ? null : RelationPose.From(Pose.FromTransform(parentInverse.Multiply(childTransform)));
        }

        private static Matrix4 ReadWorldTransform(ExportNode node)
        {
            try { return node.Source.WorldTransform; }
            catch (COMException) { return null; }
            catch (ValidationException) { return null; }
        }

        private static string EndpointKey(IDictionary<string, string> keys, ExportNode node)
        {
            string key;
            if (keys.TryGetValue(node.Id, out key)) return key;
            var source = node.Source as SwCadNode;
            key = node.Id;
            try
            {
                if (source != null && source.Component != null && !string.IsNullOrEmpty(node.AssetId) &&
                    (source.SourceDocumentKind == DocumentKind.Part || source.SourceDocumentKind == DocumentKind.Assembly &&
                     source.Component.Solving == (int)swComponentSolvingOption_e.swComponentRigidSolving))
                {
                    key = Canonical.Join(node.AssetId, source.SourcePath, source.Component.ReferencedConfiguration,
                        source.Component.ReferencedDisplayState);
                }
            }
            catch (COMException) { }
            catch (ValidationException) { }
            keys.Add(node.Id, key);
            return key;
        }

        private sealed class NativeContactMeasurer
        {
            private const int MaximumBodyCount = 12;
            private const int MaximumComponentCount = 24;
            private const int MaximumBodyPairs = 48;
            private const double KernelTolerance = 1e-7;
            private readonly SldWorks _app;
            private readonly ModelDoc2 _doc;
            private readonly Func<bool> _hasTime;
            private Modeler _modeler;

            internal NativeContactMeasurer(SldWorks app, ModelDoc2 doc, Func<bool> hasTime)
            { _app = app; _doc = doc; _hasTime = hasTime; }

            internal bool HasTime() { return _hasTime(); }

            internal ContactMeasurement Measure(ContactCandidate candidate)
            {
                var overlap = 0d;
                try
                {
                    if (_doc == null || !Equals(_app.ActiveDoc, _doc)) return Unknown("活动装配已切换。");
                    if (_modeler == null) _modeler = _app.GetModeler() as Modeler;
                    if (_modeler == null) return Unknown("SOLIDWORKS 未提供实体测量器。");
                    using (var parent = new Shape())
                    using (var child = new Shape())
                    {
                        string issue;
                        if (!ReadShape(candidate.Parent.Source as SwCadNode, parent, out issue) ||
                            !ReadShape(candidate.Child.Source as SwCadNode, child, out issue)) return Unknown(issue);

                        if (parent.BodyCount * child.BodyCount > MaximumBodyPairs)
                            return Unknown("组件实体组合数超过单次精确检查预算。");

                        var minimum = double.PositiveInfinity;
                        var uncertainty = (string)null;
                        foreach (var left in parent.Leaves)
                        foreach (var right in child.Leaves)
                        {
                            if (!HasTime()) return Partial(overlap, "本次原生接触测量达到时间预算。");
                            foreach (var first in left.Bodies)
                            foreach (var second in right.Bodies)
                            {
                                if (first.Bounds != null && second.Bounds != null && SeparatedBounds(first.Bounds, second.Bounds)) continue;
                                if (!HasTime()) return Partial(overlap, "本次原生接触测量达到时间预算。");
                                double volume;
                                if (!TryIntersection(first.Copy, second.Copy, out volume))
                                {
                                    uncertainty = "SOLIDWORKS 未能确认候选实体是否相交。";
                                    continue;
                                }
                                overlap = Math.Max(overlap, volume);
                                if (overlap >= ContactDiagnostics.SignificantOverlap)
                                    return new ContactMeasurement { Overlap = overlap };
                            }

                            if (!HasTime()) return Partial(overlap, "本次原生接触测量达到时间预算。");
                            object firstPoint, secondPoint;
                            var distance = _doc.ClosestDistance(left.Component, right.Component, out firstPoint, out secondPoint);
                            if (ValidDistance(distance, firstPoint, secondPoint)) minimum = Math.Min(minimum, distance);
                            else uncertainty = "SOLIDWORKS 未能取得精确最短间距。";
                        }
                        return new ContactMeasurement
                        {
                            Overlap = overlap, Gap = uncertainty == null && Finite(minimum) ? (double?)minimum : null,
                            UnconfirmedReason = uncertainty
                        };
                    }
                }
                catch (COMException) { return Partial(overlap, "SOLIDWORKS 实体接口没有返回可用结果。"); }
                catch (InvalidCastException) { return Partial(overlap, "SOLIDWORKS 返回了不支持的几何数据。"); }
                catch (ValidationException) { return Partial(overlap, "组件几何或位姿不可用。"); }
            }

            private bool ReadShape(SwCadNode node, Shape shape, out string issue)
            {
                if (node == null || node.Component == null)
                { issue = "未找到精确的 SOLIDWORKS 连续实体实例。"; return false; }
                if (!ReadComponent(node.Component, shape, out issue)) return false;
                if (shape.BodyCount != 0) return true;
                issue = "找不到可用于精确计算的可见实体。";
                return false;
            }

            private bool ReadComponent(Component2 component, Shape shape, out string issue)
            {
                issue = null;
                if (!HasTime() || ++shape.ComponentsVisited > MaximumComponentCount)
                { issue = "子装配范围超过一次精确几何检查的预算。"; return false; }
                if (SwComponentState.IsSuppressed(component) || !SwComponentState.IsVisible(component) || component.IsEnvelope()) return true;
                var ext = Path.GetExtension(component.GetPathName());
                if (string.Equals(ext, ".SLDASM", StringComparison.OrdinalIgnoreCase))
                {
                    var children = component.GetChildren() as Array;
                    if (children == null)
                    { issue = "无法读取子装配的已解析组件。"; return false; }
                    foreach (var child in children.OfType<Component2>())
                        if (!ReadComponent(child, shape, out issue)) return false;
                    return true;
                }
                if (!string.Equals(ext, ".SLDPRT", StringComparison.OrdinalIgnoreCase))
                { issue = "无法取得该模型类型的实体。"; return false; }

                object bodyInfo;
                var bodies = component.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as Array;
                var types = bodyInfo as Array;
                if (bodies == null || bodies.Length == 0 || types == null || types.Length != bodies.Length)
                { issue = "没有可用的已解析实体或实体信息。"; return false; }
                if (shape.BodyCount + bodies.Length > MaximumBodyCount)
                { issue = "零件实体数超过一次精确几何检查的预算。"; return false; }
                if (types.Cast<object>().Any(value => !NormalBody(value)))
                { issue = "组件含上下文修改的实体，未确认其原生测量坐标。"; return false; }

                var transform = component.Transform2;
                if (transform == null) { issue = "组件的装配变换不可用。"; return false; }
                var leaf = new Leaf(component);
                shape.Leaves.Add(leaf);
                foreach (var value in bodies)
                {
                    if (!HasTime()) { issue = "本次原生接触测量达到时间预算。"; return false; }
                    var body = value as Body2;
                    if (body == null || !body.Visible)
                    { issue = "实体不可用或存在隐藏实体，无法核对精确间距。"; return false; }
                    var copy = body.Copy() as Body2;
                    if (copy == null) { issue = "SOLIDWORKS 无法复制实体用于只读测量。"; return false; }
                    var placed = new PlacedBody { Copy = copy };
                    leaf.Bodies.Add(placed);
                    shape.BodyCount++;
                    if (!copy.ApplyTransform(transform))
                    { issue = "无法将实体副本变换到装配坐标系。"; return false; }
                    placed.Bounds = ExactBounds(copy);
                }
                return true;
            }

            private bool TryIntersection(Body2 first, Body2 second, out double volume)
            {
                object leftFaces, rightFaces, intersections;
                volume = 0;
                var success = _modeler.CheckInterferenceBetweenTwoBodies(first, second, false, out leftFaces, out rightFaces, out intersections);
                var bodies = intersections as Array;
                if (bodies != null && bodies.Length != 0)
                {
                    try { if (success && TryVolume(bodies, out volume)) return true; }
                    finally { foreach (var value in bodies) ReleaseTemporary(value); }
                }
                else if (success && EmptyArray(intersections) && EmptyArray(leftFaces) && EmptyArray(rightFaces)) return true;

                // Only documented disjoint/no-intersection statuses can resolve an ambiguous empty result.
                var firstInput = first.Copy() as Body2;
                var secondInput = second.Copy() as Body2;
                try
                {
                    if (firstInput == null || secondInput == null || !HasTime()) return false;
                    int status;
                    var result = firstInput.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, secondInput, out status);
                    var resultBodies = result as Array;
                    try
                    {
                        if (resultBodies != null && resultBodies.Length != 0)
                            return status == (int)swBodyOperationError_e.swBodyOperationNoError && TryVolume(resultBodies, out volume);
                        volume = 0;
                        return EmptyArray(result) && (status == (int)swBodyOperationError_e.swBodyOperationDisjointBodies ||
                            status == (int)swBodyOperationError_e.swBodyOperationNoIntersect);
                    }
                    finally { if (resultBodies != null) foreach (var value in resultBodies) ReleaseTemporary(value); }
                }
                finally { ReleaseTemporary(firstInput); ReleaseTemporary(secondInput); }
            }

            private static bool TryVolume(Array bodies, out double volume)
            {
                volume = 0;
                foreach (var value in bodies)
                {
                    var body = value as Body2;
                    var properties = body == null ? null : body.GetMassProperties(1d) as double[];
                    if (properties == null || properties.Length <= 3 || !Finite(properties[3]) || properties[3] <= 0) return false;
                    volume += properties[3];
                }
                return Finite(volume);
            }

            private static bool EmptyArray(object value) { return value == null || value is Array && ((Array)value).Length == 0; }

            private static bool NormalBody(object value)
            { return (value is short || value is int || value is swBodyInfo_e) && Convert.ToInt32(value) == (int)swBodyInfo_e.swNormalBody_e; }

            // Exact extreme points are only a broad phase for solid intersection. Contact is always measured with native geometry.
            private static double[] ExactBounds(Body2 body)
            {
                var bounds = new double[6];
                for (var axis = 0; axis < 3; axis++)
                {
                    var direction = new double[3];
                    direction[axis] = 1;
                    double x, y, z;
                    if (!body.GetExtremePoint(direction[0], direction[1], direction[2], out x, out y, out z)) return null;
                    bounds[axis + 3] = Axis(axis, x, y, z);
                    if (!body.GetExtremePoint(-direction[0], -direction[1], -direction[2], out x, out y, out z)) return null;
                    bounds[axis] = Axis(axis, x, y, z);
                    if (!Finite(bounds[axis]) || !Finite(bounds[axis + 3]) || bounds[axis] > bounds[axis + 3]) return null;
                }
                return bounds;
            }

            private static double Axis(int axis, double x, double y, double z) { return axis == 0 ? x : axis == 1 ? y : z; }

            private static bool SeparatedBounds(double[] first, double[] second)
            {
                for (var axis = 0; axis < 3; axis++)
                    if (first[axis] > second[axis + 3] + KernelTolerance || second[axis] > first[axis + 3] + KernelTolerance) return true;
                return false;
            }

            private static bool ValidDistance(double distance, object a, object b)
            {
                var left = a as double[]; var right = b as double[];
                if (!Finite(distance) || distance < 0 || left == null || right == null || left.Length != 3 || right.Length != 3 ||
                    left.Any(value => !Finite(value)) || right.Any(value => !Finite(value))) return false;
                var squared = left.Zip(right, (x, y) => (x - y) * (x - y)).Sum();
                return Math.Abs(Math.Sqrt(squared) - distance) <= KernelTolerance + distance * 1e-7;
            }

            private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
            private static ContactMeasurement Unknown(string issue) { return new ContactMeasurement { UnconfirmedReason = issue }; }
            private static ContactMeasurement Partial(double overlap, string issue)
            { return new ContactMeasurement { Overlap = overlap, UnconfirmedReason = issue }; }

            private static void ReleaseTemporary(object value)
            { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

            private sealed class Shape : IDisposable
            {
                internal readonly List<Leaf> Leaves = new List<Leaf>();
                internal int BodyCount;
                internal int ComponentsVisited;
                public void Dispose()
                { foreach (var body in Leaves.SelectMany(leaf => leaf.Bodies)) ReleaseTemporary(body.Copy); }
            }

            private sealed class Leaf
            {
                internal Leaf(Component2 component) { Component = component; }
                internal readonly Component2 Component;
                internal readonly List<PlacedBody> Bodies = new List<PlacedBody>();
            }

            private sealed class PlacedBody
            {
                internal Body2 Copy;
                internal double[] Bounds;
            }
        }
    }
}
