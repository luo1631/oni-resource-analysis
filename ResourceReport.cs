using System;
using System.Collections.Generic;

namespace OniResourceBalance;

internal enum FlowUnit { Kilograms, Units, Kilocalories, PlantGrowth, Kilojoules }

[Flags]
internal enum PlanBasis { None = 0, Recipe = 1, CurrentConditions = 2, Capacity = 4, Queue = 8, LongRun = 16, Forecast = 32 }

internal readonly record struct ResourceKey
{
    internal static readonly ResourceKey FoodEnergy = new("OniResourceBalance.FoodEnergy", FlowUnit.Kilocalories);
    internal static readonly ResourceKey GearOil = new("OniResourceBalance.GearOil", FlowUnit.Kilograms);
    internal static readonly ResourceKey Electricity = new("OniResourceBalance.Electricity", FlowUnit.Kilojoules);
    internal static readonly ResourceKey[] Defaults = {
        new("Water", FlowUnit.Kilograms), Electricity, new("Oxygen", FlowUnit.Kilograms)
    };

    internal readonly string Id;
    internal readonly FlowUnit Unit;

    internal ResourceKey(string id, FlowUnit unit)
    {
        Id = id;
        Unit = unit;
    }
}

internal readonly record struct FlowSource
{
    internal readonly string Id;
    internal readonly string Name;
    internal readonly string Recipe;
    internal readonly string Option;
    internal readonly string PrefabId;
    internal readonly string Room;
    internal readonly PlanBasis Basis;
    internal readonly string Detail;

    internal FlowSource(string id, string name, string recipe = "", string option = "", string prefabId = "",
        string room = "", PlanBasis basis = PlanBasis.None, string detail = "")
    {
        Id = id;
        Name = name;
        Recipe = recipe;
        Option = option;
        PrefabId = prefabId;
        Room = room;
        Basis = basis;
        Detail = detail;
    }

    internal FlowSource WithRecipe(string recipe) => new(Id, Name, recipe, Option, PrefabId, Room, Basis, Detail);

    internal FlowSource WithOption(string option) => new(Id, Name, Recipe, option, PrefabId, Room, Basis, Detail);

    internal FlowSource WithBasis(PlanBasis basis) => new(Id, Name, Recipe, Option, PrefabId, Room, basis, Detail);

    internal FlowSource WithDetail(string detail) => new(Id, Name, Recipe, Option, PrefabId, Room, Basis, detail);
}

internal sealed class FlowAmounts
{
    internal double Produced { get; private set; }
    internal double Consumed { get; private set; }
    internal double Net => Produced - Consumed;

    internal void Add(double produced, double consumed)
    {
        Produced += produced;
        Consumed += consumed;
    }
}

internal sealed class ResourceRow
{
    internal const double NetTolerance = 0.0005;
    internal readonly FlowAmounts Totals = new();
    internal readonly Dictionary<FlowSource, FlowAmounts> Sources = new();
    internal bool HasOptions { get; private set; }
    internal bool HasUnknownProduction { get; private set; }
    internal bool HasAveragedOutput { get; private set; }
    internal double? AvailableStock;
    internal double? DeficitRunway => !HasUnknownProduction && Totals.Net <= -NetTolerance ? AvailableStock / -Totals.Net : null;

    internal void AddUnquantifiedOutput(FlowSource source)
    {
        HasUnknownProduction = true;
        Sources.TryAdd(source, new FlowAmounts());
    }

    internal void Add(FlowSource source, double amount, bool output)
    {
        if (!Sources.TryGetValue(source, out var amounts))
        {
            amounts = new FlowAmounts();
            Sources.Add(source, amounts);
        }

        double produced = output ? amount : 0;
        double consumed = output ? 0 : amount;
        amounts.Add(produced, consumed);
        if (source.Option.Length == 0)
        {
            Totals.Add(produced, consumed);
            HasAveragedOutput |= output && (source.Basis & (PlanBasis.LongRun | PlanBasis.Forecast)) != 0;
        }
        else
            HasOptions = true;
    }
}

internal readonly struct MeasuredQuantity
{
    internal readonly double? Amount;
    internal readonly bool Partial;

    internal MeasuredQuantity(double? amount, bool partial)
    {
        Amount = amount;
        Partial = partial;
    }

    internal MeasuredQuantity Add(MeasuredQuantity other) => other.Amount is double amount
        ? new MeasuredQuantity((Amount ?? 0) + amount, Partial || other.Partial)
        : this;
}

