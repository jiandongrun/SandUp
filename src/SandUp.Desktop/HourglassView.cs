using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SandUp.Desktop;

public sealed class HourglassView : FrameworkElement
{
    private const double RibbonWidth = 5.2;
    private const double WhiteBaseY = 136 + RibbonWidth / 2;

    private static readonly Point Neck = new(48, 72);
    private static readonly Geometry Chamber = Outline("M15,33 C15,8 81,8 81,33 C81,49 62,60 48,69 C34,60 15,49 15,33 Z");
    // Both halves mirror each other about y = 72. Stroke the entire outline together to keep the neck continuous.
    private static readonly Geometry Ribbon = Outline("M48,72 C28,59 9,49 9,33 C9,17 27,8 48,8 C69,8 87,17 87,33 C87,49 68,59 48,72 C28,85 9,95 9,111 C9,127 27,136 48,136 C69,136 87,127 87,111 C87,95 68,85 48,72 Z");
    private static readonly Point ChamberCenter = new(48, 34);
    private static readonly Point[] ChamberOutline = FlattenChamber();
    private static readonly Brush Glass = UiTheme.Paint("#80151F2B");
    private static readonly Brush Sand = Gradient(255, 221, 104, 240, 154, 17);
    private static readonly Brush LightGrain = Frozen(new SolidColorBrush(Color.FromRgb(255, 232, 145)));
    private static readonly Brush DarkGrain = Frozen(new SolidColorBrush(Color.FromRgb(203, 117, 19)));
    private static readonly Pen RibbonEdge = RoundedPen(UiTheme.Paint("#B010151C"), 7.2);
    private static readonly Pen NormalRibbon = RoundedPen(Brushes.White, RibbonWidth);
    private static readonly Pen SandEdge = Frozen(new Pen(LightGrain, 1.1));

    private static readonly Drawing RestingArtwork = CreateRestingArtwork();

