using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SandUp.Domain;

namespace SandUp.Desktop;

internal sealed class BodyCompanionFigure : FrameworkElement
{
    private const double FigureWidth = 96;
    private const double FigureHeight = 142;
    private static readonly Geometry FaceHitArea = Outline(
        "M45,4.7 C27.4,4.7 12.2,12.3 12.2,25.7 C12.2,39.2 28.2,47.6 45,58.5 C61.8,47.6 77.8,39.2 77.8,25.7 C77.8,12.3 62.6,4.7 45,4.7 Z");
    private static readonly Geometry LeftHandHitArea = Frozen(new EllipseGeometry(new Point(9, 86), 9, 11));
    private static readonly Drawing StandingBody = HourglassView.CreateRestingArtworkWithLimbs(
        CompanionArtwork.GlassPlacement, CompanionArtwork.Boots, reflectionOpacity: .72);
    private static readonly Pen LinkStroke = Stroke(UiTheme.Paint("#5D7890"), 1, dashed: true);
    private static readonly Pen FocusLinkStroke = Stroke(UiTheme.Green, 1, dashed: true);
    private static readonly IReadOnlyDictionary<BodyPart, Point> Landmarks = new Dictionary<BodyPart, Point>
    {
        [BodyPart.Eyes] = new(79, 27.1), [BodyPart.Shoulders] = new(59.8, 50),
        [BodyPart.Back] = new(60.1, 67), [BodyPart.Abdomen] = new(77, 100),
        [BodyPart.Legs] = new(67.2, 116), [BodyPart.Pelvic] = new(47, 113)
    };

    private readonly RowDefinitionCollection rows;
    private readonly ScaleTransform eyeBlink = new(1, 1, CompanionArtwork.CenterX, CompanionArtwork.EyeCenterY);
    private readonly RotateTransform leftWave = CompanionArtwork.CreateArm();
    private readonly RotateTransform rightWave = CompanionArtwork.CreateArm();
    private readonly DispatcherTimer blinkTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private BodyPart? highlightedPart;
    private HoverRegion hoveredRegion;

    public BodyCompanionFigure(RowDefinitionCollection rows)
    {
        this.rows = rows;
        AutomationProperties.SetName(this, "身体部位示意");
        blinkTimer.Tick += (_, _) => PlayBlink();
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += OnAnimationPreferenceChanged; UpdateAnimations(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= OnAnimationPreferenceChanged; StopAnimations(); };
        IsVisibleChanged += (_, _) => UpdateAnimations();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        RegionAt(hitTestParameters.HitPoint) == HoverRegion.None ? null : new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hoveredRegion = HoverRegion.None;
    }

    private HoverRegion RegionAt(Point position)
    {
        var (scale, left, top) = FigurePlacement();
        if (scale <= 0) return HoverRegion.None;
        var point = new Point((position.X - left) / scale, (position.Y - top) / scale);
        if (FaceHitArea.FillContains(point)) return HoverRegion.Face;
        // Use the resting hand areas so the pointer stays in the same region while the hand moves.
        if (!CompanionArtwork.Exterior.FillContains(point)) return HoverRegion.None;
        if (LeftHandHitArea.FillContains(point)) return HoverRegion.LeftHand;
        return LeftHandHitArea.FillContains(CompanionArtwork.Mirror.Transform(point)) ? HoverRegion.RightHand : HoverRegion.None;
    }

    private void UpdateHover(Point position)
    {
        var region = RegionAt(position);
        if (hoveredRegion == region) return;
        hoveredRegion = region;
        if (!CanAnimate) return;

        switch (region)
        {
            case HoverRegion.Face:
                PlayBlink();
                blinkTimer.Stop();
                blinkTimer.Start();
                break;
            case HoverRegion.LeftHand:
                leftWave.BeginAnimation(RotateTransform.AngleProperty, CompanionArtwork.Wave);
                break;
            case HoverRegion.RightHand:
                rightWave.BeginAnimation(RotateTransform.AngleProperty, CompanionArtwork.Wave);
                break;
        }
    }

    public void Highlight(BodyPart? part)
    {
        if (highlightedPart == part) return;
        highlightedPart = part;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (scale, left, top) = FigurePlacement();
        if (scale <= 0) return;
        var right = ActualWidth - 5;
        Point Place(Point point) => new(left + point.X * scale, top + point.Y * scale);
        var rowTop = rows[0].ActualHeight;
        for (int i = 0; i < BodyPartDisplay.Order.Count; i++)
        {
            var part = BodyPartDisplay.Order[i];
            var rowHeight = rows[i + 1].ActualHeight;
            var target = new Point(right, rowTop + rowHeight / 2);
            rowTop += rowHeight;
            var origin = Place(Landmarks[part]);
            var pen = highlightedPart == part ? FocusLinkStroke : LinkStroke;
            var line = new StreamGeometry();
            using (var drawing = line.Open())
            {
                drawing.BeginFigure(origin, false, false);
                if (part == BodyPart.Pelvic)
                {
                    // Curve under the boot so this line stays separate from the leg guide.
                    origin = Place(new Point(73, 128));
                    drawing.BezierTo(Place(new Point(49, 118)), Place(new Point(52, 128)), origin, true, false);
                }
                var bend = Math.Max(0, (target.X - origin.X) * 0.48);
                drawing.BezierTo(new Point(origin.X + bend, origin.Y), new Point(target.X - bend, target.Y), target, true, false);
            }
            dc.DrawGeometry(null, pen, line);
            dc.DrawEllipse(pen.Brush, null, target, 1.25, 1.25);
        }

        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, left, top));
        dc.DrawDrawing(StandingBody);
        CompanionArtwork.DrawHands(dc, leftWave, rightWave);
        CompanionArtwork.DrawEyes(dc, eyeBlink);
        dc.Pop();
    }

    private (double Scale, double Left, double Top) FigurePlacement()
    {
        if (ActualWidth < 40 || ActualHeight < 40) return default;
        var header = rows[0].ActualHeight;
        var scale = Math.Min(1, Math.Min((ActualWidth - 11) / FigureWidth, (ActualHeight - header - 4) / FigureHeight));
        var left = Math.Clamp((ActualWidth - FigureWidth) / 3, 1, 18);
        var top = header + (ActualHeight - header - FigureHeight * scale) / 2;
        return (scale, left, top);
    }

    private void OnAnimationPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.InvokeAsync(UpdateAnimations);
    }

    private bool CanAnimate => IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation;

    private void PlayBlink() => eyeBlink.BeginAnimation(ScaleTransform.ScaleYProperty, CompanionArtwork.Blink);

    private void UpdateAnimations()
    {
        if (CanAnimate) blinkTimer.Start();
        else StopAnimations();
    }

    private void StopAnimations()
    {
        hoveredRegion = HoverRegion.None;
        blinkTimer.Stop();
        eyeBlink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        leftWave.BeginAnimation(RotateTransform.AngleProperty, null);
        rightWave.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private enum HoverRegion { None, Face, LeftHand, RightHand }

    private static Geometry Outline(string shape) => Frozen(Geometry.Parse(shape));

    private static Pen Stroke(Brush brush, double width, bool dashed = false)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dashed) pen.DashStyle = new DashStyle([2, 3], 0);
        return Frozen(pen);
    }

    private static T Frozen<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }
}
