using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksAssetExporter.AddIn;
using SolidWorksAssetExporter.Mcp;

namespace SolidWorksAssetExporter.Core.Tests
{
    internal static partial class Program
    {
        private static void RunMcpBindingEvidenceTests()
        {
            Run("MCP assembly evidence groups active direct mate types by exact occurrence", McpDirectMateEvidence);
            Run("MCP assembly evidence reports missing native data independently", McpUnavailableBindingEvidence);
        }

        private static void McpDirectMateEvidence()
        {
            var owner = new FakeSwComponent { NameValue = "组-1" };
            var source = new FakeSwComponent
            {
                NameValue = "组-1/架-1",
                ParentValue = owner,
                IsFixedValue = true,
                Transform2 = new McpEvidenceTransform { ArrayData = new[] { 1d, 0, 0, 0, 1, 0, 0, 0, 1, .012, .025, -.042, 1 } }
            };
            var peer = new FakeSwComponent { NameValue = "组-1/Tube-1" };
            var repeatedPeer = new FakeSwComponent { NameValue = "组-2/管-1" };
            var otherContextSource = new FakeSwComponent { NameValue = source.Name2 };
            var internalPart = new FakeSwComponent { NameValue = "组-1/架-1/零件-1" };
            source.MatesValue = new object[]
            {
                new McpEvidenceMate(1, false, peer, source),
                new McpEvidenceMate(0, false, source, peer),
                new McpEvidenceMate(0, false, source, peer),
                new McpEvidenceMate(3, true, source, peer),
                new McpEvidenceMate(1, false, source, repeatedPeer),
                new McpEvidenceMate(2, false, otherContextSource, repeatedPeer),
                new McpEvidenceMate(5, false, source, internalPart),
                new McpEvidenceMate(6, false, source, new FakeSwComponent { NameValue = peer.Name2.ToUpperInvariant() }),
                new McpEvidenceMate(6, false, source, source),
                new McpEvidenceMate(7, false, source, peer, internalPart),
                new McpEvidenceMate(8, false, source, null),
                new object()
            };
            var result = BindingEvidence(source, source.Name2, peer.Name2, repeatedPeer.Name2);
            Equal(owner.Name2, result["cad_owner"]);
            Equal<object>(null, result["fixed_in_assembly"]);
            var origin = McpProtocol.Object(result["world_origin_m"]);
            Equal(.012, Convert.ToDouble(origin["x"]));
            Equal(.025, Convert.ToDouble(origin["y"]));
            Equal(-.042, Convert.ToDouble(origin["z"]));
            var peers = ((object[])result["mate_peers"]).Select(McpProtocol.Object).ToArray();
            Equal(2, peers.Length);
            Equal(peer.Name2, peers[0]["instance"]);
            True(((object[])peers[0]["mate_types"]).Cast<int>().SequenceEqual(new[] { 0, 1 }));
            Equal(repeatedPeer.Name2, peers[1]["instance"]);
            True(((object[])peers[1]["mate_types"]).Cast<int>().SequenceEqual(new[] { 1 }));
            Equal<object>(null, result["mate_read_issue"]);
        }

        private static void McpUnavailableBindingEvidence()
        {
            var root = new FakeSwComponent { NameValue = "ROOT", IsRootValue = true };
            var source = new FakeSwComponent
            {
                ParentValue = root,
                IsFixedValue = true,
                SuppressionState = (int)swComponentSuppressionState_e.swComponentLightweight
            };
            var result = BindingEvidence(source, source.Name2);
            Equal<object>(null, result["cad_owner"]);
            Equal(true, result["fixed_in_assembly"]);
            Equal<object>(null, result["world_origin_m"]);
            True(result["pose_read_issue"] is string);
            True(result["mate_read_issue"] is string);
            Equal(0, ((object[])result["mate_peers"]).Length);

            source.MatesError = new COMException("mate list unavailable");
            result = BindingEvidence(source, source.Name2);
            Equal("mate list unavailable", result["mate_read_issue"]);
            Equal(true, result["fixed_in_assembly"]);
        }

        private static Dictionary<string, object> BindingEvidence(FakeSwComponent component, params string[] instances)
        {
            var source = new SwCadNode(new SldWorks(), component, "/test/" + component.Name2);
            var evidence = McpBindingEvidence.Describe(source, new HashSet<string>(instances, StringComparer.Ordinal));
            var json = McpProtocol.Json();
            return McpProtocol.Object(json.DeserializeObject(json.Serialize(evidence)));
        }

        private sealed class McpEvidenceTransform : MathTransform
        {
            public object ArrayData { get; set; }
        }

        private sealed class McpEvidenceMate : Mate2, Feature
        {
            private readonly bool _suppressed;
            private readonly Component2[] _components;

            internal McpEvidenceMate(int type, bool suppressed, params Component2[] components)
            {
                Type = type;
                _suppressed = suppressed;
                _components = components;
            }

            public int Type { get; private set; }
            public int GetMateEntityCount() { return _components.Length; }
            public MateEntity2 MateEntity(int index) { return new McpEvidenceMateEntity { ReferenceComponent = _components[index] }; }
            public string GetTypeName2() { return "Mate"; }
            public object IsSuppressed2(int configuration, object names) { return new[] { _suppressed }; }
        }

        private sealed class McpEvidenceMateEntity : MateEntity2
        {
            public Component2 ReferenceComponent { get; set; }
        }
    }
}
