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

    // Same shape as the NGVA C_Linear_Mount aggregate of AEP-4754 Vol V,
    // Fig. 6 (clause 3.6.3), but using T_IdentifierType directly (as
    // DDS-generated code would) instead of the plain `long`/`long?`
    // stand-ins used in the other samples/benchmarks. This is the shape
    // RegisterKind(Type,...) and Register(Uni|Bi)directional(Type,...) are
    // meant to read via reflection, driven by the JSON schema discussed
    // alongside this code.
    //
    // MountPart is NOT from Fig. 6 - the linear fragment has no
    // zero-or-many association. It is a synthetic 0..* part, kept because
    // the whole point of this sample is to cover how the engine reads a
    // ZeroOrMany reference held as an array (and, in DdsSequenceShapes.cs,
    // as a vendor ISequence<T>) of composite identifiers. The real NGVA
    // 0..* reference of this shape - C_Rotational_Mount's
    // A_movementInhibitZones_sourceID - is covered in JsonSample.

    public sealed class LinearMount
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_specification_sourceID;
        public T_IdentifierType A_softLimits_sourceID;
        public T_IdentifierType A_targetPosition_sourceID; // NIL when no position command is active
        public T_IdentifierType[] A_parts_sourceID = System.Array.Empty<T_IdentifierType>();
    }

    public sealed class LinearMountSpecification
    {
        public T_IdentifierType A_sourceID;
    }

    public sealed class LinearSoftLimits
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_linearMount_sourceID;
    }

    public sealed class MountPart
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_linearMount_sourceID;
    }

    public sealed class LinearTargetPosition
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_linearMount_sourceID;
    }
}
