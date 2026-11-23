using System.Windows.Media;

namespace SandUp.Desktop;

internal static class UiTypography
{
    public static FontFamily Family { get; } = new(
        new Uri("pack://application:,,,/", UriKind.Absolute),
        "/SandUp;component/Assets/Fonts/#Noto Sans SC, Segoe UI");
}
