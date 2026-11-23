using System.Windows;
using System.Windows.Controls;

namespace SandUp.Desktop;

internal enum SettingsLeaveChoice { KeepEditing, Discard, Save }

internal static class SettingsLeaveDialog
{
    internal static SettingsLeaveChoice Ask(Window owner)
    {
        var title = new TextBlock
        {
            Text = "要保存设置修改吗？", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.Text
        };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var content = new StackPanel { Margin = new Thickness(20), Children = { title, buttons } };
        var dialog = new Window
        {
            Owner = owner, Title = "未保存的设置", Content = content, FontFamily = UiTypography.Family,
            Width = 374, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Icon = AppBranding.WindowIcon
        };
        UiTheme.Apply(dialog);
        var choice = SettingsLeaveChoice.KeepEditing;
        AddChoice("继续编辑", SettingsLeaveChoice.KeepEditing, isCancel: true);
        AddChoice("放弃修改", SettingsLeaveChoice.Discard);
        AddChoice("保存并离开", SettingsLeaveChoice.Save);
        dialog.ShowDialog();
        return choice;

        void AddChoice(string label, SettingsLeaveChoice result, bool isCancel = false)
        {
            var button = new Button
            {
                Content = label, IsCancel = isCancel, IsDefault = isCancel,
                Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(5, 0, 0, 0),
                Background = result == SettingsLeaveChoice.Save ? UiTheme.Rest : UiTheme.Raised
            };
            button.Click += (_, _) => { choice = result; dialog.DialogResult = true; };
            buttons.Children.Add(button);
            if (isCancel) dialog.Loaded += (_, _) => button.Focus();
        }
    }
}
