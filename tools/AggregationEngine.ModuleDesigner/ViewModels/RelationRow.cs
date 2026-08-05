using System.Collections.ObjectModel;
using AggregationEngine.ModuleDesigner.Core;
using AggregationEngine.ModuleDesigner.Mvvm;

namespace AggregationEngine.ModuleDesigner.ViewModels
{
    public static class Options
    {
        public static readonly string[] Multiplicities = { "One", "ZeroOrOne", "OneOrMany", "ZeroOrMany" };
        public static readonly string[] Directions = { "Unidirectional", "Bidirectional" };
        public static readonly string[] PresenceChecks = { "Nullable", "NilIdentifier" };
    }

    // UI-editable wrapper around one CandidateRelation. Every property here
    // both raises PropertyChanged (for the DataGrid) and writes straight
    // through to the underlying model, so JsonSchemaWriter always sees the
    // user's edits, not the original guess.
    public sealed class RelationRow : ObservableObject
    {
        public CandidateRelation Model { get; }

        // Shared with the parent view model so the "target class" and
        // "reciprocal field" pickers always reflect the current Topic list.
        public ObservableCollection<string> AvailableClassNames { get; }

        public string FromClass => Model.FromClass;
        public string FromField => Model.FromField;
        public double Confidence => Model.Confidence;

        private string? _toClass;
        public string? ToClass
        {
            get => _toClass;
            set { if (Set(ref _toClass, value)) Model.ToClass = value; }
        }

        private bool _bidirectional;
        public bool Bidirectional
        {
            get => _bidirectional;
            set
            {
                if (Set(ref _bidirectional, value))
                {
                    Model.Bidirectional = value;
                    OnPropertyChanged(nameof(ShowReciprocalFields));
                    OnPropertyChanged(nameof(ShowPresenceCheck));
                }
            }
        }

        private string? _reciprocalField;
        public string? ReciprocalField
        {
            get => _reciprocalField;
            set { if (Set(ref _reciprocalField, value)) Model.ReciprocalField = value ?? ""; }
        }

        private string _multiplicity;
        public string Multiplicity
        {
            get => _multiplicity;
            set
            {
                if (Set(ref _multiplicity, value))
                {
                    Model.Multiplicity = value;
                    OnPropertyChanged(nameof(ShowPresenceCheck));
                }
            }
        }

        private string _reciprocalMultiplicity;
        public string ReciprocalMultiplicity
        {
            get => _reciprocalMultiplicity;
            set
            {
                if (Set(ref _reciprocalMultiplicity, value))
                {
                    Model.ReciprocalMultiplicity = value;
                    OnPropertyChanged(nameof(ShowPresenceCheck));
                }
            }
        }

        private string _presenceCheck;
        public string PresenceCheck
        {
            get => _presenceCheck;
            set { if (Set(ref _presenceCheck, value)) Model.PresenceCheck = value; }
        }

        // Visible only when relevant, so the grid isn't cluttered with
        // fields that don't apply to a plain unidirectional/One relation.
        public bool ShowReciprocalFields => Bidirectional;
        public bool ShowPresenceCheck => Multiplicity == "ZeroOrOne" || (Bidirectional && ReciprocalMultiplicity == "ZeroOrOne");

        public RelationRow(CandidateRelation model, ObservableCollection<string> availableClassNames)
        {
            Model = model;
            AvailableClassNames = availableClassNames;
            _toClass = model.ToClass;
            _bidirectional = model.Bidirectional;
            _reciprocalField = model.ReciprocalField;
            _multiplicity = model.Multiplicity;
            _reciprocalMultiplicity = model.ReciprocalMultiplicity;
            _presenceCheck = model.PresenceCheck;
        }
    }
}
