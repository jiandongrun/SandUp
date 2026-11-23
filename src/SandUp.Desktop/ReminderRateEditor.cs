using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SandUp.Domain;

namespace SandUp.Desktop;

internal sealed class ReminderRateEditor : Border
{
    private readonly Dictionary<BodyPart, TextBox> inputs = new();

    public ReminderRateEditor()
    {
        Margin = new Thickness(0, 8, 0, 8);
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(10);
        Background = UiTheme.Surface;
        BorderBrush = UiTheme.Line;
        BorderThickness = new Thickness(1);
        var panel = new StackPanel();
        Child = panel;
        var header = new DockPanel();
        var reset = IconButtons.Create("恢复默认扣分");
        reset.Click += (_, _) => SetRates(ReminderRates.Default);
        DockPanel.SetDock(reset, Dock.Right);
        header.Children.Add(reset);
        header.Children.Add(new TextBlock { Text = "耐久度扣分速度", VerticalAlignment = VerticalAlignment.Center,
            FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(header);
        panel.Children.Add(Description("每工作 1 小时的基础扣分，可填 0–40。填 0 关闭该项扣分。"));

        var fields = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 8) };
        foreach (var part in BodyPartDisplay.Order)
        {
            var field = new DockPanel { Margin = new Thickness(0, 4, 12, 4) };
            var input = new TextBox { MinWidth = 29, MaxWidth = 55, VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 2, 4, 2), TextAlignment = TextAlignment.Center };
            var name = BodyPartDisplay.Name(part);
            AutomationProperties.SetName(input, name + "每小时扣分");
            DockPanel.SetDock(input, Dock.Right);
            field.Children.Add(input);
            field.Children.Add(new Label { Content = name, Target = input, Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center });
            fields.Children.Add(field);
            inputs.Add(part, input);
        }
        panel.Children.Add(fields);
        var explain = new Expander { Header = "默认值和历史耐久度怎么算？", Margin = new Thickness(0, 3, 0, 0),
            Content = Description("研究提供了远眺、换姿势和起身活动的参考。六项具体分值及相对高低未经临床校准，是可调的休息提醒参数。腹部一项提醒打断久坐，肛门·前列腺一项提醒离座，详见随附使用说明。\n\n历史从 100 分开始，按此前每个有记录日依次计算：前值占 80%，当日实际结余占 20%；没有记录的日期保持不变。历史 100 分时按原速扣分，60 分时按 1.2 倍，最低 0 分时按 1.5 倍。保存新速度后，已有计时和活动将按同一套规则重新显示。") };
        panel.Children.Add(explain);
    }

    public void SetRates(ReminderRates rates)
    {
        foreach (var (part, input) in inputs)
            input.Text = rates.For(part).ToString(CultureInfo.CurrentCulture);
    }

    public IEnumerable<string> EditValues() => BodyPartDisplay.Order.Select(part => inputs[part].Text);

    public bool TryRead(out ReminderRates rates, out string error)
    {
        var values = new Dictionary<BodyPart, double>();
        foreach (var (part, input) in inputs)
        {
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                || !double.IsFinite(value) || value is < 0 or > 40)
            {
                rates = ReminderRates.Default;
                error = $"{BodyPartDisplay.Name(part)}每小时扣分须为 0–40 之间的数字。";
                input.Focus();
                input.SelectAll();
                return false;
            }
            values.Add(part, value);
        }
        rates = new(values[BodyPart.Eyes], values[BodyPart.Shoulders], values[BodyPart.Back],
            values[BodyPart.Abdomen], values[BodyPart.Legs], values[BodyPart.Pelvic]);
        error = "";
        return true;
    }

    private static TextBlock Description(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = UiTheme.Muted, FontSize = 12, Margin = new Thickness(0, 5, 0, 4) };
}
