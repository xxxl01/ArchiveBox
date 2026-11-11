using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArchiveBox.App;

public partial class MainWindow : Window
{
    internal MainViewModel ViewModel => (MainViewModel)DataContext;
    private GridLength? _archivePaneWidth;
    internal SmokeDialogs? TestDialogs { get; }
    public MainWindow()
    {
        InitializeComponent();
        if (App.IsSmokeTest) TestDialogs = new SmokeDialogs();
        DataContext = new MainViewModel(TestDialogs);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        TargetFoldersTree.SizeChanged += FolderTree_SizeChanged;
        ArchiveFoldersTree.SizeChanged += FolderTree_SizeChanged;
        UpdateColumns();
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentView)) UpdateColumns();
    }
    private void UpdateColumns()
    {
        if (!ViewModel.IsArchive)
        {
            if (ArchiveFoldersColumn.ReadLocalValue(ColumnDefinition.WidthProperty) is GridLength width && width.Value > 0) _archivePaneWidth = width;
            ArchiveFoldersColumn.ClearValue(ColumnDefinition.WidthProperty);
        }
        else if (_archivePaneWidth.HasValue) ArchiveFoldersColumn.Width = _archivePaneWidth.Value;
        var expiry = ViewModel.IsArchive || ViewModel.CurrentView is "soon" or "due";
        ExpiryColumn.Visibility = RuleColumn.Visibility = expiry ? Visibility.Visible : Visibility.Collapsed;
        ModifiedColumn.Visibility = ViewModel.IsBrowser || ViewModel.IsHistory ? Visibility.Visible : Visibility.Collapsed;
        LocationColumn.Visibility = ViewModel.CurrentView is "soon" or "due" || ViewModel.IsHistory ? Visibility.Visible : Visibility.Collapsed;
        ErrorColumn.Visibility = ViewModel.IsHistory ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        if (!App.IsSmokeTest) return;
        try { await UiRegression.RunAsync(this); }
        catch (Exception error) { App.ReportFatal(error); }
        Application.Current.Shutdown(App.SmokeFailed ? 1 : 0);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (App.IsFatalShutdown || !ViewModel.IsBusy) { ViewModel.CancelReadLoading(); return; }
        e.Cancel = true;
        MessageBox.Show(this, "文件操作正在进行，请先取消操作并等待结束，再关闭窗口。", "归档盒");
    }
    private async void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; FolderBox.IsDropDownOpen = false;
        await ViewModel.NavigateCommand.ExecuteAsync(FolderBox.Text);
    }
    private async void FolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!FolderBox.IsDropDownOpen || DataContext is not MainViewModel vm || vm.IsBusy || FolderBox.SelectedItem is not string path) return;
        FolderBox.IsDropDownOpen = false; await vm.NavigateCommand.ExecuteAsync(path);
    }
    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
        { if (source is CheckBox or DataGridColumnHeader) return; source = VisualTreeHelper.GetParent(source); }
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
    { if (DataContext is MainViewModel vm && !vm.IsBusy && e.NewValue is FolderNode node) vm.SelectTarget(node); }
    private async void ArchiveTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (DataContext is MainViewModel vm && vm.IsArchive && !vm.IsBusy && e.NewValue is FolderNode node) await vm.SelectArchiveFolderAsync(node); }
    private async void FolderNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: FolderNode node }) return;
        try { await node.LoadAsync(); } catch (Exception error) { if (DataContext is MainViewModel vm) vm.ReportError(error); }
        e.Handled = true;
    }
    private void FolderNode_Selected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        item.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => BringHeaderIntoView(item)));
        e.Handled = true;
    }
    private static void BringHeaderIntoView(TreeViewItem item)
    {
        if (item.Template?.FindName("PART_Header", item) is FrameworkElement header) header.BringIntoView();
        else item.BringIntoView(new Rect(0, 0, item.ActualWidth, Math.Min(26, item.ActualHeight)));
    }
    private void FolderTree_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TreeView tree || !tree.IsVisible) return;
        tree.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RevealSelected(tree)));
    }
    private static bool RevealSelected(ItemsControl parent)
    {
        foreach (var model in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(model) is not TreeViewItem item) continue;
            if (item.IsSelected) { BringHeaderIntoView(item); return true; }
            if (item.IsExpanded && RevealSelected(item)) return true;
        }
        return false;
    }
}