internal readonly struct ResourceAverage
{
    internal readonly double Total;
    internal readonly int Samples;
    internal double? Amount => Samples == 0 ? null : Total / Samples;

    internal ResourceAverage(double total, int samples)
    {
        Total = total;
        Samples = samples;
    }
}

internal sealed class BuildingComparison
{
    internal readonly FlowAmounts Theory = new();
    internal readonly FlowAmounts Conditional = new();
    internal readonly FlowAmounts Actual = new();
    internal PlanBasis TheoryBasis;
    internal PlanBasis ConditionalBasis;
    internal MeasurementSeries ProducedMeasurement;
    internal MeasurementSeries ConsumedMeasurement;

    internal MeasuredQuantity Measured(bool output)
    {
        var series = output ? ProducedMeasurement : ConsumedMeasurement;
        return series == null ? default : new MeasuredQuantity(series.Total,
            series.ObservedSlots < CycleMeasurements.SamplesPerCycle);
    }

    internal void AddMeasurement(MeasurementSeries source, double scale, bool output)
    {
        if (output)
        {
            ProducedMeasurement ??= new MeasurementSeries();
            ProducedMeasurement.Merge(source, scale);
            Actual.Add(source.Total * scale, 0);
        }
        else
        {
            ConsumedMeasurement ??= new MeasurementSeries();
            ConsumedMeasurement.Merge(source, scale);
            Actual.Add(0, source.Total * scale);
        }
    }
}

internal sealed class WorldReport
{
    internal readonly int Id;
    internal readonly string Name;
    internal readonly Dictionary<ResourceKey, ResourceRow> Resources = new();
    internal readonly Dictionary<ResourceKey, Dictionary<Tag, BuildingComparison>> Comparisons = new();
    private readonly Dictionary<(ResourceKey Resource, bool Output), ResourceAverage> averages = new();
    internal readonly List<RanchReport> Ranches = new();
    internal MeasurementWindow ActualWindow { get; set; }

    internal WorldReport(int id, string name)
    {
        Id = id;
        Name = name;
    }

    internal void EnsureDefaultResources()
    {
        foreach (var key in ResourceKey.Defaults)
            GetResource(key);
    }

    internal (MeasuredQuantity Produced, MeasuredQuantity Consumed) MeasuredTotals(ResourceKey key)
    {
        MeasuredQuantity produced = default;
        MeasuredQuantity consumed = default;
        if (Comparisons.TryGetValue(key, out var buildings))
        {
            foreach (var building in buildings.Values)
            {
                produced = produced.Add(building.Measured(true));
                consumed = consumed.Add(building.Measured(false));
            }
        }
        return (produced, consumed);
    }

    internal ResourceAverage Average(ResourceKey resource, bool output) =>
        averages.TryGetValue((resource, output), out var average) ? average : default;

    internal void AddAverageSample(ResourceKey resource, bool output, double amount)
    {
        var previous = Average(resource, output);
        GetResource(resource);
        averages[(resource, output)] = new ResourceAverage(previous.Total + amount, previous.Samples + 1);
    }

    internal void Add(ResourceKey resource, FlowSource source, double amount, bool output = false)
    {
        if (string.IsNullOrEmpty(resource.Id) || !(amount > 0) || double.IsInfinity(amount))
            return;

        GetResource(resource).Add(source, amount, output);
        if (resource.Unit == FlowUnit.Kilocalories && resource != ResourceKey.FoodEnergy)
            GetResource(ResourceKey.FoodEnergy).Add(source, amount, output);
    }

    internal void AddUnquantifiedOutput(ResourceKey resource, FlowSource source) =>
        GetResource(resource).AddUnquantifiedOutput(source);

    internal void AddComparison(ResourceKey resource, Tag prefab, double amount, bool output, bool conditional = false,
        PlanBasis basis = PlanBasis.None)
    {
        if (string.IsNullOrEmpty(resource.Id) || !prefab.IsValid || !(amount > 0) || double.IsInfinity(amount))
            return;

        AddComparisonToResource(resource, prefab, amount, output, conditional, basis);
        if (resource.Unit == FlowUnit.Kilocalories && resource != ResourceKey.FoodEnergy)
            AddComparisonToResource(ResourceKey.FoodEnergy, prefab, amount, output, conditional, basis);
    }

    internal void AddMeasurement(ResourceKey resource, Tag prefab, MeasurementSeries series, double scale, bool output)
    {
        if (string.IsNullOrEmpty(resource.Id) || !prefab.IsValid || !(scale > 0) || double.IsInfinity(scale))
            return;

        GetComparison(resource, prefab).AddMeasurement(series, scale, output);
        if (resource.Unit == FlowUnit.Kilocalories && resource != ResourceKey.FoodEnergy)
            GetComparison(ResourceKey.FoodEnergy, prefab).AddMeasurement(series, scale, output);
    }

