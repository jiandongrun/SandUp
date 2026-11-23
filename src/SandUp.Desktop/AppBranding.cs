using System.Windows.Media.Imaging;
using System.Windows.Media;
namespace SandUp.Desktop;
public static class AppBranding
{
    public const string DisplayName = "SandUp · 沙立";

    public static BitmapSource WindowIcon { get; }=LoadWindowIcon();
    private static BitmapSource LoadWindowIcon()
    {
        var decoded=new BitmapImage();decoded.BeginInit();decoded.UriSource=new Uri("pack://application:,,,/SandUp;component/Assets/app-icon.png");
        decoded.DecodePixelWidth=256;decoded.CacheOption=BitmapCacheOption.OnLoad;decoded.EndInit();
        // Copy pixels out of the decoder: frozen BitmapFrame still exposes a dispatcher-owned decoder to Window.Icon.
        var converted=new FormatConvertedBitmap(decoded,PixelFormats.Bgra32,null,0);var stride=converted.PixelWidth*4;
        var pixels=new byte[stride*converted.PixelHeight];converted.CopyPixels(pixels,stride,0);
        var icon=BitmapSource.Create(converted.PixelWidth,converted.PixelHeight,96,96,PixelFormats.Bgra32,null,pixels,stride);
        icon.Freeze();return icon;
    }
    public static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/SandUp;component/Assets/app.ico")).Stream;
        using var original=new System.Drawing.Icon(stream,32,32);
        return (System.Drawing.Icon)original.Clone();
    }
}
