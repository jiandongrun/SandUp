using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SandUp.Application;
using SandUp.Desktop;
using SandUp.Domain;

namespace SandUp.Desktop.Tests;

public sealed class CyclePlanEditorTests
{
    [Fact]
    public async Task CombinationLibrarySeparatesDefinitionsFromPlacementsAndPreviewsNestedPlan()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new CyclePlanEditor();
                var defaults = WorkClockApplication.Defaults();
                editor.Load(defaults);
                Assert.True(editor.TryRead(out var defaultPlan, out var emptyLibrary, out _));
                Assert.Equal(new CycleBlock[] { new CycleWork(1500), new CycleRest(300, "walk") }, defaultPlan!.Blocks);
                Assert.Empty(emptyLibrary!.Combinations);
                Assert.True(defaultPlan.MergeAdjacentRests);
                Assert.Contains(Descendants(editor).OfType<TextBlock>(), text => text.Text == "完整计划预览");
                editor.Load(defaults with { WorkDurationMs = 1_980_000, RestDurationMs = 420_000 });
                Assert.True(editor.TryRead(out var converted, out _, out _));
                Assert.Equal(new CycleBlock[] { new CycleWork(1980), new CycleRest(420, "walk") }, converted!.Blocks);
                editor.Load(defaults);

                var planEditor = Named<StackPanel>(editor, "PlanSequence");
                var library = Named<Border>(editor, "CombinationLibrary");
                var entries = Named<StackPanel>(library, "CombinationList");
                var preview = Named<StackPanel>(editor, "CyclePreview");
                var merge = Descendants(planEditor).OfType<CheckBox>().Single();
                ClickText(planEditor, "+ 组合");
                Assert.Contains(Descendants(planEditor).OfType<TextBlock>(), text => text.Text == "请先在组合库新建组合并添加模块。");

                ClickText(library, "新建组合");
                Assert.Empty(Descendants(Detail(0)).OfType<TextBox>());
                Assert.Equal(Visibility.Visible, Detail(0).Visibility);
                Assert.Equal("收起组合1", AccessibleName(Header(0)));
                Assert.True(editor.TryRead(out _, out var withEmptyCombination, out var error), error);
                Assert.Empty(Assert.Single(withEmptyCombination!.Combinations).Blocks);
                Click(Header(0));
                Assert.Equal(Visibility.Collapsed, Detail(0).Visibility);
                Click(Header(0));
                ClickText(planEditor, "+ 组合");
                Assert.DoesNotContain(Descendants(planEditor).OfType<Button>(), button => AccessibleName(button).StartsWith("插入组合"));
                ClickText(Detail(0), "+ 工作");
                ClickText(Detail(0), "+ 休息");

                ClickText(library, "新建组合");
                Assert.Equal(Visibility.Collapsed, Detail(0).Visibility);
                var detail = Detail(1);
                Assert.Equal(Visibility.Visible, detail.Visibility);
                Assert.Empty(Descendants(detail).OfType<TextBox>());
                ClickText(detail, "+ 工作");
                ClickText(detail, "+ 休息");
                Input(detail, "工作时长").Text = "20";
                Input(detail, "休息时长").Text = "20";
                Descendants(detail).OfType<ComboBox>().Single(item => AccessibleName(item) == "休息时长单位").SelectedIndex = 1;
                Descendants(detail).OfType<ComboBox>().Single(item => AccessibleName(item) == "休息方式").SelectedValue = "look_away";
                ClickNamed(detail, "上移休息");

                Insert(planEditor, "组合1");
                Insert(planEditor, "组合1");
                var repeats = Descendants(planEditor).OfType<TextBox>().Where(box => AccessibleName(box) == "引用重复次数").ToArray();
                Assert.Equal(new[] { "1", "1" }, repeats.Select(box => box.Text));
                repeats[0].Text = "0";
                Assert.False(editor.TryRead(out _, out _, out error));
                Assert.Contains("1–20", error);
                repeats[0].Text = "2";
                repeats[1].Text = "3";
                Assert.DoesNotContain(Descendants(library).OfType<TextBox>(), box => AccessibleName(box) == "引用重复次数");
                Click(Header(0));
                Assert.Equal(Visibility.Visible, Detail(0).Visibility);
                Assert.Equal(Visibility.Collapsed, Detail(1).Visibility);
                ClickNamed(Detail(0), "从组合库删除");
                Assert.Equal(2, entries.Children.Count);
                Input(Detail(0), "工作时长").Text = "30";
                Assert.Equal(2, Descendants(planEditor).OfType<TextBlock>().Count(text => text.Text.Contains("工作 30 分钟 → ")));
                Assert.Equal(5, PreviewLabels().Count(label => label == "工作 30 分钟"));
                Assert.True(editor.TryRead(out _, out var draft, out error), error);
                var placements = draft!.Blocks.OfType<CycleCombinationReference>().ToArray();
                Assert.Equal(2, placements.Length);
                Assert.Equal(placements[0].Id, placements[1].Id);
                Assert.Equal(new[] { 2, 3 }, placements.Select(link => link.RepeatCount));

