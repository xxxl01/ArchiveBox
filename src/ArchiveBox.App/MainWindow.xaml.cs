using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArchiveBox.App;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        if (!App.IsSmokeTest) return;
        // Exercise a nonempty directory and view changes using isolated fixture files.
        var fixture = Path.Combine(App.DataFolder, "fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "subfolder"));
        File.WriteAllText(Path.Combine(fixture, "sample.txt"), "ArchiveBox smoke fixture");
        await ViewModel.NavigateCommand.ExecuteAsync(fixture);
        await ViewModel.RefreshCommand.ExecuteAsync(null);
        foreach (var view in new[] { "all", "soon", "due", "history", "browse" })
            await ViewModel.SwitchViewCommand.ExecuteAsync(view);
        if (ViewModel.Rows.Count != 2) App.SmokeFailed = true;
        // Initialization is asynchronous. Close only after it finishes and WPF has
        // processed layout, instead of racing the normal busy-operation close guard.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!App.SmokeFailed)
            File.WriteAllText(Path.Combine(App.DataFolder, "smoke-success.txt"), "Window and view model initialized successfully.");
        Application.Current.Shutdown(App.SmokeFailed ? 1 : 0);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy) return;
        e.Cancel = true;
        MessageBox.Show(this, "文件操作正在进行，请先取消操作并等待结束，再关闭窗口。", "归档盒");
    }

    private async void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FolderBox.IsDropDownOpen = false;
        await ViewModel.NavigateCommand.ExecuteAsync(FolderBox.Text);
    }

    private async void FolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!FolderBox.IsDropDownOpen || ViewModel.IsBusy || FolderBox.SelectedItem is not string path) return;
        FolderBox.IsDropDownOpen = false;
        await ViewModel.NavigateCommand.ExecuteAsync(path);
    }

    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
        {
            if (source is CheckBox or DataGridColumnHeader) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (source is DataGridRow { Item: FileRow row }) await ViewModel.OpenRowAsync(row);
    }

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var row in e.RemovedItems.OfType<FileRow>()) row.IsSelected = false;
        foreach (var row in e.AddedItems.OfType<FileRow>()) row.IsSelected = true;
        vm.SelectionChanged();
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e) => ViewModel.SelectionChanged();

    private void TargetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel vm && e.NewValue is FolderNode node) vm.SelectTarget(node);
    }

    private async void FolderNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: FolderNode node }) return;
        try { await node.LoadAsync(); }
        catch (Exception error) { ViewModel.ReportError(error); }
        e.Handled = true;
    }
}
