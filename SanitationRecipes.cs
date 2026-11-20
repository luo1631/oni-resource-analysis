using System.Collections.Generic;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private sealed partial class RecipeReader
    {
        private readonly Dictionary<(Tag Input, SimHashes Output, float Intake, float Waste), BuildingComplete> toilets = new();
        private float agriculture;

        private void RegisterToilet(BuildingComplete building)
        {
            if (building.GetComponent<ToiletWorkableUse>() == null)
                return;

            var flush = building.GetComponent<FlushToilet>();
            if (flush != null)
            {
                toilets.TryAdd((FlushToilet.WaterTag, SimHashes.DirtyWater, flush.massConsumedPerUse, flush.massEmittedPerUse), building);
                return;
            }

            var outhouse = building.GetComponent<Toilet>();
            if (outhouse != null)
                toilets.TryAdd((SimHashes.Dirt.CreateTag(), outhouse.solidWastePerUse.elementID,
                    outhouse.dirtUsedPerFlush, outhouse.solidWastePerUse.mass), building);
        }

        internal void ReadColony(WorldReport report, IEnumerable<MinionIdentity> minions)
        {
            var amounts = Db.Get().Amounts;
            var farming = Db.Get().Attributes.Botanist;
            var farmPerk = Db.Get().SkillPerks.CanFarmTinker;
            var farmers = new List<float>();
            double visits = 0;
            foreach (var minion in minions)
            {
                if (minion == null || minion.HasTag(GameTags.Dead) || minion.HasTag(GameTags.Stored))
                    continue;
                ReadMinion(report, minion);

                var bladder = amounts.Bladder.Lookup(minion);
                if (bladder != null && bladder.GetDelta() > 0)
                    visits += CompletionsPerCycle(bladder, bladder.GetDelta());
                if (minion.GetComponent<MinionResume>()?.HasPerk(farmPerk) == true)
                    farmers.Add(farming.Lookup(minion)?.GetTotalValue() ?? 0);
            }

            agriculture = PlanningMath.Median(farmers);
            AddToiletFlows(report, visits);
        }

        private void AddToiletFlows(WorldReport report, double visits)
        {
            if (!(visits > 0))
                return;

            foreach (var entry in toilets)
            {
                var (input, output, intake, waste) = entry.Key;
                var source = GameText.Source(entry.Value.gameObject, GameText.Tr("卫生设施", "Sanitation"),
                    GameText.Tr("全殖民地满膀胱折算", "Colony-wide full-bladder visits"));
                if (toilets.Count > 1)
                    source = source.WithOption(GameText.Tr("按本类厕所统一使用", "If this is the only toilet type used"));
                AddMaterial(report, source, input, intake * visits, false, byMass: true);
                AddMass(report, source, output, waste * visits / PlanningMath.SecondsPerCycle, true);
            }
        }

        private void ReadTendingCost(WorldReport report, FlowSource source, Tinkerable tinker)
        {
            if (tinker == null || tinker.addedEffect != "FarmTinker")
                return;

            var effect = Db.Get().effects.Get(tinker.addedEffect);
            double duration = (effect?.duration ?? 0) * (1 + agriculture * tinker.effectMultiplier);
            var tending = source.WithRecipe(GameText.Tr("持续维持农夫之触", "Maintain Farmer's Touch"));
            AddMaterial(report, tending, FarmStationConfig.MATERIAL_FOR_TINKER,
                PlanningMath.PerRecipe(tinker.tinkerMass, duration), false, byMass: true);
        }
    }
}
