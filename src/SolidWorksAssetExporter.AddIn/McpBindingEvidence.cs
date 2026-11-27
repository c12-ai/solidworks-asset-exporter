using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal static class McpBindingEvidence
    {
        internal static object Describe(SwCadNode source, ISet<string> assetInstances)
        {
            string owner = null;
            bool? isFixed = null;
            object origin = null;
            string cadIssue = null, poseIssue = null, mateIssue = null;
            var peers = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
            var component = source.Component;
            if (source.InstanceId != "root" && component != null)
            {
                cadIssue = ReadEvidence(() =>
                {
                    var parent = component.GetParent() as Component2;
                    if (parent == null || parent.IsRoot()) isFixed = source.IsFixed;
                    else owner = parent.Name2;
                });
                poseIssue = ReadEvidence(() => origin = ReadOrigin(component));
                mateIssue = ReadMates(component, assetInstances, peers);
            }

            return new
            {
                cad_owner = owner,
                fixed_in_assembly = isFixed,
                world_origin_m = origin,
                mate_peers = peers.Select(pair => new { instance = pair.Key, mate_types = pair.Value.ToArray() }).ToArray(),
                cad_read_issue = cadIssue,
                pose_read_issue = poseIssue,
                mate_read_issue = mateIssue
            };
        }

        private static object ReadOrigin(Component2 component)
        {
            var transform = component.Transform2;
            var values = transform == null ? null : transform.ArrayData as double[];
            if (values == null || values.Length < 12 ||
                values.Skip(9).Take(3).Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new ValidationException("无法读取组件在顶层装配中的原点。");
            return new { x = values[9], y = values[10], z = values[11] };
        }

        private static string ReadMates(Component2 component, ISet<string> assetInstances,
            IDictionary<string, SortedSet<int>> peers)
        {
            Array mates = null;
            var issue = ReadEvidence(() => mates = component.GetMates() as Array);
            if (issue != null) return issue;
            if (mates == null)
            {
                var lightweight = false;
                issue = ReadEvidence(() =>
                {
                    var suppression = component.GetSuppression2();
                    lightweight = suppression == (int)swComponentSuppressionState_e.swComponentLightweight ||
                        suppression == (int)swComponentSuppressionState_e.swComponentFullyLightweight;
                });
                return issue ?? (lightweight ? "组件处于轻化状态，未返回配合；可解析组件后重读。" : null);
            }

            foreach (var candidate in mates)
            {
                var mateIssue = ReadEvidence(() =>
                {
                    var mate = candidate as Mate2;
                    if (mate != null) AddPeer(component, mate, assetInstances, peers);
                });
                if (issue == null) issue = mateIssue;
            }
            return issue;
        }

        private static void AddPeer(Component2 source, Mate2 mate, ISet<string> assetInstances,
            IDictionary<string, SortedSet<int>> peers)
        {
            if (mate.GetMateEntityCount() != 2) return;
            var first = mate.MateEntity(0);
            var second = mate.MateEntity(1);
            var left = first == null ? null : first.ReferenceComponent;
            var right = second == null ? null : second.ReferenceComponent;
            var peer = Equals(left, source) ? right : Equals(right, source) ? left : null;
            if (peer == null || Equals(peer, source)) return;
            var name = peer.Name2;
            if (string.IsNullOrEmpty(name) || name == source.Name2 || !assetInstances.Contains(name)) return;

            // Some mate RCWs do not expose the feature interface. Where it is available,
            // discard mates explicitly suppressed in the current configuration.
            var feature = mate as Feature;
            var suppressed = feature == null ? null :
                feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
            if (suppressed != null && suppressed.Length != 0 && suppressed[0]) return;

            var type = mate.Type;
            SortedSet<int> types;
            if (!peers.TryGetValue(name, out types)) peers.Add(name, types = new SortedSet<int>());
            types.Add(type);
        }

        private static string ReadEvidence(Action read)
        {
            try { read(); return null; }
            catch (COMException ex) { return ex.Message; }
            catch (InvalidComObjectException ex) { return ex.Message; }
            catch (ValidationException ex) { return ex.Message; }
        }
    }
}
