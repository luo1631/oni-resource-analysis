using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klei.AI;
using UnityEngine;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    internal static ResourceReport Read(int cycle, float elapsedSeconds, CycleMeasurements measurements)
    {
        var report = new ResourceReport(cycle, elapsedSeconds);
        var window = measurements.Previous;
        var observed = window?.Flows.ToLookup(pair => pair.Key.WorldId);
        foreach (var world in ClusterManager.Instance.WorldContainers)
        {
            if (world == null || (!world.IsDiscovered && world.id != ClusterManager.Instance.activeWorldId))
                continue;

            string name = world.GetComponent<ClusterGridEntity>()?.Name ?? world.worldName;
            var result = new WorldReport(world.id, string.IsNullOrWhiteSpace(name) ? world.id.ToString() : GameText.Plain(name));
            result.ActualWindow = window;
            var reader = new RecipeReader();
            reader.ReadBuildings(result, Components.BuildingCompletes.WorldItemsEnumerate(world.id, false));
            reader.ReadColony(result, Components.LiveMinionIdentities.WorldItemsEnumerate(world.id, false));
            reader.ReadCreatures(result, Components.Brains.WorldItemsEnumerate(world.id, false));
            reader.ReadPlants(result, Components.PlantablePlots.GetItems(world.id));
            if (observed != null)
                reader.ReadMeasurements(result, observed[world.id]);
            reader.ReadAverages(result, measurements.RecentCycles);
            foreach (var geyser in Components.Geysers.GetItems(world.id))
            {
                if (geyser != null)
                    ReadGeyser(result, geyser, elapsedSeconds);
            }
            result.EnsureDefaultResources();
            reader.ReadInventory(result, world.worldInventory);
            report.Worlds.Add(result);
        }
        report.Worlds.Sort((a, b) => a.Id.CompareTo(b.Id));
        return report;
    }

    private sealed partial class RecipeReader
    {
        private readonly struct Material
        {
            internal readonly ResourceKey Key;
            internal readonly double KcalPerUnit;
            internal readonly double KgPerUnit;

            internal Material(ResourceKey key, double kcalPerUnit, double kgPerUnit)
            {
                Key = key;
                KcalPerUnit = kcalPerUnit;
                KgPerUnit = kgPerUnit;
            }

            internal double Amount(double quantity, bool byMass) => Key.Unit switch
            {
                FlowUnit.Kilograms when !byMass => quantity * KgPerUnit,
                FlowUnit.Units when byMass => quantity / KgPerUnit,
                FlowUnit.Kilocalories => (byMass ? quantity / KgPerUnit : quantity) * KcalPerUnit,
                _ => quantity
            };
        }

        private readonly Dictionary<Tag, Material> materials = new();
        private readonly List<ElementConverter> converters = new();

        internal void ReadInventory(WorldReport report, WorldInventory inventory)
        {
            if (inventory == null || !inventory.HasValidCount)
                return;

            foreach (var entry in report.Resources)
            {
                var key = entry.Key;
                if (key == ResourceKey.FoodEnergy)
                {
                    double availableCalories = 0;
                    foreach (var food in inventory.GetAccessibleAmounts())
                    {
                        if (food.Value <= 0)
                            continue;
                        var material = GetMaterial(food.Key);
                        if (material.Key.Unit == FlowUnit.Kilocalories)
                            availableCalories += material.Amount(inventory.GetAmount(food.Key, false), false);
                    }
                    entry.Value.AvailableStock = availableCalories;
                    continue;
                }

                if (key.Unit is FlowUnit.Kilojoules or FlowUnit.PlantGrowth || key == ResourceKey.GearOil)
                    continue;
                Tag tag = new(key.Id);
                if (Assets.TryGetPrefab(tag) == null && ElementLoader.FindElementByTag(tag) == null)
                    continue;

                var stored = GetMaterial(tag);
                if (key == stored.Key)
                    entry.Value.AvailableStock = stored.Amount(inventory.GetAmount(tag, false), false);
            }
        }

        private void ReadBuilding(WorldReport report, BuildingComplete building, ComplexFabricator fabricator, float workerBonus, bool enabled)
        {
            FindConverters(building);
            var power = building.GetComponent<Generator>();
            var load = building.GetComponent<EnergyConsumer>();
            var battery = building.GetComponent<Battery>();
            var oilWell = building.GetComponent<OilWellCap>();
            if (converters.Count == 0 && power == null && load == null && battery == null && fabricator == null && oilWell == null)
                return;
            var source = GameText.Source(building.gameObject, GameText.Tr("建筑", "Building"), room: FindRoom(building));
            if (!enabled)
                source = source.WithOption(GameText.Tr("已手动停用", "Manually disabled"));
            else if (building.GetComponent<Operational>()?.IsFunctional == false)
                source = source.WithOption(GameText.Tr("设备未就绪", "Building not functional"));
            double duty = oilWell == null ? 1 : ReadOilWellGas(report, source, oilWell);
            var conversion = oilWell == null ? source : source.WithRecipe(GameText.Tr("含泄压时间", "Including pressure release"));
            if (building.Def.PrefabID == CompostConfig.ID)
                conversion = source.WithRecipe(GameText.Tr("满负荷堆肥 · 需供料与翻堆", "Continuous composting · needs feedstock and turning"));
            ReadConverters(report, conversion, duty, building.gameObject.PrefabID());
            var generation = source;
            if (power is EnergyGenerator generator)
            {
                generation = FuelledSource(generator, source).WithBasis(PlanBasis.Recipe);
                foreach (var input in generator.formula.inputs ?? Array.Empty<EnergyGenerator.InputItem>())
                    AddMaterial(report, generation, input.tag, PlanningMath.PerCycle(input.consumptionRate), false,
                        byMass: true, compare: MeasureFuel.Available);
                foreach (var output in generator.formula.outputs ?? Array.Empty<EnergyGenerator.OutputItem>())
                {
                    var element = ElementLoader.FindElementByHash(output.element);
                    if (element == null)
                        continue;
                    var key = Mass(element.tag);
                    double quantity = PlanningMath.PerCycle(output.creationRate);
                    report.Add(key, generation, quantity, true);
                    if (MeasureFuel.Available)
                        report.AddComparison(key, building.gameObject.PrefabID(), quantity, true,
                            conditional: generation.Option.Length > 0, basis: generation.Basis);
                }
            }
            double workShare = fabricator == null ? duty : ReadFabricator(report, source, fabricator, workerBonus);
            ReadBuildingPower(report, building, source, generation, power, load, battery, workShare, fabricator != null);
        }

        internal void ReadPlants(WorldReport report, List<PlantablePlot> plots)
        {
            // KPrefabID equality compares prefab tags rather than individual plants.
            var seen = new HashSet<int>();
            var plants = new List<(KPrefabID Plant, Growing Growing, Room Room, bool Eligible, EffectInstance Effect)>();
            foreach (var plot in plots)
            {
                if (plot == null || !plot.ValidPlant)
                    continue;
                var plant = plot.plant;
                if (plant == null || plant.HasTag(GameTags.Uprooted) || plant.HasTag(GameTags.Dead) || !seen.Add(plant.GetInstanceID()))
                    continue;
                var growing = plant.GetComponent<Growing>();
                if (growing != null && growing.IsWildPlanted())
                    continue;

                var room = FindRoom(plant);
                var tinker = plant.GetComponent<Tinkerable>();
                bool canTend = tinker != null && tinker.addedEffect == "FarmTinker" &&
                    room?.roomType == Db.Get().RoomTypes.Farm;
                var effect = tinker == null ? null : plant.GetComponent<Effects>()?.Get("FarmTinker");
                plants.Add((plant, growing, room, canTend, effect));
            }

            foreach (var group in plants.GroupBy(entry => (entry.Plant.PrefabTag, entry.Room)))
            {
                var eligible = group.Where(entry => entry.Eligible).ToArray();
                var active = eligible.Where(entry => entry.Effect != null).ToArray();
                bool? majority = active.Length > eligible.Length / 2 ? true :
                    eligible.Length - active.Length > eligible.Length / 2 ? false : null;
                var tended = active.FirstOrDefault().Effect?.effect.SelfModifiers;
                foreach (var (plant, growing, room, canTend, effect) in group)
                {
                    bool hasEffect = effect != null;
                    bool planned = canTend && majority.HasValue ? majority.Value : hasEffect;
                    var projection = planned == hasEffect ? default :
                        planned ? new ModifierProjection(null, tended) : new ModifierProjection(effect.effect.SelfModifiers, null);
                    ReadPlant(report, plant, growing, room, planned, projection);
                }
            }
        }

        private void ReadPlant(WorldReport report, KPrefabID plant, Growing growing, Room room, bool tended, ModifierProjection projection)
        {
            var source = GameText.Source(plant.gameObject, GameText.Tr("栽培植物", "Farmed plant"), room: room);
            FindConverters(plant);
            ReadConverters(report, source);
            AddPlantInputs(report, source, plant.GetDef<IrrigationMonitor.Def>()?.consumedElements);
            AddPlantInputs(report, source, plant.GetDef<FertilizationMonitor.Def>()?.consumedElements);
            if (tended)
                ReadTendingCost(report, source, plant.GetComponent<Tinkerable>());

            var maturity = growing == null ? null : Db.Get().Amounts.Maturity.Lookup(plant);
            var growthModifier = growing?.smi?.baseGrowingRate;
            double progressRate = ProjectedDelta(maturity, projection, growthModifier);
            bool stalled = growing != null && !growing.IsGrowing() && !growing.IsGrown();
            var growth = stalled ? source.WithOption(GameText.Tr("等待生长条件", "Waiting for growth conditions")) : source;
            if (plant.TryGetComponent<DirectlyEdiblePlant_Growth>(out _))
                report.Add(PlantGrowth(plant.PrefabTag), growth, PlantGrowthPerCycle(plant.PrefabTag, maturity, progressRate), true);

            var crop = plant.GetComponent<Crop>()?.cropVal;
            if (crop?.products == null || !crop.renewable)
                return;
            double harvests = maturity != null && growthModifier != null
                ? CompletionsPerCycle(maturity, progressRate)
                : PlanningMath.PerRecipe(1, crop.cropDuration);
            string timing = growing?.IsGrown() == true ? GameText.Tr("已可采收", "Ready for harvest") :
                maturity != null && progressRate > 0 && !stalled
                    ? GameText.Tr("距成熟约 ", "Ripens in about ") +
                      (Math.Max(0, maturity.GetMax() - maturity.value) / PlanningMath.PerCycle(progressRate)).ToString("0.##") + GameText.Tr(" 周期", " cycles")
                    : GameText.Tr("可重复采收", "Renewable harvest");
            if (plant.GetComponent<HarvestDesignatable>() is { HarvestWhenReady: false } designation)
            {
                var oldAge = growing?.shouldGrowOld == true ? Db.Get().Amounts.OldAge.Lookup(plant) : null;
                var aging = growing?.smi?.getOldRate;
                double agePerCycle = aging == null ? 0 : PlanningMath.PerCycle(ProjectedDelta(oldAge, baseline: aging));
                double wait = oldAge == null || !(agePerCycle > 0) || double.IsInfinity(agePerCycle)
                    ? double.NaN : (oldAge.GetMax() - oldAge.GetMin()) / agePerCycle;
                if (!(wait >= 0) || double.IsInfinity(wait))
                {
                    growth = Requires(growth, GameText.Tr("自动采收关闭 · 需确认采收", "Auto-harvest off · harvesting unconfirmed"));
                }
                else
                {
                    harvests = PlanningMath.HarvestsWithDelay(harvests, wait);
                    growth = growth.WithBasis(PlanBasis.LongRun);
                    if (growing.IsGrown() && !designation.MarkedForHarvest)
                    {
                        double left = Math.Max(0, oldAge.GetMax() - oldAge.value) / agePerCycle;
                        timing = GameText.Tr($"已成熟 · 约 {left:0.##} 周期后自然掉果",
                            $"Ripe · natural drop in about {left:0.##} cycles");
                    }
                    timing += GameText.Tr($" · 长期按成熟后等待 {wait:0.##} 周期",
                        $" · long-run rate includes {wait:0.##} cycles waiting after ripening");
                }
            }
            double yield = Db.Get().PlantAttributes.YieldFactor.Lookup(plant)?.GetTotalValue() ?? 1;
            if (!PlanningMath.NearlyEqual(yield, 1))
                growth = growth.WithDetail(GameText.Tr($"每次采收按产量倍率 ×{yield:0.##}", $"Harvest yield multiplier ×{yield:0.##}"));
            var harvest = growth.WithRecipe(tended ? timing + GameText.Tr(" · 农夫之触", " · Farmer's Touch") : timing);
            foreach (var product in crop.products)
                AddMaterial(report, harvest, new Tag(product.cropId), product.quantity * yield * harvests, true);
        }

        private void FindConverters(Component entity)
        {
            converters.Clear();
            entity.GetComponents(converters);
        }

        private void ReadConverters(WorldReport report, FlowSource source, double duty = 1, Tag measuredBuilding = default)
        {
            source = source.WithBasis(PlanBasis.Recipe);
            foreach (var converter in converters)
            {
                var inputs = converter.consumedElements ?? Array.Empty<ElementConverter.ConsumedElement>();
                if (inputs.Length > 0 && !inputs.Any(input => input.IsActive))
                    continue;

                foreach (var input in inputs)
                {
                    if (!input.IsActive)
                        continue;

                    var material = GetMaterial(input.Tag);
                    double quantity = material.Amount(PlanningMath.PerCycle(converter.GetMassConsumptionRateWithModifiers(input) * duty), true);
                    report.Add(material.Key, source, quantity);
                    if (measuredBuilding.IsValid)
                        report.AddComparison(material.Key, measuredBuilding, quantity, false,
                            conditional: source.Option.Length > 0, basis: source.Basis);
                }
                foreach (var output in converter.outputElements ?? Array.Empty<ElementConverter.OutputElement>())
                {
                    if (!output.IsActive)
                        continue;

                    var element = ElementLoader.FindElementByHash(output.elementHash);
                    if (element == null)
                        continue;
                    var key = Mass(element.tag);
                    double quantity = PlanningMath.PerCycle(converter.GetMassGenerationRateWithModifiers(output) * duty);
                    report.Add(key, source, quantity, true);
                    if (measuredBuilding.IsValid)
                        report.AddComparison(key, measuredBuilding, quantity, true,
                            conditional: source.Option.Length > 0, basis: source.Basis);
                }
            }
        }

        internal void ReadMeasurements(WorldReport report, IEnumerable<KeyValuePair<MeasurementKey, MeasurementSeries>> observed)
        {
            foreach (var entry in observed)
            {
                var (resource, scale, output) = MeasurementResource(entry.Key);
                report.AddMeasurement(resource, entry.Key.Building, entry.Value, scale, output);
            }
        }

        internal void ReadAverages(WorldReport report, IEnumerable<CycleTotals> history)
        {
            var daily = new Dictionary<(ResourceKey Resource, bool Output), double>();
            foreach (var cycle in history)
            {
                daily.Clear();
                foreach (var entry in cycle.Flows)
                {
                    if (entry.Key.WorldId != report.Id)
                        continue;
                    var (resource, scale, output) = MeasurementResource(entry.Key);
                    if (string.IsNullOrEmpty(resource.Id) || !(scale > 0) || double.IsInfinity(scale))
                        continue;
                    if (!output && !cycle.IncludesBreathing &&
                        (entry.Key.Material == CycleMeasurements.Oxygen || entry.Key.Material == CycleMeasurements.PollutedOxygen))
                        continue;

                    double quantity = entry.Value * scale;
                    AddDayFlow(resource, output, quantity);
                    if (resource.Unit == FlowUnit.Kilocalories && resource != ResourceKey.FoodEnergy)
                        AddDayFlow(ResourceKey.FoodEnergy, output, quantity);
                }
                foreach (var entry in daily)
                    report.AddAverageSample(entry.Key.Resource, entry.Key.Output, entry.Value);
            }

            void AddDayFlow(ResourceKey resource, bool output, double quantity)
            {
                var key = (resource, output);
                daily.TryGetValue(key, out var previous);
                daily[key] = previous + quantity;
            }
        }

        private (ResourceKey Resource, double Scale, bool Output) MeasurementResource(MeasurementKey key)
        {
            switch (key.Flow)
            {
                case MeasuredFlow.Generated:
                case MeasuredFlow.Consumed:
                    return (ResourceKey.Electricity, 1, key.Flow == MeasuredFlow.Generated);
                case MeasuredFlow.ConvertedInput:
                    var material = GetMaterial(key.Material);
                    return (material.Key, material.Amount(1, true), false);
                case MeasuredFlow.ConvertedOutput:
                    return (Mass(key.Material), 1, true);
                case MeasuredFlow.RanchEgg:
                    return (new ResourceKey(key.Material.ToString(), FlowUnit.Units), 1, true);
                case MeasuredFlow.Breathed:
                    return (Mass(key.Material), 1, false);
                default:
                    return default;
            }
        }

        private void AddPlantInputs(WorldReport report, FlowSource source, PlantElementAbsorber.ConsumeInfo[] inputs)
        {
            if (inputs == null)
                return;
            if (inputs.Length > 1)
                source = source.WithOption(GameText.Tr("可替换的灌溉或肥料", "Alternative irrigation or fertilizer"));
            foreach (var input in inputs)
                AddMaterial(report, source, input.tag, PlanningMath.PerCycle(input.massConsumptionRate), false, byMass: true);
        }

        private void AddRecipe(WorldReport report, FlowSource source, ComplexRecipe recipe, double batches)
        {
            string recipeName = GameText.Plain(recipe.GetUIName(false));
            var recipeSource = new FlowSource(source.Id + "/" + recipe.id, source.Name, recipeName,
            source.Option, source.PrefabId, source.Room, source.Basis, source.Detail);
            foreach (var input in recipe.ingredients)
            {
                if (!input.doNotConsume)
                    AddRecipeElement(report, recipeSource, input, batches, false);
            }
            foreach (var output in recipe.results)
                AddRecipeElement(report, recipeSource, output, batches, true);
        }

        private void AddRecipeElement(WorldReport report, FlowSource source, ComplexRecipe.RecipeElement element, double batches, bool output)
        {
            var choices = element.possibleMaterials;
            if (choices == null || choices.Length <= 1)
            {
                Tag material = choices != null && choices.Length == 1 ? choices[0] : element.material;
                float amount = element.possibleMaterialAmounts is { Length: 1 } amounts ? amounts[0] : element.amount;
                AddMaterial(report, source, material, amount * batches, output);
                return;
            }

            double commonKcal = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                float amount = element.possibleMaterialAmounts != null && element.possibleMaterialAmounts.Length == choices.Length
                    ? element.possibleMaterialAmounts[i] : element.amount;
                double perCycle = amount * batches;
                double kcal = GetMaterial(choices[i]).KcalPerUnit * perCycle;
                if (i == 0)
                    commonKcal = kcal;
                else if (!PlanningMath.NearlyEqual(commonKcal, kcal))
                    commonKcal = 0;
                string option = GameText.Label(choices[i].ToString());
                if (source.Option.Length > 0)
                    option = source.Option + " · " + option;
                AddMaterial(report, source.WithOption(option), choices[i], perCycle, output);
            }
            report.Add(ResourceKey.FoodEnergy, source, commonKcal, output);
        }

        private Material GetMaterial(Tag tag)
        {
            if (!tag.IsValid)
                return default;
            if (materials.TryGetValue(tag, out var material))
                return material;

            var prefab = Assets.TryGetPrefab(tag);
            var food = prefab == null ? null : prefab.GetComponent<Edible>();
            var foodInfo = food == null ? null : food.FoodInfo;
            if (foodInfo == null && food != null && !string.IsNullOrEmpty(food.FoodID))
                EdiblesManager.TryGetFoodInfo(food.FoodID, out foodInfo);
            double kcal = PlanningMath.Kilocalories(foodInfo?.CaloriesPerUnit ?? 0);
            var primary = prefab == null ? null : prefab.GetComponent<PrimaryElement>();
            bool counted = Assets.IsTagCountable(tag) || (prefab != null && (prefab.HasTag(GameTags.Egg) || prefab.HasTag(GameTags.Seed)));
            var unit = kcal > 0 ? FlowUnit.Kilocalories : counted ? FlowUnit.Units : FlowUnit.Kilograms;
            double kgPerUnit = primary?.MassPerUnit ?? 1;
            material = new Material(new ResourceKey(tag.ToString(), unit), kcal, kgPerUnit);
            materials.Add(tag, material);
            return material;
        }

        private void AddMaterial(WorldReport report, FlowSource source, Tag tag, double amount, bool output,
            bool byMass = false, bool compare = false)
        {
            var material = GetMaterial(tag);
        double quantity = material.Amount(amount, byMass);
        report.Add(material.Key, source, quantity, output);
        if (compare)
            report.AddComparison(material.Key, new Tag(source.PrefabId), quantity, output,
                conditional: source.Option.Length > 0, basis: source.Basis);
        }
    }

    private static ResourceKey Mass(Tag tag) => new(tag.IsValid ? tag.ToString() : "", FlowUnit.Kilograms);

    private static void AddMass(WorldReport report, FlowSource source, Tag tag, double ratePerSecond, bool output = false) =>
        report.Add(Mass(tag), source, PlanningMath.PerCycle(ratePerSecond), output);

    private static void AddMass(WorldReport report, FlowSource source, SimHashes hash, double ratePerSecond, bool output)
    {
        var element = ElementLoader.FindElementByHash(hash);
        if (element != null)
            AddMass(report, source, element.tag, ratePerSecond, output);
    }
}

