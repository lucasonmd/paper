using System.Collections.Generic;

namespace AggregationEngine.ModuleDesigner.Core
{
    // A class discovered in the supplied .cs files that looks like a Topic
    // (has an "A_sourceID"-named member, matching the NGVA/GVA convention
    // this whole toolchain assumes). One of these maps to one JSON "kind"
    // entry once the user confirms it.
    public sealed class DetectedTopic
    {
        public string ClassName { get; init; } = "";
        public string? Namespace { get; init; }
        public string FullName => string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";
        public string SourceFile { get; init; } = "";
        public string KeyField { get; init; } = "A_sourceID";

        // Members on this class shaped like "A_<something>_sourceID" -
        // candidates for relation fields, excluding KeyField itself.
        public List<DetectedField> CandidateFields { get; } = new();
    }

    public enum FieldShape
    {
        Scalar,      // e.g. T_IdentifierType, or a plain integer key type
        Nullable,    // T? / Nullable<T>
        Enumerable,  // T[] / List<T> / IReadOnlyList<T> / IEnumerable<T>
    }

    public sealed class DetectedField
    {
        public string FieldName { get; init; } = "";
        public string TypeText { get; init; } = "";
        public FieldShape Shape { get; init; }
    }

    // One row the user reviews/edits before export - a candidate relation
    // inferred from a DetectedField, pre-filled with best-effort guesses
    // for the parts that can't be determined from a single field's syntax
    // alone (target Kind, multiplicity, direction, presence convention).
    public sealed class CandidateRelation
    {
        // "<from class>-<to class>". Computed rather than stored so it can
        // never go stale after the user re-points ToClass in the review UI.
        // The engine never reads relation names (they only surface in
        // JsonModuleLoader's error messages and when a human reads the
        // generated file), so uniqueness is not required - two relations
        // between the same pair of classes legitimately share a name.
        public string Name => $"{FromClass}-{ToClass ?? "?"}";

        public string FromClass { get; set; } = "";
        public string FromField { get; set; } = "";
        public string? ToClass { get; set; }         // null = unresolved, user must pick
        public bool Bidirectional { get; set; }
        public string? ReciprocalField { get; set; } // set when Bidirectional and auto-paired
        public string Multiplicity { get; set; } = "One";           // JSON string values
        public string ReciprocalMultiplicity { get; set; } = "One";
        public string PresenceCheck { get; set; } = "Nullable";     // only meaningful when either side is ZeroOrOne
        public double Confidence { get; set; }       // 0..1, how sure the target-class guess is
    }
}
