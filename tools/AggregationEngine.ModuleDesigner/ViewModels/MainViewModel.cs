using System;
using System.Collections.ObjectModel;
using System.Linq;
using AggregationEngine.ModuleDesigner.Core;
using AggregationEngine.ModuleDesigner.Mvvm;

namespace AggregationEngine.ModuleDesigner.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        public ObservableCollection<string> SourceFiles { get; } = new();
        public ObservableCollection<TopicRow> Topics { get; } = new();
        public ObservableCollection<RelationRow> Relations { get; } = new();

        // Shared with every RelationRow's ToClass/ReciprocalField pickers,
        // and kept in sync with Topics so newly-added or renamed classes
        // show up without re-running Analyze.
        public ObservableCollection<string> AvailableClassNames { get; } = new();

        private string _moduleName = "MyModule";
        public string ModuleName { get => _moduleName; set => Set(ref _moduleName, value); }

        private string _statusText = "Add .cs files, then Analyze.";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private string _jsonPreview = "";
        public string JsonPreview { get => _jsonPreview; set => Set(ref _jsonPreview, value); }

        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateJsonCommand { get; }
        public RelayCommand RemoveSelectedFilesCommand { get; }

        private readonly Func<string[]?> _pickFiles;
        private readonly Action<string> _saveJson;

        public MainViewModel(Func<string[]?> pickFiles, Action<string> saveJson)
        {
            _pickFiles = pickFiles;
            _saveJson = saveJson;
            AnalyzeCommand = new RelayCommand(Analyze, _ => SourceFiles.Count > 0);
            GenerateJsonCommand = new RelayCommand(GenerateAndSaveJson, _ => Topics.Any(t => t.Include));
            RemoveSelectedFilesCommand = new RelayCommand(_ => { }); // wired from code-behind with the grid selection
        }

        public void AddFiles()
        {
            var picked = _pickFiles();
            if (picked == null) return;
            foreach (var f in picked)
                if (!SourceFiles.Contains(f)) SourceFiles.Add(f);
            StatusText = $"{SourceFiles.Count} file(s) queued. Click Analyze.";
        }

        public void RemoveFiles(System.Collections.IList selected)
        {
            foreach (var item in selected.Cast<string>().ToList())
                SourceFiles.Remove(item);
        }

        private void Analyze(object? _)
        {
            try
            {
                var detected = CsFileAnalyzer.Analyze(SourceFiles);
                var inferred = RelationInference.Infer(detected);

                Topics.Clear();
                AvailableClassNames.Clear();
                foreach (var t in detected)
                {
                    Topics.Add(new TopicRow(t));
                    AvailableClassNames.Add(t.ClassName);
                }

                Relations.Clear();
                foreach (var r in inferred)
                    Relations.Add(new RelationRow(r, AvailableClassNames));

                var unresolved = Relations.Count(r => r.ToClass == null);
                var lowConfidence = Relations.Count(r => r.Confidence < 0.8);
                StatusText = $"Detected {Topics.Count} topic(s), {Relations.Count} candidate relation(s). " +
                              $"{unresolved} unresolved, {lowConfidence} worth double-checking (confidence < 0.8). " +
                              "Review the Relations grid before exporting.";
            }
            catch (Exception ex)
            {
                StatusText = $"Analyze failed: {ex.Message}";
            }
        }

        private void GenerateAndSaveJson(object? _)
        {
            var kinds = Topics.Where(t => t.Include).Select(t => new JsonSchemaWriter.KindExport
            {
                Name = t.ClassName,
                ClrType = t.FullName,
                KeyField = t.KeyField,
                IsRoot = t.IsRoot,
            }).ToList();

            var includedNames = kinds.Select(k => k.Name).ToHashSet();
            var relations = Relations
                .Where(r => includedNames.Contains(r.FromClass) && r.ToClass != null && includedNames.Contains(r.ToClass))
                .Select(r => r.Model)
                .ToList();

            var skipped = Relations.Count - relations.Count;

            var json = JsonSchemaWriter.Write(ModuleName, kinds, relations);
            JsonPreview = json;
            _saveJson(json);

            StatusText = $"Exported {kinds.Count} kind(s), {relations.Count} relation(s)." +
                         (skipped > 0 ? $" ({skipped} relation(s) skipped - unresolved or pointing at an excluded/unresolved class.)" : "");
        }
    }
}
