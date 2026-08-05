namespace P_Mount_PSM
{
    // Namespace matches the "clrType" values in Mount.module.json exactly
    // (P_Mount_PSM.<ClassName>) - this is the whole point of the sample:
    // none of these classes are referenced by name anywhere in C# code in
    // this project. JsonModuleLoader finds them purely by scanning loaded
    // assemblies for the string "P_Mount_PSM.C_Rotational_Mount" etc.

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

    public sealed class C_Actual_Mount
    {
        public T_IdentifierType A_sourceID;
    }

    // -- Rotational mount aggregate --

    public sealed class C_Rotational_Mount
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_Actual_Mount_sourceID;
        public T_IdentifierType A_specification_sourceID;
        public T_IdentifierType A_softLimits_sourceID;
        public T_IdentifierType A_targetPosition_sourceID; // NIL when no position command is active
        public T_IdentifierType[] A_movementInhibitZones_sourceID = System.Array.Empty<T_IdentifierType>();
    }

    public sealed class C_Rotational_Mount_Specification
    {
        public T_IdentifierType A_sourceID;
    }

    public sealed class C_Rotational_Soft_Limits
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_rotationalMount_sourceID;
    }

    public sealed class C_Movement_Inhibit_Zone
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_rotationalMount_sourceID;
    }

    public sealed class C_Rotational_Target_Position
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_rotationalMount_sourceID;
    }

    // -- Linear mount aggregate (shares C_Actual_Mount with the rotational one) --

    public sealed class C_Linear_Mount
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_Actual_Mount_sourceID;
        public T_IdentifierType A_specification_sourceID;
        public T_IdentifierType A_softLimits_sourceID;
        public T_IdentifierType A_targetPosition_sourceID; // NIL when no position command is active
    }

    public sealed class C_Linear_Mount_Specification
    {
        public T_IdentifierType A_sourceID;
    }

    public sealed class C_Linear_Soft_Limits
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_linearMount_sourceID;
    }

    public sealed class C_Linear_Target_Position
    {
        public T_IdentifierType A_sourceID;
        public T_IdentifierType A_linearMount_sourceID;
    }
}
