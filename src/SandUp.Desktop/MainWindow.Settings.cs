using System.ComponentModel;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SandUp.Application;
using SandUp.Domain;

namespace SandUp.Desktop;

public partial class MainWindow
{
    private readonly TextBlock settingsStatusLabel = new() { Name = "SettingsSaveStatus", FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly Border settingsStatus = new()
    {
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(7)
    };
    private SettingsSnapshot lastSavedSettings = null!;
    private string[] savedEditValues = [];
    private bool settingsDirty, savingSettings, checkingSettingsLeave, restoringSettings;

    internal void AddStartupSetting(StartupRegistration registration)
    {
        var option = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var toggle = new CheckBox { Content = "开机自启动", Margin = new Thickness(0, 0, 0, 6) };
        var hint = new TextBlock
        {
            Text = "登录 Windows 后在托盘启动。更改自动保存，下次登录生效。",
            Foreground = UiTheme.Muted, TextWrapping = TextWrapping.Wrap
        };
        var feedback = new TextBlock { Foreground = UiTheme.Warning, TextWrapping = TextWrapping.Wrap };
        option.Children.Add(toggle);
        option.Children.Add(hint);
        option.Children.Add(feedback);

        option.IsVisibleChanged += (_, _) =>
        {
            if (!option.IsVisible) return;
            try
            {
                toggle.IsChecked = registration.IsRegistered();
                toggle.IsEnabled = registration.CanEnable || toggle.IsChecked == true;
                SetOptionalText(feedback, toggle.IsEnabled ? null : "当前启动路径无法注册。请从固定位置打开 SandUp.exe。");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
            {
                toggle.IsEnabled = false;
                SetOptionalText(feedback, "无法读取 Windows 开机自启动设置。");
            }
        };
        toggle.Click += (_, _) =>
        {
            var enabled = toggle.IsChecked == true;
            try
            {
                registration.SetEnabled(enabled);
                toggle.IsEnabled = registration.CanEnable || enabled;
                SetOptionalText(feedback, null);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
            {
                toggle.IsChecked = !enabled;
                SetOptionalText(feedback, "开机自启动设置未保存，请重试。");
            }
        };
        settingsPage.Children.Add(option);
    }

    private IEnumerable<string> SettingsEditValues() => cycleEditor.EditValues().Concat(rateEditor.EditValues());

    private void RestoreSettings(SettingsSnapshot settings)
    {
        restoringSettings = true;
        cycleEditor.Load(settings);
        rateEditor.SetRates(settings.ReminderRates);
        lastSavedSettings = settings;
        savedEditValues = SettingsEditValues().ToArray();
        restoringSettings = false;
        settingsFeedback.Text = "";
        UpdateSettingsStatus(false);
    }

    private void OnSettingsEdited()
    {
        if (restoringSettings) return;
        settingsFeedback.Text = "";
        UpdateSettingsStatus(!SettingsEditValues().SequenceEqual(savedEditValues));
    }

    private void UpdateSettingsStatus(bool dirty)
    {
        settingsDirty = dirty;
        settingsStatusLabel.Text = savingSettings ? "保存中" : dirty ? "未保存" : "已保存";
        settingsStatusLabel.Foreground = dirty ? UiTheme.Warning : UiTheme.Green;
        settingsStatus.Background = dirty ? UiTheme.WarningSurface : UiTheme.Rest;
        AutomationProperties.SetName(settingsStatusLabel, "当前设置" + settingsStatusLabel.Text);
    }

    private async Task<bool> SaveSettingsAsync()
    {
        if (savingSettings) return false;
        if (!cycleEditor.TryRead(out var plan, out var arrangement, out var cycleError)) return SettingsSaveFailed(cycleError);
        if (!rateEditor.TryRead(out var rates, out var rateError)) return SettingsSaveFailed(rateError);

        var submittedValues = SettingsEditValues().ToArray();
        savingSettings = true;
        settingsPage.IsEnabled = false;
        UpdateSettingsStatus(settingsDirty);
        try
        {
            if (app.ReadSnapshot().Persistence.Status == PersistenceStatus.PendingRetry)
            {
                var recovery = await app.RetryPendingSaveAsync();
                Render(recovery.Snapshot);
                if (recovery.Persistence.Status != PersistenceStatus.Saved)
                    return SettingsSaveFailed("本地记录仍无法写入，设置尚未保存。");
            }
            var command = new UpdateSettings((int)(lastSavedSettings.WorkDurationMs / 60_000),
                (int)(lastSavedSettings.RestDurationMs / 60_000), rates) { CyclePlan = plan, Arrangement = arrangement };
            var result = await app.HandleAsync(new(Guid.NewGuid(), command, null, null));
            Render(result.Snapshot);
            if (result.Disposition == CommandDisposition.Rejected || result.Persistence.Status != PersistenceStatus.Saved)
                return SettingsSaveFailed("设置未保存，请重试。");

            lastSavedSettings = result.Snapshot.DefaultSettings;
            savedEditValues = submittedValues;
            cycleEditor.ClearCaptureFeedback();
            settingsFeedback.Text = "设置已保存，新计划从下一轮生效。";
            return true;
        }
        catch (Exception)
        {
            return SettingsSaveFailed("设置未保存，请重试。");
        }
        finally
        {
            savingSettings = false;
            settingsPage.IsEnabled = true;
            UpdateSettingsStatus(!SettingsEditValues().SequenceEqual(savedEditValues));
        }
    }

    private bool SettingsSaveFailed(string message)
    {
        settingsFeedback.Text = message;
        settingsFeedback.BringIntoView();
        return false;
    }

    internal async Task<bool> ConfirmSettingsLeaveAsync()
    {
        if (checkingSettingsLeave || savingSettings) return false;
        if (!settingsDirty) return true;
        checkingSettingsLeave = true;
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            switch (SettingsLeaveDialog.Ask(this))
            {
                case SettingsLeaveChoice.Discard:
                    RestoreSettings(lastSavedSettings);
                    return true;
                case SettingsLeaveChoice.Save when await SaveSettingsAsync():
                    return true;
                default:
                    if (settingsPage.Visibility != Visibility.Visible) ShowPage(settingsPage, "设置");
                    if (settingsFeedback.Text.Length > 0) settingsFeedback.BringIntoView();
                    return false;
            }
        }
        finally { checkingSettingsLeave = false; }
    }

    private async void CloseToTray(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        if (!await ConfirmSettingsLeaveAsync()) return;
        Hide();
    }
}
