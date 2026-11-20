using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal sealed partial class ResourcePlanningPanel : KModalScreen
{
    private const float ScrollbarGutter = 7;
    private const float WindowWidthShare = 0.572f;
    private const float WindowHeightShare = 0.546f;
    private const float TableWidthShare = 0.63f;
    private const float SourcesStart = 0.65f;
    private static readonly Color PanelColor = new(0.965f, 0.958f, 0.970f);
    private static readonly Color HeaderColor = new(0.46f, 0.24f, 0.36f);
    private static readonly Color ToolbarColor = new(0.17f, 0.19f, 0.25f);
    private static readonly Color ButtonColor = new(0.28f, 0.31f, 0.40f);
    private static readonly Color HeaderTextColor = new(0.98f, 0.96f, 0.98f);
    private static readonly Color RowColor = new(0.993f, 0.989f, 0.995f);
    private static readonly Color AlternateRowColor = new(0.943f, 0.935f, 0.950f);
    private static readonly Color SelectedColor = new(0.934f, 0.867f, 0.910f);
    private static readonly Color ListSurfaceColor = new(0.923f, 0.916f, 0.935f);
    private static readonly Color TextColor = new(0.17f, 0.17f, 0.21f);
    private static readonly Color SecondaryColor = new(0.34f, 0.34f, 0.40f);
    private static readonly Color SurplusColor = new(0.10f, 0.36f, 0.30f);
    private static readonly Color DeficitColor = new(0.52f, 0.22f, 0.19f);

    private enum View { Table, Graph, Picker, Ranch, Guide }
    private enum BalanceFilter { All, Shortfall, Surplus }

    private readonly Dictionary<ResourceKey, (Image Image, Color Background, Image Selection)> rowImages = new();
    private readonly Dictionary<string, (Sprite Sprite, Color Tint)> icons = new(StringComparer.Ordinal);
    private TMP_FontAsset font;
    private RectTransform panelBody;
    private Vector2 lastScreenSize;
    private Vector2 lastCanvasSize;
    private RectTransform tableView;
    private RectTransform graphView;
    private RectTransform table;
    private RectTransform tableHeader;
    private RectTransform details;
    private TextMeshProUGUI actualPeriodLabel;
    private TextMeshProUGUI worldLabel;
    private TextMeshProUGUI cycleLabel;
    private ToolTip snapshotTooltip;
    private TextMeshProUGUI detailTitle;
    private KButton worldButton;
    private KButton tableButton;
    private KButton graphButton;
    private KButton detailBack;
    private KButton averageButton;
    private bool showTableActual;
    private bool showSourceActual;
    private bool showGraphActual;
    private bool showAverages;
    private bool compactUi;
    private float TitleHeight => compactUi ? 20 : 28;
    private float ToolbarHeight => compactUi ? 22 : 35;
    private float ColumnHeight => compactUi ? 19 : 26;
    private float FilterHeight => compactUi ? 20 : 30;
    private float ButtonHeight => compactUi ? 16 : 27;
    private float BodyTextSize => compactUi ? 12 : 14;
    private float MinimumTextSize => compactUi ? 9 : 11;
    private View view;
    private BalanceFilter balanceFilter;
    private readonly Dictionary<BalanceFilter, KButton> filterButtons = new();
    private int worldId;
    private ResourceKey? selected;
    private WorldReport world;
    private ResourceKey[] resources = Array.Empty<ResourceKey>();

    internal static void Attach(PinnedResourcesPanel screen)
    {
        if (screen.headerButton == null || screen.headerButton.transform is not RectTransform header)
            throw new InvalidOperationException("The pinned resources header is missing.");
        if (header.GetComponent<ResourceBalanceEntryAnchor>() != null)
            return;

        var ui = GameScreenManager.Instance;
        if (ui == null || ui.ssOverlayCanvas == null || ui.ssOverlayCanvas.transform is not RectTransform overlay)
            throw new InvalidOperationException("The game overlay canvas is missing.");
        var nativeFont = screen.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(label => label.font != null)?.font;
        var entry = MakeNativeButton(overlay, ButtonColor, () => Open(nativeFont), "ResourceBalanceEntry");
        var entryRect = (RectTransform)entry.transform;
        entryRect.anchorMin = entryRect.anchorMax = overlay.pivot;
        entryRect.pivot = new Vector2(1, 0.5f);
        entryRect.sizeDelta = new Vector2(42, 25);
        CreateLabel(entryRect, Tr("收支", "Flow"), 13, TextAlignmentOptions.Center, HeaderTextColor, nativeFont);
        entry.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(Tr("理论资源收支", "Theoretical Resource Balance"));
        header.gameObject.AddComponent<ResourceBalanceEntryAnchor>().Initialize(header, overlay, entryRect);
    }

    private static void Open(TMP_FontAsset nativeFont)
    {
        RectTransform root = null;
        ResourcePlanningPanel panel = null;
        try
        {
            var ui = GameScreenManager.Instance;
            if (ui == null || ui.ssOverlayCanvas == null)
                throw new InvalidOperationException("The game overlay canvas is missing.");

            var native = AllResourcesScreen.Instance;
            if (native != null && native.IsScreenActive())
                native.Show(false);
            root = Rect("ResourceBalanceScreen", ui.ssOverlayCanvas.transform, Vector2.zero, Vector2.one);
            root.SetAsLastSibling();
            root.gameObject.SetActive(false);
            panel = root.gameObject.AddComponent<ResourcePlanningPanel>();
            panel.font = nativeFont;
            panel.Build(root);
            panel.Activate();
        }
        catch (Exception error)
        {
            if (panel != null && root.gameObject.activeSelf)
                panel.Deactivate();
            else if (root != null)
                Destroy(root.gameObject);
            UnityEngine.Debug.LogError("[OniResourceBalance] Could not open the resources panel. " + error);
        }
    }

    protected override void OnActivate()
    {
        base.OnActivate();
        ConsumeMouseScroll = true;
        worldId = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorldId : -1;
        LoadTrackedResources();
        Canvas.ForceUpdateCanvases();
        UpdatePanelLayout();
        PlannerSession.Refresh();
        Render();
        PlannerSession.ReportChanged += Render;
    }

    protected override void OnDeactivate()
    {
        PlannerSession.ReportChanged -= Render;
        base.OnDeactivate();
    }

    public override void OnKeyDown(KButtonEvent input)
    {
        if (!input.Consumed && input.IsAction(Action.Escape))
        {
            if (view == View.Picker)
            {
                input.Consumed = true;
                if (pickerSearch.isFocused)
                    pickerSearch.DeactivateInputField();
                else
                    FinishPicker(apply: false);
                return;
            }
            if (view == View.Graph && BackFromGraphNode())
            {
                input.Consumed = true;
                return;
            }
            if (view == View.Table && openedSource != null)
            {
                input.Consumed = true;
                CloseSource();
                return;
            }
            if (view is View.Ranch or View.Guide)
            {
                input.Consumed = true;
                SetView(View.Table);
                Render();
                return;
            }
        }
        base.OnKeyDown(input);
    }

    private void Build(RectTransform overlay)
    {
        compactUi = Screen.width < 1120 || Screen.height < 690 || overlay.rect.width * WindowWidthShare < 660;
        overlay.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.06f, 0.09f, 0.19f);

        var center = Vector2.one * 0.5f;
        var halfSize = new Vector2(WindowWidthShare, WindowHeightShare) / 2;
        var body = panelBody = Rect("Panel", overlay, center - halfSize, center + halfSize);
        body.gameObject.AddComponent<Image>().color = PanelColor;

        var bar = TopBand("TitleBar", body, TitleHeight);
        bar.gameObject.AddComponent<Image>().color = HeaderColor;
        var title = FitText(Label(bar, Tr("理论资源收支", "Resource Balance"), compactUi ? 13 : 16,
            color: HeaderTextColor), MinimumTextSize);
        title.rectTransform.offsetMin = new Vector2(7, 0);
        title.rectTransform.offsetMax = new Vector2(-TitleHeight - 2, 0);
        var close = (RectTransform)MakeButton(bar, "×", Deactivate, 0).transform;
        Place(close, new Vector2(1, 0), Vector2.one);
        close.offsetMin = new Vector2(-TitleHeight + 1, 1);
        close.offsetMax = new Vector2(-1, -1);

        var header = TopBand("Toolbar", body, ToolbarHeight, TitleHeight);
        header.gameObject.AddComponent<Image>().color = ToolbarColor;
        ConfigureControls(header, 4);
        worldButton = MakeButton(header, "", NextWorld, 103);
        worldButton.GetComponent<LayoutElement>().flexibleWidth = 1;
        worldLabel = worldButton.GetComponentInChildren<TextMeshProUGUI>(true);
        cycleLabel = FitText(Label(header, "", BodyTextSize, TextAlignmentOptions.Center, HeaderTextColor), MinimumTextSize);
        var cycleLayout = cycleLabel.gameObject.AddComponent<LayoutElement>();
        cycleLayout.preferredWidth = compactUi ? 47 : 78;
        cycleLayout.preferredHeight = ButtonHeight;
        cycleLabel.raycastTarget = true;
        snapshotTooltip = cycleLabel.gameObject.AddComponent<ToolTip>();
        MakeButton(header, Tr("刷新", "Refresh"), PlannerSession.Refresh, 68);
        pickerButton = MakeButton(header, "", TogglePicker, 107);
        pickerButtonLabel = pickerButton.GetComponentInChildren<TextMeshProUGUI>(true);
        tableButton = MakeButton(header, Tr("收支表", "Balance"), () => NavigateTo(View.Table), 83);
        graphButton = MakeButton(header, Tr("流向图", "Flow"), () => NavigateTo(View.Graph), 83);
        ranchButton = MakeButton(header, Tr("畜舍", "Ranch"), () => NavigateTo(View.Ranch), 66);
        guideButton = MakeButton(header, Tr("指南", "Guide"), () => NavigateTo(View.Guide), 60);

        var main = Rect("Views", body, Vector2.zero, Vector2.one);
        main.offsetMin = new Vector2(4, 4);
        main.offsetMax = new Vector2(-4, -TitleHeight - ToolbarHeight - 3);
        tableView = Rect("TableView", main, Vector2.zero, Vector2.one);
        var filters = TopBand("BalanceFilters", tableView, FilterHeight);
        ConfigureControls(filters);
        AddFilter(filters, BalanceFilter.All, width: 82);
        AddFilter(filters, BalanceFilter.Shortfall, width: 94);
        AddFilter(filters, BalanceFilter.Surplus, width: 94);
        averageButton = MakeButton(filters, Tr("近 10 周期", "10-cycle avg"), () =>
        {
            showAverages = !showAverages;
            RenderTable(resources.Where(IsTracked).ToArray());
        }, 108);
        averageButton.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(Tr(
            "切换实测的统计窗口。按最近 10 个已结束周期中的完整样本求均值，括号标有效周期数。",
            "Switch the measured window. Average complete samples from the last 10 ended cycles. Parentheses show the valid sample count."));
        actualPeriodLabel = FitText(Label(filters, "", BodyTextSize - 1, TextAlignmentOptions.Right, SecondaryColor), MinimumTextSize);
        actualPeriodLabel.margin = new Vector4(4, 0, 8, 0);
        FlexibleLabel(actualPeriodLabel.rectTransform);
        tableHeader = TopBand("Columns", tableView, ColumnHeight, FilterHeight, right: TableWidthShare);
        tableHeader.offsetMax = new Vector2(-ScrollbarGutter, -FilterHeight);
        var titleBand = TopBand("DetailHeading", tableView, ColumnHeight, FilterHeight, left: SourcesStart);
        detailTitle = FitText(Label(titleBand, Tr("来源明细", "Breakdown"), BodyTextSize), MinimumTextSize);
        detailTitle.margin = new Vector4(5, 0, 4, 0);
        detailBack = MakeButton(titleBand, Tr("‹ 返回", "‹ Back"), CloseSource, 0);
        var backRect = (RectTransform)detailBack.transform;
        backRect.anchorMax = Vector2.up;
        backRect.offsetMin = new Vector2(1, 1);
        backRect.offsetMax = new Vector2(compactUi ? 41 : 55, -1);
        detailBack.gameObject.SetActive(false);
        table = MakeScrollArea(tableView, "Resources", Vector2.zero, new Vector2(TableWidthShare, 1), FilterHeight + ColumnHeight);
        details = MakeScrollArea(tableView, "Sources", new Vector2(SourcesStart, 0), Vector2.one, FilterHeight + ColumnHeight);
        BuildGraph(main);
        BuildPicker(main);
        BuildRanchView(main);
        BuildGuide(main);
    }

    private bool UpdatePanelLayout()
    {
        var pixels = new Vector2(Screen.width, Screen.height);
        var canvasSize = ((RectTransform)transform).rect.size;
        if (pixels == lastScreenSize && canvasSize == lastCanvasSize || pixels.x <= 0 || pixels.y <= 0 ||
            canvasSize.x <= 0 || canvasSize.y <= 0)
            return false;
        lastScreenSize = pixels;
        lastCanvasSize = canvasSize;

        float width = pixels.x * WindowWidthShare;
        float contentWidth = panelBody.rect.width - 8;
        showTableActual = width >= 640 && contentWidth * TableWidthShare - ScrollbarGutter >= 410;
        showSourceActual = width >= 690 && contentWidth * (1 - SourcesStart) - ScrollbarGutter >= 227;
        showGraphActual = width >= 540 && contentWidth >= 520;
        return true;
    }

    private void AddFilter(RectTransform bar, BalanceFilter filter, float width)
    {
        var button = MakeButton(bar, "", () =>
        {
            balanceFilter = filter;
            table.anchoredPosition = Vector2.zero;
            RenderTable(resources.Where(IsTracked).ToArray());
        }, width);
        filterButtons.Add(filter, button);
        if (filter == BalanceFilter.Shortfall)
            button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(Tr(
                "按可用库存预计耗尽的先后排序。无法估算的资源列在后面。",
                "Sorted by estimated time to deplete available stock. Resources with no estimate follow."));
    }

    private static string FilterName(BalanceFilter filter) => filter switch
    {
        BalanceFilter.Shortfall => Tr("缺口", "Deficits"),
        BalanceFilter.Surplus => Tr("盈余", "Surpluses"),
        _ => Tr("全部", "All")
    };

    private bool MatchesFilter(ResourceKey key, BalanceFilter filter)
    {
        var row = world.Resources[key];
        if (row.HasUnknownProduction)
            return filter == BalanceFilter.All;
        double net = row.Totals.Net;
        return filter switch
        {
            BalanceFilter.Shortfall => net <= -ResourceRow.NetTolerance,
            BalanceFilter.Surplus => net >= ResourceRow.NetTolerance,
            _ => true
        };
    }

    private void NextWorld()
    {
        var worlds = PlannerSession.Current?.Worlds;
        if (worlds == null || worlds.Count < 2)
            return;
        int current = worlds.FindIndex(candidate => candidate.Id == worldId);
        worldId = worlds[(current + 1) % worlds.Count].Id;
        table.anchoredPosition = details.anchoredPosition = pickerList.anchoredPosition = Vector2.zero;
        ResetGraphViewport();
        openedSource = null;
        ranchAnimals.anchoredPosition = Vector2.zero;
        Render();
    }

    private void Render()
    {
        var report = PlannerSession.Current;
        world = report?.Worlds.Find(candidate => candidate.Id == worldId) ?? report?.Worlds.FirstOrDefault();
        if (world != null)
            worldId = world.Id;

        worldButton.isInteractable = report?.Worlds.Count > 1;
        worldLabel.text = world == null ? "—" : world.Name + (worldButton.isInteractable ? "  ›" : "");
        string cycle = report == null ? "—" : (report.Cycle + 1).ToString();
        cycleLabel.text = Tr("周期 ", "Cycle ") + cycle;
        snapshotTooltip.SetSimpleTooltip(report == null ? Tr("等待统计", "Awaiting calculation") :
            Tr($"理论快照 · 周期 {cycle} 第 {report.ElapsedSeconds:0} 秒。打开面板、周期结转或按刷新时重算。",
               $"Theory snapshot · cycle {cycle}, second {report.ElapsedSeconds:0}. Recalculated on opening, rollover or Refresh."));
        resources = world?.Resources.Keys.OrderBy(ResourceOrder)
            .ThenBy(UnitName).ToArray() ?? Array.Empty<ResourceKey>();
        SetView(view);
        if (view == View.Picker)
            RenderPicker(preservePosition: true);
        else if (view == View.Graph)
            DrawGraph(resources.Where(IsTracked).ToArray());
        else if (view == View.Ranch)
            RenderRanches();
        else if (view == View.Guide)
            UpdateGuide();
        else
            RenderTable(resources.Where(IsTracked).ToArray());
    }

    private static int ResourceOrder(ResourceKey key)
    {
        int index = Array.IndexOf(ResourceKey.Defaults, key);
        return index >= 0 ? index : key == ResourceKey.FoodEnergy ? ResourceKey.Defaults.Length : ResourceKey.Defaults.Length + 1;
    }

    private void RenderTable(ResourceKey[] tracked)
    {
        Clear(tableHeader, preservePosition: true);
        AddColumns(tableHeader, null, Tr("资源 / 周期", "Resource / cycle"), Tr("理论产出", "Est. out"),
            Tr("理论消耗", "Est. in"), Tr("理论净额", "Est. net"), SecondaryColor,
            showAverages ? Tr("均值产出", "Avg. out") : Tr("实测产出", "Actual out"),
            showAverages ? Tr("均值消耗", "Avg. in") : Tr("实测消耗", "Actual in"));
        averageButton.bgImage.color = showAverages ? HeaderColor : ButtonColor;
        actualPeriodLabel.gameObject.SetActive(showTableActual);
        if (showTableActual)
            actualPeriodLabel.text = showAverages
                ? Tr("仅已计量 · 近 10 周期 · 括号为有效样本", "Metered only · last 10 cycles · valid samples in parentheses")
                : Tr("实测仅已计量 · ", "Measured flows · ") + MeasurementCaption();
        Clear(table, preservePosition: true);
        rowImages.Clear();
        foreach (var entry in filterButtons)
        {
            int count = tracked.Count(key => MatchesFilter(key, entry.Key));
            var button = entry.Value;
            button.GetComponentInChildren<TextMeshProUGUI>(true).text = $"{FilterName(entry.Key)} ({count})";
            button.bgImage.color = entry.Key == balanceFilter ? HeaderColor : ButtonColor;
        }
        if (PlannerSession.Failure != null)
            AddMessage(table, PlannerSession.Failure);
        var visible = tracked.Where(key => MatchesFilter(key, balanceFilter));
        if (balanceFilter == BalanceFilter.Shortfall && world != null)
            visible = visible.OrderBy(key => world.Resources[key].DeficitRunway ?? double.PositiveInfinity);
        var rows = visible.ToArray();
        if (rows.Length == 0 && PlannerSession.Failure == null)
        {
            string message = tracked.Length > 0
                ? Tr("已选资源中没有符合此筛选的项目。", "No checked resources match this filter.")
                : resources.Length == 0
                    ? Tr("此星球暂未找到可统计的配方。", "No supported recipes on this world.")
                    : Tr("打开「选择资源」，勾选要跟踪的资源。", "Choose the resources you want to track.");
            AddMessage(table, message, SecondaryColor);
        }
        for (int index = 0; index < rows.Length; index++)
        {
            ResourceKey key = rows[index];
            ResourceRow data = world.Resources[key];
            Color background = index % 2 == 0 ? RowColor : AlternateRowColor;
            var button = MakeButton(table, null, () => ShowDetails(key), 0, background);
            var measured = world.MeasuredTotals(key);
            string tooltip = TableRowTooltip(key, data, measured.Produced, measured.Consumed);
            button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(tooltip);
            var rect = (RectTransform)button.transform;
            var rail = Rect("Selection", rect, Vector2.zero, Vector2.up);
            rail.offsetMin = new Vector2(0, 2);
            rail.offsetMax = new Vector2(3, -2);
            var highlight = rail.gameObject.AddComponent<Image>();
            highlight.color = HeaderColor;
            highlight.raycastTarget = false;
            rowImages.Add(key, (button.bgImage, background, highlight));
            bool optionsOnly = data.HasOptions && data.Totals.Produced == 0 && data.Totals.Consumed == 0;
            bool unknownOutput = data.HasUnknownProduction && data.Totals.Produced == 0;
            bool hasNotes = data.HasOptions || data.HasUnknownProduction || data.HasAveragedOutput;
            AddColumns(rect, key.Id, UnitName(key) + (hasNotes ? " *" : ""),
                optionsOnly || unknownOutput ? "—" : Number(data.Totals.Produced),
                optionsOnly ? "—" : Number(data.Totals.Consumed),
                optionsOnly || data.HasUnknownProduction ? "—" : Number(data.Totals.Net, true),
                data.HasUnknownProduction ? SecondaryColor :
                    data.Totals.Net > 0 ? SurplusColor : data.Totals.Net < 0 ? DeficitColor : TextColor,
                showAverages ? AverageValue(world.Average(key, true)) : MeasuredValue(measured.Produced),
                showAverages ? AverageValue(world.Average(key, false)) : MeasuredValue(measured.Consumed));
        }
        ShowDetails(selected.HasValue && rows.Contains(selected.Value) ? selected : rows.Length > 0 ? rows[0] : null);
    }

    private void ShowDetails(ResourceKey? key)
    {
        Clear(details, preservePosition: selected == key);
        if (selected != key)
            openedSource = null;
        selected = key;
        foreach (var pair in rowImages)
        {
            pair.Value.Image.color = pair.Key == key ? SelectedColor : pair.Value.Background;
            pair.Value.Selection.enabled = pair.Key == key;
        }

        if (!key.HasValue || world == null || !world.Resources.TryGetValue(key.Value, out var row))
        {
            SetSourceHeading(Tr("来源明细", "Breakdown"), false);
            return;
        }
        ShowSourcePane(key.Value, row);
    }

    private void ShowInventory(ResourceKey key, ResourceRow row)
    {
        if (row.AvailableStock is not double stock)
            return;

        string summary = Tr("库存 ", "Stock ") + Number(stock) + "\u00a0" + UnitSymbol(key.Unit);
        if (row.DeficitRunway is double cycles)
        {
            string runway = cycles is > 0 and < 0.01
                ? Tr("不足 0.01 周期", "under 0.01 cycles")
                : Number(cycles) + Tr("\u00a0周期", "\u00a0cycles");
            summary += (row.HasAveragedOutput ? Tr(" · 按均值约撑 ", " · Covers at average flows ") :
                Tr(" · 可撑 ", " · Covers ")) + runway;
        }
        AddMessage(details, summary);
    }

    private static string ComparisonAmount(FlowUnit unit, double amount) => unit switch
    {
        FlowUnit.Kilojoules => FlowDetail(unit, amount),
        FlowUnit.Kilograms => Number(amount) + "\u00a0kg (" + (amount / PlanningMath.SecondsPerCycle).ToString("0.####") + "\u00a0kg/s)",
        _ => Number(amount) + "\u00a0" + UnitSymbol(unit)
    };

    private void AddColumns(RectTransform row, string icon, string name, string produced, string consumed, string net,
        Color netColor, string actualProduced, string actualConsumed)
    {
        float nameWidth = showTableActual ? 0.30f : 0.43f;
        float col = (1 - nameWidth) / (showTableActual ? 5 : 3);
        var nameCell = Rect("Name", row, Vector2.zero, new Vector2(nameWidth, 1));
        float iconSize = compactUi ? 12 : 18;
        FitText(Label(WithIcon(nameCell, icon, iconSize), name, BodyTextSize), MinimumTextSize);
        AddValue(row, nameWidth, nameWidth + col, produced, TextColor);
        AddValue(row, nameWidth + col, nameWidth + col * 2, consumed, TextColor);
        AddValue(row, nameWidth + col * 2, nameWidth + col * 3, net, netColor);
        if (showTableActual)
        {
            AddValue(row, nameWidth + col * 3, nameWidth + col * 4, actualProduced, SecondaryColor);
            AddValue(row, nameWidth + col * 4, 1, actualConsumed, SecondaryColor);
        }
    }

    private void AddValue(RectTransform row, float left, float right, string value, Color color)
    {
        var label = FitText(Label(row, value, BodyTextSize, TextAlignmentOptions.Right, color), MinimumTextSize);
        Place(label.rectTransform, new Vector2(left, 0), new Vector2(right, 1));
        label.margin = new Vector4(2, 0, 5, 0);
    }

    private string TableRowTooltip(ResourceKey key, ResourceRow row, MeasuredQuantity produced, MeasuredQuantity consumed)
    {
        var lines = new List<string> { UnitName(key) };
        if (key == ResourceKey.FoodEnergy)
            lines.Add(Tr("估算包含采收、加工与口粮。", "The estimate includes harvest, fabrication and food demand."));
        if (row.HasOptions)
            lines.Add(Tr("* 含备选方案，点来源查看。", "* Includes alternatives. Select a source to inspect."));
        if (row.HasUnknownProduction)
            lines.Add(Tr("* 有产出未量化。产出栏只含已知项，净额待确认。", "* Some output is unquantified. Output shows known sources; net is undetermined."));
        if (row.HasAveragedOutput)
            lines.Add(Tr("* 理论含跨周期均值。近期供给请查来源的阶段或分段预测。",
                "* Theory includes values averaged across cycles. Check sources for near-term phase or forecast details."));
        lines.Add(Tr("实测覆盖已计量建筑、复制人呼吸和畜舍产蛋。", "Actuals cover metered buildings, duplicant breathing and eggs laid in stables."));
        if (showAverages)
            lines.Add(AverageSummary(key));
        else if (produced.Amount.HasValue || consumed.Amount.HasValue)
        {
            lines.Add(MeasurementCaption());
            lines.Add(DirectionalText(MeasuredValue(produced), MeasuredValue(consumed)));
            if (produced.Partial || consumed.Partial)
                lines.Add(Tr("† 部分覆盖", "† Partial coverage"));
        }
        return string.Join("\n", lines);
    }

    private string AverageSummary(ResourceKey key) => Tr("近 10 周期 · 仅已计量\n", "Last 10 cycles · metered only\n") +
        DirectionalText(AverageValue(world.Average(key, true)), AverageValue(world.Average(key, false)));

    private static string AverageValue(ResourceAverage mean) => mean.Amount is double amount
        ? CompactNumber(amount) + $" ({mean.Samples}/{CycleMeasurements.AverageCycles})" : "—";

    private void AddMessage(RectTransform parent, string text, Color? color = null, string icon = null)
    {
        var message = Label(parent, text, BodyTextSize, TextAlignmentOptions.MidlineLeft, color);
        message.textWrappingMode = TextWrappingModes.Normal;
        message.lineSpacing = 1;
        bool hasIcon = PutIcon(message.rectTransform, icon, compactUi ? 14 : 18, 5);
        message.margin = new Vector4(hasIcon ? (compactUi ? 24 : 28) : 5, 2, 5, 2);
        message.gameObject.AddComponent<LayoutElement>().minHeight = ButtonHeight + 3 +
            BodyTextSize * text.Count(character => character == '\n');
    }

    private KButton MakeButton(Transform parent, string text, System.Action click, float width, Color? color = null)
    {
        var button = MakeNativeButton(parent, color ?? ButtonColor, click, "Button");
        var rect = (RectTransform)button.transform;
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.minHeight = ButtonHeight;
        layout.preferredHeight = ButtonHeight;
        if (width > 0)
            layout.preferredWidth = width * (compactUi ? 0.58f : 0.94f);
        if (text != null)
        {
            var caption = FitText(Label(rect, text, BodyTextSize, TextAlignmentOptions.Center, HeaderTextColor), MinimumTextSize);
            caption.margin = new Vector4(2, 0, 2, 0);
        }
        return button;
    }

    private static void ConfigureControls(RectTransform parent, int padding = 2)
    {
        var layout = parent.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(padding, padding, 0, 0);
        layout.spacing = 3;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
    }

    private void FlexibleLabel(RectTransform label)
    {
        var layout = label.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = 0;
        layout.flexibleWidth = 1;
        layout.preferredHeight = ButtonHeight;
    }

    private static KButton MakeNativeButton(Transform parent, Color background, System.Action click, string name)
    {
        var root = Rect(name, parent, Vector2.zero, Vector2.one).gameObject;
        root.SetActive(false);
        var image = root.AddComponent<KImage>();
        image.color = background;
        image.raycastTarget = true;
        var button = root.AddComponent<KButton>();
        button.bgImage = image;
        button.additionalKImages = Array.Empty<KImage>();
        // Native pointer handlers require a sound player even for silent buttons.
        button.soundPlayer = new ButtonSoundPlayer { Enabled = false };
        button.onClick += click;
        root.SetActive(true);
        return button;
    }

    private TextMeshProUGUI Label(Transform parent, string value, float size,
        TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, Color? color = null) =>
        CreateLabel(parent, value, size, align, color ?? TextColor, font);

    private static TextMeshProUGUI CreateLabel(Transform parent, string value, float size, TextAlignmentOptions align,
        Color color, TMP_FontAsset font)
    {
        var rect = Rect("Text", parent, Vector2.zero, Vector2.one);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null)
            label.font = font;
        label.fontSize = size;
        label.alignment = align;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.color = color;
        label.richText = false;
        label.raycastTarget = false;
        label.text = value;
        return label;
    }

    private static TextMeshProUGUI FitText(TextMeshProUGUI label, float minSize = 11)
    {
        label.fontSizeMin = minSize;
        label.fontSizeMax = label.fontSize;
        label.enableAutoSizing = true;
        return label;
    }

    private RectTransform WithIcon(RectTransform row, string id, float size)
    {
        var labels = Rect("Labels", row, Vector2.zero, Vector2.one);
        PutIcon(row, id, size, 7);
        labels.offsetMin = new Vector2(string.IsNullOrEmpty(id) ? 8 : size + 13, 0);
        labels.offsetMax = new Vector2(-4, 0);
        return labels;
    }

    private bool PutIcon(RectTransform row, string id, float size, float inset)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        if (!icons.TryGetValue(id, out var visual))
        {
            visual = FindIcon(id);
            icons.Add(id, visual);
        }
        if (visual.Sprite == null)
            return false;

        var icon = Rect("GameIcon", row, new Vector2(0, 0.5f), new Vector2(0, 0.5f));
        icon.pivot = new Vector2(0, 0.5f);
        icon.anchoredPosition = new Vector2(inset, 0);
        icon.sizeDelta = new Vector2(size, size);
        var picture = icon.gameObject.AddComponent<Image>();
        picture.sprite = visual.Sprite;
        picture.color = visual.Tint;
        picture.preserveAspect = true;
        picture.raycastTarget = false;
        return true;
    }

    private static (Sprite Sprite, Color Tint) FindIcon(string id)
    {
        try
        {
            if (id == ResourceKey.Electricity.Id)
                return (Assets.GetSprite("overlay_power"), Color.white);
            Sprite unknown = Assets.GetSprite(Def.DEFAULT_SPRITE);
            var building = Assets.GetBuildingDef(id);
            if (building?.AnimFiles?.Length > 0)
            {
                // The construction menu requests the ui animation, not the default building animation.
                Sprite sprite = building.GetUISprite("ui", false);
                if (sprite != null && sprite != unknown)
                    return (sprite, Color.white);
            }

            if (id == CompostConfig.COMPOST_TAG.ToString())
                id = SimHashes.ToxicSand.CreateTag().ToString();
            object target = id == ResourceKey.FoodEnergy.Id ? (object)Db.Get().Amounts.Calories.Id : new Tag(id);
            var visual = Def.GetUISprite(target, "ui", false);
            if (visual?.first != null && visual.first != unknown && visual.second.a > 0)
                return (visual.first, visual.second);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogWarning("[OniResourceBalance] Could not resolve icon for " + id + ": " + error.Message);
        }
        return (null, Color.white);
    }

    private static RectTransform MakeScrollArea(Transform parent, string name, Vector2 min, Vector2 max,
        float top = 0, bool fitRows = true, bool horizontal = false, System.Action<PointerEventData> onWheel = null)
    {
        var rect = Rect(name, parent, min, max);
        rect.offsetMax = new Vector2(0, -top);
        rect.gameObject.AddComponent<Image>().color = ListSurfaceColor;
        KScrollRect scroll;
        if (onWheel == null)
            scroll = rect.gameObject.AddComponent<KScrollRect>();
        else
        {
            var graph = rect.gameObject.AddComponent<FlowDiagramScroll>();
            graph.OnWheel = onWheel;
            scroll = graph;
        }
        scroll.horizontal = horizontal;
        scroll.allowHorizontalScrollWheel = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        var viewport = Rect("Viewport", rect, Vector2.zero, Vector2.one);
        viewport.offsetMin = new Vector2(0, horizontal ? ScrollbarGutter : 0);
        viewport.offsetMax = new Vector2(-ScrollbarGutter, 0);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = Rect("Content", viewport, Vector2.up, horizontal ? Vector2.up : Vector2.one);
        content.pivot = new Vector2(horizontal ? 0 : 0.5f, 1);
        if (fitRows)
        {
            var list = content.gameObject.AddComponent<VerticalLayoutGroup>();
            list.childControlHeight = true;
            list.childControlWidth = true;
            list.childForceExpandHeight = false;
            list.childForceExpandWidth = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.verticalScrollbar = MakeScrollbar(rect, false);
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        if (horizontal)
        {
            scroll.horizontalScrollbar = MakeScrollbar(rect, true);
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }
        return content;
    }

    private static Scrollbar MakeScrollbar(RectTransform parent, bool horizontal)
    {
        var track = horizontal
            ? Rect("HorizontalScrollbar", parent, Vector2.zero, Vector2.right)
            : Rect("VerticalScrollbar", parent, Vector2.right, Vector2.one);
        track.offsetMin = horizontal ? new Vector2(2, 1) : new Vector2(-ScrollbarGutter + 1, 2);
        track.offsetMax = horizontal ? new Vector2(-ScrollbarGutter, ScrollbarGutter - 1) : new Vector2(-1, -2);
        track.gameObject.AddComponent<Image>().color = new Color(0.80f, 0.80f, 0.83f);
        var slide = Rect("SlidingArea", track, Vector2.zero, Vector2.one);
        slide.offsetMin = Vector2.one;
        slide.offsetMax = -Vector2.one;
        var handle = Rect("Handle", slide, Vector2.zero, Vector2.one);
        var handleImage = handle.gameObject.AddComponent<Image>();
        handleImage.color = ButtonColor;
        var bar = track.gameObject.AddComponent<Scrollbar>();
        bar.direction = horizontal ? Scrollbar.Direction.LeftToRight : Scrollbar.Direction.BottomToTop;
        bar.handleRect = handle;
        bar.targetGraphic = handleImage;
        return bar;
    }

    private static RectTransform TopBand(string name, Transform parent, float height, float top = 0,
        float left = 0, float right = 1)
    {
        var band = Rect(name, parent, new Vector2(left, 1), new Vector2(right, 1));
        band.offsetMin = new Vector2(0, -top - height);
        band.offsetMax = new Vector2(0, -top);
        return band;
    }

    private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        Place(rect, min, max);
        return rect;
    }

    private static void Place(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void Clear(RectTransform list, bool preservePosition = false)
    {
        for (int index = list.childCount - 1; index >= 0; index--)
        {
            var child = list.GetChild(index);
            child.gameObject.SetActive(false);
            child.SetParent(null, false);
            Destroy(child.gameObject);
        }
        if (!preservePosition)
            list.anchoredPosition = Vector2.zero;
    }

    private static string UnitName(ResourceKey key) => GameText.Label(key.Id) + " (" + UnitSymbol(key.Unit) + ")";

    private static string UnitSymbol(FlowUnit unit) => unit switch
    {
        FlowUnit.Kilograms => "kg",
        FlowUnit.Kilocalories => "kcal",
        FlowUnit.Kilojoules => "kJ",
        FlowUnit.PlantGrowth => Tr("株成熟量", "full growths"),
        _ => Tr("份", "units")
    };

    private static string FlowDetail(FlowUnit unit, double amount) => unit == FlowUnit.Kilojoules
        ? Number(amount) + "\u00a0kJ (" + Number(PlanningMath.AverageWatts(amount)) + "\u00a0W)"
        : Number(amount);

    private static string Number(double value, bool withSign = false) =>
        withSign ? value.ToString("+#,##0.###;-#,##0.###;0") : value.ToString("#,##0.###");
}

