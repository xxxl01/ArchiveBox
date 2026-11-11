using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using ArchiveBox.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArchiveBox.App;

public partial class MainViewModel : ObservableObject
{
    private readonly ArchiveRepository _repository;
    private readonly ArchiveService _service;
    private readonly List<IRelayCommand> _commands = [];
    private CancellationTokenSource? _cancellation;
    private string _loadedFolder = "";
    private FolderNode? _targetNode;
    private IReadOnlyList<ArchiveItem> _items = [];

    public ObservableCollection<FileRow> Rows { get; } = [];
    public ICollectionView FilesView { get; }
    public ObservableCollection<string> RecentFolders { get; } = [];
    public ObservableCollection<FolderNode> TargetTree { get; } = [];
    public string[] Modes { get; } = ["复制到归档", "移动到归档"];
    public string[] Conflicts { get; } = ["自动重命名", "跳过同名文件"];
    public string[] Retentions { get; } = ["不设置", "7 天", "30 天", "90 天", "自定义日期"];

    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private string _archiveRoot = "";
    [ObservableProperty] private string _targetFolder = "";
    [ObservableProperty] private string _newFolderName = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _currentView = "browse";
    [ObservableProperty] private string _pageTitle = "浏览文件";
    [ObservableProperty] private string _pageSubtitle = "选择文件，把它们整理到合适的位置。";
    [ObservableProperty] private string _status = "准备就绪";
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _archiveCount;
    [ObservableProperty] private int _soonCount;
    [ObservableProperty] private int _dueCount;
    [ObservableProperty] private int _selectedMode;
    [ObservableProperty] private int _selectedConflict;
    [ObservableProperty] private int _selectedRetention;
    [ObservableProperty] private DateTime? _customDate = DateTime.Today.AddDays(30);
    [ObservableProperty] private FileRow? _focusedRow;

    public bool IsIdle => !IsBusy;
    public bool IsBrowser => CurrentView == "browse";
    public bool IsHistory => CurrentView == "history";
    public bool ShowCustomDate => SelectedRetention == 4;
    public string SelectionText => $"{FileCount} 项 · 已选 {SelectedCount} 项";
    public string TargetDisplay => string.IsNullOrEmpty(TargetFolder) ? "请先选择归档根目录" : TargetFolder;
    public string ModeHint => SelectedMode == 0 ? "保留源文件，归档目录生成副本。" : "校验归档副本后，移除源文件。";
    public string DetailName => FocusedRow?.Name ?? "未选择文件";
    public string DetailPath => FocusedRow?.Path ?? "选择文件后可查看完整路径。";
    public string DetailSource => FocusedRow?.Item is null ? "" : $"原始位置：{FocusedRow.SourceText}";
    public string DetailExpiry => FocusedRow?.Item is null ? "" : $"预计删除：{FocusedRow.ExpiryText} · {FocusedRow.StatusText}";
    public string DetailError => FocusedRow?.Item?.Error ?? "";

    public IAsyncRelayCommand ChooseSourceCommand { get; }
    public IAsyncRelayCommand<string> NavigateCommand { get; }
    public IAsyncRelayCommand UpCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<string> SwitchViewCommand { get; }
    public IAsyncRelayCommand ChooseRootCommand { get; }
    public IAsyncRelayCommand CreateFolderCommand { get; }
    public IAsyncRelayCommand RefreshTreeCommand { get; }
    public IAsyncRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand UpdateExpiryCommand { get; }
    public IAsyncRelayCommand RecycleCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RevealCommand { get; }
    public IRelayCommand OpenDataCommand { get; }
    public IRelayCommand SelectAllCommand { get; }

    public MainViewModel()
    {
        _repository = new(Path.Combine(App.DataFolder, "archivebox.db"));
        _service = new(_repository);
        FilesView = CollectionViewSource.GetDefaultView(Rows);
        FilesView.Filter = value => value is FileRow row && (string.IsNullOrWhiteSpace(SearchText) ||
            row.Name.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            row.Path.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) >= 0);
        SelectedMode = _repository.GetSetting("mode") == "Move" ? 1 : 0;
        SelectedConflict = _repository.GetSetting("conflict") == "Skip" ? 1 : 0;

        ChooseSourceCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var path = WindowsServices.PickFolder("选择待整理文件夹", _loadedFolder);
            if (path is not null) await NavigateCoreAsync(path);
        }), () => IsIdle));
        NavigateCommand = Add(new AsyncRelayCommand<string>(path => RunAsync(() => NavigateCoreAsync(path ?? CurrentFolder)), _ => IsIdle));
        UpCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var parent = Directory.GetParent(_loadedFolder)?.FullName;
            if (parent is not null) await NavigateCoreAsync(parent);
        }), () => IsIdle && IsBrowser && Directory.Exists(_loadedFolder)));
        RefreshCommand = Add(new AsyncRelayCommand(() => RunAsync(RefreshCoreAsync), () => IsIdle));
        SwitchViewCommand = Add(new AsyncRelayCommand<string>(view => RunAsync(async () =>
        {
            SetView(view ?? "browse");
            await RefreshCoreAsync();
        }), _ => IsIdle));
        ChooseRootCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var root = WindowsServices.PickFolder("选择归档根目录（也可以在对话框中新建）", ArchiveRoot);
            if (root is null) return;
            PathSafety.RejectLinks(root);
            ArchiveRoot = PathSafety.Normalize(root);
            _repository.SetSetting("archiveRoot", ArchiveRoot);
            await BuildTreeAsync();
            Status = "已设置归档目录。原有归档记录仍然保留。";
        }), () => IsIdle));
        RefreshTreeCommand = Add(new AsyncRelayCommand(() => RunAsync(BuildTreeAsync), () => IsIdle && !string.IsNullOrEmpty(ArchiveRoot)));
        CreateFolderCommand = Add(new AsyncRelayCommand(() => RunAsync(CreateFolderAsync),
            () => IsIdle && !string.IsNullOrWhiteSpace(TargetFolder) && !string.IsNullOrWhiteSpace(NewFolderName)));
        ArchiveCommand = Add(new AsyncRelayCommand(() => RunAsync(ArchiveAsync), CanArchive));
        UpdateExpiryCommand = Add(new AsyncRelayCommand(() => RunAsync(UpdateExpiryAsync), CanManageSelected));
        RecycleCommand = Add(new AsyncRelayCommand(() => RunAsync(RecycleAsync), CanManageSelected));
        RetryCommand = Add(new AsyncRelayCommand(() => RunAsync(RetryAsync),
            () => IsIdle && IsHistory && SelectedRows().Any(r => r.Item?.State == ItemState.Failed)));
        CancelCommand = Add(new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy));
        RevealCommand = Add(new RelayCommand(() => Safe(() => WindowsServices.Reveal(FocusedRow!.Path)),
            () => IsIdle && FocusedRow is not null));
        OpenDataCommand = Add(new RelayCommand(() => Safe(() => WindowsServices.Open(App.DataFolder)), () => IsIdle));
        SelectAllCommand = Add(new RelayCommand(() =>
        {
            var visible = FilesView.Cast<FileRow>().ToArray();
            var select = visible.Any(r => !r.IsSelected);
            foreach (var row in visible) row.IsSelected = select;
            SelectionChanged();
        }, () => IsIdle && Rows.Count > 0));
    }

    private T Add<T>(T command) where T : IRelayCommand { _commands.Add(command); return command; }
    private void NotifyCommands() { foreach (var command in _commands) command.NotifyCanExecuteChanged(); }
    private FileRow[] SelectedRows() => Rows.Where(r => r.IsSelected).ToArray();
    private bool CanArchive() => IsIdle && IsBrowser && !string.IsNullOrEmpty(TargetFolder) && SelectedRows().Any(r => !r.IsDirectory);
    private bool CanManageSelected() => IsIdle && !IsBrowser && SelectedRows().Any(r => r.Item?.IsAvailable == true);

    public Task InitializeAsync() => RunAsync(async () =>
    {
        var recovered = await _service.RecoverAsync();
        RefreshRecents();
        ArchiveRoot = _repository.GetSetting("archiveRoot") ?? "";
        await BuildTreeAsync();
        var previous = _repository.GetSetting("lastFolder");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var initial = Directory.Exists(previous) ? previous! : Directory.Exists(downloads) ? downloads :
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(initial)) await NavigateCoreAsync(initial);
        else await RefreshCoreAsync();
        if (recovered.Count > 0) { Status = $"已检查 {recovered.Count} 个中断任务。"; ResultDetails = string.Join("\n", recovered); }
        else if (previous is not null && !Directory.Exists(previous)) Status = "上次打开的目录不可用，已切换到默认目录。";
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cancellation = new();
        Progress = 0;
        ProgressText = "处理中…";
        try { await action(); }
        catch (OperationCanceledException)
        {
            Status = "操作已取消。已完成的文件保留，未完成的文件可重新选择后归档。";
            await RefreshAfterFailureAsync();
        }
        catch (Exception error)
        {
            ReportError(error);
            await RefreshAfterFailureAsync();
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
            ProgressText = "";
        }
    }

    private async Task RefreshAfterFailureAsync()
    {
        try { await RefreshCoreAsync(preserveStatus: true); }
        catch (Exception error) { App.WriteLog(error); }
    }

    public void ReportError(Exception error)
    {
        var log = App.WriteLog(error);
        Status = error.Message;
        ResultDetails = error.Message;
        if (App.IsSmokeTest) App.SmokeFailed = true;
        else MessageBox.Show(Application.Current.MainWindow, $"{error.Message}\n\n日志：{log}", "归档盒", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Safe(Action action) { try { action(); } catch (Exception error) { ReportError(error); } }

    private void SetView(string view)
    {
        CurrentView = view;
        (PageTitle, PageSubtitle) = view switch
        {
            "all" => ("全部归档", "归档文件仍保存在磁盘上的真实目录中。"),
            "soon" => ("即将到期", "未来 7 天内到期的文件，可以延长保留时间。"),
            "due" => ("已到期", "到期仅作提醒，核对后可手动移入回收站。"),
            "history" => ("任务记录", "查看操作结果和失败原因，支持重试失败任务。"),
            _ => ("浏览文件", "浏览当前层级，双击进入子文件夹或打开文件。")
        };
        SearchText = "";
    }

    private async Task NavigateCoreAsync(string path)
    {
        path = PathSafety.Normalize(path.Trim());
        var entries = await Task.Run(() => PathSafety.List(path));
        _loadedFolder = path;
        CurrentFolder = path;
        SetView("browse");
        _repository.RememberFolder(path);
        RefreshRecents();
        ReloadItems();
        ReplaceRows(entries.Select(FileRow.FromEntry));
        Status = $"已打开：{path}";
    }

    private void RefreshRecents()
    {
        var items = _repository.GetRecentFolders();
        RecentFolders.Clear();
        foreach (var item in items) RecentFolders.Add(item.Path);
    }

    private void ReloadItems()
    {
        _items = _repository.GetItems();
        var now = DateTimeOffset.UtcNow;
        var ready = _items.Where(i => i.IsAvailable).ToArray();
        ArchiveCount = ready.Length;
        DueCount = ready.Count(i => i.ExpiresAt <= now);
        SoonCount = ready.Count(i => i.ExpiresAt > now && i.ExpiresAt <= now.AddDays(7));
    }

    private Task RefreshCoreAsync() => RefreshCoreAsync(false);
    private async Task RefreshCoreAsync(bool preserveStatus)
    {
        await _service.RecoverAsync();
        ReloadItems();
        if (IsBrowser)
        {
            if (string.IsNullOrEmpty(_loadedFolder)) { ReplaceRows([]); return; }
            var entries = await Task.Run(() => PathSafety.List(_loadedFolder));
            ReplaceRows(entries.Select(FileRow.FromEntry));
            CurrentFolder = _loadedFolder;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            var filtered = _items.Where(i => CurrentView == "history" || (i.IsAvailable && CurrentView switch
            {
                "due" => i.ExpiresAt <= now,
                "soon" => i.ExpiresAt > now && i.ExpiresAt <= now.AddDays(7),
                _ => true
            })).ToArray();
            ReplaceRows(await Task.Run(() => filtered.Select(FileRow.FromItem).ToArray()));
        }
        if (!preserveStatus) Status = "列表已刷新。";
    }

    private void ReplaceRows(IEnumerable<FileRow> rows)
    {
        // DeferRefresh is for view settings (filter/sort), not source mutations:
        // WPF needs a valid current position while processing these notifications.
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        FileCount = FilesView.Cast<FileRow>().Count();
        FocusedRow = null;
        SelectionChanged();
    }

    private async Task BuildTreeAsync()
    {
        TargetTree.Clear();
        TargetFolder = "";
        _targetNode = null;
        if (string.IsNullOrEmpty(ArchiveRoot)) return;
        var node = new FolderNode(ArchiveRoot);
        TargetTree.Add(node);
        if (!Directory.Exists(ArchiveRoot)) { Status = "归档目录不可用，请检查磁盘连接或重新选择。"; return; }
        await node.LoadAsync();
        node.IsExpanded = true;
        node.IsSelected = true;
        SelectTarget(node);
    }

    public void SelectTarget(FolderNode node)
    {
        if (string.IsNullOrEmpty(node.Path)) return;
        _targetNode = node;
        TargetFolder = node.Path;
    }

    private async Task CreateFolderAsync()
    {
        var target = PathSafety.CreateFolder(ArchiveRoot, TargetFolder, NewFolderName.Trim());
        NewFolderName = "";
        if (_targetNode is not null) { await _targetNode.LoadAsync(force: true); _targetNode.IsExpanded = true; }
        var node = TargetTree.First();
        foreach (var part in PathSafety.RelativePath(ArchiveRoot, target).Split(Path.DirectorySeparatorChar))
        {
            await node.LoadAsync();
            node.IsExpanded = true;
            node = node.Children.First(child => child.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
        }
        node.IsSelected = true;
        SelectTarget(node);
        Status = "已创建并选择新归档目录。";
    }

    private DateTimeOffset? GetExpiry() => SelectedRetention switch
    {
        0 => null,
        1 => DateTimeOffset.UtcNow.AddDays(7),
        2 => DateTimeOffset.UtcNow.AddDays(30),
        3 => DateTimeOffset.UtcNow.AddDays(90),
        4 when CustomDate.HasValue && CustomDate.Value.Date >= DateTime.Today =>
            new DateTimeOffset(CustomDate.Value.Date.AddDays(1).AddTicks(-1)).ToUniversalTime(),
        _ => throw new IOException("请选择今天或以后的预计删除日期。")
    };

    private async Task ArchiveAsync()
    {
        var sources = SelectedRows().Where(r => !r.IsDirectory).Select(r => r.Path).ToArray();
        var expiry = GetExpiry();
        var mode = SelectedMode == 0 ? ArchiveMode.Copy : ArchiveMode.Move;
        if (mode == ArchiveMode.Move && !WindowsServices.Confirm($"将 {sources.Length} 个文件移动到：\n{TargetFolder}\n\n成功后将移除源文件。是否继续？")) return;
        ResultDetails = "";
        var results = await _service.TransferAsync(sources, ArchiveRoot, TargetFolder, mode,
            SelectedConflict == 0 ? ConflictPolicy.Rename : ConflictPolicy.Skip,
            expiry, MakeProgress(), _cancellation!.Token);
        await RefreshCoreAsync(preserveStatus: true);
        ShowResults(results);
    }

    private IProgress<TransferProgress> MakeProgress() => new Progress<TransferProgress>(p =>
    {
        var fraction = p.TotalBytes == 0 ? 1 : (double)p.Bytes / p.TotalBytes;
        Progress = 100d * (p.Index - 1 + fraction) / p.Count;
        ProgressText = $"{p.Index}/{p.Count} · {p.Name} · {FileRow.FormatSize(p.Bytes)} / {FileRow.FormatSize(p.TotalBytes)}";
    });

    private void ShowResults(IReadOnlyList<TransferResult> results)
    {
        var warnings = results.Count(r => r.Success && r.Error is not null);
        Status = $"完成 {results.Count(r => r.Success)} 项 · 跳过 {results.Count(r => r.Skipped)} 项 · 失败 {results.Count(r => !r.Success && !r.Skipped)} 项" +
            (warnings > 0 ? $" · {warnings} 项源文件保留，请查看任务记录" : "");
        ResultDetails = string.Join("\n", results.Where(r => r.Error is not null).Select(r => $"{Path.GetFileName(r.Source)}：{r.Error}"));
    }

    private async Task UpdateExpiryAsync()
    {
        var ids = SelectedRows().Where(r => r.Item?.IsAvailable == true).Select(r => r.Item!.Id).ToArray();
        _repository.SetExpiry(ids, GetExpiry());
        await RefreshCoreAsync(preserveStatus: true);
        Status = $"已更新 {ids.Length} 个文件的预计删除时间。";
    }

    private async Task RecycleAsync()
    {
        var selected = SelectedRows().Where(r => r.Item?.IsAvailable == true).ToArray();
        if (!WindowsServices.Confirm($"将所选 {selected.Length} 个归档文件移入 Windows 回收站？\n\n复制操作的源文件不会被处理。\n如 Windows 提示无法回收，请取消操作并核对磁盘。")) return;
        var errors = new List<string>();
        var completed = 0;
        foreach (var row in selected)
        {
            _cancellation!.Token.ThrowIfCancellationRequested();
            try { await _service.RecycleAsync(row.Item!.Id, new WindowsRecycleBin(), _cancellation.Token); completed++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { errors.Add($"{row.Name}：{error.Message}"); }
            Progress = (double)(completed + errors.Count) / selected.Length * 100;
        }
        await RefreshCoreAsync(preserveStatus: true);
        Status = $"已移入回收站 {completed} 项 · 未完成 {errors.Count} 项。";
        ResultDetails = string.Join("\n", errors);
    }

    private async Task RetryAsync()
    {
        var failed = SelectedRows().Select(r => r.Item).Where(i => i?.State == ItemState.Failed).Cast<ArchiveItem>().ToArray();
        if (failed.Any(i => i.Mode == ArchiveMode.Move) && !WindowsServices.Confirm("所选任务包含移动操作，成功后会移除对应源文件。是否重试？")) return;
        var results = new List<TransferResult>();
        foreach (var item in failed)
        {
            _cancellation!.Token.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(await _service.TransferAsync([item.SourcePath], item.RootPath,
                    Path.GetDirectoryName(item.FullPath)!, item.Mode,
                    SelectedConflict == 0 ? ConflictPolicy.Rename : ConflictPolicy.Skip,
                    item.ExpiresAt, MakeProgress(), _cancellation.Token));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { results.Add(new(item.SourcePath, null, false, false, error.Message)); }
        }
        await RefreshCoreAsync(preserveStatus: true);
        ShowResults(results);
    }

    public async Task OpenRowAsync(FileRow? row)
    {
        if (row is null || IsBusy) return;
        if (row.IsDirectory) await NavigateCommand.ExecuteAsync(row.Path);
        else Safe(() => WindowsServices.Open(row.Path));
    }

    public void SelectionChanged()
    {
        SelectedCount = SelectedRows().Length;
        OnPropertyChanged(nameof(SelectionText));
        NotifyCommands();
    }

    partial void OnSearchTextChanged(string value)
    {
        foreach (var row in Rows) row.IsSelected = false;
        FilesView.Refresh();
        FileCount = FilesView.Cast<FileRow>().Count();
        SelectionChanged();
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsIdle)); NotifyCommands(); }
    partial void OnCurrentViewChanged(string value) { OnPropertyChanged(nameof(IsBrowser)); OnPropertyChanged(nameof(IsHistory)); NotifyCommands(); }
    partial void OnTargetFolderChanged(string value) { OnPropertyChanged(nameof(TargetDisplay)); NotifyCommands(); }
    partial void OnArchiveRootChanged(string value) => NotifyCommands();
    partial void OnNewFolderNameChanged(string value) => NotifyCommands();
    partial void OnFileCountChanged(int value) => OnPropertyChanged(nameof(SelectionText));
    partial void OnSelectedRetentionChanged(int value) => OnPropertyChanged(nameof(ShowCustomDate));
    partial void OnSelectedModeChanged(int value) { OnPropertyChanged(nameof(ModeHint)); _repository.SetSetting("mode", value == 1 ? "Move" : "Copy"); }
    partial void OnSelectedConflictChanged(int value) => _repository.SetSetting("conflict", value == 1 ? "Skip" : "Rename");
    partial void OnFocusedRowChanged(FileRow? value)
    {
        OnPropertyChanged(nameof(DetailName)); OnPropertyChanged(nameof(DetailPath));
        OnPropertyChanged(nameof(DetailSource)); OnPropertyChanged(nameof(DetailExpiry)); OnPropertyChanged(nameof(DetailError));
        NotifyCommands();
    }
}
