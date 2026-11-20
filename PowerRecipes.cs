using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static OniResourceBalance.GameText;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private const double SimStep = 0.2;

    private sealed partial class RecipeReader
    {
        private static readonly FieldInfo FuelStorage = typeof(EnergyGenerator).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SteamInlets = typeof(SteamTurbine).GetField("srcCells", BindingFlags.Instance | BindingFlags.NonPublic);

        private static FlowSource Requires(FlowSource source, string condition) =>
            source.Option.Length == 0 ? source.WithOption(condition) : source;

        private static FlowSource FuelledSource(EnergyGenerator generator, FlowSource source)
        {
            if (generator.CircuitID == CircuitManager.INVALID_ID)
                return Requires(source, Tr("发电机待接线", "Generator needs a power circuit"));

            var inputs = generator.formula.inputs;
            if (inputs == null || inputs.Length == 0)
                return source;

            var storage = FuelStorage?.GetValue(generator) as Storage ?? generator.GetComponent<Storage>();
            if (storage == null)
                return Requires(source, Tr("待确认燃料补给", "Fuel supply unconfirmed"));
            foreach (var input in inputs)
            {
                if (input.consumptionRate > 0 && storage.GetMassAvailable(input.tag) < input.consumptionRate * SimStep)
                    return Requires(source, Tr("机内缺少燃料 · ", "Needs fuel · ") + Label(input.tag.ToString()));
            }
            return source;
        }

        private static void ReadBuildingPower(WorldReport report, BuildingComplete building, FlowSource source, FlowSource generation,
            Generator generator, EnergyConsumer load, Battery battery, double workShare, bool queuedLoad)
        {
            // Transformer output is energy forwarded from another circuit.
            if (generator is PowerTransformer or DevGenerator)
                return;

            if (load != null)
            {
                var demand = source.WithRecipe(queuedLoad ? Tr("按加工排单折算工时", "Work time from recipe queue") :
                    Tr("连续运行需求", "Continuous operating demand"))
                    .WithBasis(queuedLoad && workShare > 0 ? PlanBasis.Queue : PlanBasis.Capacity);
                if (load.CircuitID == CircuitManager.INVALID_ID)
                    demand = Requires(demand, Tr("用电设备待接线", "Load needs a power circuit"));
                if (queuedLoad && workShare <= 0)
                {
                    demand = Requires(demand, Tr("等待加工排单 · 满载需求", "Requires a recipe queue · full load"));
                    workShare = 1;
                }
                AddPower(report, demand, load.WattsNeededWhenActive * workShare, false);
            }

            if (battery != null && battery.joulesLostPerSecond > 0)
            {
                var leakage = source.WithRecipe(Tr("电池持电漏损", "Battery rundown"));
                if (battery.JoulesAvailable <= 0)
                    leakage = Requires(leakage, Tr("电池持电时", "When the battery holds charge"));
                report.Add(ResourceKey.Electricity, leakage, PlanningMath.CycleEnergy(battery.joulesLostPerSecond));
            }

            if (generator is SteamTurbine turbine)
            {
                ReadSteamPower(report, building, generation, turbine);
                return;
            }
            if (generator is ReefGeneratorPower tidal)
            {
                ReadTidalPower(report, building, generation, tidal);
                return;
            }
            if (generator == null)
                return;

            double watts = generator.WattageRating;
            var basis = PlanBasis.Capacity;
            if (generator.CircuitID == CircuitManager.INVALID_ID)
                generation = Requires(generation, Tr("发电机待接线", "Generator needs a power circuit"));
            if (generator is SolarPanel solar)
            {
                generation = Requires(generation, solar.CurrentWattage > 0
                    ? Tr("按当前日照 · 随昼夜变化", "Current sunlight · varies by time of day")
                    : Tr("无日照采样 · 额定上限", "No daylight sample · rated ceiling"));
                if (solar.CurrentWattage > 0)
                {
                    watts = solar.CurrentWattage;
                    basis = PlanBasis.CurrentConditions;
                }
            }
            else if (generator is ModuleSolarPanel)
                generation = Requires(generation, Tr("依赖航行日照 · 额定上限", "Requires sunlight in flight · rated ceiling"));
            else if (building.GetComponent<ManualGenerator>() != null)
                generation = Requires(generation, Tr("需要复制人操作 · 额定上限", "Requires duplicant operation · rated ceiling"));
            else if (generator is ElectrobankDischarger)
                generation = Requires(generation, Tr("电池放电 · 储能转移", "Electrobank discharge · stored energy transfer"));
            else if (generator is not EnergyGenerator)
                generation = Requires(generation, Tr("依设备工况 · 额定上限", "Depends on operating conditions · rated ceiling"));

            var production = generation.WithRecipe(Tr("发电", "Generation")).WithBasis(basis);
            if (CycleMeasurements.MeasuresGeneration(generator, building))
                AddPower(report, production, watts, true);
            else
                report.Add(ResourceKey.Electricity, production, PlanningMath.CycleEnergy(watts), true);
        }

        private static void ReadSteamPower(WorldReport report, BuildingComplete building, FlowSource source, SteamTurbine turbine)
        {
            var ports = SteamInlets?.GetValue(turbine) as int[];
            double maxMass = 0;
            double maxTemperature = 0;
            double flow = 0;
            double temperatureFlow = 0;
            int inSteam = 0;
            if (ports != null && ports.Length > 0)
            {
                double ratePerPort = (double)turbine.pumpKGRate / ports.Length;
                foreach (int cell in ports)
                {
                    if (!Grid.IsValidCell(cell) || Grid.Element[cell]?.id != turbine.srcElem)
                        continue;
                    double mass = Grid.Mass[cell];
                    double temperature = Grid.Temperature[cell];
                    double rate = Math.Min(ratePerPort, mass / SimStep);
                    maxMass = Math.Max(maxMass, mass);
                    maxTemperature = Math.Max(maxTemperature, temperature);
                    flow += rate;
                    temperatureFlow += rate * (temperature - turbine.outputElementTemperature);
                    if (mass > 0)
                        inSteam++;
                }
            }

            if (ports == null || ports.Length == 0)
                source = Requires(source, Tr("待确认涡轮进汽口", "Turbine intakes unconfirmed"));
            else if (maxMass < turbine.requiredMass || maxTemperature < turbine.minActiveTemperature)
                source = Requires(source, Tr("等待足量热蒸汽", "Needs enough hot steam"));
            else if (building.GetComponent<PrimaryElement>()?.Temperature >= turbine.maxBuildingTemperature)
                source = Requires(source, Tr("涡轮需要冷却", "Turbine needs cooling"));
            else if (building.GetComponent<ConduitDispenser>() is ConduitDispenser outlet &&
                     (outlet.blocked || !Grid.IsValidCell(building.GetUtilityOutputCell()) ||
                      !Game.Instance.liquidConduitFlow.HasConduit(building.GetUtilityOutputCell())))
                source = Requires(source, Tr("冷凝水出口待畅通", "Condensation outlet needs a clear pipe"));
            else if (turbine.CircuitID == CircuitManager.INVALID_ID)
                source = Requires(source, Tr("涡轮待接线", "Turbine needs a power circuit"));

            double watts = PlanningMath.TurbineWatts(turbine.WattageRating, turbine.pumpKGRate, temperatureFlow,
                turbine.idealSourceElementTemperature - turbine.outputElementTemperature);
        double batchCeiling = PlanningMath.SteamBatchPowerCeiling(turbine.WattageRating, flow, SimStep);
        watts = Math.Min(watts, batchCeiling);
            string note;
            var basis = PlanBasis.CurrentConditions;
            if (maxMass >= turbine.requiredMass && maxTemperature >= turbine.minActiveTemperature && watts > 0)
            {
                note = Tr("入口工况 ", "Steam at ") + $"{inSteam}/{ports?.Length ?? 0}" +
                    Tr(" · 等量冷凝约 ", " inlets · equal-mass condensation about ") +
                    PlanningMath.PerCycle(flow).ToString("0.##") + Tr(" kg/周期", " kg/cycle");
            var details = new List<string>();
            if (batchCeiling < turbine.WattageRating)
                details.Add(Tr($"蒸汽按批次结算 · 当前流量的平均功率上限约 {batchCeiling:0.#} W",
                    $"Steam settles in batches · average power capped near {batchCeiling:0.#} W at this flow"));
            if (ElementLoader.FindElementByHash(turbine.srcElem) is Element steam && temperatureFlow > 0)
            {
                double extracted = temperatureFlow * steam.specificHeatCapacity;
                double body = building.Def.SelfHeatKilowattsWhenActive + extracted * turbine.wasteHeatToTurbinePercent;
                details.Add(Tr($"蒸汽带走约 {extracted:0.#} kDTU/s · 涡轮机体需散热约 {body:0.#} kDTU/s",
                    $"Steam heat removed about {extracted:0.#} kDTU/s · turbine body cooling about {body:0.#} kDTU/s"));
            }
            source = source.WithDetail(string.Join("\n", details));
            }
            else
            {
                watts = turbine.WattageRating;
                basis = PlanBasis.Capacity;
                source = Requires(source, Tr("需要热蒸汽与畅通出口", "Needs hot steam and a clear outlet"));
                note = Tr("额定发电上限 · 蒸汽等量冷凝为水", "Rated ceiling · equal-mass steam condensation");
            }
            AddPower(report, source.WithRecipe(note).WithBasis(basis), watts, true);
        }

        private static void ReadTidalPower(WorldReport report, BuildingComplete building, FlowSource source, ReefGeneratorPower tidal)
        {
            int cell = Grid.PosToCell(building);
            var spring = Grid.IsValidCell(cell) ? Grid.Objects[cell, (int)ObjectLayer.Building] : null;
            if (spring == null || spring.PrefabID() != new Tag(SmallReefGeyserConfig.ID))
                source = Requires(source, Tr("需要潮汐泉", "Needs a tidal spring"));
            else if (tidal.CircuitID == CircuitManager.INVALID_ID)
                source = Requires(source, Tr("涡轮待接线", "Turbine needs a power circuit"));

            double spoutingShare = SmallReefGeyserConfig.APPROXIMATE_EXHALE_TIME_PER_CYCLE / PlanningMath.SecondsPerCycle;
            var generation = source.WithRecipe(Tr("按潮汐泉喷涌时间折算 · 需持续供液",
                "From the spring's spouting time · requires a liquid supply")).WithBasis(PlanBasis.LongRun);
            AddPower(report, generation, tidal.WattageRating * spoutingShare, true);
        }

        private static void AddPower(WorldReport report, FlowSource source, double watts, bool generated)
        {
            double energy = PlanningMath.CycleEnergy(watts);
            report.Add(ResourceKey.Electricity, source, energy, generated);
            report.AddComparison(ResourceKey.Electricity, new Tag(source.PrefabId), energy, generated,
                conditional: source.Option.Length > 0, basis: source.Basis);
        }
    }
}
