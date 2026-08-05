namespace AggregationEngine.ReflectionSample
{
    // Stand-in for the composite key type DDS-generated NGVA classes use
    // (P_LDM_Common::T_IdentifierType). NGVA_DM_030/031/037 name these exact
    // sub-fields, and NGVA_DM_037 fills an absent ZeroOrOne reference with
    // A_resourceId=0, A_instanceId=0 rather than leaving it null - that NIL
    // convention is what PresenceCheck.NilIdentifier is for.
    public struct T_IdentifierType
    {
        public long A_resourceId;
        public long A_instanceId;

        public T_IdentifierType(long resourceId, long instanceId)
        {
            A_resourceId = resourceId;
            A_instanceId = instanceId;
        }

        public static readonly T_IdentifierType Nil = new T_IdentifierType(0, 0);
    }

    // Same shape as the real NGVA C_Rotational_Mount aggregate, but using
    // T_IdentifierType directly (as DDS-generated code would) instead of
    // the plain `long`/`long?` stand-ins used in the other samples/
    // benchmarks. This is the shape RegisterKind(Type,...) and
    // Register(Uni|Bi)directional(Type,...) are meant to read via
    // reflection, driven by the JSON schema discussed alongside this code.

    public sealed class Mount
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_specification_sourceID;
        public T_IdentifierType A_softLimits_sourceID;
        public T_IdentifierType A_targetPosition_sourceID; // NIL when no position command is active
        public T_IdentifierType[] A_movementInhibitZones_sourceID = System.Array.Empty<T_IdentifierType>();
    }

    public sealed class Specification
    {
        public T_IdentifierType A_sourceID;
    }

    public sealed class SoftLimits
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_rotationalMount_sourceID;
    }

    public sealed class InhibitZone
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_rotationalMount_sourceID;
    }

    public sealed class TargetPosition
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_rotationalMount_sourceID;
    }
}