                var surface = new Border { Background = UiTheme.Canvas, Padding = new Thickness(18), Child = editor };
                TextElement.SetFontFamily(surface, new FontFamily(new Uri("pack://application:,,,/"),
                    "/SandUp;component/Assets/Fonts/#Noto Sans SC, Segoe UI"));
                var columns = Named<Grid>(editor, "CycleColumns");
                var planCard = Assert.IsType<Border>(columns.Children[0]);
                var previewCard = (Border)columns.Children[2];
                Arrange(620);
                Assert.Same(planEditor, planCard.Child);
                Assert.Equal(library.BorderThickness, planCard.BorderThickness);
                var capture = Descendants(planEditor).OfType<Button>().Single(item => Equals(item.Content, "保存为组合"));
                var moduleFrame = Assert.IsType<Border>(planEditor.Children[1]);
                Assert.Equal(new Thickness(1), moduleFrame.BorderThickness);
                Assert.Contains(Descendants(moduleFrame).OfType<Button>(), button => Equals(button.Content, "+ 工作"));
                Assert.Same(capture, planEditor.Children[2]);
                Assert.True(capture.TranslatePoint(new Point(), planEditor).Y < merge.TranslatePoint(new Point(), planEditor).Y);
                Assert.Equal(0, Grid.GetRow(library));
                Assert.Equal(1, Grid.GetColumn(library));
                Assert.True(Detail(0).TranslatePoint(new Point(), entries).Y > Header(0).TranslatePoint(new Point(), entries).Y);
                Assert.True(Header(1).TranslatePoint(new Point(), entries).Y > Detail(0).TranslatePoint(new Point(), entries).Y);
                PageNavigationTests.SaveElementPreview(surface, "combination-library.png", UiTheme.Canvas);

                // The second definition can include the first, but the first can no longer include the second.
                Click(Header(1));
                Insert(detail, "组合1");
                Input(detail, "引用重复次数").Text = "2";
                Click(Header(0));
                ClickText(Detail(0), "+ 组合");
                Assert.DoesNotContain(Descendants(Detail(0)).OfType<Button>(), button => AccessibleName(button).StartsWith("插入组合"));
                Insert(planEditor, "组合2");
                Descendants(planEditor).OfType<TextBox>().Last(box => AccessibleName(box) == "引用重复次数").Text = "2";

                Assert.True(editor.TryRead(out var plan, out var arrangement, out error), error);
                Assert.Equal(10, plan!.Compile()!.Rounds.Count);
                Assert.Equal(2, PreviewLabels().Count(label => label == "工作 50 分钟"));
                Assert.Contains("休息 5 分 20 秒 · 走动、远眺", PreviewLabels());
                Assert.DoesNotContain("合并休息", string.Join(" ", PreviewLabels()));
                Assert.Equal("工作 25 分钟", PreviewLabels()[0]);
                Assert.NotEqual(((Border)preview.Children[0]).Background, ((Border)preview.Children[1]).Background);
                merge.IsChecked = false;
                Assert.Contains("休息 20 秒 · 远眺", PreviewLabels());
                merge.IsChecked = true;

                Click(Header(1));
                Arrange(380);
                Assert.Equal(0, Grid.GetRow(library));
                Assert.Equal(1, Grid.GetRow(planCard));
                PageNavigationTests.SaveElementPreview(surface, "combination-library-narrow.png", UiTheme.Canvas);

                editor.Load(defaults with { CyclePlan = plan, Arrangement = arrangement });
                Assert.True(editor.TryRead(out var restored, out var restoredLayout, out error), error);
                Assert.True(plan.SameAs(restored));
                Assert.Equal(arrangement!.Combinations.Select(combo => combo.Id), restoredLayout!.Combinations.Select(combo => combo.Id));
                Assert.Equal(Visibility.Collapsed, Detail(0).Visibility);
                Assert.Equal(Visibility.Collapsed, Detail(1).Visibility);
                Assert.Equal(new[] { "2", "3", "2" }, Descendants(planEditor).OfType<TextBox>()
                    .Where(box => AccessibleName(box) == "引用重复次数").Select(box => box.Text));
                ClickText(planEditor, "保存为组合");
                Assert.True(editor.TryRead(out var afterCapture, out var captured, out error), error);
                Assert.True(plan.SameAs(afterCapture));
                var newCombination = captured!.Combinations[^1];
                Assert.Equal(3, newCombination.Number);
                Assert.Equal(arrangement.Blocks.ToArray(), newCombination.Blocks.ToArray());
                Assert.Equal(Visibility.Visible, Detail(2).Visibility);
                Input(planEditor, "工作时长").Text = "26";
                Assert.Equal("25", Input(Detail(2), "工作时长").Text);
                Input(planEditor, "工作时长").Text = "25";
                var row = Descendants(planEditor).OfType<Button>().First(button => AccessibleName(button).StartsWith("在组合库中打开"));
                Click(row);
                Assert.Equal(Visibility.Visible, Detail(0).Visibility);
                var input = Input(Detail(0), "工作时长");
                Assert.Equal("30", input.Text);
                input.Text = decimal.MaxValue.ToString();
                Assert.False(editor.TryRead(out _, out _, out _));
                ClickText(planEditor, "保存为组合");
                Assert.Equal(3, entries.Children.Count);
                Assert.Contains("工作时长", Named<TextBlock>(planEditor, "PlanCaptureFeedback").Text);
                ClickNamed(Detail(0), "删除工作");
                ClickNamed(Detail(0), "删除休息");
                Assert.False(editor.TryRead(out _, out _, out error));
                Assert.Contains("组合1为空", error);

