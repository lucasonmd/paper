using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AggregationEngine.ModuleDesigner.Core
{
    // Writes the module JSON in exactly the shape JsonModuleLoader (in
    // TopicManager.Extensions) expects: {module, rootKinds, kinds,
    // relations}, with relations shaped per "direction" as unidirectional
    // (from/to/fromField/multiplicity) or bidirectional (left/right/
    // leftField/rightField/leftToRightMultiplicity/rightToLeftMultiplicity/
    // presenceCheck). Property names and value casing intentionally match
    // the hand-written Mount.module.json this schema was designed against.
    public static class JsonSchemaWriter
    {
        public sealed class KindExport
        {
            public string Name { get; set; } = "";
            public string ClrType { get; set; } = "";
            public string KeyField { get; set; } = "A_sourceID";
            public bool IsRoot { get; set; }
        }

        public static string Write(string moduleName, IReadOnlyList<KindExport> kinds, IReadOnlyList<CandidateRelation> relations)
        {
            var root = new JsonObject
            {
                ["module"] = moduleName,
                ["rootKinds"] = new JsonArray(kinds.Where(k => k.IsRoot).Select(k => (JsonNode)k.Name).ToArray()),
                ["kinds"] = new JsonArray(kinds.Select(k => (JsonNode)new JsonObject
                {
                    ["name"] = k.Name,
                    ["clrType"] = k.ClrType,
                    ["keyField"] = k.KeyField,
                }).ToArray()),
                ["relations"] = new JsonArray(relations.Select(r => (JsonNode)WriteRelation(r)).ToArray()),
            };

            // Default JsonSerializerOptions HTML-escape '>','<','&' etc.
            // (> and friends) - a safe default for JSON embedded in a
            // <script> tag, irrelevant here and just noise in a file meant
            // to be read/reviewed by a person. Relaxed escaping keeps
            // "A->B"-style relation names literal.
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            return root.ToJsonString(options);
        }

        private static JsonObject WriteRelation(CandidateRelation r)
        {
            if (!r.Bidirectional)
            {
                return new JsonObject
                {
                    ["name"] = r.Name,
                    ["direction"] = "unidirectional",
                    ["from"] = r.FromClass,
                    ["to"] = r.ToClass,
                    ["fromField"] = r.FromField,
                    ["multiplicity"] = r.Multiplicity,
                };
            }

            var obj = new JsonObject
            {
                ["name"] = r.Name,
                ["direction"] = "bidirectional",
                ["left"] = r.FromClass,
                ["right"] = r.ToClass,
                ["leftField"] = r.FromField,
                ["rightField"] = r.ReciprocalField,
                ["leftToRightMultiplicity"] = r.Multiplicity,
                ["rightToLeftMultiplicity"] = r.ReciprocalMultiplicity,
            };
            if (r.Multiplicity == "ZeroOrOne" || r.ReciprocalMultiplicity == "ZeroOrOne")
                obj["presenceCheck"] = r.PresenceCheck;
            return obj;
        }
    }
}
