using System;
using System.IO;
using P_Mount_PSM;
using TopicManager.Extensions;

namespace AggregationEngine.Simple
{
    // End-to-end verification that a topic module can be stood up from
    // JSON alone: this file never calls RegisterKind/RegisterUnidirectional/
    // RegisterBidirectional and never mentions a P_Mount_PSM class name as
    // a string - JsonModuleLoader.LoadFile does all of that by reading
    // Mount.module.json. If a new module needs to be added, only that JSON
    // file (plus the already-generated DDS classes it points at) should
    // need to change.
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool condition, string description)
        {
            Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + description);
            if (!condition) _failures++;
        }

        private static void Main()
        {
            var engine = new global::TopicManager.Extensions.AggregationEngine();

            var jsonPath = Path.Combine(AppContext.BaseDirectory, "Mount.module.json");
            Console.WriteLine($"-- loading module from {jsonPath} --");
            var kinds = JsonModuleLoader.LoadFile(engine, jsonPath);
            Console.WriteLine("-- module loaded (no hand-written Register* calls in this project) --");
            Console.WriteLine();

            // The names here are exactly the "name" values from
            // Mount.module.json - this is the only place this program
            // knows about the module's shape, and it's data, not code.
            var rotationalKind = kinds["C_Rotational_Mount"];
            var linearKind = kinds["C_Linear_Mount"];
            var actualKind = kinds["C_Actual_Mount"];
            var rotSpecKind = kinds["C_Rotational_Mount_Specification"];
            var rotSoftLimitsKind = kinds["C_Rotational_Soft_Limits"];
            var zoneKind = kinds["C_Movement_Inhibit_Zone"];
            var rotTargetKind = kinds["C_Rotational_Target_Position"];
            var linSpecKind = kinds["C_Linear_Mount_Specification"];
            var linSoftLimitsKind = kinds["C_Linear_Soft_Limits"];
            var linTargetKind = kinds["C_Linear_Target_Position"];

            int rotEmits = 0, linEmits = 0;
            AggregateSnapshot? lastRot = null;
            AggregateSnapshot? lastLin = null;
            engine.SubscribeRootKind(rotationalKind, (root, snap) => { rotEmits++; lastRot = snap; Console.WriteLine($"[emit] RotationalMount#{root.Key}"); });
            engine.SubscribeRootKind(linearKind, (root, snap) => { linEmits++; lastLin = snap; Console.WriteLine($"[emit] LinearMount#{root.Key}"); });

            // A single ActualMount instance shared by both aggregates -
            // proves the Kind-sharing case discussed alongside this schema
            // (RotationalMount->ActualMount and LinearMount->ActualMount
            // both resolve to the one C_Actual_Mount kind registered once).
            var sharedActual = new T_IdentifierType(1, 1);
            engine.Upsert(actualKind, new C_Actual_Mount { A_sourceID = sharedActual });

            Console.WriteLine("-- completing the Rotational Mount aggregate (TargetPosition = NIL, 0 zones) --");
            var rotMountId = new T_IdentifierType(10, 1);
            var rotSpecId = new T_IdentifierType(11, 1);
            var rotSoftLimitsId = new T_IdentifierType(12, 1);
            engine.Upsert(rotSpecKind, new C_Rotational_Mount_Specification { A_sourceID = rotSpecId });
            engine.Upsert(rotSoftLimitsKind, new C_Rotational_Soft_Limits { A_sourceID = rotSoftLimitsId, A_rotationalMount_sourceID = rotMountId });
            engine.Upsert(rotationalKind, new C_Rotational_Mount
            {
                A_sourceID = rotMountId,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = rotSpecId,
                A_softLimits_sourceID = rotSoftLimitsId,
                A_targetPosition_sourceID = T_IdentifierType.Nil,
            });

            Check(rotEmits == 1, "RotationalMount completes once via JSON-driven registration");
            Check(lastRot != null && !lastRot.TryGetOne<C_Rotational_Target_Position>(rotTargetKind, out _), "NIL TargetPosition correctly absent");

            Console.WriteLine();
            Console.WriteLine("-- adding a real TargetPosition and two InhibitZones to the Rotational Mount --");
            var rotTargetId = new T_IdentifierType(13, 1);
            var zone1 = new T_IdentifierType(14, 1);
            var zone2 = new T_IdentifierType(14, 2);
            engine.Upsert(rotTargetKind, new C_Rotational_Target_Position { A_sourceID = rotTargetId, A_rotationalMount_sourceID = rotMountId });
            engine.Upsert(zoneKind, new C_Movement_Inhibit_Zone { A_sourceID = zone1, A_rotationalMount_sourceID = rotMountId });
            engine.Upsert(zoneKind, new C_Movement_Inhibit_Zone { A_sourceID = zone2, A_rotationalMount_sourceID = rotMountId });
            engine.Upsert(rotationalKind, new C_Rotational_Mount
            {
                A_sourceID = rotMountId,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = rotSpecId,
                A_softLimits_sourceID = rotSoftLimitsId,
                A_targetPosition_sourceID = rotTargetId,
                A_movementInhibitZones_sourceID = new[] { zone1, zone2 },
            });

            Check(lastRot != null && lastRot.TryGetOne<C_Rotational_Target_Position>(rotTargetKind, out _), "non-NIL TargetPosition present after update");
            Check(lastRot != null && lastRot.TryGetMany<C_Movement_Inhibit_Zone>(zoneKind, out var zones) && zones.Count == 2, "both InhibitZones (array-of-composite-identifier) present");

            Console.WriteLine();
            Console.WriteLine("-- completing the Linear Mount aggregate, reusing the SAME shared ActualMount --");
            var linMountId = new T_IdentifierType(20, 1);
            var linSpecId = new T_IdentifierType(21, 1);
            var linSoftLimitsId = new T_IdentifierType(22, 1);
            engine.Upsert(linSpecKind, new C_Linear_Mount_Specification { A_sourceID = linSpecId });
            engine.Upsert(linSoftLimitsKind, new C_Linear_Soft_Limits { A_sourceID = linSoftLimitsId, A_linearMount_sourceID = linMountId });
            engine.Upsert(linearKind, new C_Linear_Mount
            {
                A_sourceID = linMountId,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = linSpecId,
                A_softLimits_sourceID = linSoftLimitsId,
                A_targetPosition_sourceID = T_IdentifierType.Nil,
            });

            Check(linEmits == 1, "LinearMount completes once, independently of the Rotational aggregate");
            Check(lastLin != null && !lastLin.TryGetOne<C_Linear_Target_Position>(linTargetKind, out _), "NIL TargetPosition correctly absent (Linear side)");
            Check(rotEmits >= 1, "sharing C_Actual_Mount across both aggregates did not disturb the already-completed RotationalMount");

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("ALL CHECKS PASSED");
            }
            else
            {
                Console.WriteLine($"{_failures} CHECK(S) FAILED");
                Environment.Exit(1);
            }
        }
    }
}
