using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SandUp.Desktop;

public static class UiTheme
{
    public static Brush Canvas { get; } = Paint("#10151C");
    public static Brush Surface { get; } = Paint("#18212C");
    public static Brush Raised { get; } = Paint("#202C3A");
    public static Brush Field { get; } = Paint("#111922");
    public static Brush Line { get; } = Paint("#334353");
    public static Brush Text { get; } = Paint("#E6EFF6");
    public static Brush Muted { get; } = Paint("#AABCCF");
    public static Brush Blue { get; } = Paint("#8FC1F4");
    public static Brush Green { get; } = Paint("#7BD1AD");
    public static Brush Work { get; } = Paint("#223953");
    public static Brush Rest { get; } = Paint("#1F3B34");
    public static Brush Selected { get; } = Paint("#314B63");
    public static Brush Hover { get; } = Paint("#2B4156");
    public static Brush Warning { get; } = Paint("#F4BD84");
    public static Brush WarningSurface { get; } = Paint("#433326");
    public static Brush Danger { get; } = Paint("#F9A0A5");

    internal static Brush Paint(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal static void AddControls(FrameworkElement element) => element.Resources.MergedDictionaries.Add(new ResourceDictionary
    {
        Source = new Uri("/SandUp;component/DarkControls.xaml", UriKind.Relative)
    });

    internal static void Apply(Window window, bool dim = false)
    {
        window.Background = dim ? Canvas : Surface;
        window.Foreground = Text;
        AddControls(window);
        if (window.WindowStyle != WindowStyle.None)
            window.SourceInitialized += (_, _) => UseDarkCaption(new WindowInteropHelper(window).Handle);
    }

    private static void UseDarkCaption(IntPtr handle)
    {
        const int useDarkMode = 20;
        const int useDarkModeBeforeWindows20H1 = 19;
        int enabled = 1;
        if (DwmSetWindowAttribute(handle, useDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, useDarkModeBeforeWindows20H1, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal sealed class TrayMenuColors : System.Windows.Forms.ProfessionalColorTable
{
    public override System.Drawing.Color ToolStripDropDownBackground => ToColor(UiTheme.Surface);
    public override System.Drawing.Color MenuBorder => ToColor(UiTheme.Line);
    public override System.Drawing.Color MenuItemBorder => ToColor(UiTheme.Hover);
    public override System.Drawing.Color MenuItemSelected => ToColor(UiTheme.Hover);

    internal static System.Drawing.Color ToColor(Brush brush)
    {
        var color = ((SolidColorBrush)brush).Color;
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
