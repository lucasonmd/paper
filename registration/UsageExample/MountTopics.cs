using System;

namespace UsageExample.Mount
{
    // Topic definitions for the example, modelled on the NGVA
    // C_Rotational_Mount aggregate.
    //
    // These are simplified stand-ins written for this example, not
    // DDS-generated code: keys are plain `long` rather than the composite
    // identifier struct a real IDL-to-C# toolchain emits, so that the
    // example stays readable. The engine supports both forms (the composite
    // case is exercised in samples/AggregationEngine.ReflectionSample), and
    // nothing here depends on a DDS vendor SDK.
    //
    // The naming follows the NGVA convention the engine keys off:
    //   - entity/topic classes are prefixed "C_"
    //   - a topic's own identity is "A_sourceID"
    //   - a reference to another topic is "A_<target>_sourceID"

    /// <summary>Root topic of the aggregate: a rotational mount.</summary>
    public sealed class C_Rotational_Mount
    {
        public long A_sourceID;

        /// <summary>Reference to the base topic (specialization expressed as a reference). Multiplicity 1.</summary>
        public long A_Actual_Mount_sourceID;

        /// <summary>Reference to the static specification. Multiplicity 1.</summary>
        public long A_specification_sourceID;

        /// <summary>Reference to the software motion limits. Multiplicity 1, reciprocated.</summary>
        public long A_softLimits_sourceID;

        /// <summary>Movement inhibit zones. Multiplicity 0..*, reciprocated.</summary>
        public long[] A_movementInhibitZones_sourceID = Array.Empty<long>();

        /// <summary>Target position; present only while a position command is active. Multiplicity 0..1, reciprocated.</summary>
        public long? A_targetPosition_sourceID;
    }

    /// <summary>Base topic the rotational mount specializes.</summary>
    public sealed class C_Actual_Mount
    {
        public long A_sourceID;
    }

    /// <summary>Static parameters of the mount.</summary>
    public sealed class C_Rotational_Mount_Specification
    {
        public long A_sourceID;
    }

    /// <summary>Software motion limits, referencing its mount back.</summary>
    public sealed class C_Rotational_Soft_Limits
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }

    /// <summary>An area the mount may not be pointed into, referencing its mount back.</summary>
    public sealed class C_Movement_Inhibit_Zone
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }

    /// <summary>Commanded target position, referencing its mount back.</summary>
    public sealed class C_Rotational_Target_Position
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }
}