internal sealed class ResourceBalanceEntryAnchor : MonoBehaviour
{
    private RectTransform header;
    private RectTransform overlay;
    private RectTransform button;
    private Canvas headerCanvas;
    private Canvas overlayCanvas;
    private CanvasGroup headerVisibility;

    internal void Initialize(RectTransform header, RectTransform overlay, RectTransform button)
    {
        this.header = header;
        this.overlay = overlay;
        this.button = button;
        headerCanvas = header.GetComponentInParent<Canvas>();
        overlayCanvas = overlay.GetComponent<Canvas>();
        headerVisibility = header.GetComponentInParent<CanvasGroup>();
        LateUpdate();
    }

    private void LateUpdate()
    {
        if (button == null || overlay == null || headerCanvas == null)
            return;

        bool visible = headerCanvas.isActiveAndEnabled &&
            (headerVisibility == null || (headerVisibility.alpha > 0 && headerVisibility.blocksRaycasts));
        if (button.gameObject.activeSelf != visible)
            button.gameObject.SetActive(visible);
        if (!visible)
            return;

        Vector3 edge = header.TransformPoint(new Vector2(header.rect.xMin, header.rect.center.y));
        Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(UiCamera(headerCanvas), edge);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(overlay, screenPoint, UiCamera(overlayCanvas), out var anchor))
        {
            Vector2 position = anchor - new Vector2(5, 0);
            if (button.anchoredPosition != position)
                button.anchoredPosition = position;
        }
    }

    private void OnDisable()
    {
        if (button != null)
            button.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (button != null)
            Destroy(button.gameObject);
    }

    private static Camera UiCamera(Canvas canvas) =>
        canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
}