internal static class GameText
{
    internal static string DescribeBasis(PlanBasis basis)
    {
        if (basis == PlanBasis.None)
            return "";
        var labels = new List<string>();
        if ((basis & PlanBasis.Recipe) != 0)
            labels.Add(Tr("按配方", "Recipe rate"));
        if ((basis & PlanBasis.CurrentConditions) != 0)
            labels.Add(Tr("当前工况", "Current conditions"));
        if ((basis & PlanBasis.Capacity) != 0)
            labels.Add(Tr("额定上限", "Rated capacity"));
        if ((basis & PlanBasis.Queue) != 0)
            labels.Add(Tr("按排单", "Recipe queue"));
        if ((basis & PlanBasis.LongRun) != 0)
            labels.Add(Tr("长期均值", "Long-run average"));
        if ((basis & PlanBasis.Forecast) != 0)
            labels.Add(Tr("预测均值", "Forecast average"));
        return string.Join(" / ", labels);
    }

    private static readonly Regex Markup = new("<[^>]+>");

    internal static string Tr(string chinese, string english) =>
        Localization.GetLocale()?.Code?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? chinese : english;

    internal static string Plain(string value) => Markup.Replace(value ?? "", "");

    internal static string PrefabName(Tag tag)
    {
        string id = tag.ToString();
        if (id == MinionConfig.ID)
            return Tr("复制人", "Duplicants");
        if (id == BionicMinionConfig.ID)
            return Tr("仿生复制人", "Bionic duplicants");
        return DisplayName(Assets.TryGetPrefab(tag)?.GetComponent<Building>()?.Def?.Name) ?? Label(id);
    }