    public static readonly DependencyProperty TurnAngleProperty = DependencyProperty.Register(
        nameof(TurnAngle), typeof(double), typeof(HourglassView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly DependencyProperty CompanionProgressProperty = DependencyProperty.Register(
        nameof(CompanionProgress), typeof(double), typeof(HourglassView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty GrainPhaseProperty = DependencyProperty.Register(
        "GrainPhase", typeof(double), typeof(HourglassView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool animatingGrains;
    private double reflectionAngle;
    private DrawingGroup? reflections;
    private readonly RotateTransform companionArm = CompanionArtwork.CreateArm();
    private readonly RotateTransform companionRightArm = CompanionArtwork.CreateArm();
    private readonly ScaleTransform companionEyes = new(1, 1, CompanionArtwork.CenterX, CompanionArtwork.EyeCenterY);
    private AnimationClock? companionWaveClock;

    public double TurnAngle
    {
        get => (double)GetValue(TurnAngleProperty);
        set => SetValue(TurnAngleProperty, value);
    }

    public double RemainingFraction { get; private set; }
    public bool Running { get; private set; }
    public bool Turning { get; set; }
    internal double CompanionProgress
    {
        get => (double)GetValue(CompanionProgressProperty);
        set => SetValue(CompanionProgressProperty, value);
    }

    public HourglassView() => IsVisibleChanged += (_, _) => UpdateGrainAnimation();

    public void SetSand(double remainingFraction, bool isRunning)
    {
        RemainingFraction = Math.Clamp(remainingFraction, 0, 1);
        Running = isRunning;
        UpdateGrainAnimation();
        InvalidateVisual();
    }

    internal void SetCompanionMotion(bool active, bool bothHands = false)
    {
        if (active != (companionWaveClock is not null))
        {
            companionWaveClock = active ? Repeating(CompanionArtwork.Wave, 2100).CreateClock() : null;
            companionArm.ApplyAnimationClock(RotateTransform.AngleProperty, companionWaveClock);
        }
        var waveBoth = active && bothHands;
        if (waveBoth != companionRightArm.HasAnimatedProperties)
            companionRightArm.ApplyAnimationClock(RotateTransform.AngleProperty, waveBoth ? companionWaveClock : null);
        if (active != companionEyes.HasAnimatedProperties)
            companionEyes.BeginAnimation(ScaleTransform.ScaleYProperty, active ? Repeating(CompanionArtwork.Blink, 3100, 900) : null);
    }

    private static DoubleAnimationUsingKeyFrames Repeating(DoubleAnimationUsingKeyFrames motion, int intervalMs, int initialDelayMs = 0)
    {
        var animation = motion.Clone();
        animation.BeginTime = TimeSpan.FromMilliseconds(initialDelayMs);
        animation.Duration = TimeSpan.FromMilliseconds(intervalMs);
        animation.RepeatBehavior = RepeatBehavior.Forever;
        return animation;
    }

    private void UpdateGrainAnimation()
    {
        var animate = IsVisible && Running && RemainingFraction > 0;
        if (animate == animatingGrains) return;
        animatingGrains = animate;
        if (!animate)
        {
            BeginAnimation(GrainPhaseProperty, null);
            return;
        }

        var grains = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(840))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(grains, 30);
        BeginAnimation(GrainPhaseProperty, grains);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Leave space for the tilted outline while keeping the upright icon at its original size.
        var scale = Math.Min(ActualWidth, ActualHeight) / 176;
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform(ActualWidth / 2 - Neck.X * scale, ActualHeight / 2 - Neck.Y * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));

        var growth = Turning ? 0 : CompanionProgress;
        var lift = growth > 0 ? Math.Max(0, CompanionArtwork.BootBottomInGlass(growth) - WhiteBaseY) : 0;
        if (lift > 0) dc.PushTransform(new TranslateTransform(0, -lift));
        var angle = TurnAngle;
        if (angle == 0 && !Turning && !Running)
            dc.DrawDrawing(RestingArtwork);
        else
            DrawActiveHourglass(dc, angle);

        if (growth > 0) DrawCompanion(dc, growth);

        if (lift > 0) dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawCompanion(DrawingContext dc, double growth)
    {
        dc.PushTransform(CompanionArtwork.ToGlassCoordinates);
        CompanionArtwork.DrawBoots(dc, growth);
        CompanionArtwork.DrawHands(dc, companionArm, companionRightArm, growth);
        dc.PushTransform(new ScaleTransform(1, growth, CompanionArtwork.CenterX, CompanionArtwork.EyeCenterY));
        CompanionArtwork.DrawEyes(dc, companionEyes);
        dc.Pop();
        dc.Pop();
    }

    private void DrawActiveHourglass(DrawingContext dc, double angle)
    {
        var receiver = new RotateTransform(angle, Neck.X, Neck.Y);
        var source = new RotateTransform(angle + 180, Neck.X, Neck.Y);
        dc.PushTransform(receiver);
        dc.DrawGeometry(Glass, null, Ribbon);
        dc.Pop();

        // Liquid-like settling during the turn, followed by a short opening of the sand stream.
        var transfer = Turning ? SmoothStep((angle - 153) / 27) : 1;
        var sourceFraction = Turning || angle >= 90 ? 1 - (1 - RemainingFraction) * transfer : 1;
        var tilt = Math.Cos(angle * Math.PI / 180);
        DrawChamberSand(dc, source, sourceFraction, Math.Max(0, tilt), 0);
        var streamOpacity = Running ? transfer * Math.Min(1, RemainingFraction * 24) : 0;
        DrawChamberSand(dc, receiver, 1 - sourceFraction, Math.Max(0, -tilt), streamOpacity, (double)GetValue(GrainPhaseProperty));
        dc.DrawDrawing(GetReflections(receiver, source));

        dc.PushTransform(receiver);
        dc.DrawGeometry(null, RibbonEdge, Ribbon);
        dc.DrawGeometry(null, NormalRibbon, Ribbon);
        dc.Pop();
    }

    private static Drawing CreateRestingGlass(double reflectionOpacity = 1)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var upper = new RotateTransform(0, Neck.X, Neck.Y);
            var lower = new RotateTransform(180, Neck.X, Neck.Y);
            dc.DrawGeometry(Glass, null, Ribbon);
            DrawChamberSand(dc, lower, 1, 1, 0);
            DrawReflection(dc, upper, reflectionOpacity);
            DrawReflection(dc, lower, reflectionOpacity);
            dc.DrawGeometry(null, RibbonEdge, Ribbon);
        }
        return Frozen(drawing);
    }

    private static Drawing CreateRestingArtwork()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawDrawing(CreateRestingGlass());
            dc.DrawGeometry(null, NormalRibbon, Ribbon);
        }
        return Frozen(drawing);
    }

    internal static Drawing CreateRestingArtworkWithLimbs(Transform glassPlacement, Geometry limbs, double reflectionOpacity)
    {
        const double tolerance = .06;
        var glass = Ribbon.Clone();
        glass.Transform = glassPlacement;
        var rim = Ribbon.GetWidenedPathGeometry(NormalRibbon, tolerance, ToleranceType.Absolute);
        rim.Transform = glassPlacement;

        // Trim the limb caps at the glass, then fill the rim and limbs together to avoid a seam.
        var outside = Geometry.Combine(limbs, glass, GeometryCombineMode.Exclude, null, tolerance, ToleranceType.Absolute);
        var outline = Geometry.Combine(rim, outside, GeometryCombineMode.Union, null, tolerance, ToleranceType.Absolute);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.PushTransform(glassPlacement);
            dc.DrawDrawing(CreateRestingGlass(reflectionOpacity));
            dc.Pop();
            dc.DrawGeometry(NormalRibbon.Brush, null, outline);
        }
        return Frozen(drawing);
    }

