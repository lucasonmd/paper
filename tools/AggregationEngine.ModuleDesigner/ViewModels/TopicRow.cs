using AggregationEngine.ModuleDesigner.Core;
using AggregationEngine.ModuleDesigner.Mvvm;

namespace AggregationEngine.ModuleDesigner.ViewModels
{
    // UI-editable wrapper around a DetectedTopic. Core stays free of any
    // WPF/INotifyPropertyChanged concerns; this is where "did the user
    // confirm this as a Kind, and is it a root" lives.
    public sealed class TopicRow : ObservableObject
    {
        public DetectedTopic Source { get; }

        public string ClassName => Source.ClassName;
        public string? Namespace => Source.Namespace;
        public string FullName => Source.FullName;
        public string SourceFile => Source.SourceFile;
        public int FieldCount => Source.CandidateFields.Count;

        private bool _include = true;
        public bool Include { get => _include; set => Set(ref _include, value); }

        private bool _isRoot;
        public bool IsRoot { get => _isRoot; set => Set(ref _isRoot, value); }

        private string _keyField;
        public string KeyField { get => _keyField; set => Set(ref _keyField, value); }

        public TopicRow(DetectedTopic source)
        {
            Source = source;
            _keyField = source.KeyField;
        }
    }
}
