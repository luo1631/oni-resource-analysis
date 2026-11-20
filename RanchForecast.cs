using System;
using Klei.AI;
using Klei;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal sealed class RanchReport
{
    internal readonly string Name;
    internal readonly System.Collections.Generic.List<AnimalForecast> Animals = new();
    internal readonly System.Collections.Generic.List<RanchFeed> Feeds = new();

    internal RanchReport(string name) => Name = name;
}

internal readonly struct RanchFeed
{
    internal readonly string Id;
    internal readonly double KgPerCycle;
    internal readonly double StockKg;

    internal RanchFeed(string id, double kgPerCycle, double stockKg)
    {
        Id = id;
        KgPerCycle = kgPerCycle;
        StockKg = stockKg;
    }
}

internal sealed class AnimalForecast
{
    internal const int WindowCycles = 10;
    internal int Id;
    internal string Name;
    internal string PrefabId;
    internal string Status;
    internal double? Age;
    internal double? NaturalLife;
    internal double? MaturesIn;
    internal double? EggInterval;
    internal double? LifetimeEggs;
    internal double? RemainingEggs;
    internal double? NextEgg;
    internal double? WindowEggs;
    internal double? NaturalReplacement;
    internal string FeedId;
    internal double ReserveCycles;
    internal double WindowCyclesAvailable;
    internal double FertilityProgress;
    internal double? FedBufferEggs;
    internal bool AfterMaturity;

    internal static double EggsWithin(double cycles, double progress, double rate)
    {
        if (!(cycles >= 0) || !(rate > 0) || double.IsInfinity(cycles) || double.IsInfinity(rate))
            return 0;
        // Fertility resets after each egg. A partial charge belongs to the next egg.
        return Math.Floor(Math.Min(1, Math.Max(0, progress)) + cycles * rate + 1e-6);
    }

    internal double? EggsThrough(double cycles)
    {
        if (!EggInterval.HasValue)
            return WindowEggs;
        double breedingTime = Math.Min(cycles, WindowCyclesAvailable) - (MaturesIn ?? 0);
        return breedingTime < 0 ? 0 : EggsWithin(breedingTime, FertilityProgress, 1 / EggInterval.Value);
    }

    internal void ApplyFeedBuffer(RanchFeed feed)
    {
        if (AfterMaturity || !EggInterval.HasValue || !(feed.KgPerCycle > 0))
            return;
        double supported = Math.Min(WindowCyclesAvailable, ReserveCycles + feed.StockKg / feed.KgPerCycle);
        double delay = MaturesIn ?? 0;
        FedBufferEggs = supported >= delay ? EggsWithin(supported - delay, FertilityProgress, 1 / EggInterval.Value) : 0;
    }
}

internal static partial class GameRecipes
{
    private static bool NoCalories(CreatureBrain creature) => creature.GetSMI<CreatureCalorieMonitor.Instance>()?.IsOutOfCalories() == true;

    private static double ActiveEggRate(CreatureBrain creature, ModifierProjection projection)
    {
        var fertility = creature.GetSMI<FertilityMonitor.Instance>();
        if (creature.GetSMI<BabyMonitor.Instance>() != null || fertility?.fertility == null ||
            !FertilityMonitor.IsFertile(fertility) || NoCalories(creature))
            return 0;
        return Math.Max(0, CompletionsPerCycle(fertility.fertility, ProjectedDelta(fertility.fertility, projection)));
    }

