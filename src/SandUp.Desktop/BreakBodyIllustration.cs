using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SandUp.Desktop;

// A small, non-interactive illustration; each prompt plays once and settles.
internal sealed class BreakBodyIllustration : FrameworkElement
{
    private static readonly Drawing Glass = HourglassView.CreateRestingArtworkWithLimbs(
        CompanionArtwork.GlassPlacement, Geometry.Empty, reflectionOpacity: .72);
    private static readonly Pen LookDirection = CreateLookDirection();
    private string? prompt;
    public static readonly DependencyProperty MotionProperty = DependencyProperty.Register(nameof(Motion), typeof(double), typeof(BreakBodyIllustration), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Motion { get => (double)GetValue(MotionProperty); set => SetValue(MotionProperty, value); }
    public BreakBodyIllustration() { Width=150; Height=112; IsHitTestVisible=false; Focusable=false; }
    public void ShowPrompt(string id)
    {
        if (prompt==id) return;
        prompt=id; BeginAnimation(MotionProperty,null); Motion=0;
        if(SystemParameters.ClientAreaAnimation)
            BeginAnimation(MotionProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1800)) { FillBehavior = FillBehavior.Stop });
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var looking = prompt == "look_away";
        var stretching = prompt == "relax_shoulders_hands";
        var walking = prompt == "walk";
        var stretch = Math.Sin(Motion * Math.PI);
        var step = walking ? Math.Sin(Motion * Math.PI * 4) : 0;
        var scale = Math.Min(ActualWidth / 136, ActualHeight / 132);
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform(ActualWidth / 2 - CompanionArtwork.CenterX * scale, (ActualHeight - 124 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));

        dc.DrawDrawing(Glass);
        CompanionArtwork.DrawBoots(dc, 1,
            CompanionArtwork.CreateBootMotion(25 * step, 5 * Math.Max(0, step)),
            CompanionArtwork.CreateBootMotion(-25 * step, 5 * Math.Max(0, -step)));
        var raise = stretching ? 38 * stretch : 0;
        var left = CompanionArtwork.CreateArm();
        var right = CompanionArtwork.CreateArm();
        left.Angle = raise - 11 * step;
        right.Angle = raise + 11 * step;
        CompanionArtwork.DrawHands(dc, left, right);
        CompanionArtwork.DrawEyes(dc, looking ? new TranslateTransform(3 + 6 * stretch, -1) : Transform.Identity);
        if (looking)
        {
            var tip = 103 + 5 * stretch;
            var y = CompanionArtwork.EyeCenterY - 1;
            dc.DrawLine(LookDirection, new Point(85, y), new Point(tip, y));
            dc.DrawLine(LookDirection, new Point(tip - 5, y - 4), new Point(tip, y));
            dc.DrawLine(LookDirection, new Point(tip - 5, y + 4), new Point(tip, y));
        }
        dc.Pop();
        dc.Pop();
    }

    private static Pen CreateLookDirection()
    {
        var pen = new Pen(UiTheme.Green, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}
