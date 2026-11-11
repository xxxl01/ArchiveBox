using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using ArchiveBox.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArchiveBox.App;

public partial class MainViewModel : ObservableObject
{
    private readonly ArchiveRepository _repository;
    private readonly ArchiveService _service;
    private readonly IUserDialogs _dialogs;
    private readonly List<IRelayCommand> _commands = [];
    private CancellationTokenSource? _cancellation;
    private string _loadedFolder = "";
    private FolderNode? _targetNode;
    private FolderNode? _archiveNode;
    private IReadOnlyList<ArchiveItem> _items = [];
    private ExpiryRules _rules = new([]);

    public ObservableCollection<FileRow> Rows { get; } = [];
    public ICollectionView FilesView { get; }
    public ObservableCollection<string> RecentFolders { get; } = [];
    public ObservableCollection<FavoriteFolder> Favorites { get; } = [];
    public ObservableCollection<FolderNode> TargetTree { get; } = [];
    public ObservableCollection<FolderNode> ArchiveTree { get; } = [];
    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private string _archiveRoot = "";
    [ObservableProperty] private string _archiveFolder = "";
    [ObservableProperty] private string _targetFolder = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _currentView = "home";
    [ObservableProperty] private string _pageTitle = "首页";
    [ObservableProperty] private string _pageSubtitle = "常用文件夹，点开就能整理。";
    [ObservableProperty] private string _noticeText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private FileRow? _focusedRow;

    public bool IsIdle => !IsBusy;
    public bool IsHome => CurrentView == "home";
    public bool IsBrowser => CurrentView == "browse";
    public bool IsArchive => CurrentView == "all";
    public bool IsHistory => CurrentView == "history";
    public bool IsSettings => CurrentView == "settings";
    public bool IsFilePage => !IsHome && !IsSettings;
    public bool CanRecycleView => IsArchive || CurrentView is "soon" or "due";
    public bool HasRoot => !string.IsNullOrWhiteSpace(ArchiveRoot);
    public bool HasFavorites => Favorites.Count > 0;
    public bool HasNotice => NoticeText.Length > 0;
    public string RootName => HasRoot ? Path.GetFileName(ArchiveRoot).Length == 0 ? ArchiveRoot : Path.GetFileName(ArchiveRoot) : "尚未设置归档目录";
    public string TargetDisplay => TargetFolder.Length == 0 ? "选择归档目录后，点击树中的目标文件夹" : TargetFolder;
    public string FavoriteButtonText => IsCurrentFavorite ? "已在首页" : "添加到首页";
    public bool IsCurrentFavorite => Favorites.Any(f => Same(f.Path, _loadedFolder));
    public string SelectionText => $"{FileCount} 项 · 已选 {SelectedCount} 项";
    public string TransferHint => !HasRoot ? "先设置唯一的归档根目录" : SelectedCount == 0 ? "先在文件列表中选择文件或文件夹" : "目标：" + TargetDisplay;
    public string FolderExpiryText => _archiveNode?.PolicyDescription ?? "选择文件夹后设置到期时间";

    public IAsyncRelayCommand ChooseSourceCommand { get; }
    public IAsyncRelayCommand<string> NavigateCommand { get; }
    public IAsyncRelayCommand UpCommand { get; }
    public IAsyncRelayCommand ArchiveUpCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<string> SwitchViewCommand { get; }
    public IAsyncRelayCommand ChooseRootCommand { get; }
    public IAsyncRelayCommand CreateFolderCommand { get; }
    public IAsyncRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand MoveCommand { get; }
    public IAsyncRelayCommand SetFolderExpiryCommand { get; }
    public IAsyncRelayCommand SetFileExpiryCommand { get; }
    public IAsyncRelayCommand RecycleCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand AddHomeFolderCommand { get; }
    public IRelayCommand AddCurrentToHomeCommand { get; }
    public IRelayCommand<string> RemoveFavoriteCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand RevealCommand { get; }
    public IRelayCommand OpenDataCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand CloseNoticeCommand { get; }

