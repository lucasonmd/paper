using System;
using TopicManager.Extensions;

namespace AggregationEngine.ReflectionSample
{
    // Smoke test / worked example for the reflection-based, non-generic
    // Register* overloads: RegisterKind(Type,...), RegisterUnidirectional
    // (Type,...), RegisterBidirectional(Type,...). These exist so a JSON
    // schema (see the schema discussed alongside this code) can drive
    // registration without hand-written lambdas per topic kind - the goal
    // being that adding a new module (e.g. Mount) needs only a JSON file,
    // not new C# registration code.
    //
    // Exercises the three things that make NGVA-shaped DDS classes
    // different from the plain `long`/`long?` stand-ins used elsewhere in
    // this repo: a composite T_IdentifierType key/reference type, the NIL
    // (0,0) convention for an absent ZeroOrOne reference (NGVA_DM_037,
    // instead of a C# nullable), and a ZeroOrMany reference held as an
    // array of composite identifiers.
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool condition, string description)
        {
            if (condition)
            {
                Console.WriteLine($"  PASS  {description}");
            }
            else
            {
                Console.WriteLine($"  FAIL  {description}");
                _failures++;
            }
        }

        private static void Main()
        {
            var engine = new global::TopicManager.Extensions.AggregationEngine();

            var mountKind = engine.RegisterKind(typeof(Mount), "A_sourceID");
            var specKind = engine.RegisterKind(typeof(Specification), "A_sourceID");
            var softLimitsKind = engine.RegisterKind(typeof(SoftLimits), "A_sourceID");
            var zoneKind = engine.RegisterKind(typeof(InhibitZone), "A_sourceID");
            var targetKind = engine.RegisterKind(typeof(TargetPosition), "A_sourceID");

            engine.RegisterRootKind(mountKind);

            engine.RegisterUnidirectional(
                "Mount->Specification", mountKind, specKind, Multiplicity.One,
                typeof(Mount), "A_specification_sourceID");

            engine.RegisterBidirectional(
                "Mount<->SoftLimits", mountKind, softLimitsKind,
                Multiplicity.One, Multiplicity.One,
                typeof(Mount), "A_softLimits_sourceID",
                typeof(SoftLimits), "A_rotationalMount_sourceID");

            engine.RegisterBidirectional(
                "Mount<->InhibitZone", mountKind, zoneKind,
                Multiplicity.ZeroOrMany, Multiplicity.One,
                typeof(Mount), "A_movementInhibitZones_sourceID",
                typeof(InhibitZone), "A_rotationalMount_sourceID");

            engine.RegisterBidirectional(
                "Mount<->TargetPosition", mountKind, targetKind,
                Multiplicity.ZeroOrOne, Multiplicity.One,
                typeof(Mount), "A_targetPosition_sourceID",
                typeof(TargetPosition), "A_rotationalMount_sourceID",
                leftPresenceCheck: PresenceCheck.NilIdentifier);

            AggregateSnapshot? lastSnapshot = null;
            int emitCount = 0;
            engine.SubscribeRootKind(mountKind, (root, snapshot) =>
            {
                emitCount++;
                lastSnapshot = snapshot;
                Console.WriteLine($"[emit #{emitCount}] root=Mount#{root.Key}");
            });

            var mountId = new T_IdentifierType(1, 100);
            var specId = new T_IdentifierType(2, 100);
            var softLimitsId = new T_IdentifierType(3, 100);

            Console.WriteLine("-- feeding parts, then the mount with TargetPosition = NIL and 0 zones --");
            engine.Upsert(specKind, new Specification { A_sourceID = specId });
            engine.Upsert(softLimitsKind, new SoftLimits { A_sourceID = softLimitsId, A_rotationalMount_sourceID = mountId });
            engine.Upsert(mountKind, new Mount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_softLimits_sourceID = softLimitsId,
                A_targetPosition_sourceID = T_IdentifierType.Nil,
            });

            Check(emitCount == 1, "composite-key registration + ZeroOrOne/NIL completes with exactly one emission");
            Check(lastSnapshot != null && lastSnapshot.TryGetOne<Mount>(mountKind, out _), "snapshot contains the Mount itself");
            Check(lastSnapshot != null && !lastSnapshot.TryGetOne<TargetPosition>(targetKind, out _), "NIL TargetPosition correctly absent from snapshot");
            System.Collections.Generic.IReadOnlyList<InhibitZone> zonesEmpty = System.Array.Empty<InhibitZone>();
            lastSnapshot?.TryGetMany(zoneKind, out zonesEmpty);
            Check(zonesEmpty.Count == 0, "zero zones correctly reflected as an empty list (TryGetMany returns false, items still empty)");

            Console.WriteLine();
            Console.WriteLine("-- adding a real TargetPosition, expect re-emission with it present --");
            var targetId = new T_IdentifierType(4, 100);
            engine.Upsert(targetKind, new TargetPosition { A_sourceID = targetId, A_rotationalMount_sourceID = mountId });
            engine.Upsert(mountKind, new Mount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_softLimits_sourceID = softLimitsId,
                A_targetPosition_sourceID = targetId,
            });

            Check(lastSnapshot != null && lastSnapshot.TryGetOne<TargetPosition>(targetKind, out _), "non-NIL TargetPosition correctly present after update");

            Console.WriteLine();
            Console.WriteLine("-- adding two InhibitZones (array-of-composite-identifier, ZeroOrMany) --");
            var zone1 = new T_IdentifierType(5, 100);
            var zone2 = new T_IdentifierType(5, 101);
            engine.Upsert(zoneKind, new InhibitZone { A_sourceID = zone1, A_rotationalMount_sourceID = mountId });
            engine.Upsert(zoneKind, new InhibitZone { A_sourceID = zone2, A_rotationalMount_sourceID = mountId });
            engine.Upsert(mountKind, new Mount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_softLimits_sourceID = softLimitsId,
                A_targetPosition_sourceID = targetId,
                A_movementInhibitZones_sourceID = new[] { zone1, zone2 },
            });

            Check(lastSnapshot != null && lastSnapshot.TryGetMany<InhibitZone>(zoneKind, out var zonesTwo) && zonesTwo.Count == 2,
                "array-of-composite-identifier ZeroOrMany reflects both zones");

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