    internal static Geometry CreateExteriorClip(Transform glassPlacement, Rect bounds)
    {
        var glass = Ribbon.Clone();
        glass.Transform = glassPlacement;
        return Frozen(Geometry.Combine(new RectangleGeometry(bounds), glass, GeometryCombineMode.Exclude, null));
    }

    private DrawingGroup GetReflections(RotateTransform upperChamber, RotateTransform lowerChamber)
    {
        if (reflections is not null && reflectionAngle == upperChamber.Angle) return reflections;

        var drawing = new DrawingGroup();
        using (var layer = drawing.Open())
        {
            DrawReflection(layer, upperChamber);
            DrawReflection(layer, lowerChamber);
        }

        reflectionAngle = upperChamber.Angle;
        reflections = Frozen(drawing);
        return reflections;
    }

    private static void DrawReflection(DrawingContext dc, RotateTransform chamber, double opacity = 1)
    {
        const double RadiansPerDegree = Math.PI / 180;
        const double LightDirection = -146;
        var tilt = Math.Cos(chamber.Angle * RadiansPerDegree);
        var exposure = (1 + tilt) / 2;
        var spread = 25 + 14 * exposure;
        var strength = opacity * (.38 + .43 * exposure) * (.84 + .16 * Math.Abs(tilt));
        var transform = chamber.Value;
        var points = new Point[13];

        // The light is fixed in screen coordinates. Sample the rim facing it in the chamber's rotating coordinates.
        for (var i = 0; i < points.Length; i++)
        {
            var position = i / (double)(points.Length - 1);
            var bearing = (LightDirection - chamber.Angle + spread * (2 * position - 1)) * RadiansPerDegree;
            var ray = new Vector(Math.Cos(bearing), Math.Sin(bearing));
            var inset = 4 + 2 * Math.Sin(Math.PI * position);
            points[i] = transform.Transform(ChamberCenter + ray * (ChamberRadius(ray) - inset));
        }

        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(points[0], false, false);
            path.PolyLineTo(new ArraySegment<Point>(points, 1, points.Length - 1), true, true);
        }
        curve.Freeze();

