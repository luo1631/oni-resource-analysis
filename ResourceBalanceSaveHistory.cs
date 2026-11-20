using System.Collections.Generic;
using System.Linq;
using KSerialization;

namespace OniResourceBalance;

[SerializationConfig(MemberSerialization.OptIn)]
public sealed class ResourceBalanceSaveHistory : KMonoBehaviour
{
    [Serialize] private List<SavedCycle> cycles = new();

    internal void Store(IEnumerable<CycleTotals> history)
    {
        cycles = history.Select(day => new SavedCycle
        {
            Cycle = day.Cycle,
            IncludesBreathing = day.IncludesBreathing,
            Flows = day.Flows.Select(entry => new SavedFlow
            {
                World = entry.Key.WorldId,
                Prefab = entry.Key.Building.ToString(),
                Material = entry.Key.Material.IsValid ? entry.Key.Material.ToString() : "",
                Flow = (int)entry.Key.Flow,
                Amount = entry.Value
            }).ToList()
        }).ToList();
    }

    internal IEnumerable<CycleTotals> Read(int currentCycle)
    {
        if (cycles == null)
            yield break;
        var seen = new HashSet<int>();
        foreach (var day in cycles.Where(day => day?.Flows != null &&
                     day.Cycle >= currentCycle - CycleMeasurements.AverageCycles &&
                     day.Cycle < currentCycle).OrderBy(day => day.Cycle))
        {
            if (!seen.Add(day.Cycle))
                continue;
            var flows = new Dictionary<MeasurementKey, double>(day.Flows.Count);
            foreach (var flow in day.Flows)
            {
                if (flow == null || flow.World < 0 || string.IsNullOrEmpty(flow.Prefab) ||
                    flow.Flow < (int)MeasuredFlow.Generated || flow.Flow > (int)MeasuredFlow.Breathed ||
                    (flow.Flow >= (int)MeasuredFlow.ConvertedOutput && string.IsNullOrEmpty(flow.Material)) ||
                    !(flow.Amount >= 0) || double.IsInfinity(flow.Amount))
                    break;
                var material = string.IsNullOrEmpty(flow.Material) ? default : new Tag(flow.Material);
                var key = new MeasurementKey(flow.World, new Tag(flow.Prefab), material, (MeasuredFlow)flow.Flow);
                if (!flows.TryAdd(key, flow.Amount))
                    break;
            }
            if (flows.Count == day.Flows.Count)
                yield return new CycleTotals(day.Cycle, flows, day.IncludesBreathing);
        }
    }

    [SerializationConfig(MemberSerialization.OptIn)]
    public sealed class SavedCycle
    {
        [Serialize] public int Cycle;
        [Serialize] public bool IncludesBreathing;
        [Serialize] public List<SavedFlow> Flows;
    }

    [SerializationConfig(MemberSerialization.OptIn)]
    public sealed class SavedFlow
    {
        [Serialize] public int World;
        [Serialize] public string Prefab;
        [Serialize] public string Material;
        [Serialize] public int Flow;
        [Serialize] public double Amount;
    }
}
