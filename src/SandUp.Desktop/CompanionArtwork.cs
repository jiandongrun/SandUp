using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SandUp.Desktop;

internal static class CompanionArtwork
{
    internal const double CenterX = 45;
    internal const double EyeCenterY = 27.1;
    internal const double GlassScale = .84;
    internal const double GlassOffsetX = CenterX - 48 * GlassScale;
    internal const double GlassOffsetY = -2;
    internal static readonly Transform GlassPlacement = Frozen(new MatrixTransform(GlassScale, 0, 0, GlassScale, GlassOffsetX, GlassOffsetY));
    internal static readonly Transform ToGlassCoordinates = Frozen(new MatrixTransform(1 / GlassScale, 0, 0, 1 / GlassScale, -GlassOffsetX / GlassScale, -GlassOffsetY / GlassScale));
    internal static readonly ScaleTransform Mirror = Frozen(new ScaleTransform(-1, 1, CenterX, 0));
    internal static readonly Geometry Exterior = HourglassView.CreateExteriorClip(GlassPlacement, new Rect(-4, 0, 104, 142));
    private static readonly Point Shoulder = new(19.2, 79);
    private static readonly Point Hip = new(29, 109);
    private const double BootSoleY = 123.1;
    private static readonly Geometry LeftHand = Frozen(Geometry.Parse(
        "M20.5,77 C15.2,76.9 10.4,79.1 6.8,82.8 C3.4,86.2 1.9,89.7 3.1,92.4 " +
        "C4.2,95.2 7.7,95 9.4,92.6 C11.1,90.1 11.4,86.8 14.3,84.1 C16.6,82 20.2,80.3 20.5,77 Z"));
    private static readonly Geometry LeftBoot = Frozen(Geometry.Parse(
        "M23,107 C26.7,107.5 32.6,108.8 35.2,109.6 L35.6,119.8 " +
        "Q35.6,123.1 32.4,123.1 H26.2 Q22.6,123.1 22.6,119.8 Z"));
    internal static readonly Geometry Boots = CreateBoots();
    internal static readonly DoubleAnimationUsingKeyFrames Blink = CreateBlink();
    internal static readonly DoubleAnimationUsingKeyFrames Wave = CreateWave();

    internal static RotateTransform CreateArm() => new(0, Shoulder.X, Shoulder.Y);

    internal static Transform CreateBootMotion(double angle, double lift)
    {
        var motion = Matrix.Identity;
        motion.RotateAt(angle, Hip.X, Hip.Y);
        motion.Translate(0, -lift);
        return new MatrixTransform(motion);
    }

    internal static double BootBottomInGlass(double growth) => (Hip.Y + (BootSoleY - Hip.Y) * growth - GlassOffsetY) / GlassScale;

    internal static void DrawHands(DrawingContext dc, Transform left, Transform right, double growth = 1)
    {
        dc.PushClip(Exterior);
        DrawLimb(dc, LeftHand, Shoulder, growth, left);
        dc.PushTransform(Mirror);
        DrawLimb(dc, LeftHand, Shoulder, growth, right);
        dc.Pop();
        dc.Pop();
    }

    internal static void DrawBoots(DrawingContext dc, double growth, Transform? left = null, Transform? right = null)
    {
        dc.PushClip(Exterior);
        DrawLimb(dc, LeftBoot, Hip, growth, left ?? Transform.Identity);
        dc.PushTransform(Mirror);
        DrawLimb(dc, LeftBoot, Hip, growth, right ?? Transform.Identity);
        dc.Pop();
        dc.Pop();
    }

    internal static void DrawEyes(DrawingContext dc, Transform transform)
    {
        dc.PushTransform(transform);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(29.6, 20.9, 7.2, 12.4), 1.8, 1.8);
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(53.2, 20.9, 7.2, 12.4), 1.8, 1.8);
        dc.Pop();
    }

    private static void DrawLimb(DrawingContext dc, Geometry shape, Point joint, double growth, Transform motion)
    {
        dc.PushTransform(motion);
        dc.PushTransform(growth == 1 ? Transform.Identity : new ScaleTransform(growth, growth, joint.X, joint.Y));
        dc.DrawGeometry(Brushes.White, null, shape);
        dc.Pop();
        dc.Pop();
    }

    private static Geometry CreateBoots()
    {
        var right = LeftBoot.Clone();
        right.Transform = Mirror;
        return Frozen(new GeometryGroup { FillRule = FillRule.Nonzero, Children = { LeftBoot, right } });
    }

    private static DoubleAnimationUsingKeyFrames CreateBlink() => Frozen(new DoubleAnimationUsingKeyFrames
    {
        FillBehavior = FillBehavior.Stop,
        KeyFrames =
        {
            new LinearDoubleKeyFrame(1, TimeSpan.Zero),
            new EasingDoubleKeyFrame(.1, TimeSpan.FromMilliseconds(85), new QuadraticEase { EasingMode = EasingMode.EaseIn }),
            new LinearDoubleKeyFrame(.1, TimeSpan.FromMilliseconds(120)),
            new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(275), new QuadraticEase { EasingMode = EasingMode.EaseOut })
        }
    });

    private static DoubleAnimationUsingKeyFrames CreateWave()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        return Frozen(new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0, TimeSpan.Zero),
                new EasingDoubleKeyFrame(82, TimeSpan.FromMilliseconds(220), ease),
                new EasingDoubleKeyFrame(42, TimeSpan.FromMilliseconds(360), ease),
                new EasingDoubleKeyFrame(77, TimeSpan.FromMilliseconds(500), ease),
                new EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(780), ease)
            }
        });
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
