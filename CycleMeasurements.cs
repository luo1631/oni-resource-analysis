using System;
using System.Collections.Generic;

namespace OniResourceBalance;

internal enum MeasuredFlow { Generated, Consumed, ConvertedOutput, ConvertedInput, RanchEgg, Breathed }

internal readonly record struct MeasurementKey
{
    internal readonly int WorldId;
    internal readonly Tag Building;
    internal readonly Tag Material;
    internal readonly MeasuredFlow Flow;

    internal MeasurementKey(int worldId, Tag building, Tag material, MeasuredFlow flow)
    {
        WorldId = worldId;
        Building = building;
        Material = material;
        Flow = flow;
    }
}

internal sealed class CycleMeasurements
{
    internal const int SampleSpacing = 3;
    internal const int SamplesPerCycle = (int)PlanningMath.SecondsPerCycle / SampleSpacing;
    internal const int AverageCycles = 10;
    private const int ColonyObservationSlots = 10;
    internal static readonly Tag Oxygen = new("Oxygen");
    internal static readonly Tag PollutedOxygen = new("ContaminatedOxygen");

    private sealed class Device
    {
        internal readonly BuildingComplete Building;
        internal readonly Tag Prefab;
        internal readonly Generator Generator;
        internal readonly EnergyConsumer Load;
        internal readonly ElementConverter[] Converters;

        internal Device(BuildingComplete building, Generator generator, EnergyConsumer load, ElementConverter[] converters)
        {
            Building = building;
            Prefab = building.gameObject.PrefabID();
            Generator = generator;
            Load = load;
            Converters = converters;
        }
    }

    private readonly Dictionary<BuildingComplete, Device> devices = new();
    private readonly Dictionary<KMonoBehaviour, Device> owners = new();
    private readonly Dictionary<MinionIdentity, (OxygenBreather Breather, Tag Prefab,
        Action<SimHashes, float, float, byte, int> Consumed)> breathers = new();
    private readonly List<ElementConverter> converters = new();
    private readonly Queue<CycleTotals> recentCycles = new(AverageCycles);
    private readonly Action<object> collectSample;
    private MeasurementWindow current;
    private ResourceBalanceSaveHistory history;
    private GameClock clock;
    private SchedulerHandle pendingSample;
    private int nextSlot;
    private bool jumping;

    internal MeasurementWindow Previous { get; private set; }
    internal IEnumerable<CycleTotals> RecentCycles => recentCycles;
    internal bool IsCollecting => !jumping && current != null && current.Cycle == clock.GetCycle();

    internal CycleMeasurements() => collectSample = TakeSample;

    internal void Begin(GameClock gameClock, ResourceBalanceSaveHistory savedHistory)
    {
        clock = gameClock;
        history = savedHistory;
        if (history != null)
        {
            foreach (var cycle in history.Read(clock.GetCycle()))
                recentCycles.Enqueue(cycle);
        }
        Components.BuildingCompletes.OnAdd += Track;
        Components.BuildingCompletes.OnRemove += Untrack;
        foreach (var building in Components.BuildingCompletes.Items)
            Track(building);
        Components.LiveMinionIdentities.OnAdd += TrackBreather;
        Components.LiveMinionIdentities.OnRemove += UntrackBreather;
        foreach (var minion in Components.LiveMinionIdentities.Items)
            TrackBreather(minion);

        StartWindow(atBoundary: false);
    }

    internal void End()
    {
        CancelSample();
        if (clock != null)
        {
            Components.BuildingCompletes.OnAdd -= Track;
            Components.BuildingCompletes.OnRemove -= Untrack;
            Components.LiveMinionIdentities.OnAdd -= TrackBreather;
            Components.LiveMinionIdentities.OnRemove -= UntrackBreather;
        }
        clock = null;
        history = null;
        devices.Clear();
        owners.Clear();
        foreach (var device in breathers.Values)
            device.Breather.onBreathableGasConsumed -= device.Consumed;
        breathers.Clear();
        converters.Clear();
        recentCycles.Clear();
        current = Previous = null;
        jumping = false;
    }

    private void CancelSample()
    {
        if (pendingSample.IsValid)
            pendingSample.ClearScheduler();
        pendingSample = default;
    }

    internal void Advance()
    {
        int cycle = clock.GetCycle();
        if (current?.Cycle == cycle)
            return;

        bool historyChanged = false;
        if (current != null && cycle < current.Cycle)
        {
            recentCycles.Clear();
            historyChanged = true;
        }
        while (recentCycles.Count > 0 && recentCycles.Peek().Cycle < cycle - AverageCycles)
        {
            recentCycles.Dequeue();
            historyChanged = true;
        }
        Previous = current?.Cycle == cycle - 1 && current.ObservedSlots > 0 ? current : null;
        Previous?.Close();
        if (Previous?.IsComplete == true)
        {
            recentCycles.Enqueue(new CycleTotals(Previous));
            historyChanged = true;
        }
        if (historyChanged && history != null)
            history.Store(recentCycles);

        CancelSample();
        StartWindow(atBoundary: !jumping);
    }

