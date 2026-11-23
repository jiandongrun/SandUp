using Microsoft.Win32;

namespace SandUp.Desktop;

internal sealed class StartupRegistration(string? executablePath, string registryPath = StartupRegistration.RunKey)
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupArgument = "--startup";
    private const string EntryName = "SandUp";
    private readonly string? command = StartupCommand(executablePath);

    internal bool CanEnable => command is not null;

    internal bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        return key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string;
    }

    internal void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (command is null || !File.Exists(executablePath))
                throw new InvalidOperationException("当前程序位置无法注册开机自启动。");
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            key.SetValue(EntryName, command, RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
        existing?.DeleteValue(EntryName, throwOnMissingValue: false);
    }

    private static string? StartupCommand(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var command = $"\"{path}\" {StartupArgument}";
        return command.Length <= 260 ? command : null;
    }
}