    public MainViewModel(IUserDialogs? dialogs = null)
    {
        _dialogs = dialogs ?? new DesktopDialogs();
        _repository = new(Path.Combine(App.DataFolder, "archivebox.db")); _service = new(_repository);
        FilesView = CollectionViewSource.GetDefaultView(Rows);
        FilesView.Filter = value => value is FileRow row && (string.IsNullOrWhiteSpace(SearchText) ||
            row.Name.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) >= 0 || row.Path.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) >= 0);
        ChooseSourceCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        { var path = _dialogs.PickFolder("选择要浏览的文件夹", _loadedFolder); if (path is not null) await NavigateCoreAsync(path); }), () => IsIdle));
        NavigateCommand = Add(new AsyncRelayCommand<string>(p => RunAsync(() => NavigateCoreAsync(p ?? CurrentFolder)), _ => IsIdle));
        UpCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        { var parent = Directory.GetParent(_loadedFolder); if (parent is not null) await NavigateCoreAsync(parent.FullName); }), () => IsIdle && IsBrowser));
        ArchiveUpCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        { var parent = Directory.GetParent(ArchiveFolder); if (parent is not null) await SelectArchivePathCoreAsync(parent.FullName); }),
            () => IsIdle && IsArchive && HasRoot && !Same(ArchiveFolder, ArchiveRoot)));
        SwitchViewCommand = Add(new AsyncRelayCommand<string>(v => RunAsync(async () =>
        { SetView(v ?? "home"); if (IsArchive && HasRoot) await BuildTreesAsync(); await RefreshCoreAsync(); }), _ => IsIdle));
        RefreshCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        { if ((IsBrowser || IsArchive) && HasRoot) await BuildTreesAsync(); await RefreshCoreAsync(); }), () => IsIdle));
        ChooseRootCommand = Add(new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var root = _dialogs.PickFolder("选择唯一的归档根目录", ArchiveRoot);
            if (root is null || Same(root, ArchiveRoot)) return;
            if (HasRoot && !_dialogs.Confirm($"将归档根目录切换到：\n{root}\n\n现有文件不会自动搬移，旧操作记录仍保留。是否切换？")) return;
            await SetRootCoreAsync(root);
        }), () => IsIdle));
        CreateFolderCommand = Add(new AsyncRelayCommand(() => RunAsync(CreateFolderAsync), () => IsIdle && HasRoot && (IsBrowser || IsArchive)));
        CopyCommand = Add(new AsyncRelayCommand(() => RunAsync(() => TransferAsync(ArchiveMode.Copy)), CanTransfer));
        MoveCommand = Add(new AsyncRelayCommand(() => RunAsync(() => TransferAsync(ArchiveMode.Move)), CanTransfer));
        SetFolderExpiryCommand = Add(new AsyncRelayCommand(() => RunAsync(SetFolderExpiryAsync), () => IsIdle && IsArchive && _archiveNode is not null));
        SetFileExpiryCommand = Add(new AsyncRelayCommand(() => RunAsync(SetFileExpiryAsync), () => IsIdle && IsArchive && SelectedRows().Length > 0));
        RecycleCommand = Add(new AsyncRelayCommand(() => RunAsync(RecycleAsync), () => IsIdle && CanRecycleView && SelectedRows().Length > 0));
        RetryCommand = Add(new AsyncRelayCommand(() => RunAsync(RetryAsync), () => IsIdle && IsHistory && SelectedRows().Any(r => r.Item?.State == ItemState.Failed && Same(r.Item.RootPath, ArchiveRoot))));
        AddHomeFolderCommand = Add(new AsyncRelayCommand(() => RunAsync(() =>
        { var path = _dialogs.PickFolder("添加快速文件夹到首页", _loadedFolder); if (path is not null) AddFavorite(path); return Task.CompletedTask; }), () => IsIdle));
        AddCurrentToHomeCommand = Add(new RelayCommand(() => Safe(() => AddFavorite(_loadedFolder)), () => IsIdle && IsBrowser && _loadedFolder.Length > 0 && !IsCurrentFavorite));
        RemoveFavoriteCommand = Add(new RelayCommand<string>(p => Safe(() => { if (p is not null) { _repository.SetFavorite(p, false); ReloadFavorites(); } }), _ => IsIdle));
        CancelCommand = Add(new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy));
        RevealCommand = Add(new RelayCommand(() => Safe(() => WindowsServices.Reveal(FocusedRow!.Path)), () => IsIdle && FocusedRow is not null));
        OpenDataCommand = Add(new RelayCommand(() => Safe(() => WindowsServices.Open(App.DataFolder)), () => IsIdle));
        SelectAllCommand = Add(new RelayCommand(() =>
        { var visible = FilesView.Cast<FileRow>().ToArray(); var select = visible.Any(r => !r.IsSelected); foreach (var row in visible) row.IsSelected = select; SelectionChanged(); }, () => IsIdle && Rows.Count > 0));
        CloseNoticeCommand = Add(new RelayCommand(() => NoticeText = ""));
    }
    private T Add<T>(T command) where T : IRelayCommand { _commands.Add(command); return command; }
    private void NotifyCommands() { foreach (var command in _commands) command.NotifyCanExecuteChanged(); }
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private FileRow[] SelectedRows() => Rows.Where(r => r.IsSelected).ToArray();
    private bool CanTransfer() => IsIdle && IsBrowser && TargetFolder.Length > 0 && SelectedRows().Length > 0;
    private void Safe(Action action) { try { action(); } catch (Exception e) { ReportError(e); } }
    public void ReportError(Exception error)
    { var log = App.WriteLog(error); if (App.IsSmokeTest) App.SmokeFailed = true; else _dialogs.Error(error.Message + "\n\n日志：" + log); }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; _cancellation = new(); Progress = 0; ProgressText = "正在处理…";
        try { await action(); }
        catch (OperationCanceledException) { NoticeText = "已取消；已完成的项目保留。"; await RefreshAfterFailureAsync(); }
        catch (Exception error) { ReportError(error); await RefreshAfterFailureAsync(); }
        finally { _cancellation.Dispose(); _cancellation = null; IsBusy = false; ProgressText = ""; }
    }
    private async Task RefreshAfterFailureAsync() { try { await RefreshCoreAsync(); } catch (Exception e) { App.WriteLog(e); } }

    public Task InitializeAsync() => RunAsync(async () =>
    {
        await _service.RecoverAsync();
        _loadedFolder = _repository.GetSetting("lastFolder") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        CurrentFolder = _loadedFolder;
        ArchiveRoot = _repository.GetSetting("archiveRoot") ?? "";
        ArchiveFolder = ArchiveRoot;
        TargetFolder = _repository.GetSetting("lastTargetFolder") ?? ArchiveRoot;
        ReloadMetadata(); ReloadFavorites(); ReloadRecents();
        if (HasRoot) await BuildTreesAsync();
        SetView("home"); await RefreshCoreAsync();
    });
    private void SetView(string view)
    {
        CurrentView = view; SearchText = ""; NoticeText = "";
        (PageTitle, PageSubtitle) = view switch
        {
            "home" => ("首页", "常用文件夹，点开就能整理。"),
            "settings" => ("设置", "在这里设置唯一的归档根目录。"),
            "all" => ("归档文件", "左侧选择文件夹；文件单独设置的到期时间优先。"),
            "soon" => ("即将到期", "未来 7 天内到期的文件，按文件夹规则及文件覆盖计算。"),
            "due" => ("已到期", "核对文件后再移入回收站。"),
            "history" => ("任务记录", "操作结果和失败原因。"),
            _ => ("浏览文件", "选择文件或文件夹，再选择右侧目标，点击复制或移动。")
        };
    }
    private void ReloadMetadata() { _items = _repository.GetItems(); _rules = _repository.GetRules(); }
    private void ReloadFavorites()
    { Favorites.Clear(); foreach (var folder in _repository.GetFavorites()) Favorites.Add(folder); OnPropertyChanged(nameof(HasFavorites)); FavoriteChanged(); }
    private void FavoriteChanged() { OnPropertyChanged(nameof(IsCurrentFavorite)); OnPropertyChanged(nameof(FavoriteButtonText)); NotifyCommands(); }
    private void ReloadRecents() { RecentFolders.Clear(); foreach (var folder in _repository.GetRecentFolders()) RecentFolders.Add(folder.Path); }
    private void AddFavorite(string path)
    { PathSafety.RejectLinks(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不可用。"); _repository.SetFavorite(path, true); ReloadFavorites(); NoticeText = "已添加到首页。"; }
    private async Task NavigateCoreAsync(string path)
    {
        path = PathSafety.Normalize(path.Trim()); var entries = await Task.Run(() => PathSafety.List(path));
        _loadedFolder = path; CurrentFolder = path; SetView("browse");
        _repository.RememberFolder(path); ReloadRecents(); FavoriteChanged();
        ReplaceRows(entries.Select(FileRow.FromEntry));
    }
    private async Task RefreshCoreAsync()
    {
        ReloadMetadata();
        if (IsHome || IsSettings) { ReloadFavorites(); ReloadRecents(); ReplaceRows([]); return; }
        if (IsBrowser)
        {
            if (string.IsNullOrEmpty(_loadedFolder)) { ReplaceRows([]); return; }
            ReplaceRows((await Task.Run(() => PathSafety.List(_loadedFolder))).Select(FileRow.FromEntry)); return;
        }
        if (IsHistory) { ReplaceRows(await Task.Run(() => _items.Select(FileRow.FromItem).ToArray())); return; }
        if (!HasRoot || !Directory.Exists(ArchiveRoot)) { ReplaceRows([]); return; }
        if (IsArchive)
        {
            if (!Directory.Exists(ArchiveFolder)) ArchiveFolder = ArchiveRoot;
            var entries = await Task.Run(() => PathSafety.List(ArchiveFolder));
            ReplaceRows(entries.Where(e => !e.IsDirectory).Select(ArchiveRow));
            foreach (var node in ArchiveTree) node.RefreshPolicy(); return;
        }
        var now = DateTimeOffset.UtcNow;
        var rows = await Task.Run(() =>
        {
            var result = new List<FileRow>(); var folders = new Queue<string>(); folders.Enqueue(ArchiveRoot);
            while (folders.Count > 0)
            {
                _cancellation!.Token.ThrowIfCancellationRequested();
                var folder = folders.Dequeue();
                try
                {
                    foreach (var entry in PathSafety.List(folder))
                    {
                        if (entry.IsDirectory) { folders.Enqueue(entry.Path); continue; }
                        var row = ArchiveRow(entry); var expiry = row.Expiry?.ExpiresAt;
                        if (expiry.HasValue && (CurrentView == "due" ? expiry <= now : expiry > now && expiry <= now.AddDays(7))) result.Add(row);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.WriteLog(e); }
            }
            return result;
        });
        ReplaceRows(rows.OrderBy(r => r.Expiry?.ExpiresAt));
    }
    private FileRow ArchiveRow(DirectoryEntry entry)
    {
        var item = _items.FirstOrDefault(i => i.IsAvailable && Same(i.FullPath, entry.Path));
        return FileRow.FromArchive(entry, item, _rules.Resolve(ArchiveRoot, PathSafety.RelativePath(ArchiveRoot, entry.Path)));
    }
    private void ReplaceRows(IEnumerable<FileRow> rows)
    { Rows.Clear(); foreach (var row in rows) Rows.Add(row); FileCount = FilesView.Cast<FileRow>().Count(); FocusedRow = null; SelectionChanged(); }

    private async Task SetRootCoreAsync(string root)
    {
        PathSafety.RejectLinks(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException("归档目录不可用。");
        ArchiveRoot = PathSafety.Normalize(root); ArchiveFolder = ArchiveRoot; TargetFolder = ArchiveRoot;
        _repository.SetSetting("archiveRoot", ArchiveRoot); _repository.SetSetting("lastTargetFolder", TargetFolder);
        await BuildTreesAsync(); await RefreshCoreAsync();
    }
    private ExpiryDecision FolderPolicy(string path) => _rules.Resolve(ArchiveRoot, PathSafety.RelativePath(ArchiveRoot, path), true);
    private async Task BuildTreesAsync()
    {
        TargetTree.Clear(); ArchiveTree.Clear(); _targetNode = null; _archiveNode = null;
        if (!HasRoot || !Directory.Exists(ArchiveRoot)) { TargetFolder = ""; return; }
        var target = new FolderNode(ArchiveRoot); var archive = new FolderNode(ArchiveRoot, policy: FolderPolicy);
        TargetTree.Add(target); ArchiveTree.Add(archive);
        await target.LoadAsync(); await archive.LoadAsync(); target.IsExpanded = true; archive.IsExpanded = true;
        try { _targetNode = await FindNodeAsync(target, TargetFolder); } catch (IOException) { _targetNode = target; }
        try { _archiveNode = await FindNodeAsync(archive, ArchiveFolder); } catch (IOException) { _archiveNode = archive; }
        _targetNode.IsSelected = true; TargetFolder = _targetNode.Path;
        _archiveNode.IsSelected = true; ArchiveFolder = _archiveNode.Path;
        OnPropertyChanged(nameof(FolderExpiryText));
    }
    private static async Task<FolderNode> FindNodeAsync(FolderNode node, string path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        var relative = PathSafety.RelativePath(node.Path, PathSafety.Resolve(node.Path, PathSafety.RelativePath(node.Path, path)));
        if (relative == ".") return node;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        { await node.LoadAsync(); node.IsExpanded = true; var child = node.Children.FirstOrDefault(n => Same(n.Name, part)); if (child is null) return node; node = child; }
        return node;
    }
    public void SelectTarget(FolderNode node)
    { if (node.Path.Length == 0 || IsBusy) return; _targetNode = node; TargetFolder = node.Path; }
    public Task SelectArchiveFolderAsync(FolderNode node) => RunAsync(async () =>
    { if (node.Path.Length == 0) return; _archiveNode = node; ArchiveFolder = node.Path; OnPropertyChanged(nameof(FolderExpiryText)); await RefreshCoreAsync(); });
    private async Task SelectArchivePathCoreAsync(string path)
    { _archiveNode = await FindNodeAsync(ArchiveTree.First(), path); _archiveNode.IsSelected = true; ArchiveFolder = _archiveNode.Path; OnPropertyChanged(nameof(FolderExpiryText)); await RefreshCoreAsync(); }
    private async Task CreateFolderAsync()
    {
        var parent = IsArchive ? ArchiveFolder : TargetFolder;
        var name = _dialogs.NewFolder(parent); if (name is null) return;
        var path = PathSafety.CreateFolder(ArchiveRoot, parent, name);
        if (IsArchive) ArchiveFolder = path; else TargetFolder = path;
        await BuildTreesAsync(); if (IsArchive) await RefreshCoreAsync();
    }
    private async Task TransferAsync(ArchiveMode mode)
    {
        var selected = SelectedRows().Select(r => r.Path).ToArray();
        var initial = _repository.GetSetting("conflict") == "Skip" ? ConflictPolicy.Skip : ConflictPolicy.Rename;
        var decision = _dialogs.ConfirmTransfer(mode, selected.Length, TargetFolder, initial);
        if (!decision.HasValue) return;
        _repository.SetSetting("conflict", decision.Value.ToString());
        var results = await _service.TransferSelectionAsync(selected, ArchiveRoot, TargetFolder, mode, decision.Value, MakeProgress(), _cancellation!.Token);
        await RefreshCoreAsync(); await BuildTreesAsync(); ShowResults(results);
    }
    private IProgress<TransferProgress> MakeProgress() => new Progress<TransferProgress>(p =>
    { Progress = 100d * (p.Index - 1 + (p.TotalBytes == 0 ? 1 : (double)p.Bytes / p.TotalBytes)) / p.Count; ProgressText = $"{p.Name} · {FileRow.FormatSize(p.Bytes)} / {FileRow.FormatSize(p.TotalBytes)}"; });
    private void ShowResults(IReadOnlyList<TransferResult> results)
    {
        NoticeText = $"完成 {results.Count(r => r.Success)} 项 · 跳过 {results.Count(r => r.Skipped)} 项 · 失败 {results.Count(r => !r.Success && !r.Skipped)} 项";
        var errors = results.Where(r => !r.Skipped && r.Error is not null).ToArray();
        if (errors.Length > 0) _dialogs.Error(string.Join("\n", errors.Take(10).Select(r => Path.GetFileName(r.Source) + "：" + r.Error)) + "\n\n完整记录可在任务记录中查看。");
    }
    private async Task SetFolderExpiryAsync()
    {
        var choice = _dialogs.ChooseExpiry("文件夹到期时间", "设置文件夹：" + ArchiveFolder + "\n对子文件夹和文件生效，文件单独设置可以覆盖。", FolderPolicy(ArchiveFolder));
        if (choice is null) return;
        var relative = PathSafety.RelativePath(ArchiveRoot, ArchiveFolder);
        if (choice.Inherit) _repository.RemoveRule(ArchiveRoot, relative, true); else _repository.SetRule(ArchiveRoot, relative, true, choice.ExpiresAt);
        await RefreshCoreAsync(); OnPropertyChanged(nameof(FolderExpiryText)); NoticeText = "已保存文件夹到期规则。";
    }
    private async Task SetFileExpiryAsync()
    {
        var selected = SelectedRows(); var choice = _dialogs.ChooseExpiry("文件到期时间", $"设置所选 {selected.Length} 个文件；单独设置将覆盖文件夹规则。", selected.First().Expiry ?? new(null, false, null));
        if (choice is null) return;
        foreach (var row in selected)
        { var relative = PathSafety.RelativePath(ArchiveRoot, row.Path); if (choice.Inherit) _repository.RemoveRule(ArchiveRoot, relative, false); else _repository.SetRule(ArchiveRoot, relative, false, choice.ExpiresAt); }
        await RefreshCoreAsync(); NoticeText = choice.Inherit ? "已清除文件覆盖，恢复继承文件夹规则。" : "已保存文件单独设置。";
    }
    private async Task RecycleAsync()
    {
        var selected = SelectedRows(); if (!_dialogs.Confirm($"将所选 {selected.Length} 个归档文件移入 Windows 回收站？")) return;
        var errors = new List<string>(); var count = 0;
        foreach (var row in selected)
        {
            _cancellation!.Token.ThrowIfCancellationRequested();
            try { var item = await _service.RegisterExistingAsync(ArchiveRoot, row.Path, _cancellation.Token); await _service.RecycleAsync(item.Id, new WindowsRecycleBin(), _cancellation.Token); count++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(row.Name + "：" + e.Message); }
        }
        await RefreshCoreAsync(); NoticeText = $"已移入回收站 {count} 项。"; if (errors.Count > 0) _dialogs.Error(string.Join("\n", errors));
    }
    private async Task RetryAsync()
    {
        var failed = SelectedRows().Select(r => r.Item).Where(i => i?.State == ItemState.Failed && Same(i.RootPath, ArchiveRoot)).Cast<ArchiveItem>().ToArray();
        if (!_dialogs.Confirm($"重试所选 {failed.Length} 个失败任务？移动操作成功后会移除源文件。")) return;
        var results = new List<TransferResult>();
        foreach (var item in failed)
            results.AddRange(await _service.TransferAsync([item.SourcePath], item.RootPath, Path.GetDirectoryName(item.FullPath)!, item.Mode, ConflictPolicy.Rename, null, MakeProgress(), _cancellation!.Token));
        await RefreshCoreAsync(); ShowResults(results);
    }
    public async Task OpenRowAsync(FileRow? row)
    { if (row is null || IsBusy) return; if (row.IsDirectory) await NavigateCommand.ExecuteAsync(row.Path); else Safe(() => WindowsServices.Open(row.Path)); }
    public void SelectionChanged()
    { SelectedCount = SelectedRows().Length; OnPropertyChanged(nameof(SelectionText)); OnPropertyChanged(nameof(TransferHint)); NotifyCommands(); }
    partial void OnSearchTextChanged(string value)
    { foreach (var row in Rows) row.IsSelected = false; FilesView.Refresh(); FileCount = FilesView.Cast<FileRow>().Count(); SelectionChanged(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsIdle)); NotifyCommands(); }
    partial void OnCurrentViewChanged(string value)
    { foreach (var name in new[] { nameof(IsHome), nameof(IsBrowser), nameof(IsArchive), nameof(IsHistory), nameof(IsSettings), nameof(IsFilePage), nameof(CanRecycleView) }) OnPropertyChanged(name); NotifyCommands(); }
    partial void OnArchiveRootChanged(string value) { OnPropertyChanged(nameof(HasRoot)); OnPropertyChanged(nameof(RootName)); NotifyCommands(); }
    partial void OnArchiveFolderChanged(string value) => NotifyCommands();
    partial void OnTargetFolderChanged(string value) { if (value.Length > 0) _repository.SetSetting("lastTargetFolder", value); OnPropertyChanged(nameof(TargetDisplay)); OnPropertyChanged(nameof(TransferHint)); NotifyCommands(); }
    partial void OnFileCountChanged(int value) => OnPropertyChanged(nameof(SelectionText));
    partial void OnFocusedRowChanged(FileRow? value) => NotifyCommands();
    partial void OnNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasNotice));
}
