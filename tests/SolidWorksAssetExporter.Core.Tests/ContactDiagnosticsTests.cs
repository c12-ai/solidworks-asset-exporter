using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunContactDiagnosticTests()
        {
            Run("Contact check deduplicates equivalent poses and samples each family fairly", ContactRepresentativeBudget);
            Run("Contact check uses native volume and distance without changing model bodies", ContactNativeSignals);
            Run("Crossing bounds and unavailable native measurements do not confirm penetration", ContactUncertainSignals);
        }

        private static void ContactRepresentativeBudget()
        {
            var parent = new ExportNode { Id = "parent", Name = "parent" };
            var child = new ExportNode { Id = "child", Name = "child" };
            var samples = new List<ContactCandidate>();
            for (var n = 0; n < 200; n++)
                samples.Add(new ContactCandidate(parent, child, "family 1", new RelationPose { Qw = n % 2 == 0 ? 1 : -1 }));
            samples.Add(new ContactCandidate(parent, child, "family 1", new RelationPose { Tx = 0.02, Qw = 1 }));
            samples.Add(new ContactCandidate(parent, child, "family 2", RelationPose.Identity));
            var measured = new List<string>();
            var results = ContactDiagnostics.Analyze(samples, candidate =>
            {
                measured.Add(candidate.Family);
                return new ContactMeasurement { Gap = 0, Overlap = 0 };
            }, () => true, 2);
            Equal(true, measured.SequenceEqual(new[] { "family 1", "family 2" }));
            Equal(200, results[0].RepresentativeCount);
            Equal("contact_sampling_limit", results[2].Code);
            Equal(1, results[2].RepresentativeCount);
        }

        private static void ContactNativeSignals()
        {
            var fixture = new ContactFixture();
            fixture.Document.Distance = 0;
            fixture.Modeler.HasResults = true;
            Equal("contact_touching", fixture.Check().Code);
            Equal(1, fixture.Modeler.Calls);
            Equal(0, fixture.ParentBody.Transforms);
            Equal(0, fixture.ChildBody.Transforms);

            fixture.Modeler.InterferenceVolume = 0.000000002;
            var overlap = fixture.Check();
            Equal("contact_penetration", overlap.Code);
            Near(2d, overlap.OverlapCubicMillimeters.Value);
            Equal(true, overlap.NeedsReview);

            var calls = fixture.Modeler.Calls;
            fixture.ChildComponent.Transform2 = new ContactFixtureTransform(0.013);
            fixture.Document.Distance = 0.003;
            var gap = fixture.Check();
            Equal("contact_gap", gap.Code);
            Near(3d, gap.GapMillimeters.Value);
            Equal(calls, fixture.Modeler.Calls);
        }

        private static void ContactUncertainSignals()
        {
            var fixture = new ContactFixture();
            fixture.Document.Distance = 0;
            Equal("contact_unconfirmed", fixture.Check().Code);
            fixture.ParentBody.BooleanStatus = swBodyOperationError_e.swBodyOperationNoIntersect;
            Equal("contact_touching", fixture.Check().Code);
            fixture.Document.MissingPoints = true;
            Equal("contact_unconfirmed", fixture.Check().Code);
            fixture.ChildComponent.TestBodyInfo = new[] { (int)swBodyInfo_e.swUserBody_e };
            Equal(true, fixture.Check().Message.Contains("上下文修改"));
        }

        private sealed class ContactFixture
        {
            internal readonly ContactFixtureModeler Modeler = new ContactFixtureModeler();
            internal readonly ContactFixtureDocument Document = new ContactFixtureDocument();
            internal readonly ContactFixtureBody ParentBody = new ContactFixtureBody();
            internal readonly ContactFixtureBody ChildBody = new ContactFixtureBody();
            internal readonly FakeSwComponent ChildComponent;
            private readonly ContactFixtureApp _app;
            private readonly ExportNode _parent, _child;

            internal ContactFixture()
            {
                _app = new ContactFixtureApp(Document, Modeler);
                var parentComponent = FixtureComponent("parent", ParentBody, 0);
                ChildComponent = FixtureComponent("child", ChildBody, 0.01);
                _parent = FixtureNode(parentComponent);
                _child = FixtureNode(ChildComponent);
            }

            internal ContactDiagnostic Check()
            { return SwContactDiagnostics.ForBinding(_app, Document, _parent, new[] { _child }).Single(); }

            private ExportNode FixtureNode(FakeSwComponent component)
            {
                return new ExportNode { Id = component.Name2, Name = component.Name2, AssetId = component.Name2 + ":1",
                    Source = new SwCadNode(_app, component, "/" + component.Name2) };
            }

            private static FakeSwComponent FixtureComponent(string name, ContactFixtureBody body, double x)
            {
                return new FakeSwComponent
                {
                    NameValue = name, PathValue = "C:\\models\\" + name + ".SLDPRT",
                    VisibleValue = (int)swComponentVisibilityState_e.swComponentVisible,
                    SuppressionState = (int)swComponentSuppressionState_e.swComponentResolved,
                    TestSolidBodies = new[] { body }, TestBodyInfo = new[] { (short)swBodyInfo_e.swNormalBody_e },
                    Transform2 = new ContactFixtureTransform(x)
                };
            }
        }

        private sealed class ContactFixtureTransform : MathTransform
        {
            private readonly double _x;
            internal ContactFixtureTransform(double x) { _x = x; }
            public object ArrayData { get { return new[] { 1d, 0, 0, 0, 1, 0, 0, 0, 1, _x, 0, 0, 1, 0, 0, 0 }; } }
        }

        private sealed class ContactFixtureApp : SldWorks
        {
            private readonly ContactFixtureModeler _modeler;
            internal ContactFixtureApp(ModelDoc2 document, ContactFixtureModeler modeler) { ActiveDoc = document; _modeler = modeler; }
            public override object GetModeler() { return _modeler; }
        }

        private sealed class ContactFixtureDocument : FakeSwDocument
        {
            internal ContactFixtureDocument() : base("preview", "C:\\models\\preview.SLDASM", true) { }
            internal double Distance;
            internal bool MissingPoints;
            public override double ClosestDistance(object first, object second, out object point1, out object point2)
            {
                if (!(first is Component2 && second is Component2)) throw new InvalidOperationException("Distance must use actual assembly components.");
                point1 = MissingPoints ? null : new[] { 0d, 0, 0 };
                point2 = MissingPoints ? null : new[] { Distance, 0, 0 };
                return Distance;
            }
        }

        private sealed class ContactFixtureModeler : Modeler
        {
            internal int Calls;
            internal bool HasResults;
            internal double InterferenceVolume;
            public bool CheckInterferenceBetweenTwoBodies(object first, object second, bool coincident, out object firstFaces,
                out object secondFaces, out object intersection)
            {
                Calls++;
                if (coincident) throw new InvalidOperationException("Coincidence must not be included in the solid-volume calculation.");
                firstFaces = secondFaces = null;
                intersection = InterferenceVolume > 0 ? new[] { new ContactFixtureBody { Volume = InterferenceVolume } } : null;
                return HasResults;
            }
        }

        private sealed class ContactFixtureBody : Body2
        {
            internal int Transforms;
            internal double Volume;
            internal swBodyOperationError_e BooleanStatus = swBodyOperationError_e.swBodyOperationUnknownError;
            private double _x;
            public bool Visible { get { return true; } }
            public object Copy() { return new ContactFixtureBody { _x = _x, Volume = Volume, BooleanStatus = BooleanStatus }; }
            public bool ApplyTransform(MathTransform transform) { Transforms++; _x += ((double[])transform.ArrayData)[9]; return true; }
            public object GetMassProperties(double density) { return new[] { 0d, 0, 0, Volume }; }
            public object Operations2(int operation, object tool, out int error) { error = (int)BooleanStatus; return null; }
            public bool GetExtremePoint(double x, double y, double z, out double pointX, out double pointY, out double pointZ)
            {
                pointX = _x + (x > 0 ? 0.01 : 0);
                pointY = y > 0 ? 0.01 : 0;
                pointZ = z > 0 ? 0.01 : 0;
                return true;
            }
        }
    }
}