    private static AnimalForecast ForecastAnimal(CreatureBrain creature, Tag adultTag, double adultRate, double peerRate)
    {
        var projected = new AnimalForecast
        {
            Id = creature.GetInstanceID(),
            PrefabId = creature.gameObject.PrefabID().ToString(),
            Name = Plain(creature.GetComponent<KSelectable>()?.entityName)
        };
        if (string.IsNullOrWhiteSpace(projected.Name) || projected.Name.StartsWith("MISSING.", StringComparison.OrdinalIgnoreCase))
            projected.Name = Label(projected.PrefabId);

        var age = creature.GetSMI<AgeMonitor.Instance>()?.age;
        if (age != null)
        {
            projected.Age = age.value;
            projected.NaturalLife = age.GetMax();
        }

        var baby = creature.GetSMI<BabyMonitor.Instance>();
        var monitor = creature.GetSMI<FertilityMonitor.Instance>();
        var adult = Assets.TryGetPrefab(adultTag);
        var fertilityDef = monitor?.def ?? adult?.GetDef<FertilityMonitor.Def>();
        var egg = fertilityDef == null ? null : Assets.TryGetPrefab(fertilityDef.eggPrefab);
        var incubation = egg?.GetDef<IncubationMonitor.Def>();
        var young = incubation == null ? null : Assets.TryGetPrefab(incubation.spawnedCreature);
        var youngDef = baby?.def ?? young?.GetDef<BabyMonitor.Def>();
        double aging = PlanningMath.PerCycle(age?.GetDelta() ?? 0);
        bool accelerated = GenericGameSettings.instance.acceleratedLifecycle;
        double? maturityAge = youngDef?.adultPrefab == adultTag ? accelerated ? 0.005 : (double)youngDef.adultThreshold
            : incubation?.spawnedCreature == adultTag ? 0 : (double?)null;
        if (baby != null && maturityAge.HasValue && aging > 0)
            projected.MaturesIn = Math.Max(0, maturityAge.Value - age.value) / aging;

        double incubationPerCycle = PlanningMath.PerCycle(accelerated ? 100.0 / 3 : incubation?.baseIncubationRate ?? 0);
        if (incubationPerCycle > 0 && maturityAge.HasValue && aging > 0)
            projected.NaturalReplacement = 100 / incubationPerCycle + maturityAge.Value / aging;

        if (adultRate > 0)
            projected.Status = Tr("可繁殖 · 按当前畜舍条件", "Fertile · current stable conditions");
        else if (NoCalories(creature))
            projected.Status = Tr("热量耗尽 · 等待补料", "Out of calories · needs feed");
        else if (baby != null)
            projected.Status = peerRate > 0 ? Tr("幼体 · 等待成熟", "Juvenile · maturing") :
                Tr("幼体 · 无可参考的同舍繁殖速率", "Juvenile · no fertile peers to estimate rate");
        else if (fertilityDef == null)
            projected.Status = Tr("无常规产卵配方", "No regular egg-laying recipe");
        else
        {
            if (creature.HasTag(GameTags.Creatures.Confined))
                projected.Status = Tr("封闭 · 繁殖暂停", "Confined · breeding paused");
            else if (creature.GetComponent<Effects>()?.Get("FutureOvercrowded") != null)
                projected.Status = Tr("狭窄 · 房内的蛋占用潜在空间", "Cramped · eggs count toward future crowding");
            else
                projected.Status = Tr("繁殖暂停 · 查看个体状态和幸福度", "Breeding paused · check conditions and happiness");
            projected.WindowEggs = 0;
        }

        double rate = adultRate;
        if (baby != null && !NoCalories(creature) && projected.MaturesIn.HasValue && peerRate > 0)
        {
            rate = peerRate;
            projected.AfterMaturity = true;
            projected.Status += Tr(" · 成年后按同舍个体估算", " · adult estimate from stable peers");
        }
        if (!(rate > 0) || double.IsInfinity(rate))
            return projected;

        projected.EggInterval = 1 / rate;
        if (maturityAge.HasValue && projected.NaturalLife.HasValue && aging > 0)
            projected.LifetimeEggs = AnimalForecast.EggsWithin(
                Math.Max(0, projected.NaturalLife.Value - maturityAge.Value) / aging, 0, rate);

        double fraction = 0;
        if (baby == null && monitor?.fertility != null)
        {
            var fertility = monitor.fertility;
            double range = fertility.GetMax() - fertility.GetMin();
            if (range > 0)
                fraction = Math.Max(0, Math.Min(1, (fertility.value - fertility.GetMin()) / range));
        }
        double lifeLeft = age != null && aging > 0 ? Math.Max(0, age.GetMax() - age.value) / aging : double.PositiveInfinity;
        double delay = projected.MaturesIn ?? 0;
        double next = delay + (1 - fraction) / rate;
        if (next <= lifeLeft)
            projected.NextEgg = next;
        if (!double.IsInfinity(lifeLeft))
            projected.RemainingEggs = lifeLeft >= delay ? AnimalForecast.EggsWithin(lifeLeft - delay, fraction, rate) : 0;
        double window = Math.Min(AnimalForecast.WindowCycles, lifeLeft);
        projected.WindowCyclesAvailable = window;
        projected.FertilityProgress = fraction;
        projected.WindowEggs = window >= delay ? AnimalForecast.EggsWithin(window - delay, fraction, rate) : 0;
        return projected;
    }
}
