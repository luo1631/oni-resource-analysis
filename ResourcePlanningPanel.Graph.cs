using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal sealed partial class ResourcePlanningPanel
{
    private float CardWidth => compactUi ? 122 : 140;
    private float CardHeight => compactUi ? 30 : 41;
    private float ColumnGap => compactUi ? 91 : 102;
    private float RowGap => compactUi ? 9 : 11;
    private const float GraphInset = 9;
    private const int LinksPerLayer = 192;
    private static readonly Color ConnectionColor = new(0.28f, 0.35f, 0.45f);
    private static readonly float[] QuantityPositions = { 0.50f, 0.42f, 0.58f, 0.34f, 0.66f, 0.27f, 0.73f };
    private RectTransform graphQuantities;
    private FlowDiagramConnections[] graphConnections = Array.Empty<FlowDiagramConnections>();
    private List<ChainLink> graphLinks;
    private ChainNode graphFocus;
    private readonly Dictionary<ChainNode, (Outline Border, CanvasGroup Opacity)> graphCards = new();

    private sealed class ChainNode
    {
        internal readonly string Name;
        internal readonly string Icon;
        internal readonly ResourceKey? Resource;
        internal readonly string Option;
        internal readonly HashSet<string> Recipes = new();
        internal readonly List<ChainLink> Links = new();
        internal (string Id, string Name, string Icon, string Option)? Process;
        internal bool IsActualOnly;
        internal Vector2 Position;

        internal ChainNode(string name, string icon, ResourceKey? resource = null, string option = "")
        {
            Name = name;
            Icon = icon;
            Resource = resource;
            Option = option;
        }
    }

    private sealed class ChainLink
    {
        internal readonly ChainNode From;
        internal readonly ChainNode To;
        internal readonly FlowUnit Unit;
        internal readonly double? Amount;
        internal readonly bool Optional;
        internal MeasuredQuantity Actual;
        internal Vector2 Start;
        internal Vector2 End;
        internal bool IsActualOnly => !Amount.HasValue && !Optional && Actual.Amount.HasValue;

        internal ChainLink(ChainNode from, ChainNode to, FlowUnit unit, double? amount, bool optional)
        {
            From = from;
            To = to;
            Unit = unit;
            Amount = amount;
            Optional = optional;
        }

        internal ChainNode Other(ChainNode node) => From == node ? To : From;
    }

    private void DrawGraph(ResourceKey[] tracked)
    {
        Vector2 origin = GraphPointAt(Vector2.zero);
        var previousFocus = graphFocus;
        Clear(graphContent, preservePosition: true);
        graphCards.Clear();
        bool hasUtilities = tracked.Any(IsGraphUtility);
        graphUtilitiesButton.gameObject.SetActive(hasUtilities);
        graphUtilitiesButton.bgImage.color = includeGraphUtilities ? HeaderColor : ButtonColor;
        var plotted = hasUtilities && !includeGraphUtilities ? tracked.Where(key => !IsGraphUtility(key)).ToArray() : tracked;
        var (nodes, links) = BuildChain(plotted);
        graphEmpty.gameObject.SetActive(plotted.Length == 0);
        graphEmpty.text = tracked.Length > 0
            ? Tr("当前仅选了氧气和电力类资源，点上方「氧气/电力」显示。", "Select Oxygen/Power above to show the tracked utility resources.")
            : Tr("选择资源后显示流向。", "Choose resources to show flows.");
        graphContent.sizeDelta = LayOutChain(nodes);
        graphLinks = links;
        graphFocus = previousFocus == null ? null : nodes.Find(node =>
            node.Resource == previousFocus.Resource && node.Process == previousFocus.Process);
        graphConnections = new FlowDiagramConnections[(links.Count + LinksPerLayer - 1) / LinksPerLayer];
        for (int index = 0; index < graphConnections.Length; index++)
        {
            var layer = Rect("FlowConnections", graphContent, Vector2.zero, Vector2.one);
            graphConnections[index] = layer.gameObject.AddComponent<FlowDiagramConnections>();
            graphConnections[index].raycastTarget = false;
        }
        graphQuantities = Rect("FlowAmounts", graphContent, Vector2.zero, Vector2.one);
        foreach (var node in nodes)
        {
            AssignPorts(node);
            DrawChainNode(node, () => SelectGraphNode(graphFocus == node ? null : node));
        }
        RenderGraphSelection(preservePosition: graphFocus != null);
        if (!hasGraphLayout)
        {
            float fit = GraphFitZoom();
            graphZoom = fit >= 0.70f ? fit : 1;
            hasGraphLayout = true;
        }
        LayoutGraphAt(graphZoom, origin, Vector2.zero);
    }

    private static bool IsGraphUtility(ResourceKey key) => key == ResourceKey.Electricity || key.Id == "Oxygen";

    private (List<ChainNode> Nodes, List<ChainLink> Links) BuildChain(ResourceKey[] tracked)
    {
        var nodes = new List<ChainNode>();
        var links = new List<ChainLink>();
        if (tracked.Length == 0)
            return (nodes, links);
        var materials = new Dictionary<ResourceKey, ChainNode>();
        var processes = new Dictionary<(string Id, string Name, string Icon, string Option), ChainNode>();
        foreach (var key in tracked)
        {
            var material = new ChainNode(GameText.Label(key.Id), key.Id, key);
            nodes.Add(material);
            materials.Add(key, material);
            // The breakdown keeps room coordinates. The diagram combines the same process and option across rooms.
            var groups = world.Resources[key].Sources.GroupBy(pair => ProcessIdentity(pair.Key));
            foreach (var group in groups)
            {
                var (_, name, icon, option) = group.Key;
                if (!processes.TryGetValue(group.Key, out var process))
                {
                    process = new ChainNode(name, icon, option: option) { Process = group.Key };
                    processes.Add(group.Key, process);
                    nodes.Add(process);
                }
                process.Recipes.UnionWith(group.Select(pair => pair.Key.Recipe).Where(recipe => recipe.Length > 0));
                double produced = group.Sum(pair => pair.Value.Produced);
                double consumed = group.Sum(pair => pair.Value.Consumed);
                if (produced > 0)
                    AddLink(links, new ChainLink(process, material, key.Unit, produced, option.Length > 0));
                if (consumed > 0)
                    AddLink(links, new ChainLink(material, process, key.Unit, consumed, option.Length > 0));
            }
        }
        AddCompatibleInputs(materials, processes, links);
        AddMeasuredFlows(materials, nodes, links);
        return (nodes, links);
    }

    private void AddMeasuredFlows(Dictionary<ResourceKey, ChainNode> materials, List<ChainNode> nodes, List<ChainLink> links)
    {
        var observedProcesses = new Dictionary<Tag, ChainNode>();
        foreach (var material in materials)
        {
            if (!world.Comparisons.TryGetValue(material.Key, out var buildings))
                continue;

            foreach (var entry in buildings)
            {
                PairMeasurement(material.Value, material.Key.Unit, entry.Key, output: true, entry.Value.Measured(true));
                PairMeasurement(material.Value, material.Key.Unit, entry.Key, output: false, entry.Value.Measured(false));
            }
        }

        void PairMeasurement(ChainNode material, FlowUnit unit, Tag prefab, bool output, MeasuredQuantity measured)
        {
            if (!measured.Amount.HasValue)
                return;
            string id = prefab.ToString();
            var matches = material.Links.Where(link => (link.Amount.HasValue || link.Optional) &&
                (link.To == material) == output && link.Other(material).Icon == id).Take(2).ToArray();
            if (matches.Length == 1 && !matches[0].Optional)
            {
                matches[0].Actual = measured;
                return;
            }

            // The meter is for the building type. Keep it independent of specific recipe alternatives.
            if (!showGraphActual || measured.Amount <= 0)
                return;
            if (!observedProcesses.TryGetValue(prefab, out var process))
            {
                string name = PrefabName(prefab) + Tr(" · 实测合计", " · metered total");
                process = new ChainNode(name, id) { Process = (id, name, id, ""), IsActualOnly = true };
                observedProcesses.Add(prefab, process);
                nodes.Add(process);
            }
            var from = output ? process : material;
            var to = output ? material : process;
            AddLink(links, new ChainLink(from, to, unit, null, false) { Actual = measured });
        }
    }

    private static (string Id, string Name, string Icon, string Option) ProcessIdentity(FlowSource source)
    {
        if (source.PrefabId == MinionConfig.ID)
            return (source.PrefabId, Tr("复制人", "Duplicants"), source.PrefabId, source.Option);
        if (source.PrefabId == BionicMinionConfig.ID)
            return (source.PrefabId, Tr("仿生复制人", "Bionic duplicants"), source.PrefabId, source.Option);
        return (source.Id, source.Name, source.PrefabId, source.Option);
    }

    private void AddCompatibleInputs(Dictionary<ResourceKey, ChainNode> materials,
        Dictionary<(string Id, string Name, string Icon, string Option), ChainNode> processes, List<ChainLink> links)
    {
        foreach (var entry in world.Resources)
        {
            var tag = new Tag(entry.Key.Id);
            if (Assets.TryGetPrefab(tag) != null || ElementLoader.FindElementByTag(tag) != null)
                continue;
            var consumers = entry.Value.Sources.Where(source => source.Value.Consumed > 0)
                .Select(source => processes.TryGetValue(ProcessIdentity(source.Key), out var node) ? node : null)
                .Where(node => node != null).Distinct().ToArray();
            if (consumers.Length == 0)
                continue;

            foreach (var item in materials)
            {
                var prefab = Assets.TryGetPrefab(new Tag(item.Key.Id));
                if (prefab == null || !prefab.HasTag(tag))
                    continue;
                var material = item.Value;
                foreach (var process in consumers)
                {
                    // Category demand has no quantity for any one compatible material.
                    if (!material.Links.Any(link => link.To == process))
                        AddLink(links, new ChainLink(material, process, item.Key.Unit, null, true));
                }
            }
        }
    }

    private static void AddLink(List<ChainLink> links, ChainLink link)
    {
        links.Add(link);
        link.From.Links.Add(link);
        link.To.Links.Add(link);
    }

    private Vector2 LayOutChain(List<ChainNode> nodes)
    {
        var unused = new HashSet<ChainNode>(nodes);
        float width = CardWidth;
        float top = GraphInset;
        var roots = nodes.OrderBy(node => node.Links.Any(link => link.To == node && !link.Optional))
            .ThenByDescending(node => node.Links.Count);
        foreach (var root in roots)
        {
            if (!unused.Remove(root))
                continue;

            // In a bipartite graph, undirected BFS puts each connection between neighboring columns, even in a cycle.
            var columns = new List<List<ChainNode>>();
            var next = new List<ChainNode> { root };
            while (next.Count > 0)
            {
                var column = next;
                columns.Add(column);
                next = column.SelectMany(node => node.Links.OrderBy(link => link.To == node)
                    .Select(link => link.Other(node))).Where(unused.Remove).ToList();
            }

            ReduceCrossings(columns);
            int rows = columns.Max(column => column.Count);
            for (int column = 0; column < columns.Count; column++)
            {
                var members = columns[column];
                float startY = top + Mathf.Min(0.5f, (rows - members.Count) / 2f) * (CardHeight + RowGap);
                for (int row = 0; row < members.Count; row++)
                    members[row].Position = new Vector2(GraphInset + column * (CardWidth + ColumnGap),
                        startY + row * (CardHeight + RowGap));
            }
            width = Mathf.Max(width, columns.Count * (CardWidth + ColumnGap) - ColumnGap);
            top += rows * (CardHeight + RowGap) + GraphInset;
        }
        return new Vector2(width + GraphInset * 2, top);
    }

    private static void ReduceCrossings(List<List<ChainNode>> columns)
    {
        // Barycentric sweeps place each node near the average row of its neighbors in the adjoining column.
        for (int pass = 0; pass < 4; pass++)
        {
            int step = pass % 2 == 0 ? 1 : -1;
            int first = step > 0 ? 1 : columns.Count - 2;
            for (int index = first; index >= 0 && index < columns.Count; index += step)
            {
                var adjacent = columns[index - step];
                var positions = adjacent.Select((node, row) => (node, row))
                    .ToDictionary(item => item.node, item => (item.row + 0.5f) / adjacent.Count);
                var column = columns[index];
                columns[index] = column.Select((node, row) => (node, row))
                    .OrderBy(item => NeighborRow(item.node, positions, (item.row + 0.5f) / column.Count))
                    .Select(item => item.node).ToList();
            }
        }
    }

    private static float NeighborRow(ChainNode node, Dictionary<ChainNode, float> positions, float fallback)
    {
        float total = 0;
        float weight = 0;
        foreach (var link in node.Links)
        {
            if (!positions.TryGetValue(link.Other(node), out float position))
                continue;
            float contribution = link.Optional ? 0.4f : 1f;
            total += position * contribution;
            weight += contribution;
        }
        return weight > 0 ? total / weight : fallback;
    }

    private void AssignPorts(ChainNode node)
    {
        var sides = node.Links.GroupBy(link => link.Other(node).Position.x > node.Position.x);
        foreach (var side in sides)
        {
            var ports = side.OrderBy(link => link.Other(node).Position.y)
                .ThenBy(link => side.Key ? link.To == node : link.From == node).ToArray();
            for (int index = 0; index < ports.Length; index++)
            {
                var link = ports[index];
                var point = node.Position + new Vector2(side.Key ? CardWidth : 0,
                    4 + (CardHeight - 8) * (index + 1) / (ports.Length + 1));
                if (link.From == node)
                    link.Start = point;
                else
                    link.End = point;
            }
        }
    }

    private void DrawChainNode(ChainNode node, System.Action focus)
    {
        bool material = node.Resource.HasValue;
        bool optional = node.Option.Length > 0;
        var button = MakeNativeButton(graphContent, material ? SelectedColor : optional || node.IsActualOnly ? AlternateRowColor : RowColor,
            focus, material ? "MaterialNode" : "ProcessNode");
        var card = (RectTransform)button.transform;
        PlaceCard(card, node.Position, new Vector2(CardWidth, CardHeight));
        var border = card.gameObject.AddComponent<Outline>();
        border.effectColor = HeaderColor;
        border.effectDistance = Vector2.one;
        border.enabled = false;
        graphCards.Add(node, (border, card.gameObject.AddComponent<CanvasGroup>()));
        var text = WithIcon(card, node.Icon, compactUi ? 16 : 22);
        var title = FitText(Label(text, node.Name, BodyTextSize), MinimumTextSize);
        Place(title.rectTransform, new Vector2(0, 0.48f), Vector2.one);

        string note = optional ? Tr("备选 · ", "Option · ") + node.Option :
            node.IsActualOnly ? Tr("实测来源", "Measured source") :
            node.Recipes.Count == 1 ? node.Recipes.First() : "";
        if (node.Resource is ResourceKey key)
        {
            var row = world.Resources[key];
            string net = row.HasOptions && row.Totals.Produced == 0 && row.Totals.Consumed == 0
                ? "—" : Number(row.Totals.Net, true) + "\u00a0" + UnitSymbol(key.Unit);
            note = Tr("理论净额 ", "Est. net ") + net;
        }
        if (note.Length > 0)
        {
            var subtitle = FitText(Label(text, note, BodyTextSize - 2, color: SecondaryColor), MinimumTextSize - 1);
            Place(subtitle.rectTransform, Vector2.zero, new Vector2(1, 0.48f));
        }
        else
        {
            Place(title.rectTransform, Vector2.zero, Vector2.one);
        }

        string tooltip = node.Name + (note.Length == 0 ? "" : "\n" + note);
        if (node.Resource == ResourceKey.FoodEnergy)
            tooltip += "\n" + Tr("食物的热量汇总", "Aggregate calories across foods");
        foreach (var link in node.Links.OrderBy(link => link.IsActualOnly).Take(12))
        {
            string direction = link.From == node ? "→ " : "← ";
            tooltip += "\n" + direction + link.Other(node).Name + " · " + GraphFlowText(link);
        }
        button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(tooltip);
    }

    private void DrawChainLinks(FlowDiagramConnections[] connections, RectTransform quantities, List<ChainLink> links, ChainNode focused)
    {
        foreach (var layer in connections)
            layer.Clear();
        Clear(quantities);
        var used = graphCards.Keys.Select(node => new Rect(node.Position - Vector2.one * 2,
            new Vector2(CardWidth + 4, CardHeight + 4))).ToList();
        var theoryLabels = new List<(ChainLink Link, Rect Bounds)>();
        var measuredOnly = new List<ChainLink>();
        for (int index = 0; index < links.Count; index++)
        {
            var link = links[index];
            bool emphasized = focused == null || link.From == focused || link.To == focused;
            var color = link.Optional || link.IsActualOnly ? SecondaryColor : ConnectionColor;
            color.a = emphasized ? link.IsActualOnly ? 0.62f : 1f : 0.22f;
            connections[index / LinksPerLayer].Add(link.Start, link.End, color, link.Optional);
            if (!emphasized || (focused == null && links.Count > 24))
                continue;

            if (link.IsActualOnly)
                measuredOnly.Add(link);
            else if (FindQuantityBox(link, TheoryQuantity(link), used, out var bounds))
                theoryLabels.Add((link, bounds));
        }

        // Reserve space for theoretical labels first. An actual label can use the space below it.
        foreach (var (link, reserved) in theoryLabels)
        {
            var primary = reserved;
            string actual = showGraphActual ? ActualQuantity(link) : "";
            Rect secondary = default;
            bool paired = actual.Length > 0 && TryPlaceActualBelow(link, actual, used, ref primary, out secondary);
            DrawQuantity(quantities, primary, TheoryQuantity(link), link.Optional ? SecondaryColor : ConnectionColor);
            if (paired)
                DrawQuantity(quantities, secondary, actual, SecondaryColor);
        }
        foreach (var link in measuredOnly)
        {
            string actual = ActualQuantity(link);
            if (FindQuantityBox(link, actual, used, out var bounds))
                DrawQuantity(quantities, bounds, actual, SecondaryColor);
        }
    }

    private float QuantityWidth(string value) =>
        Mathf.Min(ColumnGap + 12, Mathf.Max(36, graphLegend.GetPreferredValues(value).x + 9));

    private bool TryPlaceActualBelow(ChainLink link, string actual, List<Rect> used, ref Rect primary, out Rect secondary)
    {
        Rect reserved = primary;
        used.Remove(reserved);
        float width = QuantityWidth(actual);
        var centers = new List<Vector2> { reserved.center };
        centers.AddRange(QuantityPositions.Select(position => FlowDiagramConnections.PointOnCurve(link.Start, link.End, position)));
        foreach (var center in centers)
        {
            var theory = new Rect(center - reserved.size / 2, reserved.size);
            var measured = new Rect(center.x - width / 2, theory.yMax, width, reserved.height);
            if (used.Any(box => box.Overlaps(theory) || box.Overlaps(measured)))
                continue;
            primary = theory;
            secondary = measured;
            used.Add(theory);
            used.Add(measured);
            return true;
        }
        used.Add(reserved);
        secondary = default;
        return false;
    }

    private bool FindQuantityBox(ChainLink link, string value, List<Rect> used, out Rect bounds)
    {
        float width = QuantityWidth(value);
        float height = compactUi ? 17 : 21;
        foreach (float position in QuantityPositions)
        {
            Vector2 center = FlowDiagramConnections.PointOnCurve(link.Start, link.End, position);
            var candidate = new Rect(center.x - width / 2, center.y - height / 2, width, height);
            if (used.Any(existing => existing.Overlaps(candidate)))
                continue;
            bounds = candidate;
            used.Add(candidate);
            return true;
        }
        bounds = default;
        return false;
    }

    private void DrawQuantity(RectTransform parent, Rect bounds, string value, Color color)
    {
        var chip = Rect("FlowAmount", parent, Vector2.up, Vector2.up);
        PlaceCard(chip, bounds.position, bounds.size);
        var background = chip.gameObject.AddComponent<Image>();
        background.color = PanelColor;
        background.raycastTarget = false;
        FitText(Label(chip, value, BodyTextSize - 1.5f, TextAlignmentOptions.Center, color), MinimumTextSize - 1);
    }

    private static string TheoryQuantity(ChainLink link) => link.Amount is double amount
        ? CompactNumber(amount) + "\u00a0" + UnitSymbol(link.Unit)
        : link.Optional ? Tr("可用原料", "Usable input") : "—";

    private static string ActualQuantity(ChainLink link) => link.Actual.Amount.HasValue
        ? Tr("实测 ", "Actual ") + MeasuredValue(link.Actual) + "\u00a0" + UnitSymbol(link.Unit) : "";

    private static string GraphFlowText(ChainLink link) =>
        Tr("理论 ", "Theory ") + TheoryQuantity(link) +
        (link.Optional && link.Amount.HasValue ? Tr("（备选）", " (option)") : "") +
        (link.Actual.Amount.HasValue ? " · " + ActualQuantity(link) : "");

    private static void PlaceCard(RectTransform rect, Vector2 topLeft, Vector2 size)
    {
        Place(rect, Vector2.up, Vector2.up);
        rect.pivot = Vector2.up;
        rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
        rect.sizeDelta = size;
    }
}

