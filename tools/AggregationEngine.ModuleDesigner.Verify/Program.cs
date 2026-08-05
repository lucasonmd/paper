using System;
using System.Collections.Generic;
using System.Linq;
using AggregationEngine.ModuleDesigner.Core;

namespace AggregationEngine.ModuleDesigner.Verify
{
    // Runs the Core parsing/inference pipeline against the real
    // Topics.cs already committed for AggregationEngine.JsonSample and
    // compares the result, qualitatively, against the hand-written
    // Mount.module.json sitting right next to it - the whole point of
    // ModuleDesigner is to get close to that file automatically.
    internal static class Program
    {
        private static void Main(string[] args)
        {
            var csFile = args.Length > 0
                ? args[0]
                : @"C:\Source\paper\paper-gva\samples\AggregationEngine.JsonSample\Topics.cs";

            Console.WriteLine($"-- analyzing {csFile} --");
            var topics = CsFileAnalyzer.Analyze(new[] { csFile });
            Console.WriteLine($"detected {topics.Count} topic(s):");
            foreach (var t in topics)
            {
                Console.WriteLine($"  {t.FullName}  (key={t.KeyField}, {t.CandidateFields.Count} candidate field(s))");
                foreach (var f in t.CandidateFields)
                    Console.WriteLine($"      {f.FieldName} : {f.TypeText}  [{f.Shape}]");
            }

            Console.WriteLine();
            var relations = RelationInference.Infer(topics);
            Console.WriteLine($"inferred {relations.Count} relation(s) (after bidirectional pairing):");
            foreach (var r in relations)
            {
                if (r.Bidirectional)
                    Console.WriteLine($"  [BI]  {r.FromClass}.{r.FromField} <-> {r.ToClass}.{r.ReciprocalField}  " +
                                       $"({r.Multiplicity} / {r.ReciprocalMultiplicity})  conf={r.Confidence:F2}");
                else
                    Console.WriteLine($"  [UNI] {r.FromClass}.{r.FromField} -> {(r.ToClass ?? "???")}  " +
                                       $"({r.Multiplicity})  conf={r.Confidence:F2}" + (r.ToClass == null ? "  ** UNRESOLVED **" : ""));
            }

            Console.WriteLine();
            Console.WriteLine("-- checks against the known-correct Mount.module.json shape --");
            int failures = 0;
            void Check(bool cond, string desc)
            {
                Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + desc);
                if (!cond) failures++;
            }

            Check(topics.Count == 10, $"detected all 10 topic classes (got {topics.Count})");
            Check(topics.Any(t => t.ClassName == "C_Rotational_Mount"), "found C_Rotational_Mount");
            Check(topics.Any(t => t.ClassName == "C_Linear_Mount"), "found C_Linear_Mount");
            Check(relations.Count(r => r.ToClass == null) == 0, "no unresolved relation targets");

            bool BiPair(string a, string fa, string b, string fb) => relations.Any(r =>
                r.Bidirectional &&
                ((r.FromClass == a && r.FromField == fa && r.ToClass == b && r.ReciprocalField == fb) ||
                 (r.FromClass == b && r.FromField == fb && r.ToClass == a && r.ReciprocalField == fa)));

            Check(BiPair("C_Rotational_Mount", "A_softLimits_sourceID", "C_Rotational_Soft_Limits", "A_rotationalMount_sourceID"),
                "auto-paired Mount<->SoftLimits as bidirectional");
            Check(BiPair("C_Rotational_Mount", "A_movementInhibitZones_sourceID", "C_Movement_Inhibit_Zone", "A_rotationalMount_sourceID"),
                "auto-paired Mount<->InhibitZone as bidirectional (plural field name vs singular class name)");
            Check(BiPair("C_Rotational_Mount", "A_targetPosition_sourceID", "C_Rotational_Target_Position", "A_rotationalMount_sourceID"),
                "auto-paired Mount<->TargetPosition as bidirectional");

            var zoneRel = relations.FirstOrDefault(r => r.FromClass == "C_Rotational_Mount" && r.FromField == "A_movementInhibitZones_sourceID");
            Check(zoneRel != null && zoneRel.Multiplicity == "ZeroOrMany", "InhibitZone field (array) guessed as ZeroOrMany");

            var specRel = relations.FirstOrDefault(r => r.FromClass == "C_Rotational_Mount" && r.FromField == "A_specification_sourceID");
            Check(specRel != null && !specRel.Bidirectional && specRel.ToClass == "C_Rotational_Mount_Specification",
                "Mount->Specification correctly left unidirectional (Shared Aggregation - no reciprocal field exists to pair with)");

            var actualRel = relations.FirstOrDefault(r => r.FromClass == "C_Rotational_Mount" && r.FromField == "A_Actual_Mount_sourceID");
            Check(actualRel != null && actualRel.ToClass == "C_Actual_Mount",
                "Mount->ActualMount (specialization-as-reference) resolved to C_Actual_Mount");

            var linSpecRel = relations.FirstOrDefault(r => r.FromClass == "C_Linear_Mount" && r.FromField == "A_specification_sourceID");
            Check(linSpecRel != null && linSpecRel.ToClass == "C_Linear_Mount_Specification",
                "LinearMount->Specification resolved to the LINEAR specification, not the rotational one (ambiguous-name tiebreak)");

            Check(BiPair("C_Linear_Mount", "A_softLimits_sourceID", "C_Linear_Soft_Limits", "A_linearMount_sourceID"),
                "auto-paired LinearMount<->SoftLimits (previously failed because the target was wrongly resolved)");
            Check(BiPair("C_Linear_Mount", "A_targetPosition_sourceID", "C_Linear_Target_Position", "A_linearMount_sourceID"),
                "auto-paired LinearMount<->TargetPosition");

            Console.WriteLine();
            var kinds = topics.Select(t => new JsonSchemaWriter.KindExport
            {
                Name = t.ClassName,
                ClrType = t.FullName,
                KeyField = t.KeyField,
                IsRoot = t.ClassName is "C_Rotational_Mount" or "C_Linear_Mount",
            }).ToList();
            var json = JsonSchemaWriter.Write("P_Mount_PSM", kinds, relations);
            Console.WriteLine("-- generated JSON --");
            Console.WriteLine(json);

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            if (failures > 0) Environment.Exit(1);
        }
    }
}
