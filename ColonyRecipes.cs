using System;
using System.Collections.Generic;
using Klei.AI;
using Klei.CustomSettings;
using TUNING;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private static void ReadMinion(WorldReport report, MinionIdentity minion)
    {
        string name = GameText.Plain(minion.GetProperName());
        string prefab = minion.gameObject.PrefabID().ToString();
        var source = new FlowSource(prefab + "/" + minion.GetInstanceID(), name, prefabId: prefab);
        var standard = DUPLICANTSTATS.STANDARD.BaseStats;
        var calories = minion.GetSMI<CalorieMonitor.Instance>()?.calories;
        if (calories != null)
        {
            double demand = PlanningMath.Kilocalories(PlanningMath.PerCycle(-calories.GetDelta()));
            double baseline = PlanningMath.Kilocalories(standard.CALORIES_BURNED_PER_CYCLE);
            string recipe = DescribeDemand(minion, calories.deltaAttribute, GameText.Tr("口粮需求", "Food requirement"),
                demand - baseline, "kcal", FoodDifficulty());
            report.Add(ResourceKey.FoodEnergy, source.WithRecipe(recipe), demand);
        }

        var breather = minion.GetComponent<OxygenBreather>();
        if (breather != null)
        {
            bool bionic = minion.GetSMI<BionicOxygenTankMonitor.Instance>() != null;
            double consumption = breather.ConsumptionRate;
            string recipe = bionic ? GameText.Tr("氧气罐补给", "Oxygen tank refill") :
                DescribeDemand(minion, Db.Get().Attributes.AirConsumptionRate.Lookup(minion), GameText.Tr("呼吸", "Breathing"),
                    PlanningMath.PerCycle(consumption - standard.OXYGEN_USED_PER_SECOND), "kg");
            var respiration = source.WithRecipe(recipe);
            AddMass(report, respiration, SimHashes.Oxygen, consumption, false);
            if (!bionic)
                AddMass(report, respiration, SimHashes.CarbonDioxide, consumption * breather.O2toCO2conversion, true);
        }

        var oil = minion.GetSMI<BionicOilMonitor.Instance>()?.oilAmount;
        if (oil != null)
            report.Add(ResourceKey.GearOil, source.WithRecipe(GameText.Tr("齿轮润滑", "Gear lubrication")), PlanningMath.PerCycle(-oil.GetDelta()));

        var gunk = Db.Get().Amounts.BionicGunk.Lookup(minion);
        if (gunk != null)
            AddMass(report, source.WithRecipe(GameText.Tr("仿生废料积累", "Bionic gunk buildup"))
                .WithOption(GameText.Tr("需定期排出", "Requires periodic extraction")), GunkMonitor.GunkElement, gunk.GetDelta(), true);
    }

    internal static string FoodDifficulty()
    {
        var config = CustomGameSettingConfigs.CalorieBurn;
        var setting = CustomGameSettings.Instance.GetCurrentQualitySetting(config);
        return setting != null && setting.id != config.GetDefaultLevelId() ? GameText.Plain(setting.label) : "";
    }

    private static string DescribeDemand(MinionIdentity minion, AttributeInstance attribute, string activity,
        double difference, string unit, string difficulty = "")
    {
        var labels = new List<string>();
        if (attribute != null && minion.GetComponent<Traits>() is Traits traits)
        {
            foreach (var trait in traits.TraitList)
            {
                if (trait.SelfModifiers.Exists(modifier => modifier.AttributeId == attribute.Id && HasModifier(attribute, modifier)))
                    labels.Add(GameText.Plain(trait.GetName()));
            }
        }
        if (difficulty.Length > 0)
            labels.Add(difficulty);
        if (Math.Abs(difference) >= 0.005)
        {
            string delta = difference.ToString("+#,##0.##;-#,##0.##");
            labels.Add(GameText.Tr("较基础 ", "vs. base ") + delta + " " + unit + GameText.Tr("/周期", "/cycle"));
        }
        return labels.Count > 0 ? activity + " · " + string.Join(" · ", labels) : activity;
    }

    private static double CompletionsPerCycle(AmountInstance progress, double perSecond)
    {
        double range = progress.GetMax() - progress.GetMin();
        return range > 0 ? PlanningMath.PerCycle(perSecond) / range : 0;
    }
}
