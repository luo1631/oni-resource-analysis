using System.Collections.Generic;

namespace OniResourceBalance;

internal sealed class MeasurementCoverage
{
    private readonly bool[] observed = new bool[CycleMeasurements.SamplesPerCycle];
    internal int Slots { get; private set; }
    internal bool IsObserved(int slot) => observed[slot];

    internal void Set(int slot, bool value)
    {
        if (observed[slot] == value)
            return;
        observed[slot] = value;
        Slots += value ? 1 : -1;
    }
}

internal sealed class MeasurementSeries
{
    private readonly MeasurementCoverage coverage;
    private double[] amounts;

    internal double Total { get; private set; }
    internal int ObservedSlots => coverage.Slots;
    internal int ObservedSeconds => ObservedSlots * CycleMeasurements.SampleSpacing;
    internal bool IsObserved(int slot) => coverage.IsObserved(slot);
    internal double Amount(int slot) => amounts?[slot] ?? 0;

    internal MeasurementSeries() : this(new MeasurementCoverage()) { }
    internal MeasurementSeries(MeasurementCoverage coverage) => this.coverage = coverage;

    internal void Add(int slot, double amount)
    {
        amounts ??= new double[CycleMeasurements.SamplesPerCycle];
        amounts[slot] += amount;
        Total += amount;
    }

    internal void KeepObservedAmounts()
    {
        if (amounts == null || ObservedSlots == amounts.Length)
            return;
        Total = 0;
        for (int slot = 0; slot < amounts.Length; slot++)
        {
            if (coverage.IsObserved(slot))
                Total += amounts[slot];
            else
                amounts[slot] = 0;
        }
    }

    internal void Merge(MeasurementSeries source, double scale)
    {
        for (int slot = 0; slot < CycleMeasurements.SamplesPerCycle; slot++)
        {
            if (!source.IsObserved(slot))
                continue;
            coverage.Set(slot, true);
            double amount = source.Amount(slot) * scale;
            if (amount > 0)
                Add(slot, amount);
        }
    }
}

internal sealed class MeasurementWindow
{
    internal readonly int Cycle;
    internal readonly Dictionary<MeasurementKey, MeasurementSeries> Flows = new();
    private readonly MeasurementCoverage coverage = new();
    internal int ObservedSlots => coverage.Slots;
    internal bool IsComplete => ObservedSlots == CycleMeasurements.SamplesPerCycle;
    internal int ObservedSeconds => ObservedSlots * CycleMeasurements.SampleSpacing;

    internal MeasurementWindow(int cycle) => Cycle = cycle;

    internal void ObserveSlot(int slot) => coverage.Set(slot, true);
    internal void InvalidateSlot(int slot) => coverage.Set(slot, false);

    internal void Close()
    {
        foreach (var series in Flows.Values)
            series.KeepObservedAmounts();
    }

    internal MeasurementSeries GetFlow(MeasurementKey key)
    {
        if (!Flows.TryGetValue(key, out var series))
        {
            // A hook observes its entire scope even before a device is built or after it is removed.
            series = new MeasurementSeries(coverage);
            Flows.Add(key, series);
        }
        return series;
    }
}

internal sealed class CycleTotals
{
    internal readonly int Cycle;
    internal readonly Dictionary<MeasurementKey, double> Flows;
    internal readonly bool IncludesBreathing;

    internal CycleTotals(MeasurementWindow window)
    {
        Cycle = window.Cycle;
        IncludesBreathing = true;
        Flows = new Dictionary<MeasurementKey, double>(window.Flows.Count);
        foreach (var entry in window.Flows)
            Flows.Add(entry.Key, entry.Value.Total);
    }

    internal CycleTotals(int cycle, Dictionary<MeasurementKey, double> flows, bool includesBreathing)
    {
        Cycle = cycle;
        Flows = flows;
        IncludesBreathing = includesBreathing;
    }
}