    internal void BeginTimeJump()
    {
        if (current != null)
        {
            int slot = (int)(clock.GetTimeSinceStartOfCycle() / SampleSpacing);
            if (slot >= 0 && slot < SamplesPerCycle)
                current.InvalidateSlot(slot);
        }
        jumping = true;
        CancelSample();
    }

    internal void EndTimeJump()
    {
        jumping = false;
        if (current == null)
            return;

        nextSlot = Math.Max(nextSlot, FirstWholeSlot());
        ScheduleNextSample();
    }

    private void StartWindow(bool atBoundary)
    {
        current = new MeasurementWindow(clock.GetCycle());
        nextSlot = atBoundary ? 0 : FirstWholeSlot();
        ScheduleNextSample();
    }

    private int FirstWholeSlot()
    {
        float elapsed = clock.GetTimeSinceStartOfCycle();
        return Math.Max(0, (int)Math.Ceiling(elapsed / SampleSpacing));
    }

    private float NextSampleTime => (nextSlot + 0.5f) * SampleSpacing;

    private void ScheduleNextSample()
    {
        if (jumping || nextSlot >= SamplesPerCycle || GameScheduler.Instance == null)
            return;
        float delay = Math.Max(0, NextSampleTime - clock.GetTimeSinceStartOfCycle());
        pendingSample = GameScheduler.Instance.Schedule("Resource balance observation", delay, collectSample);
    }

    private void TakeSample(object _)
    {
        pendingSample = default;
        if (jumping || current == null || current.Cycle != clock.GetCycle())
            return;

        float elapsed = clock.GetTimeSinceStartOfCycle();
        nextSlot = Math.Max(nextSlot, (int)(elapsed / SampleSpacing));
        if (nextSlot < SamplesPerCycle && elapsed >= NextSampleTime - 0.01f)
        {
            try
            {
                ObserveDevices();
                if (nextSlot % ColonyObservationSlots == 0)
                {
                    ObserveRanches();
                    ObserveBreathers();
                }
                current.ObserveSlot(nextSlot);
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("[OniResourceBalance] An observation slot was incomplete. " + error);
            }
            nextSlot++;
        }
        ScheduleNextSample();
    }

    private void ObserveDevices()
    {
        foreach (var device in devices.Values)
        {
            var building = device.Building;
            if (building == null || !building.isActiveAndEnabled)
                continue;

            int worldId = building.GetMyWorldId();
            if (worldId < 0)
                continue;

            var prefab = device.Prefab;
            if (device.Generator != null && device.Generator.isSpawned)
            {
                current.GetFlow(new MeasurementKey(worldId, prefab, default, MeasuredFlow.Generated));
                if (MeasureFuel.Available && device.Generator is EnergyGenerator fuelled)
                    ObserveFuel(worldId, prefab, fuelled);
            }
            if (device.Load != null && device.Load.isSpawned)
                current.GetFlow(new MeasurementKey(worldId, prefab, default, MeasuredFlow.Consumed));
            foreach (var converter in device.Converters)
            {
                if (converter == null || !converter.isSpawned)
                    continue;

                if (converter.consumedElements != null)
                {
                    foreach (var input in converter.consumedElements)
                    {
                        if (input.Accumulator.IsValid() && input.Tag.IsValid)
                            current.GetFlow(new MeasurementKey(worldId, prefab, input.Tag, MeasuredFlow.ConvertedInput));
                    }
                }
                if (converter.outputElements != null)
                {
                    foreach (var output in converter.outputElements)
                    {
                        if (output.accumulator.IsValid() && ElementLoader.FindElementByHash(output.elementHash) is Element element)
                            current.GetFlow(new MeasurementKey(worldId, prefab, element.tag, MeasuredFlow.ConvertedOutput));
                    }
                }
            }
        }
    }

    private void ObserveFuel(int worldId, Tag prefab, EnergyGenerator generator)
    {
        foreach (var input in generator.formula.inputs ?? Array.Empty<EnergyGenerator.InputItem>())
        {
            if (input.consumptionRate > 0 && input.tag.IsValid)
                current.GetFlow(new MeasurementKey(worldId, prefab, input.tag, MeasuredFlow.ConvertedInput));
        }
        foreach (var output in generator.formula.outputs ?? Array.Empty<EnergyGenerator.OutputItem>())
        {
            if (output.creationRate > 0 && ElementLoader.FindElementByHash(output.element) is Element element)
                current.GetFlow(new MeasurementKey(worldId, prefab, element.tag, MeasuredFlow.ConvertedOutput));
        }
    }

    private void ObserveRanches()
    {
        foreach (var brain in Components.Brains.Items)
        {
            if (brain is not CreatureBrain creature || GameRecipes.StableRoom(creature) == null)
                continue;
            int worldId = creature.GetMyWorldId();
            var adult = GameRecipes.AdultCritter(creature);
            var fertility = creature.GetSMI<FertilityMonitor.Instance>();
            var definition = fertility?.def ?? Assets.TryGetPrefab(adult)?.GetDef<FertilityMonitor.Def>();
            if (worldId < 0 || definition == null)
                continue;

            ObserveEgg(definition.eggPrefab);
            if ((fertility?.breedingChances ?? definition.initialBreedingWeights) is { } chances)
            {
                foreach (var chance in chances)
                    ObserveEgg(chance.egg);
            }

            void ObserveEgg(Tag egg)
            {
                if (egg.IsValid)
                    current.GetFlow(new MeasurementKey(worldId, adult, egg, MeasuredFlow.RanchEgg));
            }
        }
    }

