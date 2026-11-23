using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
namespace SandUp.Desktop;
public static class IconButtons
{
    public static Button Create(string label)
    {
        var shape=label switch
        {
            "设置"=>"M9,3 L15,3 16,6 19,7 21,10 19,12 21,15 19,18 16,18 15,21 9,21 8,18 5,17 3,14 5,12 3,9 5,6 8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
            "记录" or "查看记录"=>"M5,3 L19,3 19,21 5,21 Z M8,7 L16,7 M8,12 L16,12 M8,17 L13,17",
            "返回"=>"M19,12 L5,12 M11,5 L4,12 11,19",
            "开始工作" or "继续本轮"=>"M8,4 L20,12 8,20 Z",
            "暂停"=>"M8,4 L8,20 M16,4 L16,20",
            "现在休息" or "开始休息"=>"M4,7 L17,7 17,16 Q17,20 13,20 L8,20 Q4,20 4,16 Z M17,8 L20,8 Q23,12 17,14 M7,2 L7,4 M12,2 L12,4",
            "延后 3 分钟" or "延后 5 分钟"=>"M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 L12,12 15,14",
            "应急退出" or "应急退出  Esc"=>"M10,3 L4,3 4,21 10,21 M10,12 L22,12 M17,7 L22,12 17,17",
            "结束本轮"=>"M5,5 L19,5 19,19 5,19 Z",
            "重试保存" or "换一个提示" or "刷新统计" or "恢复默认扣分"=>"M20,9 A8,8 0 1 0 20,16 M20,3 L20,9 14,9",
            "保存" or "保存设置"=>"M4,3 L17,3 21,7 21,21 3,21 3,3 Z M7,3 L7,9 17,9 17,3 M7,21 L7,14 17,14 17,21",
            "导出全部记录"=>"M4,15 L4,21 20,21 20,15 M12,16 L12,3 M7,8 L12,3 17,8",
            "清空已结束记录"=>"M3,6 L21,6 M8,6 L8,3 16,3 16,6 M6,6 L7,21 17,21 18,6 M10,10 L10,17 M14,10 L14,17",
            "补填或修改活动"=>"M4,16 L16,4 20,8 8,20 3,21 Z M13,7 L17,11",
            "取消"=>"M5,5 L19,19 M19,5 L5,19",
            "保存并开始下一轮"=>"M2,12 L6,16 12,8 M15,5 L23,12 15,19 Z",
            "不记录，继续"=>"M4,5 L15,12 4,19 Z M19,5 L19,19",
            "稍后继续"=>"M20,12 A8,8 0 1 1 4,12 A8,8 0 1 1 20,12 M12,6 L12,12 16,14",
            "加载更多"=>"M5,8 L12,15 19,8",
            _=>throw new ArgumentException("未定义按钮图案："+label)
        };
        var danger=label.StartsWith("应急退出")||label=="清空已结束记录";
        var canvas=new Canvas{Width=24,Height=24};
        canvas.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse(shape),Stroke=danger?UiTheme.Danger:UiTheme.Green,StrokeThickness=1.8,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round});
        if(label.StartsWith("延后"))
        {
            var badge=new TextBlock{Text=label.Contains('3')?"3":"5",FontSize=12,FontWeight=FontWeights.Bold,Background=UiTheme.Raised,Foreground=UiTheme.Green,Padding=new Thickness(1,0,1,0)};
            Canvas.SetLeft(badge,16);Canvas.SetTop(badge,14);canvas.Children.Add(badge);
        }
        var background = label is "开始工作" or "开始休息" or "继续本轮" ? UiTheme.Rest : UiTheme.Raised;
        var button=new Button{Content=canvas,ToolTip=label,Width=44,Height=44,Padding=new Thickness(9),Margin=new Thickness(0,6,8,6),Background=background,BorderBrush=UiTheme.Line,BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Left};
        AutomationProperties.SetName(button,label);ToolTipService.SetInitialShowDelay(button,250);
        return button;
    }
}
