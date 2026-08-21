using System;
using System.IO;
using TopicManager.Extensions;
using UsageExample.Dds;
using UsageExample.Mount;

namespace UsageExample
{
    // Worked example of how TopicManager.Extensions.AggregationEngine is
    // used in a DDS subscriber.
    //
    // The whole integration is three steps:
    //
    //   1. Load the module definition (Mount.module.json). This registers
    //      every topic Kind and the reference relations between them, so
    //      no per-topic registration code is written by hand.
    //   2. Create one DDS reader per topic and forward each received
    //      sample straight into engine.Upsert(...). These callbacks contain
    //      no matching, storage, or completeness logic - that is what the
    //      engine replaces.
    //   3. Subscribe once, per root topic, to receive fully assembled
    //      aggregates. Application logic lives only here.
    internal static class Program
    {
        private static void Main()
        {
            var engine = new AggregationEngine();

            // --- 1. module definition -------------------------------------
            var jsonPath = Path.Combine(AppContext.BaseDirectory, "Mount.module.json");
            var kinds = JsonModuleLoader.LoadFile(engine, jsonPath);

            // --- 2. DDS readers -> engine.Upsert --------------------------
            var participant = new DdsParticipant();
            SubscribeTopics(engine, participant, kinds);

            // --- 3. completed aggregates -> application logic -------------
            engine.SubscribeRootKind(kinds["C_Linear_Mount"], OnMountAggregateCompleted);

            Console.WriteLine($"Module loaded: {kinds.Count} topic kinds registered.");
            Console.WriteLine($"DDS readers created for {participant.CreatedTopics.Count} topics:");
            foreach (var topic in participant.CreatedTopics)
                Console.WriteLine($"  - {topic}");
            Console.WriteLine();
            Console.WriteLine("Subscriber is wired. Completed aggregates will be delivered to");
            Console.WriteLine("OnMountAggregateCompleted as their topics arrive from DDS.");
        }

        /// <summary>
        /// Creates one reader per topic and routes every received sample into
        /// the engine. Each callback is a single Upsert call: the engine
        /// stores the topic, updates its relation indexes, works out which
        /// aggregates the sample affects, and decides whether any of them is
        /// now complete.
        /// </summary>
        private static void SubscribeTopics(
            AggregationEngine engine,
            DdsParticipant participant,
            System.Collections.Generic.IReadOnlyDictionary<string, KindId> kinds)
        {
            // Topic names follow the NGVA convention
            // "<PIM package>__<class>" (AEP-4754 Vol V, NGVA_DM_027).
            Route<C_Linear_Mount>(engine, participant, kinds, "Mount__C_Linear_Mount", "C_Linear_Mount");
            Route<C_Actual_Mount>(engine, participant, kinds, "Mount__C_Actual_Mount", "C_Actual_Mount");
            Route<C_Linear_Mount_Specification>(engine, participant, kinds, "Mount__C_Linear_Mount_Specification", "C_Linear_Mount_Specification");
            Route<C_Linear_Soft_Limits>(engine, participant, kinds, "Mount__C_Linear_Soft_Limits", "C_Linear_Soft_Limits");
            Route<C_Linear_Target_Position>(engine, participant, kinds, "Mount__C_Linear_Target_Position", "C_Linear_Target_Position");
            Route<C_Mount_Part>(engine, participant, kinds, "Mount__C_Mount_Part", "C_Mount_Part");
        }

        /// <summary>
        /// Creates the reader for one topic type and connects its callback to
        /// the engine. This is the only glue code a new topic needs - adding a
        /// topic means one entry in Mount.module.json and one line here.
        /// </summary>
        private static void Route<T>(
            AggregationEngine engine,
            DdsParticipant participant,
            System.Collections.Generic.IReadOnlyDictionary<string, KindId> kinds,
            string topicName,
            string kindName)
            where T : class
        {
            var kind = kinds[kindName];
            var reader = participant.CreateReader<T>(topicName);

            // The receive callback does one thing: hand the sample to the
            // engine. Everything the hand-written approach used to do here -
            // storing it, matching sourceIDs against other topics, tracking
            // which parts have arrived - happens inside Upsert.
            reader.OnDataAvailable += sample => engine.Upsert(kind, sample);
        }

        /// <summary>
        /// Called once a linear mount aggregate is complete: the mount
        /// itself, its base topic and its specification - the parts Fig. 6
        /// declares with multiplicity 1 - have all arrived, every
        /// bidirectional reference agrees in both directions, and any
        /// optional parts present are included.
        ///
        /// The snapshot is the assembled aggregate. Read the parts with
        /// snapshot.TryGetOne&lt;T&gt;(kind, out var part) for single-valued
        /// references and snapshot.TryGetMany&lt;T&gt;(kind, out var parts) for
        /// 0..* references; SubscribeRootKind&lt;TAggregate&gt; can bind the
        /// whole snapshot into a caller-defined object instead.
        ///
        /// Application logic goes here. Keep it short: this runs on the
        /// thread that delivered the triggering sample - the DDS receive
        /// thread in a real system - so anything slow should be queued to a
        /// worker rather than done inline.
        /// </summary>
        private static void OnMountAggregateCompleted(RootId root, AggregateSnapshot snapshot)
        {
            // Intentionally empty - this example shows the wiring, not a
            // particular application's behaviour.
        }
    }
}
