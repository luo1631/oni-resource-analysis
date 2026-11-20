using System;
using HarmonyLib;
using KMod;

namespace OniResourceBalance;

public sealed class Mod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        base.OnLoad(harmony);
        UnityEngine.Debug.Log("[OniResourceBalance] Resource planning mod loaded.");
    }
}

internal static class PlannerSession
{
    private static GameClock clock;
    private static int newDaySubscription;

    internal static readonly CycleMeasurements Measurements = new();
    internal static ResourceReport Current { get; private set; }
    internal static string Failure { get; private set; }
    internal static event System.Action ReportChanged;

    internal static void Begin()
    {
        End();
        clock = GameClock.Instance;
        if (clock == null)
            return;

        Measurements.Begin(clock, SaveGame.Instance?.GetComponent<ResourceBalanceSaveHistory>());
        newDaySubscription = clock.Subscribe((int)GameHashes.NewDay, OnNewDay);
    }

    internal static void End()
    {
        if (clock != null)
            clock.Unsubscribe(ref newDaySubscription);
        clock = null;
        Measurements.End();
        Current = null;
        Failure = null;
        ReportChanged = null;
    }

    internal static void Refresh()
    {
        if (GameClock.Instance == null || ClusterManager.Instance == null)
            return;

        try
        {
            var snapshot = GameClock.Instance;
            Current = GameRecipes.Read(snapshot.GetCycle(), snapshot.GetTimeSinceStartOfCycle(), Measurements);
            Failure = null;
        }
        catch (Exception error)
        {
            Failure = GameText.Tr("统计失败，请查看游戏日志。", "Calculation failed. See the game log.");
            UnityEngine.Debug.LogError("[OniResourceBalance] " + error);
        }
        try
        {
            ReportChanged?.Invoke();
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogError("[OniResourceBalance] Could not refresh the panel. " + error);
        }
    }

    private static void OnNewDay(object _)
    {
        Measurements.Advance();
        if (ReportChanged != null)
        {
            Refresh();
            return;
        }

        Current = null;
        Failure = null;
    }
}

[HarmonyPatch(typeof(SaveGame), "OnPrefabInit")]
internal static class AttachMeasurementHistory
{
    private static void Postfix(SaveGame __instance) => __instance.gameObject.AddOrGet<ResourceBalanceSaveHistory>();
}

[HarmonyPatch(typeof(Game), "OnSpawn")]
internal static class StartPlanner
{
    private static void Postfix() => PlannerSession.Begin();
}

[HarmonyPatch(typeof(Game), "OnCleanUp")]
internal static class StopPlanner
{
    private static void Prefix() => PlannerSession.End();
}

[HarmonyPatch(typeof(PinnedResourcesPanel), "OnSpawn")]
internal static class AddPlannerButton
{
    private static void Postfix(PinnedResourcesPanel __instance)
    {
        try
        {
            ResourcePlanningPanel.Attach(__instance);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogError("[OniResourceBalance] Could not attach the resources panel. " + error);
        }
    }
}
