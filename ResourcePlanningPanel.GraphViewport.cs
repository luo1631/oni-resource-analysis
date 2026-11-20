using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal sealed partial class ResourcePlanningPanel
{
    private const float MinimumGraphZoom = 0.04f;
    private const float MaximumGraphZoom = 2f;
    private float InspectorHeaderHeight => compactUi ? 20 : 30;

    private KScrollRect graphScroll;
    private RectTransform graphSurface;
    private RectTransform graphContent;
    private RectTransform graphDock;
    private RectTransform graphFlowList;
    private TextMeshProUGUI graphEmpty;
    private TextMeshProUGUI graphLegend;
    private TextMeshProUGUI graphZoomLabel;
    private TextMeshProUGUI graphNodeLabel;
    private TextMeshProUGUI graphDetailsLabel;
    private KButton graphDetailsButton;
    private KButton graphUtilitiesButton;
    private KButton zoomInButton;
    private KButton zoomOutButton;
    private float graphZoom = 1;
    private Vector2 graphViewportSize;
    private bool hasGraphLayout;
    private bool inspectorOpen;
    private bool includeGraphUtilities;

    private static string GraphLegend => Tr("理论流量 / 周期 · 虚线为备选", "Theory / cycle · Dashed: options");

    private void BuildGraph(RectTransform parent)
    {
        graphView = Rect("GraphView", parent, Vector2.zero, Vector2.one);
        graphView.gameObject.SetActive(false);
        var controls = TopBand("GraphTools", graphView, FilterHeight);
        ConfigureControls(controls);
        graphLegend = FitText(Label(controls, GraphLegend, BodyTextSize - 1, color: SecondaryColor), MinimumTextSize);
        FlexibleLabel(graphLegend.rectTransform);
        graphUtilitiesButton = MakeButton(controls, Tr("氧气/电力", "Oxygen/Power"), () =>
        {
            includeGraphUtilities = !includeGraphUtilities;
            hasGraphLayout = false;
            Render();
        }, 118);
        graphUtilitiesButton.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(Tr(
            "在图中显示或隐藏已勾选的氧气、电力和对应连线。",
            "Show or hide tracked oxygen, electricity, and their links in the graph."));
        zoomOutButton = MakeButton(controls, "−", () => ZoomGraphBy(1 / 1.25f), 28);
        graphZoomLabel = FitText(Label(controls, "", BodyTextSize - 1, TextAlignmentOptions.Center, SecondaryColor), MinimumTextSize);
        graphZoomLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = compactUi ? 32 : 47;
        zoomInButton = MakeButton(controls, "+", () => ZoomGraphBy(1.25f), 28);
        var fit = MakeButton(controls, Tr("适合视窗", "Fit view"), FitGraph, 89);
        fit.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(Tr("显示整张图。也可用滚轮缩放，按住空白处拖动。",
            "Show the whole graph. Use the wheel to zoom and drag the canvas to pan."));

        graphSurface = MakeScrollArea(graphView, "FlowDiagram", Vector2.zero, Vector2.one, FilterHeight,
            fitRows: false, horizontal: true, onWheel: ZoomGraphWithWheel);
        graphScroll = graphSurface.GetComponentInParent<KScrollRect>(true);
        graphContent = Rect("Network", graphSurface, Vector2.up, Vector2.up);
        graphContent.pivot = Vector2.up;
        graphEmpty = Label(graphScroll.viewport, Tr("选择资源后显示流向。", "Choose resources to show flows."),
            12, TextAlignmentOptions.Center, SecondaryColor);
        graphEmpty.gameObject.SetActive(false);

        graphDock = Rect("GraphInspector", graphView, Vector2.zero, Vector2.right);
        graphDock.gameObject.AddComponent<Image>().color = RowColor;
        var actions = TopBand("InspectorActions", graphDock, InspectorHeaderHeight);
        ConfigureControls(actions, 5);
        graphNodeLabel = FitText(Label(actions, "", BodyTextSize), MinimumTextSize);
        FlexibleLabel(graphNodeLabel.rectTransform);
        graphDetailsButton = MakeButton(actions, "", ToggleInspector, 103);
        graphDetailsLabel = graphDetailsButton.GetComponentInChildren<TextMeshProUGUI>(true);
        MakeButton(actions, Tr("定位", "Center"), () => CenterGraphOn(graphFocus), 63);
        MakeButton(actions, Tr("取消高亮", "Clear focus"), () => SelectGraphNode(null), 104);
        graphFlowList = MakeScrollArea(graphDock, "GraphFlows", Vector2.zero, Vector2.one, InspectorHeaderHeight);
        graphDock.gameObject.SetActive(false);
    }

    private void ResetGraphViewport()
    {
        graphFocus = null;
        inspectorOpen = false;
        hasGraphLayout = false;
    }

    private void LateUpdate()
    {
        if (UpdatePanelLayout())
            Render();
        if (view == View.Graph && hasGraphLayout && graphScroll.viewport.rect.size != graphViewportSize)
            LayoutGraphAt(graphZoom, GraphPointAt(Vector2.zero), Vector2.zero);
    }

    private Vector2 GraphPointAt(Vector2 viewportPoint) => hasGraphLayout
        ? (viewportPoint - graphSurface.anchoredPosition - graphContent.anchoredPosition) / graphZoom
        : Vector2.zero;

    private float GraphFitZoom()
    {
        Vector2 size = graphScroll.viewport.rect.size;
        Vector2 drawing = graphContent.sizeDelta;
        return Mathf.Clamp(Mathf.Min(size.x / Mathf.Max(1, drawing.x), size.y / Mathf.Max(1, drawing.y)), MinimumGraphZoom, 1);
    }

    private void FitGraph()
    {
        LayoutGraphAt(GraphFitZoom(), Vector2.zero, Vector2.zero);
    }

    private void ZoomGraphBy(float multiplier)
    {
        Vector2 middle = ViewportMiddle();
        LayoutGraphAt(graphZoom * multiplier, GraphPointAt(middle), middle);
    }

    private void ZoomGraphWithWheel(PointerEventData input)
    {
        if (Mathf.Approximately(input.scrollDelta.y, 0))
            return;
        var viewport = graphScroll.viewport;
        var canvas = viewport.GetComponentInParent<Canvas>()?.rootCanvas;
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null : input.enterEventCamera ?? canvas.worldCamera;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, input.position, camera, out var pivot))
        {
            pivot -= new Vector2(viewport.rect.xMin, viewport.rect.yMax);
            float steps = Mathf.Clamp(input.scrollDelta.y, -3, 3);
            LayoutGraphAt(graphZoom * Mathf.Pow(1.22f, steps), GraphPointAt(pivot), pivot);
            input.Use();
        }
    }

    private Vector2 ViewportMiddle()
    {
        Vector2 size = graphScroll.viewport.rect.size;
        return new Vector2(size.x, -size.y) / 2;
    }

    private void LayoutGraphAt(float zoom, Vector2 graphPoint, Vector2 viewportPoint)
    {
        graphViewportSize = graphScroll.viewport.rect.size;
        if (graphViewportSize.x <= 0 || graphViewportSize.y <= 0)
            return;

        graphZoom = Mathf.Clamp(zoom, MinimumGraphZoom, MaximumGraphZoom);
        Vector2 drawing = graphContent.sizeDelta * graphZoom;
        Vector2 surface = Vector2.Max(drawing, graphViewportSize);
        graphSurface.sizeDelta = surface;
        graphContent.localScale = new Vector3(graphZoom, graphZoom, 1);
        Vector2 padding = (surface - drawing) / 2;
        graphContent.anchoredPosition = new Vector2(padding.x, -padding.y);

        Vector2 target = viewportPoint - graphPoint * graphZoom - graphContent.anchoredPosition;
        Vector2 travel = surface - graphViewportSize;
        graphScroll.StopMovement();
        graphSurface.anchoredPosition = new Vector2(Mathf.Clamp(target.x, -travel.x, 0), Mathf.Clamp(target.y, 0, travel.y));
        graphZoomLabel.text = Mathf.RoundToInt(graphZoom * 100) + "%";
        zoomOutButton.isInteractable = graphZoom > MinimumGraphZoom;
        zoomInButton.isInteractable = graphZoom < MaximumGraphZoom;
        graphQuantities.gameObject.SetActive(graphZoom >= 0.57f);
    }

    private void SelectGraphNode(ChainNode node)
    {
        Vector2 origin = GraphPointAt(Vector2.zero);
        graphFocus = node;
        RenderGraphSelection();
        LayoutGraphAt(graphZoom, origin, Vector2.zero);
    }

    private void RenderGraphSelection(bool preservePosition = false)
    {
        foreach (var entry in graphCards)
        {
            bool focused = entry.Key == graphFocus;
            entry.Value.Border.enabled = focused;
            entry.Value.Opacity.alpha = graphFocus == null || focused ||
                entry.Key.Links.Any(link => link.Other(entry.Key) == graphFocus) ? 1f : 0.55f;
        }
        DrawChainLinks(graphConnections, graphQuantities, graphLinks, graphFocus);
        RenderGraphInspector(preservePosition);
    }

    private void ToggleInspector()
    {
        Vector2 origin = GraphPointAt(Vector2.zero);
        inspectorOpen = !inspectorOpen;
        RenderGraphInspector(preservePosition: true);
        LayoutGraphAt(graphZoom, origin, Vector2.zero);
    }

    private bool BackFromGraphNode()
    {
        if (graphFocus == null)
            return false;
        if (inspectorOpen)
            ToggleInspector();
        else
            SelectGraphNode(null);
        return true;
    }

    private void CenterGraphOn(ChainNode node)
    {
        if (node == null)
            return;
        Vector2 point = node.Position + new Vector2(CardWidth, CardHeight) / 2;
        LayoutGraphAt(Mathf.Max(graphZoom, 0.85f), new Vector2(point.x, -point.y), ViewportMiddle());
    }

    private void RenderGraphInspector(bool preservePosition)
    {
        if (graphFocus == null || graphFocus.Links.Count == 0)
            inspectorOpen = false;

        var area = (RectTransform)graphScroll.transform;
        float maximumBody = Mathf.Clamp(graphView.rect.height * 0.40f, 42, 135);
        float body = inspectorOpen ? Mathf.Min(maximumBody, graphFocus.Links.Sum(GraphFlowRowHeight)) : 0;
        float dockHeight = graphFocus == null ? 0 : InspectorHeaderHeight + body;
        area.offsetMin = new Vector2(0, dockHeight);
        graphDock.offsetMax = new Vector2(0, dockHeight);
        graphDock.gameObject.SetActive(graphFocus != null);
        var scroll = graphFlowList.GetComponentInParent<KScrollRect>(true);
        scroll.gameObject.SetActive(inspectorOpen);
        Clear(graphFlowList, preservePosition);
        bool showPeriod = world?.ActualWindow != null && (showGraphActual ||
            (inspectorOpen && graphFocus.Links.Any(link => link.Actual.Amount.HasValue)));
        graphLegend.text = PlannerSession.Failure ?? (showPeriod
            ? GraphLegend + Tr(" · 实测 ", " · Actual ") + MeasurementCaption() : GraphLegend);
        if (graphFocus == null)
            return;

        graphNodeLabel.text = graphFocus.Name;
        graphDetailsLabel.text = inspectorOpen ? Tr("收起", "Hide") :
            Tr($"流量 ({graphFocus.Links.Count})", $"Flows ({graphFocus.Links.Count})");
        graphDetailsButton.isInteractable = graphFocus.Links.Count > 0;
        if (!inspectorOpen)
            return;

        int index = 0;
        foreach (var link in graphFocus.Links.OrderBy(link => link.IsActualOnly).ThenBy(link => link.From == graphFocus)
                     .ThenBy(link => link.Optional).ThenBy(link => link.Other(graphFocus).Name))
            AddGraphFlowRow(graphFocus, link, index++);
    }

    private float GraphFlowRowHeight(ChainLink link) => link.Actual.Amount.HasValue ? ButtonHeight + 22 : ButtonHeight;

    private void AddGraphFlowRow(ChainNode node, ChainLink link, int index)
    {
        ChainNode other = link.Other(node);
        string direction = link.From == node ? Tr("流出", "Out") : Tr("流入", "In");
        string theory = Tr("理论 ", "Theory ") + TheoryQuantity(link);
        if (link.Optional && link.Amount.HasValue)
            theory += Tr(" · 备选", " · Option");

        Color background = index % 2 == 0 ? RowColor : AlternateRowColor;
        var button = MakeButton(graphFlowList, null, () =>
        {
            SelectGraphNode(other);
            CenterGraphOn(other);
        }, 0, background);
        var layout = button.GetComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = GraphFlowRowHeight(link);
        var rect = (RectTransform)button.transform;
        float nameEnd = compactUi ? 0.55f : 0.60f;
        var who = Rect("Counterpart", rect, Vector2.zero, new Vector2(nameEnd, 1));
        FitText(Label(WithIcon(who, other.Icon, compactUi ? 12 : 18), direction + " · " + other.Name, BodyTextSize), MinimumTextSize);
        var values = Rect("FlowComparison", rect, new Vector2(nameEnd, 0), Vector2.one);
        if (link.Actual.Amount.HasValue)
        {
            var primary = Rect("Theory", values, new Vector2(0, 0.5f), Vector2.one);
            var secondary = Rect("Actual", values, Vector2.zero, new Vector2(1, 0.5f));
            AddValue(primary, 0, 1, theory, TextColor);
            AddValue(secondary, 0, 1, ActualQuantity(link), SecondaryColor);
        }
        else
            AddValue(values, 0, 1, theory, TextColor);
        button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(direction + " · " + other.Name + "\n" + GraphFlowText(link));
    }
}

internal sealed class FlowDiagramScroll : KScrollRect
{
    internal System.Action<PointerEventData> OnWheel;

    public override void OnScroll(PointerEventData eventData)
    {
        OnWheel?.Invoke(eventData);
    }
}
