using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArchiveBox.Core;

namespace ArchiveBox.App;

public sealed record RetentionChoice(bool Inherit, DateTimeOffset? ExpiresAt);
public interface IUserDialogs
{
    string? PickFolder(string title, string initial);
    string? NewFolder(string parent);
    ConflictPolicy? ConfirmTransfer(ArchiveMode mode, int count, string target, ConflictPolicy conflict);
    RetentionChoice? ChooseExpiry(string title, string description, ExpiryDecision current);
    bool Confirm(string message);
    void Error(string message);
}

public sealed class DesktopDialogs : IUserDialogs
{
    public string? PickFolder(string title, string initial) => WindowsServices.PickFolder(title, initial);
    public bool Confirm(string message) => WindowsServices.Confirm(message);
    public void Error(string message) => MessageBox.Show(Application.Current.MainWindow, message, "归档盒", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static Window Dialog(string title, StackPanel content)
    {
        var window = new Window { Title = title, Width = 490, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, Content = content, ShowInTaskbar = false };
        content.Margin = new Thickness(24);
        return window;
    }
    private static void Text(StackPanel panel, string text, double size = 13)
        => panel.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
    private static Button Buttons(StackPanel panel, Window window, string confirm)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 78 };
        var ok = new Button { Content = confirm, MinWidth = 90, IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        bar.Children.Add(cancel); bar.Children.Add(ok); panel.Children.Add(bar);
        return ok;
    }
    public string? NewFolder(string parent)
    {
        var panel = new StackPanel(); var window = Dialog("新建归档文件夹", panel);
        Text(panel, "创建位置：" + parent); Text(panel, "文件夹名称（可以用 / 创建多级目录）");
        var input = new TextBox { MinWidth = 400 }; panel.Children.Add(input);
        var ok = Buttons(panel, window, "创建");
        ok.IsEnabled = false;
        input.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => Keyboard.Focus(input);
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public ConflictPolicy? ConfirmTransfer(ArchiveMode mode, int count, string target, ConflictPolicy conflict)
    {
        var verb = mode == ArchiveMode.Copy ? "复制" : "移动";
        var panel = new StackPanel(); var window = Dialog(verb + "到归档", panel);
        Text(panel, $"{verb}所选 {count} 个项目", 19);
        Text(panel, "目标文件夹：\n" + target);
        Text(panel, mode == ArchiveMode.Copy ? "源文件保留。" : "成功校验后移除源文件，文件夹保留内部层级。");
        Text(panel, "遇到同名项目时");
        var conflicts = new ComboBox { ItemsSource = new[] { "自动重命名，不覆盖", "跳过同名项目" }, SelectedIndex = conflict == ConflictPolicy.Rename ? 0 : 1 };
        panel.Children.Add(conflicts);
        Buttons(panel, window, "确认" + verb).Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? conflicts.SelectedIndex == 0 ? ConflictPolicy.Rename : ConflictPolicy.Skip : null;
    }
    public RetentionChoice? ChooseExpiry(string title, string description, ExpiryDecision current)
    {
        var panel = new StackPanel(); var window = Dialog(title, panel);
        Text(panel, description); Text(panel, "当前：" + current.DisplayDate + "（" + current.SourceText + "）");
        var options = new ComboBox { ItemsSource = new[] { "继承上级规则（清除单独设置）", "不设到期（覆盖上级规则）", "7 天后", "30 天后", "90 天后", "指定日期" },
            SelectedIndex = current.IsExplicit ? current.ExpiresAt.HasValue ? 5 : 1 : 0 };
        var date = new DatePicker { SelectedDate = current.ExpiresAt?.LocalDateTime.Date ?? DateTime.Today.AddDays(30),
            Margin = new Thickness(0, 12, 0, 0) };
        date.Visibility = options.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        options.SelectionChanged += (_, _) => date.Visibility = options.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(options); panel.Children.Add(date);
        Text(panel, "\n文件夹规则对子文件夹和文件生效；更具体的规则优先。到期后仍需确认回收。");
        var ok = Buttons(panel, window, "保存");
        ok.Click += (_, _) => { if (options.SelectedIndex == 5 && !date.SelectedDate.HasValue) return; window.DialogResult = true; };
        if (window.ShowDialog() != true) return null;
        DateTimeOffset? expiry = options.SelectedIndex switch
        { 2 => DateTimeOffset.UtcNow.AddDays(7), 3 => DateTimeOffset.UtcNow.AddDays(30), 4 => DateTimeOffset.UtcNow.AddDays(90),
          5 => new DateTimeOffset(date.SelectedDate!.Value.Date.AddDays(1).AddTicks(-1)).ToUniversalTime(), _ => null };
        return new(options.SelectedIndex == 0, expiry);
    }
}