        var fade = Color.FromArgb(0, 255, 255, 255);
        var peak = Color.FromArgb((byte)(255 * strength), 255, 255, 255);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = points[0],
            EndPoint = points[^1],
            GradientStops = new GradientStopCollection
            {
                new(fade, 0), new(peak, .37), new(peak, .48), new(fade, 1)
            }
        };
        dc.DrawGeometry(null, RoundedPen(brush, 1.8 + .65 * exposure), curve);
    }

    private static double ChamberRadius(Vector ray)
    {
        var previous = ChamberOutline[^1];
        foreach (var point in ChamberOutline)
        {
            var edge = point - previous;
            var offset = previous - ChamberCenter;
            previous = point;
            var denominator = Vector.CrossProduct(ray, edge);
            if (Math.Abs(denominator) < 1e-6) continue;
            var radius = Vector.CrossProduct(offset, edge) / denominator;
            var edgePosition = Vector.CrossProduct(offset, ray) / denominator;
            if (radius > 0 && edgePosition >= 0 && edgePosition <= 1) return radius;
        }
        return 0;
    }

    private static void DrawChamberSand(DrawingContext dc, RotateTransform chamber, double amount, double pile, double streamOpacity, double grainPhase = 0)
    {
        if (amount <= 0 && streamOpacity <= 0) return;
        Span<Point> outline = stackalloc Point[ChamberOutline.Length];
        var transform = chamber.Value;
        for (var i = 0; i < outline.Length; i++) outline[i] = transform.Transform(ChamberOutline[i]);
        var level = FindSandLevel(outline, amount * .70, out var floor);
        var peak = Math.Min(7, (floor - level) * .55) * pile;
        var surface = new StreamGeometry();
        using (var path = surface.Open())
        {
            path.BeginFigure(new Point(-48, level), true, true);
            path.BezierTo(new Point(12, level), new Point(34, level - peak), new Point(48, level - peak), true, false);
            path.BezierTo(new Point(62, level - peak), new Point(84, level), new Point(144, level), true, false);
            path.LineTo(new Point(144, 160), false, false);
            path.LineTo(new Point(-48, 160), false, false);
            path.LineTo(new Point(-48, level), false, false);
        }

        // Clip to the turned glass, then draw in screen coordinates so gravity stays vertical.
        dc.PushTransform(chamber);
        dc.PushClip(Chamber);
        dc.PushTransform(new RotateTransform(-chamber.Angle, Neck.X, Neck.Y));
        if (amount > 0)
        {
            dc.DrawGeometry(Sand, SandEdge, surface);
            dc.PushClip(surface);
            dc.PushOpacity(.28);
            for (var i = 0; i < 48; i++)
            {
                var x = 9 + i * 23 % 78;
                var y = level - 7 + i * 17 % 48;
                dc.DrawEllipse(i % 2 == 0 ? LightGrain : DarkGrain, null, new Point(x, y), .85, .6);
            }
            dc.Pop();
            dc.Pop();
        }
        if (streamOpacity > 0) DrawFallingGrains(dc, level - peak, streamOpacity, grainPhase);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void DrawFallingGrains(DrawingContext dc, double surface, double opacity, double phase)
    {
        if (surface <= 77) return;
        dc.PushOpacity(opacity);
        for (var i = 0; i < 10; i++)
        {
            var travel = (phase + i / 10d) % 1;
            var y = 75 + (surface - 75) * travel * (.32 + .68 * travel);
            var x = Neck.X + (i % 3 - 1) * (1 + travel);
            dc.DrawEllipse(i % 3 == 0 ? DarkGrain : Sand, null, new Point(x, y), .9, 1.2);
        }
        dc.Pop();
    }

    private static double FindSandLevel(ReadOnlySpan<Point> outline, double fill, out double floor)
    {
        var ceiling = double.PositiveInfinity;
        floor = double.NegativeInfinity;
        foreach (var point in outline)
        {
            ceiling = Math.Min(ceiling, point.Y);
            floor = Math.Max(floor, point.Y);
        }
        if (fill <= 0) return floor;
        var target = AreaBelow(outline, ceiling) * fill;
        var low = ceiling;
        var high = floor;
        for (var i = 0; i < 12; i++)
        {
            var middle = (low + high) / 2;
            if (AreaBelow(outline, middle) > target) low = middle;
            else high = middle;
        }
        return (low + high) / 2;
    }

    private static double AreaBelow(ReadOnlySpan<Point> points, double level)
    {
        // Integrate each edge under the horizontal surface. The cut edge at y=level adds zero.
        var twiceArea = 0d;
        var a = points[^1];
        foreach (var b in points)
        {
            var ax = a.X;
            var bx = b.X;
            var ay = a.Y - level;
            var by = b.Y - level;
            if (ay > 0 || by > 0)
            {
                if (ay < 0) { ax += (bx - ax) * (-ay / (by - ay)); ay = 0; }
                if (by < 0) { bx += (ax - bx) * (-by / (ay - by)); by = 0; }
                twiceArea += ax * by - bx * ay;
            }
            a = b;
        }
        return Math.Abs(twiceArea) / 2;
    }

    private static Point[] FlattenChamber()
    {
        var figure = Chamber.GetFlattenedPathGeometry(.45, ToleranceType.Absolute).Figures[0];
        var points = new List<Point> { figure.StartPoint };
        foreach (var segment in figure.Segments)
        {
            if (segment is PolyLineSegment polyline) points.AddRange(polyline.Points);
            else if (segment is LineSegment line) points.Add(line.Point);
        }
        return points.ToArray();
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static Geometry Outline(string data) => Frozen(Geometry.Parse(data));

    private static Pen RoundedPen(Brush brush, double thickness) => Frozen(new Pen(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    });

    private static Brush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) =>
        Frozen(new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 55));

    private static T Frozen<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }
}