                var legacy = CyclePlan.Compose([new CycleRepeat(20, [new CycleWork(180), new CycleRest(30, "look_away")])]);
                editor.Load(defaults with { CyclePlan = legacy });
                Assert.True(editor.TryRead(out var migrated, out var migratedLayout, out error), error);
                Assert.True(legacy.SameAs(migrated));
                Assert.Single(migratedLayout!.Combinations);
                Assert.Equal(20, Assert.IsType<CycleCombinationReference>(Assert.Single(migratedLayout.Blocks)).RepeatCount);
                Assert.False(merge.IsChecked);
                Arrange(380);
                var previewScroll = Descendants(editor).OfType<ScrollViewer>().Single(viewer => viewer.Content == preview);
                Assert.True(previewScroll.ScrollableHeight > 0);
                Assert.Equal(0, previewScroll.ScrollableWidth);
                Assert.Equal(40, preview.Children.Count);
                Assert.Equal("休息 30 秒 · 远眺", PreviewLabels()[39]);

                CycleBlock deepest = new CycleWork(60);
                for (var level = 0; level < CyclePlan.MaxDepth; level++)
                    deepest = new CycleRepeat(2, [deepest, new CycleRest(10, "look_away")]);
                var fullDepthPlan = CyclePlan.Compose([new CycleRest(8, "walk"), deepest], true);
                editor.Load(defaults with { CyclePlan = fullDepthPlan });
                ClickText(planEditor, "保存为组合");
                Assert.True(editor.TryRead(out afterCapture, out captured, out error), error);
                Assert.True(fullDepthPlan.SameAs(afterCapture));
                var atLimit = captured!.Combinations[^1];
                Assert.Equal(16, atLimit.Blocks.OfType<CycleWork>().Count());
                Assert.Equal(31, atLimit.Blocks.OfType<CycleRest>().Count());
                Assert.Equal(new CycleRest(8, "walk"), atLimit.Blocks[0]);
                Assert.Equal(new CycleRest(10, "look_away"), atLimit.Blocks[^1]);
                Assert.Contains("已展开", Named<TextBlock>(planEditor, "PlanCaptureFeedback").Text);

                editor.Load(defaults with { CyclePlan = CyclePlan.Compose([
                    new CycleWork(60), new CycleRepeat(2, [new CycleWork(120)]), new CycleRest(15, "walk")], true) });
                Assert.Equal("合并连续模块", merge.Content);
                Assert.Equal(new[] { "工作 5 分钟", "休息 15 秒 · 走动" }, PreviewLabels());
                Arrange(620);
                PageNavigationTests.SaveElementPreview(surface, "cycle-merge-modules.png", UiTheme.Canvas);
                merge.IsChecked = false;
                Assert.Equal(new[] { "工作 1 分钟", "工作 2 分钟", "工作 2 分钟", "休息 15 秒 · 走动" }, PreviewLabels());
                finished.SetResult();

                Button Header(int index) => (Button)((StackPanel)entries.Children[index]).Children[0];
                Border Detail(int index) => Named<Border>((StackPanel)entries.Children[index], "CombinationDetail");
                string[] PreviewLabels() => preview.Children.OfType<Border>().Select(cell => ((TextBlock)cell.Child).Text).ToArray();

                void Arrange(double width)
                {
                    surface.Width = width;
                    surface.Measure(new Size(width, double.PositiveInfinity));
                    surface.Arrange(new Rect(surface.DesiredSize));
                    surface.UpdateLayout();
                    Assert.True(previewCard.TranslatePoint(new Point(), columns).X >= planEditor.ActualWidth);
                    var first = preview.Children[0].TranslatePoint(new Point(), preview);
                    var second = preview.Children[1].TranslatePoint(new Point(), preview);
                    Assert.Equal(first.X, second.X);
                    Assert.True(second.Y > first.Y);
                }
            }
            catch (Exception e) { finished.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static string AccessibleName(DependencyObject node) => AutomationProperties.GetName(node);

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(item => item.Name == name);

    private static TextBox Input(DependencyObject root, string name) =>
        Descendants(root).OfType<TextBox>().Single(item => AccessibleName(item) == name);

    private static void ClickText(DependencyObject root, string label) =>
        Click(Descendants(root).OfType<Button>().Single(button => Equals(button.Content, label)));

    private static void ClickNamed(DependencyObject root, string label) =>
        Click(Descendants(root).OfType<Button>().Single(button => AccessibleName(button) == label));

    private static void Insert(DependencyObject root, string match)
    {
        ClickText(root, "+ 组合");
        Click(Descendants(root).OfType<Button>().Single(button => AccessibleName(button).StartsWith("插入组合") &&
            AccessibleName(button).Contains(match)));
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
}
