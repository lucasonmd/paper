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

            var mountKind = engine.RegisterKind(typeof(LinearMount), "A_sourceID");
            var specKind = engine.RegisterKind(typeof(LinearMountSpecification), "A_sourceID");
            var softLimitsKind = engine.RegisterKind(typeof(LinearSoftLimits), "A_sourceID");
            var partKind = engine.RegisterKind(typeof(MountPart), "A_sourceID");
            var targetKind = engine.RegisterKind(typeof(LinearTargetPosition), "A_sourceID");

            engine.RegisterRootKind(mountKind);

            engine.RegisterUnidirectional(
                "LinearMount->LinearMountSpecification", mountKind, specKind, Multiplicity.One,
                typeof(LinearMount), "A_specification_sourceID");

            engine.RegisterBidirectional(
                "LinearMount<->LinearSoftLimits", mountKind, softLimitsKind,
                Multiplicity.One, Multiplicity.One,
                typeof(LinearMount), "A_softLimits_sourceID",
                typeof(LinearSoftLimits), "A_linearMount_sourceID");

            engine.RegisterBidirectional(
                "LinearMount<->MountPart", mountKind, partKind,
                Multiplicity.ZeroOrMany, Multiplicity.One,
                typeof(LinearMount), "A_parts_sourceID",
                typeof(MountPart), "A_linearMount_sourceID");

            engine.RegisterBidirectional(
                "LinearMount<->LinearTargetPosition", mountKind, targetKind,
                Multiplicity.ZeroOrOne, Multiplicity.One,
                typeof(LinearMount), "A_targetPosition_sourceID",
                typeof(LinearTargetPosition), "A_linearMount_sourceID",
                leftPresenceCheck: PresenceCheck.NilIdentifier);

            AggregateSnapshot? lastSnapshot = null;
            int emitCount = 0;
            engine.SubscribeRootKind(mountKind, (root, snapshot) =>
            {
                emitCount++;
                lastSnapshot = snapshot;
                Console.WriteLine($"[emit #{emitCount}] root=LinearMount#{root.Key}");
            });

            var mountId = new T_IdentifierType(1, 100);
            var specId = new T_IdentifierType(2, 100);
            var softLimitsId = new T_IdentifierType(3, 100);

            Console.WriteLine("-- feeding parts, then the mount with LinearTargetPosition = NIL and 0 parts --");
            engine.Upsert(specKind, new LinearMountSpecification { A_sourceID = specId });
            engine.Upsert(softLimitsKind, new LinearSoftLimits { A_sourceID = softLimitsId, A_linearMount_sourceID = mountId });
            engine.Upsert(mountKind, new LinearMount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_softLimits_sourceID = softLimitsId,
                A_targetPosition_sourceID = T_IdentifierType.Nil,
            });

            Check(emitCount == 1, "composite-key registration + ZeroOrOne/NIL completes with exactly one emission");
            Check(lastSnapshot != null && lastSnapshot.TryGetOne<LinearMount>(mountKind, out _), "snapshot contains the LinearMount itself");
            Check(lastSnapshot != null && !lastSnapshot.TryGetOne<LinearTargetPosition>(targetKind, out _), "NIL LinearTargetPosition correctly absent from snapshot");
            System.Collections.Generic.IReadOnlyList<MountPart> partsEmpty = System.Array.Empty<MountPart>();
            lastSnapshot?.TryGetMany(partKind, out partsEmpty);
            Check(partsEmpty.Count == 0, "zero parts correctly reflected as an empty list (TryGetMany returns false, items still empty)");

            Console.WriteLine();
            Console.WriteLine("-- adding a real LinearTargetPosition, expect re-emission with it present --");
            var targetId = new T_IdentifierType(4, 100);
            engine.Upsert(targetKind, new LinearTargetPosition { A_sourceID = targetId, A_linearMount_sourceID = mountId });
            engine.Upsert(mountKind, new LinearMount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_softLimits_sourceID = softLimitsId,
                A_targetPosition_sourceID = targetId,
            });

            Check(lastSnapshot != null && lastSnapshot.TryGetOne<LinearTargetPosition>(targetKind, out _), "non-NIL LinearTargetPosition correctly present after update");

            Console.WriteLine();
            Console.WriteLine("-- adding two parts (array-of-composite-identifier, ZeroOrMany) --");
            var part1 = new T_IdentifierType(5, 100);
            var part2 = new T_IdentifierType(5, 101);
            engine.Upsert(partKind, new MountPart { A_sourceID = part1, A_linearMount_sourceID = mountId });
            engine.Upsert(partKind, new MountPart { A_sourceID = part2, A_linearMount_sourceID = mountId });
            engine.Upsert(mountKind, new LinearMount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_softLimits_sourceID = softLimitsId,
                A_targetPosition_sourceID = targetId,
                A_parts_sourceID = new[] { part1, part2 },
            });

            Check(lastSnapshot != null && lastSnapshot.TryGetMany<MountPart>(partKind, out var partsTwo) && partsTwo.Count == 2,
                "array-of-composite-identifier ZeroOrMany reflects both parts");

            RunVendorSequenceCheck();

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

        // Verifies the engine copes with a vendor sequence interface
        // (ISequence<T> : IList<T>, ICollection<T>, IEnumerable<T>,
        // IEnumerable) holding composite identifiers - the shape a real
        // DDS IDL-to-C# mapping emits for
        // `sequence<P_LDM_Common::T_IdentifierType>`. Element-type
        // resolution here goes through the GetInterfaces() fallback rather
        // than the known-collection-types fast path, so it is worth
        // covering explicitly rather than assuming.
        private static void RunVendorSequenceCheck()
        {
            Console.WriteLine();
            Console.WriteLine("-- vendor ISequence<T> of composite identifiers (DDS sequence<> shape) --");

            var engine = new global::TopicManager.Extensions.AggregationEngine();

            var mountKind = engine.RegisterKind(typeof(SeqLinearMount), "A_sourceID");
            var specKind = engine.RegisterKind(typeof(SeqLinearMountSpecification), "A_sourceID");
            var partKind = engine.RegisterKind(typeof(SeqMountPart), "A_sourceID");

            engine.RegisterRootKind(mountKind);

            engine.RegisterUnidirectional(
                "SeqLinearMount->LinearMountSpecification", mountKind, specKind, Multiplicity.One,
                typeof(SeqLinearMount), "A_specification_sourceID");

            // The field under test: ISequence<T_IdentifierType>, ZeroOrMany.
            engine.RegisterBidirectional(
                "SeqLinearMount<->MountPart", mountKind, partKind,
                Multiplicity.ZeroOrMany, Multiplicity.One,
                typeof(SeqLinearMount), "A_parts_sourceID",
                typeof(SeqMountPart), "A_linearMount_sourceID");

            AggregateSnapshot? snap = null;
            engine.SubscribeRootKind(mountKind, (_, s) => snap = s);

            var mountId = new T_IdentifierType(9, 1);
            var specId = new T_IdentifierType(9, 2);
            var p1 = new T_IdentifierType(9, 10);
            var p2 = new T_IdentifierType(9, 11);
            var p3 = new T_IdentifierType(9, 12);

            engine.Upsert(specKind, new SeqLinearMountSpecification { A_sourceID = specId });
            engine.Upsert(partKind, new SeqMountPart { A_sourceID = p1, A_linearMount_sourceID = mountId });
            engine.Upsert(partKind, new SeqMountPart { A_sourceID = p2, A_linearMount_sourceID = mountId });
            engine.Upsert(partKind, new SeqMountPart { A_sourceID = p3, A_linearMount_sourceID = mountId });

            engine.Upsert(mountKind, new SeqLinearMount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_parts_sourceID = new BoundedSequence<T_IdentifierType>(new[] { p1, p2, p3 }),
            });

            Check(snap != null, "aggregate using ISequence<T> completed");

            System.Collections.Generic.IReadOnlyList<SeqMountPart> parts = Array.Empty<SeqMountPart>();
            snap?.TryGetMany(partKind, out parts);
            Check(parts.Count == 3, $"all 3 parts resolved through ISequence<T> (got {parts.Count})");

            // Shrinking the sequence must drop the removed part from the
            // snapshot - proves the sequence is re-read on every Upsert, not
            // captured once at registration.
            engine.Upsert(mountKind, new SeqLinearMount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_parts_sourceID = new BoundedSequence<T_IdentifierType>(new[] { p1 }),
            });
            snap?.TryGetMany(partKind, out parts);
            Check(parts.Count == 1, $"shrinking the sequence to 1 element is reflected (got {parts.Count})");

            // An empty bounded sequence is a valid ZeroOrMany state.
            engine.Upsert(mountKind, new SeqLinearMount
            {
                A_sourceID = mountId,
                A_specification_sourceID = specId,
                A_parts_sourceID = new BoundedSequence<T_IdentifierType>(),
            });
            parts = Array.Empty<SeqMountPart>();
            snap?.TryGetMany(partKind, out parts);
            Check(parts.Count == 0, $"empty ISequence<T> is a valid ZeroOrMany state (got {parts.Count})");
        }
    }
}
