using System;
using System.Linq;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunBindingEditorTests()
        {
            RunInterfaceEditingTests();
            Run("Binding batch resolves captured endpoints once per transaction", delegate {
                var f = new BindingFixture(); var count = 0;
                var result = InstanceBindingRules.Apply(f.Records, f.E("A"), new[] { f.E("T1"), f.E("T2"), f.E("T3") }, e => { count++; return f.Resolve(e); }, false);
                Equal(3, result.Count); Equal(4, count);
            });
            Run("Binding editor 200 tips have one occurrence parent group", delegate {
                var f = new BindingFixture(); var records = Enumerable.Range(1, 200).Select(i => new InstanceBinding { Parent = f.E("A"), Child = f.E("tip" + i) }).ToList();
                Equal(1, BindingEditorState.Group(records).Count()); Equal(200, BindingEditorState.Group(records).Single().Count());
                records.Add(new InstanceBinding { Parent = f.E("B"), Child = f.E("other") }); Equal(2, BindingEditorState.Group(records).Count());
            });
            Run("Binding editor incremental clicks accumulate and deduplicate", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A"));
                d.AddChildren(new[] { f.E("T1") }); d.AddChildren(new[] { f.E("T2"), f.E("T1"), f.E("A") }); Equal(2, d.Children.Count);
                d.Children.RemoveAt(0); d.AddChildren(new[] { f.E("T3") }); Equal("T2", d.Children[0].Context); Equal(2, d.Children.Count);
            });
            Run("Binding editor shows every parent of a shared child in separate groups", delegate {
                var f = new BindingFixture(); f.Apply("A", false, "T1", "T2"); f.Apply("B", false, "T1");
                var groups = BindingEditorState.Group(f.Records).ToList(); Equal(2, groups.Count);
                Equal(2, groups.Count(g => g.Any(r => r.Child.Context == "T1")));
                var parents = BindingEditorState.Parents(f.Records, f.E("T1")); Equal(2, parents.Count);
                True(parents.Any(p => p.Context == "A")); True(parents.Any(p => p.Context == "B"));
                Equal(1, BindingEditorState.Parents(f.Records, f.E("T2")).Count);
            });
            Run("Binding editor normal apply clears next-round draft only", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A")); d.AddChildren(new[] { f.E("T1") });
                var proposed = InstanceBindingRules.Apply(f.Records, d.Parent, d.Children, f.Resolve, false); d.Applied(false);
                True(d.Parent == null); Equal(0, d.Children.Count); Equal(1, proposed.Count); Equal("T1", proposed.Single().Child.Context);
            });
            Run("Binding editor single-child chain retains incoming edge", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A")); d.AddChildren(new[] { f.E("T1") });
                var first = InstanceBindingRules.Apply(f.Records, d.Parent, d.Children, f.Resolve, false); d.Applied(true);
                Equal("T1", d.Parent.Context); Equal(0, d.Children.Count); d.AddChildren(new[] { f.E("T2") });
                var second = InstanceBindingRules.Apply(first, d.Parent, d.Children, f.Resolve, false); Equal(2, second.Count);
                True(second.Any(r => r.Parent.Context == "A" && r.Child.Context == "T1")); True(second.Any(r => r.Parent.Context == "T1" && r.Child.Context == "T2"));
                d.AddChildren(new[] { f.E("A") }); Throws<ValidationException>(() => InstanceBindingRules.Apply(second, d.Parent, d.Children, f.Resolve, false));
                Equal("T1", d.Parent.Context); Equal(2, d.Children.Count); Equal(2, second.Count);
            });
            Run("Binding editor multi-child chain requires explicit branch", delegate {
                var f = new BindingFixture(); var d = new BindingEditorState(); d.SetParent(f.E("A")); d.AddChildren(new[] { f.E("T1"), f.E("T2") });
                d.Applied(true); True(d.Parent == null); Equal(2, d.NextParents.Count); Equal(0, d.Children.Count);
                Throws<ValidationException>(() => d.ContinueFrom(f.E("B"))); True(d.Parent == null);
                d.ContinueFrom(f.E("T2")); Equal("T2", d.Parent.Context); Equal(0, d.NextParents.Count);
            });
            Run("Binding editor bulk selection stays inside same sibling source", delegate {
                var a = new BindingEndpoint { Context = "1/2/3", AssetId = "tip:1", SourceIdentity = "tip", SourcePath = "C:/tip.sldprt" };
                var b = new BindingEndpoint { Context = "1/2/4", AssetId = "tip:1", SourceIdentity = "tip", SourcePath = "C:/tip.sldprt" };
                True(BindingEditorState.SameSiblingAsset(a, b)); b.Context = "1/9/4"; True(!BindingEditorState.SameSiblingAsset(a, b));
                b.Context = "1/2/4"; b.SourcePath = "C:/other.sldprt"; True(!BindingEditorState.SameSiblingAsset(a, b));
            });
        }
    }
}
