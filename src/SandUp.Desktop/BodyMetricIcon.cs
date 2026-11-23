using System.Windows;
using System.Windows.Media;
using SandUp.Domain;

namespace SandUp.Desktop;

internal sealed class BodyMetricIcon : FrameworkElement
{
    private static readonly IReadOnlyDictionary<BodyPart, Geometry> Drawings = new Dictionary<BodyPart, Geometry>
    {
        [BodyPart.Eyes] = Outline("M2.5,12 Q6.8,5.8 12,5.8 Q17.2,5.8 21.5,12 Q17.2,18.2 12,18.2 Q6.8,18.2 2.5,12 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12 Z"),
        [BodyPart.Shoulders] = Outline("M8.7,4.1 L8.7,8.2 Q8.7,10.6 6.1,11.7 L4.3,12.5 Q2.3,13.5 2.3,18 M15.3,4.1 L15.3,8.2 Q15.3,10.6 17.9,11.7 L19.7,12.5 Q21.7,13.5 21.7,18 M8.5,14.2 Q12,16.5 15.5,14.2"),
        [BodyPart.Back] = Outline("M14.1,2 Q11.5,4.5 12.5,7.5 C14,10.2 13.6,11.5 11,14.2 Q8,18.4 10.4,22 M10,3.6 L14.8,4.6 M10.3,7.6 L15.1,8.1 M11,11.2 L15.2,12.3 M8.1,14.6 L12.7,16.2 M7.1,18.7 L11.8,19.8"),
        [BodyPart.Abdomen] = Outline("M7,2.9 Q8.2,6.8 6.4,10.2 C3.7,15.2 4.2,19 6.8,21.2 M17,2.9 Q15.8,6.8 17.6,10.2 C20.3,15.2 19.8,19 17.2,21.2 M6.8,17.1 Q12,20.5 17.2,17.1 M11.6,12.8 Q12,13.7 12.5,12.8"),
        [BodyPart.Legs] = Outline(
            "M9.1,1.9 H14.1 Q14.7,1.9 14.7,2.5 L14.9,5.2 Q15.2,6.7 14.75,7.8 " +
            "L17.35,11.5 Q18,12.4 18.2,13.8 L19.5,19.25 L22.15,20.35 Q23,20.7 22.55,21.5 " +
            "L18.65,22.25 Q17.9,22.4 17.55,21.55 L15,14.7 L10.65,10.95 " +
            "C10.2,12.05 9.4,13.5 8.15,14.95 C7.55,16.65 6.4,18.15 5.1,19.55 L6,20.9 " +
            "Q6.5,21.8 5.6,22.25 Q5.2,22.55 4.7,22.1 L2.55,19.7 Q1.95,19.15 2.45,18.5 " +
            "L5.25,13.4 Q5.65,12.35 6.3,11.8 L7.55,9.85 C6.3,8.15 7.35,5.6 8,4.3 " +
            "Q8.55,3 8.55,2.4 Q8.65,1.9 9.1,1.9 Z"),
        [BodyPart.Pelvic] = Outline("M3.2,5.4 C6.4,4.2 7.6,7.4 7.5,10.2 Q8.6,12.3 12,12.8 Q15.4,12.3 16.5,10.2 C16.4,7.4 17.6,4.2 20.8,5.4 C22.4,9.6 19.8,12.8 18,14.3 L16,18.3 Q12,21.1 8,18.3 L6,14.3 Q2.2,11.5 3.2,5.4 Z M10.5,15.9 Q12,14.5 13.5,15.9 M12,12.8 L12,14.3")
    };
    private static readonly Pen Stroke = MakeStroke();
    private readonly BodyPart part;

    public BodyMetricIcon(BodyPart part)
    {
        this.part = part;
        Width = Height = 24;
        IsHitTestVisible = false;
    }

    private static Geometry Outline(string shape)
    {
        var geometry = Geometry.Parse(shape);
        geometry.Freeze();
        return geometry;
    }

    private static Pen MakeStroke()
    {
        var pen = new Pen(UiTheme.Green, 1.6)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double canvasSize = 24;
        var scale = Math.Min(ActualWidth, ActualHeight) / canvasSize;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale,
            (ActualWidth - canvasSize * scale) / 2, (ActualHeight - canvasSize * scale) / 2));
        dc.DrawGeometry(null, Stroke, Drawings[part]);
        dc.Pop();
    }
}
