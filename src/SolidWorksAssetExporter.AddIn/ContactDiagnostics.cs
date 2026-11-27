using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    public sealed class ContactDiagnostic
    {
        public string Code { get; internal set; }
        public string Message { get; internal set; }
        public bool NeedsReview { get; internal set; }
        public string Parent { get; internal set; }
        public string Child { get; internal set; }
        public int RepresentativeCount { get; internal set; }
        public double? GapMillimeters { get; internal set; }
        public double? OverlapCubicMillimeters { get; internal set; }
    }

    internal sealed class ContactCandidate
    {
        internal ContactCandidate(ExportNode parent, ExportNode child, string family, RelationPose pose)
        {
            Parent = parent; Child = child; Family = family;
            Key = pose == null ? Canonical.Join(family, parent.Id, child.Id) : Canonical.Join(family, PoseKey(pose));
        }

        internal readonly ExportNode Parent, Child;
        internal readonly string Family, Key;

        private static string PoseKey(RelationPose pose)
        {
            var rotation = new[] { pose.Qw, pose.Qx, pose.Qy, pose.Qz };
            var sign = rotation.First(value => Math.Abs(value) > 1e-8) < 0 ? -1d : 1d;
            var translation = new[] { pose.Tx, pose.Ty, pose.Tz }.Select(value => Math.Round(value * 1e7));
            return Canonical.Join(translation.Concat(rotation.Select(value => Math.Round(value * sign * 1e8)))
                .Select(value => value.ToString("0", CultureInfo.InvariantCulture)).ToArray());
        }
    }

    internal sealed class ContactMeasurement
    {
        internal double? Gap;
        internal double? Overlap;
        internal string UnconfirmedReason;
    }

    internal static class ContactDiagnostics
    {
        internal const double SignificantOverlap = 1e-12; // 0.001 mm³, reject kernel noise.
        private const double ContactTolerance = 1e-7; // 0.0001 mm.
        private const double ReviewGap = 0.0005; // 0.5 mm, small fits do not produce a warning.

        internal static IList<ContactDiagnostic> Analyze(IEnumerable<ContactCandidate> candidates, Func<ContactCandidate, ContactMeasurement> measure,
            Func<bool> hasTime, int limit)
        {
            // Check one pose from each asset/configuration family before revisiting families with many slots.
            var groups = candidates.GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
                .Select(group => new { Sample = group.First(), Count = group.Count() })
                .GroupBy(group => group.Sample.Family, StringComparer.Ordinal)
                .SelectMany(family => family.Select((group, index) => new { Group = group, Index = index }))
                .OrderBy(item => item.Index).ThenBy(item => item.Group.Sample.Family, StringComparer.Ordinal).ToList();
            var output = new List<ContactDiagnostic>();
            foreach (var item in groups)
            {
                if (output.Count >= limit || !hasTime()) break;
                output.Add(Describe(item.Group.Sample, item.Group.Count, measure(item.Group.Sample)));
            }
            if (output.Count < groups.Count)
            {
                var skipped = groups.Skip(output.Count).ToArray();
                output.Add(new ContactDiagnostic
                {
                    Code = "contact_sampling_limit", NeedsReview = true, RepresentativeCount = skipped.Sum(item => item.Group.Count),
                    Message = "接触检查达到本次预算，另 " + skipped.Length + " 种放置（" +
                        skipped.Sum(item => item.Group.Count) + " 条绑定）未确认。"
                });
            }
            return output;
        }

        private static ContactDiagnostic Describe(ContactCandidate candidate, int count, ContactMeasurement measurement)
        {
            var result = new ContactDiagnostic
            {
                Parent = candidate.Parent.Name, Child = candidate.Child.Name, RepresentativeCount = count,
                GapMillimeters = measurement.Gap * 1000d, OverlapCubicMillimeters = measurement.Overlap * 1e9
            };
            string detail;
            if (measurement.Overlap >= SignificantOverlap)
            {
                result.Code = "contact_penetration"; result.NeedsReview = true;
                detail = "测得实体穿透，单对实体相交体积 " + Format(result.OverlapCubicMillimeters.Value) +
                    " mm³。请核对是否为设计允许的配合。";
            }
            else if (measurement.UnconfirmedReason != null || !measurement.Gap.HasValue)
            {
                result.Code = "contact_unconfirmed"; result.NeedsReview = true;
                detail = "接触未确认。" + (measurement.UnconfirmedReason ?? "无法取得原生间距测量。");
            }
            else if (measurement.Overlap > 0)
            {
                result.Code = "contact_small_overlap";
                detail = "测得微小相交体积，低于本次穿透提醒阈值。";
            }
            else if (measurement.Gap > ReviewGap)
            {
                result.Code = "contact_gap"; result.NeedsReview = true;
                detail = "测得最小间距 " + Format(result.GapMillimeters.Value) + " mm。请核对是否悬空或间隙符合设计。";
            }
            else if (measurement.Gap <= ContactTolerance)
            {
                result.Code = "contact_touching";
                detail = "原生几何测量显示接触。";
            }
            else
            {
                result.Code = "contact_clearance";
                detail = "测得小间隙 " + Format(result.GapMillimeters.Value) + " mm。";
            }
            result.Message = "[" + result.Parent + "] → [" + result.Child + "]。" + detail +
                (count > 1 ? "本次检查代表 " + count + " 条同类放置。" : string.Empty);
            return result;
        }

        private static string Format(double value) { return value.ToString("0.######", CultureInfo.InvariantCulture); }
    }
}