    internal static string Label(string id)
    {
        if (id == ResourceKey.FoodEnergy.Id)
            return Tr("食物总热量", "Total food energy");
        if (id == ResourceKey.GearOil.Id)
            return Tr("齿轮润滑油需求", "Gear oil requirement");
        if (id == ResourceKey.Electricity.Id)
            return Tr("电力", "Electricity");
        if (id == CompostConfig.COMPOST_TAG.ToString())
            return Tr("可堆肥物", "Compostables");
        var tag = new Tag(id);
        return DisplayName(ElementLoader.FindElementByTag(tag)?.name)
            ?? DisplayName(TagManager.GetProperName(tag, true))
            ?? id;
    }

    internal static FlowSource Source(GameObject entity, string category, string recipe = "", Room room = null)
    {
        string id = entity.PrefabID().ToString();
        string name = DisplayName(entity.GetComponent<Building>()?.Def?.Name)
            ?? DisplayName(entity.GetComponent<KSelectable>()?.entityName)
            ?? Label(id);
        var cavity = room?.cavity;
        string place = cavity == null || room.roomType == null ? "" : Plain(room.GetProperName()) + $" ({cavity.minX}, {cavity.minY})";
        return new FlowSource(category + "/" + id, name + " · " + category, recipe, prefabId: id, room: place);
    }

    private static string DisplayName(string value)
    {
        string name = Plain(value).Trim();
        return name.Length == 0 || name.StartsWith("MISSING.", StringComparison.OrdinalIgnoreCase) ? null : name;
    }
}