    private void AddComparisonToResource(ResourceKey resource, Tag prefab, double amount, bool output, bool conditional,
        PlanBasis basis)
    {
        var comparison = GetComparison(resource, prefab);
        if (conditional)
            comparison.ConditionalBasis |= basis;
        else
            comparison.TheoryBasis |= basis;
        var amounts = conditional ? comparison.Conditional : comparison.Theory;
        amounts.Add(output ? amount : 0, output ? 0 : amount);
    }

    private BuildingComparison GetComparison(ResourceKey resource, Tag prefab)
    {
        GetResource(resource);
        if (!Comparisons.TryGetValue(resource, out var devices))
        {
            devices = new Dictionary<Tag, BuildingComparison>();
            Comparisons.Add(resource, devices);
        }
        if (!devices.TryGetValue(prefab, out var comparison))
        {
            comparison = new BuildingComparison();
            devices.Add(prefab, comparison);
        }

        return comparison;
    }

    private ResourceRow GetResource(ResourceKey resource)
    {
        if (!Resources.TryGetValue(resource, out var row))
        {
            row = new ResourceRow();
            Resources.Add(resource, row);
        }

        return row;
    }
}

internal sealed class ResourceReport
{
    internal readonly int Cycle;
    internal readonly float ElapsedSeconds;
    internal readonly List<WorldReport> Worlds = new();

    internal ResourceReport(int cycle, float elapsedSeconds)
    {
        Cycle = cycle;
        ElapsedSeconds = elapsedSeconds;
    }
}

internal static class PlanningMath
{
    internal const double SecondsPerCycle = 600;

    internal static double PerCycle(double ratePerSecond) => ratePerSecond * SecondsPerCycle;

    internal static double PerRecipe(double amount, double recipeSeconds) =>
        recipeSeconds > 0 ? PerCycle(amount / recipeSeconds) : 0;

    internal static double HarvestsWithDelay(double harvestsPerCycle, double waitCycles) =>
        harvestsPerCycle > 0 && waitCycles >= 0 ? harvestsPerCycle / (1 + harvestsPerCycle * waitCycles) : 0;

    internal static double Kilocalories(double calories) => calories / 1000;
    internal static double CycleEnergy(double watts) => PerCycle(watts) / 1000;
    internal static double AverageWatts(double kilojoules) => kilojoules * 1000 / SecondsPerCycle;

    internal static double TurbineWatts(double rated, double ratedFlow, double temperatureFlow, double ratedDifference)
    {
        double nominal = ratedFlow * ratedDifference;
        return nominal > 0 && rated > 0 ? Math.Max(0, Math.Min(rated, rated * temperatureFlow / nominal)) : 0;
    }

    internal static double SteamBatchPowerCeiling(double rated, double steamPerSecond, double stepSeconds)
    {
        double massPerStep = steamPerSecond * stepSeconds;
        if (!(massPerStep > 0))
            return 0;
        // SteamTurbine.EnergySim200ms consumes only when the steam store exceeds 0.1 kg.
        double stepsPerBatch = Math.Floor((double)0.1f / massPerStep) + 1;
        return rated / stepsPerBatch;
    }

    internal static bool NearlyEqual(double first, double second) =>
        Math.Abs(first - second) <= 1e-6 * Math.Max(Math.Abs(first), Math.Abs(second));

    internal static float Median(IEnumerable<float> samples)
    {
        var sorted = new List<float>(samples);
        sorted.Sort();
        int count = sorted.Count;
        return count == 0 ? 0 : (sorted[(count - 1) / 2] + sorted[count / 2]) / 2;
    }

    internal static double QueuePasses(IReadOnlyList<(double Seconds, int Count)> orders, double availableSeconds)
    {
        double timePerPass = 0;
        var finite = new SortedDictionary<int, double>();
        foreach (var (seconds, count) in orders)
        {
            timePerPass += seconds;
            if (count > 0)
            {
                finite.TryGetValue(count, out double queuedSeconds);
                finite[count] = queuedSeconds + seconds;
            }
        }

        double remaining = availableSeconds;
        int passes = 0;
        foreach (var limit in finite)
        {
            double duration = (limit.Key - passes) * timePerPass;
            if (remaining <= duration)
                return passes + remaining / timePerPass;
            remaining -= duration;
            timePerPass -= limit.Value;
            passes = limit.Key;
        }
        return timePerPass > 1e-6 ? passes + remaining / timePerPass : passes;
    }
}