internal sealed class FlowDiagramConnections : MaskableGraphic
{
    private readonly List<(Vector2 Start, Vector2 End, Color Color, bool Optional)> paths = new();

    internal void Add(Vector2 start, Vector2 end, Color color, bool optional) => paths.Add((start, end, color, optional));

    internal void Clear()
    {
        paths.Clear();
        SetVerticesDirty();
    }

    internal static Vector2 PointOnCurve(Vector2 start, Vector2 end, float t)
    {
        Vector2 delta = end - start;
        float bow = -Mathf.Sign(delta.x) * 9 * Mathf.Clamp01(1 - Mathf.Abs(delta.y) / 20);
        var first = start + new Vector2(delta.x * 0.43f, bow);
        var second = end + new Vector2(-delta.x * 0.43f, bow);
        float rest = 1 - t;
        return rest * rest * rest * start + 3 * rest * rest * t * first
            + 3 * rest * t * t * second + t * t * t * end;
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Rect bounds = rectTransform.rect;
        foreach (var path in paths)
        {
            Vector2 tip = ToMeshPoint(path.End, bounds);
            var direction = (tip - ToMeshPoint(PointOnCurve(path.Start, path.End, 0.99f), bounds)).normalized;
            Vector2 arrowBase = tip - direction * 6;
            int segments = Mathf.Clamp(12 + Mathf.CeilToInt(Mathf.Abs(path.Start.y - path.End.y) / 24), 12, 24);
            Vector2 previous = ToMeshPoint(path.Start, bounds);
            for (int index = 1; index <= segments; index++)
            {
                Vector2 point = index == segments ? arrowBase : ToMeshPoint(PointOnCurve(path.Start, path.End, (float)index / segments), bounds);
                if (!path.Optional || index % 3 != 0)
                    AddSpan(mesh, previous, point, path.Color);
                previous = point;
            }
            var normal = new Vector2(-direction.y, direction.x) * 3.2f;
            int offset = mesh.currentVertCount;
            mesh.AddVert(tip, path.Color, Vector2.zero);
            mesh.AddVert(arrowBase + normal, path.Color, Vector2.zero);
            mesh.AddVert(arrowBase - normal, path.Color, Vector2.zero);
            mesh.AddTriangle(offset, offset + 1, offset + 2);
        }
    }

    private static Vector2 ToMeshPoint(Vector2 point, Rect bounds) => new(bounds.xMin + point.x, bounds.yMax - point.y);

    private static void AddSpan(VertexHelper mesh, Vector2 start, Vector2 end, Color color)
    {
        Vector2 delta = end - start;
        if (delta.sqrMagnitude < 0.0001f)
            return;
        Vector2 direction = delta.normalized;
        Vector2 normal = new(-direction.y * 0.7f, direction.x * 0.7f);
        int offset = mesh.currentVertCount;
        mesh.AddVert(start + normal, color, Vector2.zero);
        mesh.AddVert(start - normal, color, Vector2.zero);
        mesh.AddVert(end - normal, color, Vector2.zero);
        mesh.AddVert(end + normal, color, Vector2.zero);
        mesh.AddTriangle(offset, offset + 1, offset + 2);
        mesh.AddTriangle(offset, offset + 2, offset + 3);
    }
}
