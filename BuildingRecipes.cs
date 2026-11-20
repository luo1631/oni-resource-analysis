using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TUNING;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private sealed partial class RecipeReader
    {
        private static readonly FieldInfo WorkableUsesLighting = typeof(Workable)
            .GetField("lightEfficiencyBonus", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkableUsesLaboratory = typeof(Workable)
            .GetField("useLaboratoryEfficiencyBonus", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkableInLaboratory = typeof(Workable)
            .GetField("currentlyInLaboratory", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkableMinimumSpeed = typeof(Workable)
            .GetField("minimumAttributeMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);

        internal void ReadBuildings(WorldReport report, IEnumerable<BuildingComplete> buildings)
        {
            var registered = buildings.Where(building => building != null && building.Def != null)
                .Select(building => (Building: building, Fabricator: building.GetComponent<ComplexFabricator>())).ToArray();
            var workerBonuses = registered.Select(entry => (
                    Prefab: entry.Building.gameObject.PrefabID(), Bonus: ReadWorkerBonus(entry.Fabricator)))
                .Where(entry => entry.Bonus.HasValue)
                .GroupBy(entry => entry.Prefab)
                .ToDictionary(group => group.Key, group => PlanningMath.Median(group.Select(entry => entry.Bonus.Value)));

            foreach (var (building, fabricator) in registered)
            {
                workerBonuses.TryGetValue(building.gameObject.PrefabID(), out float bonus);
                bool enabled = building.GetComponent<BuildingEnabledButton>()?.IsEnabled != false;
                if (enabled)
                {
                    RegisterToilet(building);
                    RegisterFeeder(building);
                }
                ReadBuilding(report, building, fabricator, bonus, enabled);
            }
        }

        private static double ReadOilWellGas(WorldReport report, FlowSource source, OilWellCap well)
        {
            double fillRate = well.addGasRate;
            double releaseRate = well.releaseGasRate;
            if (!(fillRate > 0))
                return 1;
            if (!(releaseRate > 0))
                return 0;

            // A complete pressure cycle accumulates and releases the same mass.
            double drillingShare = releaseRate / (fillRate + releaseRate);
            AddMass(report, source.WithRecipe(GameText.Tr("周期泄压", "Periodic pressure release")), well.gasElement,
                fillRate * drillingShare, true);
            return drillingShare;
        }

        private static float? ReadWorkerBonus(ComplexFabricator fabricator)
        {
            var workable = fabricator == null ? null : fabricator.Workable;
            var worker = workable == null ? null : workable.worker;
            var attribute = workable == null ? null : workable.AttributeConverter;
            if (worker == null || attribute == null)
                return null;
            return worker.GetAttributeConverter(attribute.Id)?.Evaluate();
        }

        private static (double Speed, bool Lit, bool Laboratory) FabricatorSpeed(ComplexFabricator fabricator, float typicalWorkerBonus)
        {
            var workable = fabricator.Workable;
            if (workable == null)
                return (1, false, false);
            var worker = workable.worker;
            var light = DUPLICANTSTATS.STANDARD.Light;
            int cell = worker == null ? Grid.InvalidCell : Grid.PosToCell(worker);
            bool lit = WorkableUsesLighting?.GetValue(workable) is true && Grid.IsValidCell(cell) &&
                Grid.LightIntensity[cell] > light.NO_LIGHT;
            bool lab = WorkableUsesLaboratory?.GetValue(workable) is true &&
                WorkableInLaboratory?.GetValue(workable) is true;

            // The native efficiency method publishes status items. Read the terms without changing the worker.
            double bonus = worker == null ? typicalWorkerBonus : ReadWorkerBonus(fabricator) ?? 0;
            double speed = 1 + bonus + workable.GetTotalEfficiencyBoost() +
                (lit ? light.LIGHT_WORK_EFFICIENCY_BONUS : 0) + (lab ? 0.1 : 0);
            return (Math.Max(WorkableMinimumSpeed?.GetValue(workable) is float minimum ? minimum : 0.5, speed), lit, lab);
        }

        private double ReadFabricator(WorldReport report, FlowSource source, ComplexFabricator fabricator, float workerBonus)
        {
            var recipes = fabricator.GetRecipes();
            var current = fabricator.CurrentWorkingOrder;
            var (speed, lit, lab) = FabricatorSpeed(fabricator, workerBonus);
            double timeMultiplier = fabricator.GetTotalSpeedModifier() / speed;
            string speedDetail = GameText.Tr($"工速 ×{speed:0.##}", $"Work speed ×{speed:0.##}");
            if (lit)
                speedDetail += GameText.Tr(" · 含照明", " · lit workspace");
            if (lab)
                speedDetail += GameText.Tr(" · 含实验室", " · laboratory");
            source = source.WithDetail(speedDetail);
            var queue = new List<(ComplexRecipe Recipe, double Seconds, int Count)>();
            foreach (var recipe in recipes)
            {
                int count = fabricator.IsRecipeQueued(recipe) ? fabricator.GetRecipeQueueCount(recipe) : 0;
                if (count == 0 && recipe == current)
                    count = 1;
                double seconds = recipe.time * timeMultiplier;
                if (count != 0 && seconds > 0 && !double.IsInfinity(seconds))
                    queue.Add((recipe, seconds, count));
            }

            if (queue.Count == 0)
            {
                var unqueued = source.Option.Length > 0 ? source :
                    source.WithOption(GameText.Tr("未排单的配方", "Unqueued recipe"));
                foreach (var recipe in recipes)
                    AddRecipe(report, unqueued.WithBasis(PlanBasis.Capacity), recipe, PlanningMath.PerRecipe(1, recipe.time * timeMultiplier));
                return 0;
            }

            var planned = source.WithBasis(PlanBasis.Queue);
            double budget = PlanningMath.SecondsPerCycle;
            double progress = fabricator.OrderProgress;
            int started = current != null && progress > 0 ? queue.FindIndex(order => order.Recipe == current) : -1;
            if (started >= 0)
            {
                var order = queue[started];
                double remaining = order.Seconds * Math.Max(0, 1 - progress);
                if (remaining <= budget)
                    AddRecipe(report, planned, order.Recipe, 1);
                budget = Math.Max(0, budget - remaining);
                if (order.Count == 1)
                    queue.RemoveAt(started);
                else if (order.Count > 1)
                    queue[started] = (order.Recipe, order.Seconds, order.Count - 1);
            }

            // Fabricators rotate through workable recipes. Share the remaining work budget.
            double passes = PlanningMath.QueuePasses(queue.Select(order => (order.Seconds, order.Count)).ToArray(), budget);
            double workingSeconds = PlanningMath.SecondsPerCycle - budget;
            foreach (var (recipe, workSeconds, count) in queue)
            {
                double batches = count == ComplexFabricator.QUEUE_INFINITE ? passes : Math.Min(passes, count);
                if (batches > 0)
                    AddRecipe(report, planned, recipe, batches);
                workingSeconds += batches * workSeconds;
            }
            return Math.Min(1, workingSeconds / PlanningMath.SecondsPerCycle);
        }
    }
}
