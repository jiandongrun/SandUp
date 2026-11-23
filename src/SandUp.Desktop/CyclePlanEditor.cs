using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SandUp.Domain;

namespace SandUp.Desktop;

public sealed class CyclePlanEditor : StackPanel
{
    private static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>
    {
        ["walk"] = "走动",
        ["look_away"] = "远眺",
        ["relax_shoulders_hands"] = "放松肩手"
    };
    private static readonly Brush Ink = UiTheme.Text;
    private static readonly Brush WorkColor = UiTheme.Work;
    private static readonly Brush RestColor = UiTheme.Rest;
    private static readonly Brush GroupColor = UiTheme.Raised;
    private static readonly Brush GroupOutline = UiTheme.Line;
    private static readonly Brush AddColor = UiTheme.Rest;
    private static readonly Brush SecondaryInk = UiTheme.Muted;

    private readonly CheckBox merge = new() { Content = "合并连续模块", Margin = new Thickness(0, 9, 0, 2) };
    private readonly TextBlock previewSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly StackPanel timeline = new() { Name = "CyclePreview", Orientation = Orientation.Vertical };
    private readonly SequenceEditor root;
    private readonly List<CombinationEditor> combinations = [];
    private readonly StackPanel combinationList = new() { Name = "CombinationList", Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock libraryFeedback = new() { TextWrapping = TextWrapping.Wrap, Foreground = SecondaryInk };
    private readonly TextBlock captureFeedback = new()
    {
        Name = "PlanCaptureFeedback", TextWrapping = TextWrapping.Wrap, Foreground = SecondaryInk,
        Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed
    };
    private readonly Dictionary<Guid, CombinationLabel> labels = [];
    private Guid? selected;
    private bool loading;
    private CyclePlan? originalLegacy;
    internal event Action? Edited;

    public CyclePlanEditor()
    {
        UiTheme.AddControls(this);
        root = CreateSequence(null);
        var columns = new Grid { Name = "CycleColumns", Margin = new Thickness(0, 4, 0, 0) };
        for (var index = 0; index < 3; index++) columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(columns);

        var editor = new StackPanel { Name = "PlanSequence" };
        editor.Children.Add(SectionTitle("计划安排"));
        editor.Children.Add(new Border
        {
            Child = root.Panel, Background = UiTheme.Canvas, BorderBrush = UiTheme.Line,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4)
        });
        var capture = Button("保存为组合", CapturePlan, addModule: true);
        capture.HorizontalAlignment = HorizontalAlignment.Left;
        capture.Margin = new Thickness(0, 7, 0, 1);
        editor.Children.Add(capture);
        editor.Children.Add(merge);
        editor.Children.Add(captureFeedback);
        var planCard = Card(editor);
        columns.Children.Add(planCard);

        var library = new StackPanel();
        library.Children.Add(SectionTitle("组合库"));
        var newCombination = Button("新建组合", CreateCombination, addModule: true);
        newCombination.HorizontalAlignment = HorizontalAlignment.Left;
        library.Children.Add(newCombination);
        library.Children.Add(libraryFeedback);
        library.Children.Add(combinationList);
        var libraryCard = Card(library);
        libraryCard.Name = "CombinationLibrary";
        columns.Children.Add(libraryCard);

        var preview = new StackPanel();
        preview.Children.Add(SectionTitle("完整计划预览"));
        preview.Children.Add(previewSummary);
        preview.Children.Add(new ScrollViewer
        {
            Content = timeline, MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var previewCard = Card(preview);
        columns.Children.Add(previewCard);
        ArrangeColumns(true);
        columns.SizeChanged += (_, e) => ArrangeColumns(e.NewSize.Width >= 550);
        merge.Checked += (_, _) => Refresh();
        merge.Unchecked += (_, _) => Refresh();

        void ArrangeColumns(bool wide)
        {
            columns.ColumnDefinitions[0].Width = new GridLength(wide ? 1.35 : 1.65, GridUnitType.Star);
            columns.ColumnDefinitions[1].Width = new GridLength(wide ? 1.25 : 0, GridUnitType.Star);
            columns.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(planCard, wide ? 0 : 1);
            Grid.SetColumn(libraryCard, wide ? 1 : 0);
            Grid.SetColumnSpan(libraryCard, wide ? 1 : 3);
            Grid.SetRow(previewCard, wide ? 0 : 1);
            Grid.SetColumn(previewCard, 2);
            libraryCard.Margin = wide ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 0, 12);
            previewCard.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
    };

    private static Border Card(UIElement content) => new()
    {
        Child = content, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(8),
        Background = UiTheme.Surface, BorderBrush = UiTheme.Line,
        BorderThickness = new Thickness(1), Padding = new Thickness(8)
    };

    public void Load(SettingsSnapshot settings)
    {
        loading = true;
        var plan = settings.CyclePlan;
        originalLegacy = plan is { Blocks: null } ? plan : null;
        var arrangement = settings.Arrangement ?? CycleArrangement.FromBlocks(plan?.EditorBlocks() ??
        [
            new CycleWork((int)(settings.WorkDurationMs / 1000)),
            new CycleRest((int)(settings.RestDurationMs / 1000), "walk")
        ]);
        combinations.Clear();
        combinationList.Children.Clear();
        selected = null;
        foreach (var combination in arrangement.Combinations) AddCombination(combination);
        root.Load(arrangement.Blocks);
        merge.IsChecked = plan?.MergeAdjacentRests ?? true;
        loading = false;
        Refresh();
    }

    public bool TryRead(out CyclePlan? plan, out CycleArrangement? arrangement, out string error)
    {
        if (!TryCompose(out plan, out arrangement, out error)) return false;
        if (plan!.Compile() is not null) return true;
        if (originalLegacy?.SameAs(plan) == true)
        {
            plan = originalLegacy;
            return true;
        }
        error = LimitMessage();
        return false;
    }

    internal IEnumerable<string> EditValues()
    {
        yield return merge.IsChecked == true ? "merge" : "split";
        foreach (var value in root.EditValues()) yield return value;
        foreach (var editor in combinations)
        {
            yield return editor.Id.ToString();
            yield return editor.Number.ToString(CultureInfo.InvariantCulture);
            foreach (var value in editor.Sequence.EditValues()) yield return value;
        }
    }

    private static string LimitMessage() =>
        $"完整循环展开后最多支持 {CyclePlan.MaxSteps} 个工作或休息模块。请减少重复次数或模块数。";

    private bool TryCompose(out CyclePlan? plan, out CycleArrangement? arrangement, out string error)
    {
        plan = null;
        arrangement = null;
        if (!root.TryRead(out var blocks, out error)) return false;
        var definitions = new List<CycleCombination>(combinations.Count);
        var used = root.References.Concat(combinations.SelectMany(editor => editor.Sequence.References)).ToHashSet();
        foreach (var editor in combinations)
        {
            if (!editor.TryRead(out var combination, out error)) return false;
            if (combination!.Blocks.Count == 0 && used.Contains(combination.Id))
            {
                error = $"{editor.Name}为空。请添加模块，或移除它的引用。";
                return false;
            }
            definitions.Add(combination!);
        }
        arrangement = new CycleArrangement(blocks, definitions);
        if (arrangement.Resolve() is not { } resolved)
        {
            error = $"请检查组合引用。组合最多嵌套 {CyclePlan.MaxDepth} 层，且不可互相循环引用。";
            return false;
        }
        plan = CyclePlan.Compose(resolved, merge.IsChecked == true);
        return true;
    }

    private void Refresh()
    {
        if (loading) return;
        Edited?.Invoke();
        ClearCaptureFeedback();
        RefreshCombinationLabels();
        timeline.Children.Clear();
        if (!TryCompose(out var plan, out _, out var error))
        {
            previewSummary.Text = error;
            return;
        }

        var schedule = plan!.Compile();
        if (schedule is null)
        {
            previewSummary.Text = LimitMessage();
            return;
        }
        var rounds = schedule.Rounds.Skip(schedule.RepeatStart).ToArray();
        var workMs = rounds.Sum(round => round.WorkMs);
        var restMs = rounds.Sum(round => round.Rests.Sum(rest => rest.DurationMs));
        previewSummary.Text = $"工作 {Duration(workMs)}  ·  休息 {Duration(restMs)}";
        if (schedule.RepeatStart != 0)
        {
            var opening = schedule.Rounds[0];
            var openingRestMs = opening.Rests.Sum(rest => rest.DurationMs);
            var first = opening.WorkMs > 0
                ? $"工作 {Duration(opening.WorkMs)}，再休息 {Duration(openingRestMs)}"
                : $"休息 {Duration(openingRestMs)}";
            previewSummary.Text += $"\n第一圈先{first}。之后这段接在每圈末尾。";
        }
        foreach (var round in rounds)
        {
            if (round.WorkMs > 0) AddPreview("工作", round.WorkMs, WorkColor);
            if (round.Rests.Count == 0) continue;
            if (plan.MergeAdjacentRests && round.Rests.Count > 1)
            {
                var cues = round.Rests.Select(rest => Actions[rest.PromptId]).Distinct().ToArray();
                var tooltip = string.Join("\n", round.Rests.Select(rest => $"{Actions[rest.PromptId]} {Duration(rest.DurationMs)}"));
                AddPreview("休息", round.Rests.Sum(rest => rest.DurationMs), RestColor,
                    string.Join("、", cues), tooltip);
            }
            else
                foreach (var rest in round.Rests)
                    AddPreview("休息", rest.DurationMs, RestColor, Actions[rest.PromptId]);
        }
    }

    private void AddPreview(string label, long duration, Brush background, string? activity = null, string? tooltip = null)
    {
        var text = $"{label} {Duration(duration)}";
        if (activity is not null) text += $" · {activity}";
        timeline.Children.Add(new Border
        {
            Background = background, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(8, 5, 8, 5), ToolTip = tooltip,
            Child = new TextBlock { Text = text, Foreground = Ink, TextWrapping = TextWrapping.Wrap }
        });
    }

    private static string Duration(long milliseconds)
    {
        var seconds = milliseconds / 1000;
        if (seconds < 60) return $"{seconds} 秒";
        return seconds % 60 == 0 ? $"{seconds / 60} 分钟" : $"{seconds / 60} 分 {seconds % 60} 秒";
    }

    private static Button Button(string text, Action action, bool addModule = false)
    {
        var button = new Button
        {
            Content = text, Padding = addModule ? new Thickness(7, 2, 7, 2) : new Thickness(4, 1, 4, 1),
            MinWidth = 20, Margin = new Thickness(0, 1, 4, 1)
        };
        button.Background = addModule ? AddColor : Brushes.Transparent;
        button.Click += (_, _) => action();
        return button;
    }

    private SequenceEditor CreateSequence(Guid? owner) => new(owner, Choices, id => SelectCombination(id), Refresh);

    private void CreateCombination() => AddAndOpenCombination(NewCombination([]));

    private void CapturePlan()
    {
        if (!TryCompose(out var plan, out var arrangement, out var error))
        {
            SetCaptureFeedback(error);
            return;
        }

        if (plan!.ExpandTimedBlocks() is not { } timedBlocks)
        {
            SetCaptureFeedback(LimitMessage());
            return;
        }

        var combination = NewCombination(arrangement!.Blocks);
        // A plan already at the nesting limit cannot gain an outer layer with its links intact.
        var needsExpansion = (arrangement with { Combinations = [.. arrangement.Combinations, combination] }).Resolve() is null;
        if (needsExpansion) combination = combination with { Blocks = timedBlocks };
        AddAndOpenCombination(combination);
        var name = $"组合{combination.Number}";
        SetCaptureFeedback(needsExpansion
            ? $"已展开为{name}。点击「保存设置」保留。"
            : $"已加入{name}。点击「保存设置」保留。");
    }

    private CycleCombination NewCombination(IReadOnlyList<CycleBlock> blocks)
    {
        var number = combinations.Count == 0 ? 1 : combinations.Max(item => item.Number) + 1;
        return new CycleCombination(Guid.NewGuid(), number, blocks);
    }

    private void AddAndOpenCombination(CycleCombination combination)
    {
        AddCombination(combination);
        SelectCombination(combination.Id);
        Refresh();
    }

    internal void ClearCaptureFeedback() => SetCaptureFeedback("");

    private void SetCaptureFeedback(string message)
    {
        captureFeedback.Text = message;
        captureFeedback.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddCombination(CycleCombination combination)
    {
        var editor = new CombinationEditor(combination, CreateSequence(combination.Id),
            id => SelectCombination(selected == id ? null : id), RemoveCombination);
        combinations.Add(editor);
        combinationList.Children.Add(editor.View);
    }

    private void SelectCombination(Guid? id)
    {
        SetLibraryFeedback("");
        selected = id;
        foreach (var editor in combinations)
        {
            editor.SetExpanded(editor.Id == selected);
            editor.Sequence.ClosePicker();
            if (editor.Id == selected) editor.Header.BringIntoView();
        }
    }

    private void RemoveCombination(Guid id)
    {
        if (root.References.Contains(id) || combinations.Any(item => item.Sequence.References.Contains(id)))
        {
            SetLibraryFeedback("请先从计划和其他组合中移除对此组合的引用。");
            return;
        }

        var editor = combinations.Single(item => item.Id == id);
        combinationList.Children.Remove(editor.View);
        combinations.Remove(editor);
        selected = null;
        Refresh();
    }

    private void SetLibraryFeedback(string message)
    {
        libraryFeedback.Text = message;
        libraryFeedback.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<KeyValuePair<Guid, CombinationLabel>> Choices(Guid? owner)
    {
        var sequences = combinations.ToDictionary(item => item.Id, item => item.Sequence);
        return labels.Where(entry => sequences[entry.Key].HasModules &&
            (owner is null || !FindOwner(entry.Key, owner.Value, [])));

        bool FindOwner(Guid candidate, Guid target, HashSet<Guid> visited) => candidate == target ||
            (visited.Add(candidate) && sequences.TryGetValue(candidate, out var sequence) &&
             sequence.References.Any(child => FindOwner(child, target, visited)));
    }

    private void RefreshCombinationLabels()
    {
        labels.Clear();
        SetLibraryFeedback(combinations.Count == 0 ? "新建后，可在计划或其他组合里插入使用。" : "");

        var names = combinations.ToDictionary(editor => editor.Id, editor => editor.Name);
        foreach (var editor in combinations)
        {
            var pattern = editor.Sequence.TryRead(out var blocks, out _) ? blocks.Select(Describe).ToArray() : ["待完善"];
            var summary = string.Join(" → ", pattern.Take(3));
            if (pattern.Length > 3) summary += " → …";
            labels.Add(editor.Id, new CombinationLabel(editor.Name, summary, string.Join(" → ", pattern)));
        }

        root.UpdateReferences(labels);
        foreach (var editor in combinations) editor.Sequence.UpdateReferences(labels);

        string Describe(CycleBlock block) => block switch
        {
            CycleWork work => $"工作 {Duration(work.Seconds * 1000L)}",
            CycleRest rest => $"休息 {Duration(rest.Seconds * 1000L)}（{Actions[rest.PromptId]}）",
            CycleCombinationReference reference when names.TryGetValue(reference.Id, out var name) => $"{name} × {reference.RepeatCount}",
            _ => "待完善"
        };
    }

    private sealed record CombinationLabel(string Name, string Summary, string Details);

    private sealed class CombinationButton
    {
        private readonly TextBlock name = new() { Foreground = Ink, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock summary = new() { Foreground = SecondaryInk, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly string actionName;
        private CombinationLabel? displayed;

        public Guid Id { get; }
        public Button Control { get; }

        public CombinationButton(Guid id, string actionName, Action action)
        {
            Id = id;
            this.actionName = actionName;
            Control = Button("", action);
            Control.Background = GroupColor;
            Control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Control.Padding = new Thickness(5, 3, 5, 3);
            Control.Margin = new Thickness(0, 0, 0, 4);
            Control.Content = new StackPanel { Children = { name, summary } };
        }

        public void Update(CombinationLabel label)
        {
            if (label == displayed) return;
            displayed = label;
            name.Text = label.Name;
            summary.Text = label.Summary;
            summary.Visibility = label.Summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Control.ToolTip = label.Details.Length == 0 ? null : new TextBlock { Text = label.Details, MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(Control, label.Summary.Length == 0 ? $"{actionName} {label.Name}" : $"{actionName} {label.Name} · {label.Summary}");
        }
    }

    private sealed class CombinationEditor
    {
        private readonly Border detail;
        private readonly TextBlock chevron = new() { Foreground = SecondaryInk, VerticalAlignment = VerticalAlignment.Center };
        public Guid Id { get; }
        public int Number { get; }
        public string Name => $"组合{Number}";
        public SequenceEditor Sequence { get; }
        public StackPanel View { get; } = new() { Margin = new Thickness(0, 0, 0, 4) };
        public Button Header { get; }

        public CombinationEditor(CycleCombination combination, SequenceEditor sequence, Action<Guid> toggle, Action<Guid> remove)
        {
            Id = combination.Id;
            Number = combination.Number;
            Sequence = sequence;
            Sequence.Load(combination.Blocks);
            Header = Button(Name, () => toggle(Id));
            Header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Header.Padding = new Thickness(7, 4, 7, 4);
            Header.Margin = new Thickness(0);
            var title = new DockPanel { Children = { chevron, new TextBlock { Text = Name, FontWeight = FontWeights.SemiBold } } };
            DockPanel.SetDock(chevron, Dock.Right);
            Header.Content = title;
            View.Children.Add(Header);

            var delete = Button("×", () => remove(Id));
            delete.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(delete, "从组合库删除");
            delete.ToolTip = "从组合库删除";
            detail = new Border
            {
                Name = "CombinationDetail", Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1), BorderBrush = GroupOutline, Background = GroupColor,
                Child = new StackPanel { Children = { Sequence.Panel, delete } }
            };
            View.Children.Add(detail);
            SetExpanded(false);
        }

        public void SetExpanded(bool expanded)
        {
            detail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            Header.Background = expanded ? UiTheme.Selected : GroupColor;
            chevron.Text = expanded ? "▾" : "▸";
            AutomationProperties.SetName(Header, (expanded ? "收起" : "展开") + Name);
        }

        public bool TryRead(out CycleCombination? combination, out string error)
        {
            combination = null;
            if (!Sequence.TryRead(out var children, out error)) return false;
            combination = new CycleCombination(Id, Number, children);
            return true;
        }
    }

    private static TextBlock InlineLabel(string text) => new()
    {
        Text = text, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
    };

    private static TextBox NumberBox() => new()
    {
        MinWidth = 24, MaxWidth = 55, Margin = new Thickness(0, 1, 4, 1), Padding = new Thickness(4, 2, 4, 2),
        TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };

    private sealed class SequenceEditor
    {
        public StackPanel Panel { get; } = new();
        private readonly StackPanel rowsPanel = new();
        private readonly StackPanel picker = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        private readonly List<BlockEditor> rows = [];
        private readonly Guid? owner;
        private readonly Func<Guid?, IEnumerable<KeyValuePair<Guid, CombinationLabel>>> choices;
        private readonly Action<Guid> select;
        private readonly Action changed;
        public IEnumerable<Guid> References => rows.Select(row => row.Reference?.Id).OfType<Guid>();
        public bool HasModules => rows.Count != 0;

        public SequenceEditor(Guid? owner, Func<Guid?, IEnumerable<KeyValuePair<Guid, CombinationLabel>>> choices,
            Action<Guid> select, Action changed)
        {
            this.owner = owner;
            this.choices = choices;
            this.select = select;
            this.changed = changed;
            Panel.Children.Add(rowsPanel);
            var add = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            add.Children.Add(Button("+ 工作", () => Add(new CycleWork(1500)), addModule: true));
            add.Children.Add(Button("+ 休息", () => Add(new CycleRest(300, "walk")), addModule: true));
            add.Children.Add(Button("+ 组合", TogglePicker, addModule: true));
            Panel.Children.Add(add);
            Panel.Children.Add(new ScrollViewer
            {
                Content = picker, MaxHeight = 208, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
        }

        public void Load(IReadOnlyList<CycleBlock> blocks)
        {
            rows.Clear();
            rowsPanel.Children.Clear();
            ClosePicker();
            foreach (var block in blocks) AddRow(block);
            UpdateMovement();
        }

        private void Add(CycleBlock block)
        {
            AddRow(block);
            UpdateMovement();
            changed();
        }

        private void AddRow(CycleBlock block)
        {
            var row = new BlockEditor(block, select, changed, Move, Delete);
            rows.Add(row);
            rowsPanel.Children.Add(row.View);
        }

        private void Move(BlockEditor row, int direction)
        {
            var index = rows.IndexOf(row);
            var target = index + direction;
            if (target < 0 || target >= rows.Count) return;
            rows.RemoveAt(index);
            rows.Insert(target, row);
            rowsPanel.Children.RemoveAt(index);
            rowsPanel.Children.Insert(target, row.View);
            UpdateMovement();
            changed();
        }

        private void Delete(BlockEditor row)
        {
            rows.Remove(row);
            rowsPanel.Children.Remove(row.View);
            UpdateMovement();
            changed();
        }

        private void UpdateMovement()
        {
            for (var i = 0; i < rows.Count; i++)
                rows[i].SetPosition(i, rows.Count);
        }

        private void TogglePicker()
        {
            if (picker.Visibility == Visibility.Visible)
            {
                ClosePicker();
                return;
            }
            foreach (var (id, label) in choices(owner))
            {
                var option = new CombinationButton(id, "插入组合", () =>
                {
                    ClosePicker();
                    Add(new CycleCombinationReference(id, 1));
                });
                option.Update(label);
                picker.Children.Add(option.Control);
            }
            if (picker.Children.Count == 0)
            {
                var hint = owner.HasValue ? "暂无可插入的其他组合。" : "请先在组合库新建组合并添加模块。";
                picker.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryInk });
            }
            picker.Visibility = Visibility.Visible;
        }

        public void ClosePicker()
        {
            picker.Visibility = Visibility.Collapsed;
            picker.Children.Clear();
        }

        public void UpdateReferences(IReadOnlyDictionary<Guid, CombinationLabel> labels)
        {
            ClosePicker();
            foreach (var row in rows)
                if (row.Reference is { } reference && labels.TryGetValue(reference.Id, out var label)) reference.Update(label);
        }

        public bool TryRead(out IReadOnlyList<CycleBlock> blocks, out string error)
        {
            var result = new List<CycleBlock>();
            blocks = result;
            error = "";
            if (owner is null && !HasModules)
            {
                error = "添加工作、休息或组合以开始编排。";
                return false;
            }
            foreach (var row in rows)
            {
                if (!row.TryRead(out var block, out error)) return false;
                result.Add(block!);
            }
            return true;
        }

        public IEnumerable<string> EditValues()
        {
            yield return rows.Count.ToString(CultureInfo.InvariantCulture);
            foreach (var row in rows)
                foreach (var value in row.EditValues()) yield return value;
        }
    }

    private sealed class BlockEditor
    {
        public Border View { get; }
        public CombinationButton? Reference { get; }
        private readonly TextBox number = NumberBox();
        private readonly ComboBox? unit, activity;
        private readonly Button moveUp, moveDown;
        private readonly bool isWork;

        public BlockEditor(CycleBlock block, Action<Guid> select, Action changed, Action<BlockEditor, int> move, Action<BlockEditor> delete)
        {
            var body = new StackPanel();
            var controls = new WrapPanel();
            body.Children.Add(controls);
            isWork = block is CycleWork;
            var name = block switch { CycleWork => "工作", CycleRest => "休息", _ => "组合" };
            if (block is CycleCombinationReference reference)
            {
                Reference = new CombinationButton(reference.Id, "在组合库中打开", () => select(reference.Id));
                body.Children.Insert(0, Reference.Control);
                number.Text = reference.RepeatCount.ToString(CultureInfo.CurrentCulture);
                AutomationProperties.SetName(number, "引用重复次数");
                var repeat = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0) };
                repeat.Children.Add(InlineLabel("重复"));
                repeat.Children.Add(number);
                repeat.Children.Add(InlineLabel("次"));
                controls.Children.Add(repeat);
            }
            else
            {
                var section = InlineLabel(name);
                section.FontWeight = FontWeights.SemiBold;
                controls.Children.Add(section);
                AutomationProperties.SetName(number, name + "时长");
                controls.Children.Add(number);
                var seconds = block is CycleWork work ? work.Seconds : ((CycleRest)block).Seconds;
                var minutes = seconds % 60 == 0;
                number.Text = (minutes ? seconds / 60 : seconds).ToString(CultureInfo.CurrentCulture);
                unit = new ComboBox { Width = 48, ItemsSource = new[] { "分钟", "秒" }, SelectedIndex = minutes ? 0 : 1, Margin = new Thickness(0, 1, 5, 1) };
                controls.Children.Add(unit);
                AutomationProperties.SetName(unit, name + "时长单位");
                unit.SelectionChanged += (_, _) => changed();
                if (block is CycleRest rest)
                {
                    activity = new ComboBox { Width = 72, DisplayMemberPath = "Value", SelectedValuePath = "Key", ItemsSource = Actions, Margin = new Thickness(0, 1, 5, 1) };
                    activity.SelectedValue = rest.PromptId;
                    controls.Children.Add(activity);
                    AutomationProperties.SetName(activity, "休息方式");
                    activity.SelectionChanged += (_, _) => changed();
                }
            }
            number.TextChanged += (_, _) => changed();

            moveUp = Button("↑", () => move(this, -1));
            moveDown = Button("↓", () => move(this, 1));
            var remove = Button("×", () => delete(this));
            AutomationProperties.SetName(moveUp, "上移" + name);
            AutomationProperties.SetName(moveDown, "下移" + name);
            AutomationProperties.SetName(remove, "删除" + name);
            moveUp.ToolTip = "上移";
            moveDown.ToolTip = "下移";
            remove.ToolTip = "删除";
            var rowActions = new StackPanel { Orientation = Orientation.Horizontal };
            rowActions.Children.Add(moveUp);
            rowActions.Children.Add(moveDown);
            rowActions.Children.Add(remove);
            controls.Children.Add(rowActions);
            View = new Border
            {
                Child = body, Background = Reference is not null ? GroupColor : isWork ? WorkColor : RestColor,
                BorderBrush = Reference is not null ? GroupOutline : null,
                BorderThickness = Reference is not null ? new Thickness(1) : new Thickness(0),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 4, 3, 4), Margin = new Thickness(0, 0, 0, 5)
            };
        }

        public void SetPosition(int position, int count)
        {
            moveUp.IsEnabled = position > 0;
            moveDown.IsEnabled = position < count - 1;
        }

        public IEnumerable<string> EditValues()
        {
            yield return Reference is not null ? "link" : isWork ? "work" : "rest";
            yield return number.Text;
            if (Reference is { } reference) yield return reference.Id.ToString();
            else
            {
                yield return unit!.SelectedIndex.ToString(CultureInfo.InvariantCulture);
                if (!isWork) yield return activity!.SelectedValue as string ?? "";
            }
        }

        public bool TryRead(out CycleBlock? block, out string error)
        {
            block = null;
            error = "";
            if (Reference is { } reference)
            {
                if (!int.TryParse(number.Text, out var count) || count is < 1 or > CyclePlan.MaxRepeatCount)
                {
                    error = $"引用的重复次数须为 1–{CyclePlan.MaxRepeatCount}。";
                    return false;
                }
                block = new CycleCombinationReference(reference.Id, count);
                return true;
            }

            var limit = isWork ? 10800 : 1800;
            var secondsPerUnit = unit!.SelectedIndex == 0 ? 60 : 1;
            if (!decimal.TryParse(number.Text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var quantity) ||
                quantity <= 0 || quantity > limit / secondsPerUnit ||
                quantity * secondsPerUnit < 1 || decimal.Truncate(quantity * secondsPerUnit) != quantity * secondsPerUnit)
            {
                error = isWork ? "工作时长须相当于 1 秒至 180 分钟，精确到整秒。" : "休息时长须相当于 1 秒至 30 分钟，精确到整秒。";
                return false;
            }

            var seconds = (int)(quantity * secondsPerUnit);
            block = isWork ? new CycleWork(seconds) : new CycleRest(seconds, (string)activity!.SelectedValue);
            return true;
        }
    }
}
