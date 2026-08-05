using System.IO;
using System.Windows;
using AggregationEngine.ModuleDesigner.ViewModels;
using Microsoft.Win32;

namespace AggregationEngine.ModuleDesigner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(PickCsFiles, SaveJson);
        DataContext = _vm;
    }

    private static string[]? PickCsFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select .cs file(s) containing DDS-generated topic classes",
            Filter = "C# source files (*.cs)|*.cs|All files (*.*)|*.*",
            Multiselect = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    private static void SaveJson(string json)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save module JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "Module.json",
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, json);
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e) => _vm.AddFiles();

    private void OnRemoveFiles(object sender, RoutedEventArgs e)
    {
        if (FilesListBox.SelectedItems.Count > 0)
            _vm.RemoveFiles(FilesListBox.SelectedItems);
    }
}
