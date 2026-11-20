using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace OniResourceBalance;

[HarmonyPatch(typeof(GameClock), nameof(GameClock.SetTime))]
internal static class MeasurementTimeJump
{
    private static void Prefix(GameClock __instance, float __0, out bool __state)
    {
        __state = __instance == GameClock.Instance && __0 > __instance.GetTime();
        if (__state)
            PlannerSession.Measurements.BeginTimeJump();
    }

    private static void Postfix(bool __state)
    {
        if (__state)
            PlannerSession.Measurements.EndTimeJump();
    }
}

[HarmonyPatch(typeof(Generator), nameof(Generator.GenerateJoules))]
internal static class MeasureGeneration
{
    private static void Postfix(Generator __instance, float __0) =>
        PlannerSession.Measurements.Record(__instance, MeasuredFlow.Generated, __0 / 1000.0);
}

[HarmonyPatch]
internal static class MeasurePowerSupply
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CircuitManager), "PowerFromGenerator");
        yield return AccessTools.Method(typeof(CircuitManager), "PowerFromBatteries");
    }

    private static void Prefix(float __0, out float __state) => __state = __0;

    private static void Postfix(float __state, IEnergyConsumer __2, float __result)
    {
        if (__2 is EnergyConsumer device)
            PlannerSession.Measurements.Record(device, MeasuredFlow.Consumed, ((double)__state - __result) / 1000);
    }
}

[HarmonyPatch(typeof(ElementConverter), "ConvertMass")]
internal static class MeasureConversions
{
    internal static bool Available { get; private set; }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var accumulate = AccessTools.Method(typeof(Accumulators), nameof(Accumulators.Accumulate));
        var inputHandle = AccessTools.Field(typeof(ElementConverter.ConsumedElement), nameof(ElementConverter.ConsumedElement.Accumulator));
        var outputHandle = AccessTools.Field(typeof(ElementConverter.OutputElement), nameof(ElementConverter.OutputElement.accumulator));
        int input = FindSettlement(code, accumulate, inputHandle);
        int output = FindSettlement(code, accumulate, outputHandle);
        if (input < 0 || output <= input)
        {
            Available = false;
            UnityEngine.Debug.LogWarning("[OniResourceBalance] Converter measurements are unavailable because the native settlement changed.");
            return code;
        }

        // Keep the game's accumulator and pass the settled mass to the report with its material.
        AttachObserver(code, output, AccessTools.Field(typeof(ElementConverter.OutputElement), nameof(ElementConverter.OutputElement.elementHash)),
            AccessTools.Method(typeof(MeasureConversions), nameof(AccumulateOutput)));
        AttachObserver(code, input, AccessTools.Field(typeof(ElementConverter.ConsumedElement), nameof(ElementConverter.ConsumedElement.Tag)),
            AccessTools.Method(typeof(MeasureConversions), nameof(AccumulateInput)));
        Available = true;
        return code;
    }

    private static int FindSettlement(List<CodeInstruction> code, MethodInfo accumulate, FieldInfo handle)
    {
        int settlement = -1;
        for (int i = 3; i < code.Count; i++)
        {
            if (!code[i].Calls(accumulate) || code[i - 2].opcode != OpCodes.Ldfld ||
                !Equals(code[i - 2].operand, handle) || !code[i - 3].IsLdloc() || !code[i - 1].IsLdloc())
                continue;

            if (settlement >= 0 || code[i].labels.Count > 0 || code[i].blocks.Count > 0)
                return -1;
            settlement = i;
        }
        return settlement;
    }

    private static void AttachObserver(List<CodeInstruction> code, int index, FieldInfo material, MethodInfo observer)
    {
        CodeInstruction load = code[index - 3];
        code[index] = new CodeInstruction(OpCodes.Call, observer);
        code.InsertRange(index, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(load.opcode, load.operand),
            new CodeInstruction(OpCodes.Ldfld, material)
        });
    }

    private static void AccumulateInput(Accumulators accumulators, HandleVector<int>.Handle handle,
        float mass, ElementConverter converter, Tag material)
    {
        accumulators.Accumulate(handle, mass);
        PlannerSession.Measurements.Record(converter, MeasuredFlow.ConvertedInput, mass, material);
    }

    private static void AccumulateOutput(Accumulators accumulators, HandleVector<int>.Handle handle,
        float mass, ElementConverter converter, SimHashes element)
    {
        accumulators.Accumulate(handle, mass);
        PlannerSession.Measurements.RecordMaterialOutput(converter, element, mass);
    }
}

[HarmonyPatch(typeof(EnergyGenerator), nameof(EnergyGenerator.EnergySim200ms))]
internal static class MeasureFuel
{
    internal static bool Available { get; private set; }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var consume = AccessTools.Method(typeof(Storage), nameof(Storage.ConsumeIgnoringDisease), new[] { typeof(Tag), typeof(float) });
        int index = code.FindIndex(instruction => instruction.Calls(consume));
        if (index < 0 || code.FindLastIndex(instruction => instruction.Calls(consume)) != index ||
            code[index].labels.Count > 0 || code[index].blocks.Count > 0)
        {
            Available = false;
            UnityEngine.Debug.LogWarning("[OniResourceBalance] Fuel measurements are unavailable because the native settlement changed.");
            return code;
        }

        code[index] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MeasureFuel), nameof(Consume)));
        code.Insert(index, new CodeInstruction(OpCodes.Ldarg_0));
        Available = true;
        return code;
    }

    private static void Consume(Storage storage, Tag material, float requested, EnergyGenerator generator)
    {
        // ConsumeIgnoringDisease delegates to this overload and discards the returned mass.
        storage.ConsumeAndGetDisease(material, requested, out float consumed, out _, out _);
        PlannerSession.Measurements.Record(generator, MeasuredFlow.ConvertedInput, consumed, material);
    }
}

[HarmonyPatch(typeof(EnergyGenerator), "Emit")]
internal static class MeasureGeneratorByproducts
{
    private static void Postfix(EnergyGenerator __instance, EnergyGenerator.OutputItem __0, float __1)
    {
        if (MeasureFuel.Available)
            PlannerSession.Measurements.RecordMaterialOutput(__instance, __0.element, __0.creationRate * __1);
    }
}

[HarmonyPatch(typeof(FertilityMonitor.Instance), nameof(FertilityMonitor.Instance.ShowEgg))]
internal static class MeasureRanchEggs
{
    private static void Prefix(GameObject ___egg, out GameObject __state)
    {
        __state = PlannerSession.Measurements.IsCollecting && ___egg != null && !___egg.activeSelf ? ___egg : null;
    }

    private static void Postfix(FertilityMonitor.Instance __instance, GameObject __state)
    {
        if (__state != null && __state.activeSelf && __instance.PrefabId != null &&
            __instance.PrefabId.TryGetComponent<CreatureBrain>(out var creature))
            PlannerSession.Measurements.RecordRanchEgg(creature, __state.PrefabID());
    }
}
