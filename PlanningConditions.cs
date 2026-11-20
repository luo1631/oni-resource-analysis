using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Klei.AI;
using UnityEngine;

namespace OniResourceBalance;

internal static partial class GameRecipes
{
    private static Room FindRoom(Component entity) => Game.Instance?.roomProber?.GetRoomOfGameObject(entity.gameObject);

    private readonly struct ModifierProjection
    {
        internal readonly IReadOnlyList<AttributeModifier> Removed;
        internal readonly IReadOnlyList<AttributeModifier> Added;

        internal ModifierProjection(IReadOnlyList<AttributeModifier> removed, IReadOnlyList<AttributeModifier> added)
        {
            Removed = removed;
            Added = added;
        }

        internal bool IsEmpty => Removed == null && Added == null;
    }

    private static float ProjectedDelta(AmountInstance amount, ModifierProjection projection = default,
        AttributeModifier baseline = null, AttributeModifier replaced = null) =>
        amount == null ? 0 : ProjectedValue(amount.deltaAttribute, projection, baseline, replaced);

    private static float ProjectedValue(AttributeInstance attribute, ModifierProjection projection,
        AttributeModifier baseline = null, AttributeModifier replaced = null)
    {
        if (projection.IsEmpty && replaced == null && (baseline == null || HasModifier(attribute, baseline)))
            return attribute.GetTotalValue();

        var modifiers = new List<AttributeModifier>(attribute.Modifiers.Count + 1);
        for (int index = 0; index < attribute.Modifiers.Count; index++)
        {
            var modifier = attribute.Modifiers[index];
            if (!ReferenceEquals(modifier, replaced) && projection.Removed?.Contains(modifier) != true)
                modifiers.Add(modifier);
        }

        if (projection.Added != null)
        {
            foreach (var modifier in projection.Added)
            {
                if (modifier.AttributeId == attribute.Id && !modifiers.Contains(modifier))
                    modifiers.Add(modifier);
            }
        }

        // The game removes the base modifier while a crop or coat waits at full growth.
        if (baseline != null && !modifiers.Contains(baseline))
            modifiers.Add(baseline);
        return AttributeInstance.GetTotalValue(attribute.Attribute, modifiers);
    }

    private static bool HasModifier(AttributeInstance attribute, AttributeModifier modifier)
    {
        for (int index = 0; index < attribute.Modifiers.Count; index++)
        {
            if (ReferenceEquals(attribute.Modifiers[index], modifier))
                return true;
        }
        return false;
    }

    private sealed class CritterMood
    {
        private static readonly string[] EffectIds = { "Happy", "Neutral", "Glum", "Miserable" };
        private static readonly FieldInfo FertilityModifier = typeof(HappinessMonitor.Instance)
            .GetField("fertilityFromHappiness", BindingFlags.Instance | BindingFlags.NonPublic);

        internal readonly float Level;
        internal readonly bool Happy;
        internal readonly List<AttributeModifier> Modifiers;

        private CritterMood(float level, bool happy, List<AttributeModifier> modifiers)
        {
            Level = level;
            Happy = happy;
            Modifiers = modifiers;
        }

        internal static CritterMood Read(CreatureBrain creature)
        {
            var happiness = creature.GetSMI<HappinessMonitor.Instance>();
            var effects = creature.GetComponent<Effects>();
            if (happiness?.happiness == null || effects == null || FertilityModifier == null)
                return null;

            var activeEffect = EffectIds.Select(effects.Get).FirstOrDefault(effect => effect != null);
            if (activeEffect == null)
                return null;

            var modifiers = new List<AttributeModifier>(activeEffect.effect.SelfModifiers);
            if (FertilityModifier.GetValue(happiness) is AttributeModifier fertility)
            {
                var delta = creature.GetSMI<FertilityMonitor.Instance>()?.fertility?.deltaAttribute;
                if (delta != null && HasModifier(delta, fertility))
                    modifiers.Add(fertility);
            }
            return new CritterMood(happiness.happiness.GetTotalValue(), activeEffect.effect.Id == "Happy", modifiers);
        }
    }
}
