using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 第一试卷下载模块（二级页面）：本地题库缓存 + 学科/年级联动 + 版本/关键词筛选 + 勾选批量下载。
/// </summary>
[ToolModule("firstpaper", "第一试卷下载", "📝", "按学科年级更新题库，勾选批量下载试卷",
    Order = 120, Tables = new string[] { "FirstPaperRecords" })]
public partial class FirstPaperView : UserControl
{
    private bool _loaded;

    public FirstPaperView()
    {
        InitializeComponent();
        VM = new FirstPaperViewModel();
        DataContext = VM;

        VM.Load();
        SubjectCombo.SelectedItem = VM.Subjects.FirstOrDefault(s => s.Key == VM.Config.SubjectKey);
        GradeCombo.SelectedItem = VM.Config.Grade;
        VersionCombo.SelectedItem = "全部";
        _loaded = true;

        VM.PropertyChanged += VM_PropertyChanged;
    }

    private FirstPaperViewModel VM { get; }

    private void VM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FirstPaperViewModel.Busy))
        {
            return;
        }

        var enabled = !VM.Busy;
        UpdateBtn.IsEnabled = enabled;
        QueryBtn.IsEnabled = enabled;
        DownloadBtn.IsEnabled = enabled;
        SelectAllBtn.IsEnabled = enabled;
        ClearSelectBtn.IsEnabled = enabled;
        PrevBtn.IsEnabled = enabled;
        NextBtn.IsEnabled = enabled;
        SubjectCombo.IsEnabled = enabled;
        GradeCombo.IsEnabled = enabled;
    }

    private void SubjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || e.AddedItems.Count == 0 || e.AddedItems[0] is not FirstPaperSubject subject)
        {
            return;
        }

        if (subject.Key == VM.Config.SubjectKey)
        {
            return;
        }

        VM.SetSubject(subject);
        GradeCombo.SelectedItem = VM.Config.Grade;
    }

    private void GradeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || e.AddedItems.Count == 0 || e.AddedItems[0] is not string grade)
        {
            return;
        }

        if (grade != VM.Config.Grade)
        {
            VM.SetGrade(grade);
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        await VM.UpdateLibraryAsync(Window.GetWindow(this));
    }

    private void Query_Click(object sender, RoutedEventArgs e)
    {
        VM.Query(KeywordBox.Text.Trim(), VersionCombo.SelectedItem?.ToString() ?? "全部");
    }

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Query_Click(sender, e);
        }
    }

    private void ClearKeyword_Click(object sender, RoutedEventArgs e)
    {
        KeywordBox.Clear();
        VM.Query(string.Empty, VersionCombo.SelectedItem?.ToString() ?? "全部");
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        VM.RefreshSelectionCount();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        VM.SelectPage(true);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        VM.SelectPage(false);
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        VM.GoPage(VM.PageIndex - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        VM.GoPage(VM.PageIndex + 1);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择试卷保存目录",
            InitialDirectory = Directory.Exists(VM.DownloadDir) ? VM.DownloadDir : string.Empty
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            VM.DownloadDir = dlg.FolderName;
            VM.SaveSettings();
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        VM.OpenDownloadFolder();
    }

    private void ClearRule_Click(object sender, RoutedEventArgs e)
    {
        VM.NameTemplate = "[版本]-[标题]";
        VM.SaveSettings();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        VM.SaveSettings();
        await VM.DownloadSelectedAsync(Window.GetWindow(this));
    }
}