    internal void RecordRanchEgg(CreatureBrain creature, Tag egg)
    {
        if (IsCollecting && egg.IsValid && GameRecipes.StableRoom(creature) != null)
            Add(creature.GetMyWorldId(), GameRecipes.AdultCritter(creature), egg, MeasuredFlow.RanchEgg, 1);
    }

    private void TrackBreather(MinionIdentity minion)
    {
        if (minion == null || breathers.ContainsKey(minion) || !minion.TryGetComponent<OxygenBreather>(out var breather))
            return;
        Tag prefab = minion.gameObject.PrefabID();
        void Consume(SimHashes gas, float mass, float _, byte __, int ___)
        {
            if (IsCollecting && minion != null && mass > 0 && !float.IsInfinity(mass) &&
                ElementLoader.FindElementByHash(gas) is Element element)
                Add(minion.GetMyWorldId(), prefab, element.tag, MeasuredFlow.Breathed, mass);
        }
        breather.onBreathableGasConsumed += Consume;
        breathers.Add(minion, (breather, prefab, Consume));
    }

    private void UntrackBreather(MinionIdentity minion)
    {
        if (minion is not null && breathers.Remove(minion, out var device))
            device.Breather.onBreathableGasConsumed -= device.Consumed;
    }

    private void ObserveBreathers()
    {
        foreach (var (minion, device) in breathers)
        {
            if (minion == null || !device.Breather.isSpawned || !device.Breather.O2Accumulator.IsValid())
                continue;
            int worldId = minion.GetMyWorldId();
            if (worldId < 0)
                continue;
            // Both vanilla breathable gases are covered by the same callback, including suit supply.
            current.GetFlow(new MeasurementKey(worldId, device.Prefab, Oxygen, MeasuredFlow.Breathed));
            current.GetFlow(new MeasurementKey(worldId, device.Prefab, PollutedOxygen, MeasuredFlow.Breathed));
        }
    }

    internal void Record(KMonoBehaviour component, MeasuredFlow flow, double amount, Tag material = default)
    {
        if (jumping || current == null || !(amount > 0) || double.IsInfinity(amount) ||
            !owners.TryGetValue(component, out var device))
            return;

        Record(device, flow, amount, material);
    }

    internal void RecordMaterialOutput(KMonoBehaviour component, SimHashes hash, float mass)
    {
        if (jumping || current == null || !(mass > 0) || float.IsInfinity(mass) ||
            !owners.TryGetValue(component, out var device) || ElementLoader.FindElementByHash(hash) is not Element element)
            return;

        Record(device, MeasuredFlow.ConvertedOutput, mass, element.tag);
    }

    private void Record(Device device, MeasuredFlow flow, double amount, Tag material)
    {
        if (device.Building == null || !IsCollecting)
            return;

        Add(device.Building.GetMyWorldId(), device.Prefab, material, flow, amount);
    }

    private void Add(int worldId, Tag prefab, Tag material, MeasuredFlow flow, double amount)
    {
        float elapsed = clock.GetTimeSinceStartOfCycle();
        if (worldId >= 0 && elapsed >= 0 && elapsed < PlanningMath.SecondsPerCycle)
        {
            int slot = (int)(elapsed / SampleSpacing);
            current.GetFlow(new MeasurementKey(worldId, prefab, material, flow)).Add(slot, amount);
        }
    }

    internal static bool MeasuresGeneration(Generator generator, BuildingComplete building) =>
        generator is SteamTurbine or ReefGeneratorPower or SolarPanel or ModuleSolarPanel or EnergyGenerator ||
        (generator != null && building.TryGetComponent<ManualGenerator>(out _));

    private void Track(BuildingComplete building)
    {
        if (building == null)
            return;

        var generator = building.GetComponent<Generator>();
        if (!MeasuresGeneration(generator, building))
            generator = null;
        var load = building.GetComponent<EnergyConsumer>();
        converters.Clear();
        if (MeasureConversions.Available)
            building.GetComponents(converters);
        if (generator == null && load == null && converters.Count == 0)
            return;

        Untrack(building);
        var device = new Device(building, generator, load,
            converters.Count > 0 ? converters.ToArray() : Array.Empty<ElementConverter>());
        devices.Add(building, device);
        if (generator != null)
            owners.Add(generator, device);
        if (load != null)
            owners.Add(load, device);
        foreach (var converter in device.Converters)
            owners.Add(converter, device);
    }

    private void Untrack(BuildingComplete building)
    {
        if (!devices.TryGetValue(building, out var device))
            return;
        devices.Remove(building);
        if (device.Generator is not null)
            owners.Remove(device.Generator);
        if (device.Load is not null)
            owners.Remove(device.Load);
        foreach (var converter in device.Converters)
            owners.Remove(converter);
    }
}
