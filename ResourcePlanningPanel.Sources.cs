using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal sealed partial class ResourcePlanningPanel
{
    private float SourceRowHeight => compactUi ? 17 : 24;
    private string openedSource;
    private Vector2 sourceListOffset;

    private sealed class SourceGroup
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly string Icon;
        internal readonly FlowAmounts Theory = new();
        internal readonly List<KeyValuePair<FlowSource, FlowAmounts>> Entries = new();
        internal BuildingComparison Comparison;
        internal bool HasOptions;

        internal SourceGroup(string id, string name, string icon)
        {
            Id = id;
            Name = name;
            Icon = icon;
        }
    }

    private void ShowSourcePane(ResourceKey key, ResourceRow row)
    {
        var groups = GroupSources(key, row);
        if (openedSource != null && groups.TryGetValue(openedSource, out var chosen))
        {
            SetSourceHeading(chosen.Name + " · " + UnitName(key), true);
            ShowSourceDetails(key, chosen);
            return;
        }

        openedSource = null;
        SetSourceHeading(UnitName(key) + Tr(" / 周期", " / cycle"), false);
        ShowInventory(key, row);
        if (showAverages && !showTableActual)
            AddMessage(details, AverageSummary(key), SecondaryColor);
        if (groups.Count == 0)
        {
            AddMessage(details, Tr("此资源暂无可列出的来源。", "There are no sources to list for this resource."), SecondaryColor);
            return;
        }

        bool actual = showSourceActual && world.ActualWindow != null && !showAverages;
        var heading = Rect("SourceColumns", details, Vector2.zero, Vector2.one);
        heading.gameObject.AddComponent<LayoutElement>().preferredHeight = ColumnHeight - 2;
        AddSourceColumns(heading, null, Tr("来源", "Source"), Tr("理论", "Theory"),
            actual ? Tr("实测", "Actual") : null);

        int index = 0;
        foreach (var group in groups.Values.OrderBy(group => group.Theory.Produced + group.Theory.Consumed == 0)
                     .ThenByDescending(group => group.Theory.Produced + group.Theory.Consumed)
                     .ThenByDescending(group => group.HasOptions).ThenBy(group => group.Name))
        {
            var button = MakeButton(details, null, () => OpenSource(group.Id), 0,
                index++ % 2 == 0 ? RowColor : AlternateRowColor);
            button.name = "BalanceSource";
            var layout = button.GetComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = SourceRowHeight;
            var produced = group.Comparison?.Measured(true) ?? default;
            var consumed = group.Comparison?.Measured(false) ?? default;
            string theory = SourceTheory(group);
            string measured = CompactFlows(produced.Amount, consumed.Amount);
            if (produced.Partial || consumed.Partial)
                measured += "†";
            AddSourceColumns((RectTransform)button.transform, group.Icon,
                group.Name + (group.HasOptions ? " *" : ""), theory, actual ? measured : null);
            var tooltip = group.Name + Tr("\n理论 ＋产 －耗  ", "\nTheory +out −in  ") + theory;
            if (produced.Amount.HasValue || consumed.Amount.HasValue)
                tooltip += "\n" + MeasurementCaption() + "\n" +
                    Tr("已计量 ", "Observed ") + DirectionalText(MeasuredValue(produced), MeasuredValue(consumed));
            button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(tooltip);
        }
    }

    private Dictionary<string, SourceGroup> GroupSources(ResourceKey key, ResourceRow row)
    {
        var groups = new Dictionary<string, SourceGroup>(StringComparer.Ordinal);
        foreach (var entry in row.Sources)
        {
            var source = entry.Key;
            string id = source.PrefabId.Length > 0 ? source.PrefabId : source.Id;
            if (!groups.TryGetValue(id, out var group))
            {
                var tag = new Tag(source.PrefabId);
                bool usePrefabName = source.PrefabId == MinionConfig.ID || source.PrefabId == BionicMinionConfig.ID ||
                    (source.PrefabId.Length > 0 && Assets.TryGetPrefab(tag)?.GetComponent<Building>() != null);
                string name = usePrefabName ? PrefabName(tag) : source.Name;
                group = new SourceGroup(id, name, source.PrefabId);
                groups.Add(id, group);
            }
            group.Entries.Add(entry);
            if (source.Option.Length == 0)
                group.Theory.Add(entry.Value.Produced, entry.Value.Consumed);
            else
                group.HasOptions = true;
        }

        if (world.Comparisons.TryGetValue(key, out var comparisons))
        {
            foreach (var entry in comparisons)
            {
                string id = entry.Key.ToString();
                if (!groups.TryGetValue(id, out var group))
                {
                    group = new SourceGroup(id, PrefabName(entry.Key), id);
                    groups.Add(id, group);
                }
                group.Comparison = entry.Value;
            }
        }
        return groups;
    }

    private void AddSourceColumns(RectTransform row, string icon, string name, string theory, string actual)
    {
        float nameEnd = actual == null ? 0.55f : 0.42f;
        float theoryEnd = actual == null ? 1 : 0.73f;
        var cell = Rect("SourceName", row, Vector2.zero, new Vector2(nameEnd, 1));
        FitText(Label(WithIcon(cell, icon, compactUi ? 12 : 16), name, BodyTextSize), MinimumTextSize);
        AddValue(row, nameEnd, theoryEnd, theory, TextColor);
        if (actual != null)
            AddValue(row, theoryEnd, 1, actual, SecondaryColor);
    }

    private static string SourceTheory(SourceGroup group)
    {
        var theory = group.Theory;
        return theory.Produced > 0 || theory.Consumed > 0
            ? CompactFlows(theory.Produced, theory.Consumed)
            : group.HasOptions ? Tr("备选", "Options") : "—";
    }

    private static string CompactFlows(double? produced, double? consumed)
    {
        if (produced > 0 && consumed > 0)
            return "+" + CompactNumber(produced.Value) + " / −" + CompactNumber(consumed.Value);
        if (produced > 0)
            return "+" + CompactNumber(produced.Value);
        if (consumed > 0)
            return "−" + CompactNumber(consumed.Value);
        return produced.HasValue || consumed.HasValue ? "0" : "—";
    }

    private static string CompactNumber(double amount) => amount is > 0 and < 0.0005 ? "<0.001" : Number(amount);

    private void SetSourceHeading(string title, bool back)
    {
        detailTitle.text = title;
        detailTitle.margin = new Vector4(back ? (compactUi ? 45 : 59) : 5, 0, 4, 0);
        detailBack.gameObject.SetActive(back);
    }

    private void OpenSource(string id)
    {
        sourceListOffset = details.anchoredPosition;
        openedSource = id;
        timelineExpanded = showIndividualSlots = false;
        timelineSegment = 0;
        details.GetComponentInParent<KScrollRect>(true).StopMovement();
        details.anchoredPosition = Vector2.zero;
        ShowDetails(selected);
    }

    private void CloseSource()
    {
        openedSource = null;
        ShowDetails(selected);
        details.anchoredPosition = sourceListOffset;
    }

    private void ShowSourceDetails(ResourceKey key, SourceGroup group)
    {
        string theory = SourceTheory(group);
        bool hasTheory = group.Theory.Produced > 0 || group.Theory.Consumed > 0;
        AddMessage(details, Tr("理论  ", "Theory  ") + theory + (hasTheory ? "\u00a0" + UnitSymbol(key.Unit) : ""), HeaderColor);
        bool showingOptions = false;
        var sources = group.Entries.GroupBy(entry => (entry.Key.Id, entry.Key.Name, entry.Key.Recipe, entry.Key.Option,
                entry.Key.Basis, entry.Key.Detail))
            .Select(entries => (Source: entries.Key, Produced: entries.Sum(entry => entry.Value.Produced),
                Consumed: entries.Sum(entry => entry.Value.Consumed),
                Rooms: entries.Select(entry => entry.Key.Room).Where(room => room.Length > 0).Distinct().OrderBy(room => room)))
            .OrderBy(entry => entry.Source.Option.Length > 0).ThenByDescending(entry => entry.Produced + entry.Consumed);
        foreach (var entry in sources)
        {
            var source = entry.Source;
            if (!showingOptions && source.Option.Length > 0)
            {
                AddMessage(details, Tr("备选方案", "Alternatives"), HeaderColor);
                showingOptions = true;
            }
            string flow = DirectionalText(
                entry.Produced > 0 ? FlowDetail(key.Unit, entry.Produced) : "",
                entry.Consumed > 0 ? FlowDetail(key.Unit, entry.Consumed) : "");
            string context = string.Join(" · ", new[] { source.Recipe, DescribeBasis(source.Basis), source.Option }
                .Where(note => note.Length > 0).Distinct());
            string rooms = string.Join(" · ", entry.Rooms);
            string name = group.Name == source.Name || source.Name.StartsWith(group.Name + " · ", StringComparison.Ordinal)
                ? "" : source.Name;
            string message = string.Join("\n", new[] { name, flow, context, rooms, source.Detail }.Where(line => line.Length > 0));
            AddMessage(details, message, source.Option.Length > 0 ? SecondaryColor : TextColor);
        }
        if (group.Comparison != null)
            ShowSourceActual(key, group.Comparison);
    }
}
