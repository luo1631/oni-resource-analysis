using System;
using System.Collections.Generic;
using System.Linq;
using Klei.AI;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private static ResourceKey PlantGrowth(Tag tag) => new(tag.ToString(), FlowUnit.PlantGrowth);

    internal static Tag AdultCritter(CreatureBrain creature) =>
        creature.GetSMI<BabyMonitor.Instance>()?.def.adultPrefab ?? creature.gameObject.PrefabID();

    internal static Room StableRoom(CreatureBrain creature)
    {
        if (creature == null || creature.HasTag(GameTags.Dead) || creature.HasTag(GameTags.Stored) ||
            creature.GetSMI<WildnessMonitor.Instance>()?.IsWild() != false)
            return null;
        var room = FindRoom(creature);
        return room?.roomType == Db.Get().RoomTypes.CreaturePen ? room : null;
    }

    private static double PlantGrowthPerCycle(Tag plant, AmountInstance maturity, double rate)
    {
        if (maturity == null)
            return 0;
        // Grazers consume fractions of the original cultivar's full growth.
        var original = Db.Get().traits.Get(plant + "Original");
        var originalMax = original?.SelfModifiers.Find(modifier => modifier.AttributeId == maturity.amount.maxAttribute.Id);
        double growthPerHarvest = originalMax?.Value ?? maturity.GetMax();
        return CompletionsPerCycle(maturity, rate) * growthPerHarvest;
    }

    private sealed partial class RecipeReader
    {
        private sealed class FeederSupply
        {
            internal readonly HashSet<Tag> Tags = new();
            internal readonly HashSet<Storage> Storages = new();
            internal readonly Dictionary<Tag, double> Demand = new();
            internal bool HasUnresolvedTags;
        }

        private readonly Dictionary<Room, FeederSupply> feederFilters = new();

        private void RegisterFeeder(BuildingComplete building)
        {
            var feeder = building.GetComponent<CreatureFeeder>();
            if (feeder == null)
                return;
            var filter = building.GetComponent<TreeFilterable>()?.AcceptedTags;
            if (filter == null || filter.Count == 0)
                return;
            var target = feeder.GetTargetFeederCell();
            int cell = Grid.XYToCell(target.x, target.y);
            var room = Grid.IsValidCell(cell) ? Game.Instance?.roomProber?.GetCavityForCell(cell)?.room : null;
            if (room == null || room.roomType != Db.Get().RoomTypes.CreaturePen)
                return;

            if (!feederFilters.TryGetValue(room, out var configured))
            {
                configured = new FeederSupply();
                feederFilters.Add(room, configured);
            }
            foreach (var tag in filter)
            {
                if (Assets.TryGetPrefab(tag) == null && ElementLoader.FindElementByTag(tag) == null)
                    configured.HasUnresolvedTags = true;
                else
                    configured.Tags.Add(tag);
            }
            foreach (var storage in feeder.storages ?? Array.Empty<Storage>())
            {
                if (storage != null)
                    configured.Storages.Add(storage);
            }
        }

        private Tag SingleFeederFood(Room room, Diet diet)
        {
            if (room == null || !feederFilters.TryGetValue(room, out var filter) || filter.HasUnresolvedTags ||
                diet.infos.Any(info => info.foodType != Diet.Info.FoodType.EatSolid))
                return Tag.Invalid;

            Tag selected = Tag.Invalid;
            foreach (var tag in filter.Tags)
            {
                var entry = diet.GetDietInfo(tag);
                if (entry == null || !(entry.caloriesPerKg > 0))
                    continue;
                if (selected.IsValid)
                    return Tag.Invalid;
                selected = tag;
            }
            return selected;
        }

        internal void ReadCreatures(WorldReport report, IEnumerable<Brain> brains)
        {
            var rooms = brains.OfType<CreatureBrain>().Select(creature => (Creature: creature, Room: StableRoom(creature)))
                .Where(entry => entry.Room != null).GroupBy(entry => entry.Room);
            foreach (var room in rooms)
            {
                var cavity = room.Key.cavity;
                var ranch = new RanchReport(GameText.Plain(room.Key.GetProperName()) + $" ({cavity.minX}, {cavity.minY})");
                var breeding = new List<(CreatureBrain Creature, Tag Adult, FlowSource Source, AnimalForecast Forecast)>();
                var groups = room.Select(entry => entry.Creature).GroupBy(AdultCritter);
                foreach (var group in groups)
                {
                    var animals = group.Select(creature => (Creature: creature, Mood: CritterMood.Read(creature),
                        Adult: creature.GetSMI<BabyMonitor.Instance>() == null, Fed: !NoCalories(creature))).ToArray();
                    var adults = animals.Where(entry => entry.Adult && entry.Fed).ToArray();
                    var majority = adults.Where(entry => entry.Mood != null).GroupBy(entry => entry.Mood.Level)
                        .FirstOrDefault(state => state.Count() > adults.Length / 2);
                    var representative = majority == null || majority.Count() == adults.Length ? null : majority.First().Mood;
                    var samples = animals.Select(entry =>
                    {
                        var plannedMood = entry.Adult && entry.Fed && entry.Mood != null ? representative : null;
                        var projection = plannedMood != null && entry.Mood.Level != plannedMood.Level
                            ? new ModifierProjection(entry.Mood.Modifiers, plannedMood.Modifiers) : default;
                        return (entry.Creature, Mood: plannedMood, Projection: projection, Rate: ActiveEggRate(entry.Creature, projection));
                    }).ToArray();
                    double peerRate = PlanningMath.Median(samples.Where(sample => sample.Rate > 0).Select(sample => (float)sample.Rate));
                    foreach (var sample in samples)
                    {
                        var forecast = ForecastAnimal(sample.Creature, group.Key, sample.Rate, peerRate);
                        ranch.Animals.Add(forecast);
                        var source = ReadCreature(report, sample.Creature, room.Key, sample.Mood, sample.Projection, forecast);
                        breeding.Add((sample.Creature, group.Key, source, forecast));
                    }
                }
                if (feederFilters.TryGetValue(room.Key, out var feed) && feed.Storages.Count > 0)
                {
                    foreach (var demand in feed.Demand)
                        ranch.Feeds.Add(new RanchFeed(demand.Key.ToString(), demand.Value,
                            feed.Storages.Sum(storage => (double)storage.GetMassAvailable(demand.Key))));
                }
                var rations = ranch.Feeds.ToDictionary(ration => ration.Id, StringComparer.Ordinal);
                foreach (var (creature, adult, source, forecast) in breeding)
                {
                    if (forecast.FeedId != null && rations.TryGetValue(forecast.FeedId, out var ration))
                        forecast.ApplyFeedBuffer(ration);
                    ReadBreeding(report, source, creature, adult, forecast);
                }
                report.Ranches.Add(ranch);
            }
        }

        private FlowSource ReadCreature(WorldReport report, CreatureBrain creature, Room room, CritterMood representative,
            ModifierProjection projection, AnimalForecast forecast)
        {
            string label = GameText.Tr("驯化动物", "Tame critter");
            if (representative != null)
                label += GameText.Tr(" · 代表幸福度 ", " · Planned happiness ") + representative.Level.ToString("0.##");
            var source = GameText.Source(creature.gameObject, label, room: room);
            bool starved = NoCalories(creature);
            var production = starved ? source.WithOption(GameText.Tr("等待补料", "Needs feed")) : source;
            bool happy = representative?.Happy ?? creature.HasTag(GameTags.Creatures.Happy);
            FindConverters(creature);
            ReadConverters(report, production);
            ReadDiet(report, source, production.Option, creature.GetSMI<CreatureCalorieMonitor.Instance>(), projection, room, forecast);
            ReadShearing(report, production.WithRecipe(GameText.Tr("剪毛或刮鳞", "Shearing")), creature, projection);
            ReadMilk(report, production, creature, projection, happy);
            ReadMolts(report, production, creature, happy);
            ReadLifetimeDrops(report, source, creature);
            return source;
        }

        private void ReadDiet(WorldReport report, FlowSource source, string productionCondition, CreatureCalorieMonitor.Instance monitor,
            ModifierProjection projection, Room room, AnimalForecast forecast)
        {
            var diet = monitor?.stomach?.diet;
            if (diet == null || monitor.calories == null)
                return;
            float calories = (float)PlanningMath.PerCycle(-CalorieBurnRate(monitor, projection));
            if (!(calories > 0) || float.IsInfinity(calories))
                return;

            Tag foodTag = SingleFeederFood(room, diet);
            if (foodTag.IsValid)
            {
                var feed = diet.GetDietInfo(foodTag);
                float eaten = feed.ConvertCaloriesToConsumptionMass(calories);
                var feeding = source.WithRecipe(GameText.Tr("按同房间喂食器配置", "From the room's feeder filters"));
                var demand = feederFilters[room].Demand;
                demand.TryGetValue(foodTag, out double previous);
                demand[foodTag] = previous + eaten;
                forecast.FeedId = foodTag.ToString();
                forecast.ReserveCycles = Math.Max(0, monitor.calories.value - monitor.calories.GetMin()) / calories;
                AddMaterial(report, feeding, foodTag, eaten, false, byMass: true);
                AddMaterial(report, feeding.WithOption(productionCondition), feed.producedElement,
                    feed.ConvertConsumptionMassToProducedMass(eaten), true, byMass: true);
                return;
            }

            var foods = new List<(Tag Food, Diet.Info Info, double Eaten, double Produced)>();
            bool unquantified = false;
            foreach (var info in diet.infos)
            {
                if (info.foodType is Diet.Info.FoodType.EatPrey or Diet.Info.FoodType.EatButcheredPrey ||
                    !(info.caloriesPerKg > 0) || info.consumedTags.Count == 0)
                {
                    unquantified = true;
                    continue;
                }

                float eaten = info.ConvertCaloriesToConsumptionMass(calories);
                float produced = info.ConvertConsumptionMassToProducedMass(eaten);
                foreach (var tag in info.consumedTags)
                    foods.Add((tag, info, eaten, produced));
            }
            if (foods.Count == 0)
                return;

            var first = foods[0];
            var dietSource = source.WithRecipe(GameText.Tr("食谱", "Diet"));
            bool commonProduct = !unquantified && first.Info.producedElement.IsValid && foods.All(food =>
                food.Info.producedElement == first.Info.producedElement && PlanningMath.NearlyEqual(food.Produced, first.Produced));
            if (commonProduct)
                AddMaterial(report, dietSource.WithOption(productionCondition), first.Info.producedElement, first.Produced, true, byMass: true);

            foreach (var food in foods)
            {
                var candidate = foods.Count > 1 || unquantified
                    ? dietSource.WithOption(GameText.Label(food.Food.ToString())) : dietSource;
                AddDietInput(report, candidate, food.Food, food.Info.foodType, food.Eaten);
                if (!commonProduct && food.Info.producedElement.IsValid)
                    AddMaterial(report, Requires(candidate, productionCondition), food.Info.producedElement, food.Produced, true, byMass: true);
            }
        }

        private static float CalorieBurnRate(CreatureCalorieMonitor.Instance monitor, ModifierProjection projection)
        {
            // Native updates cache this multiplier on a sim tick and leave it stale during a pause.
            var cached = monitor.deltaCalorieMetabolismModifier;
            float multiplier = 1 - ProjectedValue(monitor.metabolism, projection) / 100f;
            if (cached.Value == multiplier)
                return ProjectedDelta(monitor.calories, projection);

            var current = new AttributeModifier(cached.AttributeId, multiplier, cached.GetDescription(), true);
            return ProjectedDelta(monitor.calories, projection, current, cached);
        }

        private void AddDietInput(WorldReport report, FlowSource source, Tag food, Diet.Info.FoodType type, double eaten)
        {
            if (type == Diet.Info.FoodType.EatSolid)
            {
                AddMaterial(report, source, food, eaten, false, byMass: true);
                return;
            }

            var plant = Assets.TryGetPrefab(food);
            if (plant == null)
                return;
            if (type == Diet.Info.FoodType.EatPlantDirectly && plant.GetComponent<DirectlyEdiblePlant_Growth>() != null)
                report.Add(PlantGrowth(food), source, eaten);
            if (type == Diet.Info.FoodType.EatPlantStorage)
            {
                var stored = plant.GetComponent<DirectlyEdiblePlant_StorageElement>();
                if (stored != null && stored.tagToConsume.IsValid)
                    AddMaterial(report, source, stored.tagToConsume, eaten, false, byMass: true);
            }
        }

        private void ReadBreeding(WorldReport report, FlowSource source, CreatureBrain creature, Tag adult, AnimalForecast forecast)
        {
            double planned = forecast.WindowEggs ?? 0;
            if (!(planned > 0))
                return;
            var breeding = source.WithRecipe(GameText.Tr("按未来 10 周期产蛋折算", "Eggs scheduled over the next 10 cycles"))
                .WithBasis(PlanBasis.Forecast);
            if (forecast.AfterMaturity)
            {
                var adultDefinition = Assets.TryGetPrefab(adult)?.GetDef<FertilityMonitor.Def>();
                if (adultDefinition != null)
                {
                    var mature = breeding.WithOption(GameText.Tr("成熟后同舍估算 · 持续补料 · 成体初始蛋种概率",
                        "After maturity · stable peers, refills and adult's initial egg weights"));
                    AddEggs(report, mature, adultDefinition, adultDefinition.initialBreedingWeights, planned);
                }
                return;
            }

            var monitor = creature.GetSMI<FertilityMonitor.Instance>();
            if (monitor?.fertility == null)
                return;
            double buffered = Math.Min(planned, forecast.FedBufferEggs ?? 0);
            AddEggs(report, breeding, monitor.def, monitor.breedingChances, buffered);
            if (buffered < planned)
            {
                var condition = forecast.FedBufferEggs.HasValue
                    ? GameText.Tr("持续补料可增加的产蛋", "Additional eggs with refills")
                    : GameText.Tr("供料待确认 · 持续补料", "Feed availability unconfirmed · with refills");
                AddEggs(report, breeding.WithOption(condition), monitor.def, monitor.breedingChances, planned - buffered);
            }
        }

        private void AddEggs(WorldReport report, FlowSource breeding, FertilityMonitor.Def definition,
            List<FertilityMonitor.BreedingChance> chances, double eggs)
        {
            if (!(eggs > 0))
                return;
            double perCycle = eggs / AnimalForecast.WindowCycles;
            double total = chances?.Where(chance => chance.egg.IsValid && chance.weight > 0).Sum(chance => (double)chance.weight) ?? 0;
            if (!(total > 0))
            {
                AddMaterial(report, breeding, definition.eggPrefab, perCycle, true);
                return;
            }
            foreach (var chance in chances)
            {
                if (chance.egg.IsValid && chance.weight > 0)
                    AddMaterial(report, breeding, chance.egg, perCycle * chance.weight / total, true);
            }
        }

        private void ReadShearing(WorldReport report, FlowSource shear, CreatureBrain creature, ModifierProjection projection)
        {
            var scale = creature.GetSMI<ScaleGrowthMonitor.Instance>();
            if (scale?.scaleGrowth != null)
            {
                var atmosphere = scale.def.targetAtmosphere;
                int cell = Grid.PosToCell(creature);
                var conditional = shear;
                if (atmosphere != 0 && (!Grid.IsValidCell(cell) || Grid.Element[cell].id != atmosphere))
                {
                    var gas = ElementLoader.FindElementByHash(atmosphere);
                    conditional = shear.WithOption(GameText.Tr("生长气氛 · ", "Growing atmosphere · ") +
                        (gas == null ? atmosphere.ToString() : GameText.Label(gas.tag.ToString())));
                }
                double growth = CompletionsPerCycle(scale.scaleGrowth, ProjectedDelta(scale.scaleGrowth, projection, scale.scaleGrowthModifier));
                AddMaterial(report, conditional, scale.def.itemDroppedOnShear, scale.def.dropMass * growth, true, byMass: true);
            }

            var fed = creature.GetSMI<WellFedShearable.Instance>();
            if (fed?.scaleGrowth != null && fed.HasWellFedEffect())
            {
                double growth = CompletionsPerCycle(fed.scaleGrowth, ProjectedDelta(fed.scaleGrowth, projection));
                AddMaterial(report, shear, fed.def.itemDroppedOnShear, fed.def.dropMass * growth, true, byMass: true);
            }

            var element = creature.GetSMI<ElementGrowthMonitor.Instance>();
            if (element?.elementGrowth != null)
            {
                var progress = element.elementGrowth;
                AttributeModifier baseline = null;
                if (element.IsFullyGrown())
                {
                    float temperature = element.lastConsumedTemperature;
                    bool optimal = temperature == 0 ||
                        (temperature >= element.def.minTemperature && temperature <= element.def.maxTemperature);
                    baseline = optimal ? element.growingGrowthModifier : element.stuntedGrowthModifier;
                }
                double growth = CompletionsPerCycle(progress, ProjectedDelta(progress, projection, baseline));
                AddMaterial(report, shear, element.def.itemDroppedOnShear, element.def.dropMass * growth, true, byMass: true);
            }
        }

        private static void ReadMilk(WorldReport report, FlowSource source, CreatureBrain creature, ModifierProjection projection, bool happy)
        {
            var milk = creature.GetSMI<MilkProductionMonitor.Instance>();
            if (milk?.milkAmountInstance != null)
            {
                AddMass(report, source.WithRecipe(GameText.Tr("当前产奶效果", "Current milk production effect")),
                    milk.GetMilkElement(), ProjectedDelta(milk.milkAmountInstance, projection), true);
            }

            // Milking can use up the same fertility that would otherwise produce eggs.
            var fertilityMilk = creature.GetSMI<FertilityShearable.Instance>();
            if (fertilityMilk?.fertility != null && (!fertilityMilk.def.requiresHappy || happy) &&
                (!fertilityMilk.def.suppressedByElderly || creature.GetSMI<AgeMonitor.Instance>()?.IsElderly != true) &&
                fertilityMilk.def.percentFertilityConsumedPerMilking > 0)
            {
                var milking = source.WithRecipe(GameText.Tr("挤奶", "Milking"))
                    .WithOption(GameText.Tr("使用生殖进度", "Uses reproduction progress"));
                double fertilityPerMilking = fertilityMilk.fertility.GetMax() * fertilityMilk.def.percentFertilityConsumedPerMilking;
                if (fertilityPerMilking > 0)
                    AddMass(report, milking, fertilityMilk.GetMilkElement(),
                        ProjectedDelta(fertilityMilk.fertility, projection) * fertilityMilk.def.dropMass / fertilityPerMilking, true);
            }
        }

        private void ReadMolts(WorldReport report, FlowSource source, CreatureBrain creature, bool happy)
        {
            var molt = creature.GetSMI<MoltDropperMonitor.Instance>();
            if (molt == null || !happy || creature.HasTag(GameTags.Creatures.Hungry))
                return;

            // These native rules limit recurring molts to one per cycle in a suitable cell.
            bool validSite;
            if (molt.def.isReadyToMolt == CrabTuning.IsReadyToMolt)
                validSite = CrabTuning.IsValidDropCell(molt);
            else if (molt.def.isReadyToMolt == BaseSnailConfig.IsReadyToMolt)
                validSite = BaseSnailConfig.IsValidDropCell(molt);
            else
                return;

            var shedding = source.WithRecipe(GameText.Tr("周期性蜕壳", "Recurring molt"));
            if (!validSite)
                shedding = shedding.WithOption(GameText.Tr("需要合适的蜕壳位置", "Requires a suitable molting cell"));
            AddMaterial(report, shedding, new Tag(molt.def.onGrowDropID), molt.def.massToDrop, true, byMass: true);
        }

        private void ReadLifetimeDrops(WorldReport report, FlowSource source, CreatureBrain creature)
        {
            var age = creature.GetSMI<AgeMonitor.Instance>()?.age;
            var drops = creature.GetComponent<Butcherable>()?.drops;
            if (age == null || drops == null || creature.GetSMI<BabyMonitor.Instance>() != null)
                return;

            var turnover = source.WithRecipe(GameText.Tr("死亡掉落", "Death drops"))
                .WithOption(GameText.Tr("按自然寿命与稳定存栏折算", "Natural lifespan at stable population"));
            double replacements = CompletionsPerCycle(age, age.GetDelta());
            foreach (var drop in drops)
            {
                var tag = new Tag(drop.Key);
                var prefab = Assets.TryGetPrefab(tag);
                var primary = prefab == null ? null : prefab.GetComponent<PrimaryElement>();
                if (primary != null)
                    AddMaterial(report, turnover, tag, primary.Mass * drop.Value * replacements, true, byMass: true);
            }
        }
    }
}
