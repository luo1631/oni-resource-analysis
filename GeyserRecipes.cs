using System;
using System.Collections.Generic;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private static void ReadGeyser(WorldReport report, Geyser geyser, float elapsedSeconds)
    {
        int cell = Grid.PosToCell(geyser);
        if (geyser.configuration == null || !Grid.IsValidCell(cell) || !Grid.IsVisible(cell))
            return;

        var config = geyser.configuration;
        var element = ElementLoader.FindElementByHash(config.GetElement());
        if (element == null)
            return;
        var key = Mass(element.tag);
        var source = Source(geyser.gameObject, Tr("间歇泉", "Vent"));
        var position = Grid.CellToXY(cell);
        string location = Tr("坐标 ", "Cell ") + $"({position.x}, {position.y})";
        string name = Plain(geyser.GetComponent<UserNameable>()?.savedName).Trim();
        if (name.Length > 0 && !source.Name.StartsWith(name + " · ", StringComparison.Ordinal))
            location = name + " · " + location;
        if (geyser.GetComponent<Studyable>()?.Studied != true)
        {
            report.AddUnquantifiedOutput(key, new FlowSource(source.Id, source.Name,
                Tr("待分析", "Analysis pending"), prefabId: source.PrefabId, room: location,
                detail: Tr("分析后可查看此喷口的产率和阶段计时。", "Analyze this vent to see its rates and phase timing.")));
            return;
        }

        var lines = new List<string>();
        double rate = config.GetEmitRate();
        double longRun = config.GetAverageEmission();
        double? burst = EruptionMass(rate, config.GetOnDuration());
        if (burst.HasValue)
        {
            lines.Add(Tr("喷发时 ", "While erupting ") + $"{rate:0.###} kg/s" +
                Tr(" · 单次无阻塞约 ", " · unobstructed burst about ") + $"{burst.Value:0.##} kg");
        }
        double period = config.GetIterationLength();
        if (burst.HasValue && period > 0 && !double.IsInfinity(period))
        {
            double activeYield = PlanningMath.PerCycle(burst.Value / period);
            lines.Add(Tr($"活跃期平均 {activeYield:0.##} kg/周期 · 含喷发间歇",
                $"While active, average {activeYield:0.##} kg/cycle including gaps between bursts"));
        }
        double dormantSeconds = config.GetYearOffDuration();
        if (dormantSeconds > 0 && EruptionMass(longRun, dormantSeconds) is double storage && storage > 0)
        {
            double dormantCycles = dormantSeconds / PlanningMath.SecondsPerCycle;
            lines.Add(Tr($"完整休眠 {dormantCycles:0.##} 周期 · 单靠此泉按长期均值用料，需预留约 {storage:0.##} kg",
                $"Full dormancy {dormantCycles:0.##} cycles · using this vent alone at its long-run rate requires about {storage:0.##} kg stored"));
        }
        var smi = geyser.smi;
        if (smi?.sm != null)
        {
            if (smi.IsInsideState(smi.sm.dormant))
            {
                double dormant = geyser.RemainingDormantTime();
                lines.Add(Tr("休眠中", "Dormant") + TimeRemaining(dormant));
                if (dormant >= PlanningMath.SecondsPerCycle - elapsedSeconds && elapsedSeconds < PlanningMath.SecondsPerCycle)
                    lines.Add(Tr("按当前排程，本周期余下时间仍休眠。", "Scheduled to stay dormant until this cycle ends."));
            }
            else if (smi.IsInsideState(smi.sm.erupt))
            {
                bool blocked = geyser.GetComponent<ElementEmitter>()?.isEmitterBlocked == true;
                double remaining = geyser.RemainingEruptTime();
                lines.Add((blocked ? Tr("喷发阶段 · 过压", "Eruption phase · overpressure") :
                    Tr("喷发阶段", "Eruption phase")) + TimeRemaining(remaining));
                if (remaining > 0 && EruptionMass(rate, Math.Min(remaining, PlanningMath.SecondsPerCycle - elapsedSeconds)) is double potential)
                {
                    lines.Add(Tr("本次喷发在本周期内的无阻塞上限约 ", "Unobstructed ceiling for this burst before cycle end ") +
                        $"{potential:0.##} kg");
                }
            }
            else if (smi.IsInsideState(smi.sm.pre_erupt))
                lines.Add(Tr("压力上升", "Pressure building") + TimeRemaining(geyser.RemainingEruptPreTime()));
            else if (smi.IsInsideState(smi.sm.post_erupt))
                lines.Add(Tr("喷发后休整", "Post-eruption recovery") + TimeRemaining(geyser.RemainingEruptPostTime()));
            else if (smi.IsInsideState(smi.sm.idle))
                lines.Add(Tr("活跃期内闲置", "Idle in the active period") + TimeRemaining(geyser.RemainingIdleTime()));
        }
        lines.Add(Tr("按当前调谐折算。实际排入地格的质量未计量。", "Uses current geotuning. Mass actually released into tiles is unmetered."));

        var estimate = new FlowSource(source.Id, source.Name,
            Tr("无阻塞长期均值 · 含休眠", "Unobstructed long-run mean · includes dormancy"),
            prefabId: source.PrefabId, room: location, basis: PlanBasis.LongRun, detail: string.Join("\n", lines));
        double average = PlanningMath.PerCycle(longRun);
        if (average >= 0 && !double.IsInfinity(average))
            report.Add(key, estimate, average, true);
        else
            report.AddUnquantifiedOutput(key, estimate);
    }

    internal static double? EruptionMass(double rate, double seconds) =>
        rate >= 0 && seconds >= 0 && !double.IsInfinity(rate) && !double.IsInfinity(seconds)
            ? rate * seconds : null;

    private static string TimeRemaining(double seconds) => seconds > 0 && !double.IsInfinity(seconds)
        ? seconds >= PlanningMath.SecondsPerCycle
            ? Tr(" · 余 ", " · ") + $"{seconds / PlanningMath.SecondsPerCycle:0.##}" + Tr(" 周期", " cycles left")
            : Tr(" · 余 ", " · ") + $"{seconds:0.#}" + Tr(" 游戏秒", " game seconds left")
        : "";
}
