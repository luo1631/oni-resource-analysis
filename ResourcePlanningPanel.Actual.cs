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
    private const int SegmentSeconds = 60;
    private const int SlotsPerSegment = SegmentSeconds / CycleMeasurements.SampleSpacing;
    private const int SegmentCount = CycleMeasurements.SamplesPerCycle / SlotsPerSegment;
    private static readonly Color PartialCoverageColor = new(0.65f, 0.43f, 0.16f);
    private bool timelineExpanded;
    private int timelineSegment;
    private bool showIndividualSlots;

    private string MeasurementCaption()
    {
        var window = world?.ActualWindow;
        if (window == null)
            return Tr("上周期暂无本次会话实测", "No previous-cycle observations in this session");

        int cycle = window.Cycle + 1;
        return Tr($"上周期 {cycle} · 覆盖 {window.ObservedSeconds}/600 秒",
            $"Previous cycle {cycle} · coverage {window.ObservedSeconds}/600 s");
    }

    private static string MeasuredValue(MeasuredQuantity quantity) => quantity.Amount is double amount
        ? CompactNumber(amount) + (quantity.Partial ? "†" : "") : "—";

    private void ShowSourceActual(ResourceKey key, BuildingComparison comparison)
    {
        if (world.ActualWindow == null)
        {
            AddMessage(details, MeasurementCaption(), SecondaryColor);
            return;
        }

        if (comparison.ProducedMeasurement == null && comparison.ConsumedMeasurement == null)
        {
            AddMessage(details, Tr("实测 — 未观测", "Actual — no observation"), SecondaryColor);
            return;
        }

        bool output = comparison.ProducedMeasurement != null || comparison.Theory.Produced > 0 || comparison.Conditional.Produced > 0;
        bool input = comparison.ConsumedMeasurement != null || comparison.Theory.Consumed > 0 || comparison.Conditional.Consumed > 0;
        string period = MeasurementCaption();
        string amount = DirectionalText(output ? ActualAmount(key.Unit, comparison.ProducedMeasurement) : "",
            input ? ActualAmount(key.Unit, comparison.ConsumedMeasurement) : "");
        AddMessage(details, Tr("实测 · ", "Actual · ") + period + "\n" + amount, SecondaryColor);

        MakeButton(details, timelineExpanded ? Tr("收起周期明细", "Hide cycle detail") : Tr("查看周期明细", "Inspect cycle detail"),
            () => { timelineExpanded = !timelineExpanded; ShowDetails(key); }, 0);
        if (timelineExpanded)
            RenderTimeline(key, comparison);
    }

    private static string ActualAmount(FlowUnit unit, MeasurementSeries series)
    {
        if (series == null)
            return "—";
        if (series.ObservedSlots == CycleMeasurements.SamplesPerCycle)
            return ComparisonAmount(unit, series.Total);
        return Number(series.Total) + "\u00a0" + UnitSymbol(unit) + Tr($" ({series.ObservedSeconds}/600 秒)", $" ({series.ObservedSeconds}/600 s)");
    }

    private static string DirectionalText(string output, string input)
    {
        string text = output.Length > 0 ? Tr("产 ", "Out ") + output : "";
        if (input.Length > 0)
            text += (text.Length > 0 ? " · " : "") + Tr("耗 ", "In ") + input;
        return text;
    }

    private void RenderTimeline(ResourceKey key, BuildingComparison comparison)
    {
        AddMessage(details, Tr("每格 60 秒 · 左产右耗 · 黄边为缺测", "60 s per bin · out left, in right · amber for gaps"), SecondaryColor);
        var rows = Rect("MeasuredTimeline", details, Vector2.zero, Vector2.one);
        rows.gameObject.AddComponent<LayoutElement>().minHeight = ButtonHeight + 6;
        ConfigureControls(rows, 2);
        var produced = new (double Total, double Peak, int Slots)[SegmentCount];
        var consumed = new (double Total, double Peak, int Slots)[SegmentCount];
        double maximum = 0;
        for (int index = 0; index < SegmentCount; index++)
        {
            produced[index] = Segment(comparison.ProducedMeasurement, index);
            consumed[index] = Segment(comparison.ConsumedMeasurement, index);
            maximum = Math.Max(maximum, Math.Max(produced[index].Total, consumed[index].Total));
        }

        for (int index = 0; index < SegmentCount; index++)
        {
            int segment = index;
            var button = MakeButton(rows, null, () => { timelineSegment = segment; ShowDetails(key); }, 0,
                timelineSegment == segment ? SelectedColor : RowColor);
            button.name = "MeasuredSegment";
            var layout = button.GetComponent<LayoutElement>();
            layout.flexibleWidth = 1;
            layout.minHeight = layout.preferredHeight = ButtonHeight + 2;
            var rect = (RectTransform)button.transform;
            var output = produced[index];
            var input = consumed[index];
            bool missing = output.Slots == 0 && input.Slots == 0;
            bool partial = (comparison.ProducedMeasurement != null && output.Slots < SlotsPerSegment) ||
                (comparison.ConsumedMeasurement != null && input.Slots < SlotsPerSegment);
            button.gameObject.AddComponent<ToolTip>().SetSimpleTooltip(SegmentTitle(segment));
            AddSegmentBar(rect, output.Total, maximum, 0.12f, 0.46f, SurplusColor);
            AddSegmentBar(rect, input.Total, maximum, 0.54f, 0.88f, DeficitColor);
            if (missing)
                Label(rect, "—", 11, TextAlignmentOptions.Center, SecondaryColor);
            else if (!partial && output.Total == 0 && input.Total == 0)
                Label(rect, "0", 11, TextAlignmentOptions.Center, SecondaryColor);
            if (partial && !missing)
            {
                var gap = Rect("PartialCoverage", rect, Vector2.up, Vector2.one);
                gap.offsetMin = new Vector2(2, -2);
                gap.offsetMax = new Vector2(-2, 0);
                var indicator = gap.gameObject.AddComponent<Image>();
                indicator.color = PartialCoverageColor;
                indicator.raycastTarget = false;
            }
        }

        string outputDetail = DirectionalText(SegmentText(key.Unit, comparison.ProducedMeasurement, produced[timelineSegment]), "");
        string inputDetail = DirectionalText("", SegmentText(key.Unit, comparison.ConsumedMeasurement, consumed[timelineSegment]));
        AddMessage(details, string.Join("\n", new[] { SegmentTitle(timelineSegment), outputDetail, inputDetail }
            .Where(line => line.Length > 0)));
        MakeButton(details, showIndividualSlots ? Tr("收起 3 秒明细", "Hide 3-second readings") :
            Tr("展开 3 秒明细", "Show 3-second readings"), () => { showIndividualSlots = !showIndividualSlots; ShowDetails(key); }, 0);
        if (showIndividualSlots)
            ShowSlots(key.Unit, comparison);
    }

    private static (double Total, double Peak, int Slots) Segment(MeasurementSeries series, int segment)
    {
        if (series == null)
            return default;
        double total = 0;
        double peak = 0;
        int observed = 0;
        for (int slot = segment * SlotsPerSegment; slot < (segment + 1) * SlotsPerSegment; slot++)
        {
            if (!series.IsObserved(slot))
                continue;
            double amount = series.Amount(slot);
            total += amount;
            peak = Math.Max(peak, amount);
            observed++;
        }
        return (total, peak, observed);
    }

    private static string SegmentTitle(int index) => $"{index * SegmentSeconds}–{(index + 1) * SegmentSeconds}" +
        Tr(" 游戏秒", " game seconds");

    private static string SegmentText(FlowUnit unit, MeasurementSeries series, (double Total, double Peak, int Slots) segment)
    {
        if (series == null)
            return "";
        if (segment.Slots == 0)
            return Tr("— 缺测", "— missing");
        string peak = unit == FlowUnit.Kilojoules
            ? Number(segment.Peak * 1000 / CycleMeasurements.SampleSpacing) + "\u00a0W"
            : Number(segment.Peak / CycleMeasurements.SampleSpacing) + "\u00a0" + UnitSymbol(unit) + "/s";
        return Number(segment.Total) + "\u00a0" + UnitSymbol(unit) +
            Tr($" · {segment.Slots * CycleMeasurements.SampleSpacing}/60 秒\n3 秒均值峰值 {peak}",
                $" · {segment.Slots * CycleMeasurements.SampleSpacing}/60 s\nPeak 3-second rate {peak}");
    }

    private static void AddSegmentBar(RectTransform rect, double amount, double maximum, float left, float right, Color color)
    {
        if (amount <= 0)
            return;
        float height = (float)(amount / maximum) * 0.81f;
        var bar = Rect("MeasuredBar", rect, new Vector2(left, 0.07f), new Vector2(right, 0.07f + Mathf.Max(0.045f, height)));
        var image = bar.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    private void ShowSlots(FlowUnit unit, BuildingComparison comparison)
    {
        var lines = new List<string>();
        int start = timelineSegment * SlotsPerSegment;
        for (int slot = start; slot < start + SlotsPerSegment; slot++)
        {
            int second = slot * CycleMeasurements.SampleSpacing;
            string output = SlotText(comparison.ProducedMeasurement, unit, slot);
            string input = SlotText(comparison.ConsumedMeasurement, unit, slot);
            lines.Add($"{second}–{second + CycleMeasurements.SampleSpacing}s  " + DirectionalText(output, input));
        }
        AddMessage(details, string.Join("\n", lines), SecondaryColor);
    }

    private static string SlotText(MeasurementSeries series, FlowUnit unit, int slot)
    {
        if (series == null)
            return "";
        return series.IsObserved(slot) ? Number(series.Amount(slot)) + "\u00a0" + UnitSymbol(unit) : "—";
    }
}
