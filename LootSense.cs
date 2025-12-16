using HarmonyLib;

[HarmonyPatch(typeof(EntityPlayerLocal), "Update")]
public static class LootSense
{
    private static readonly LootSenseController Controller = new();

    [HarmonyPostfix]
    public static void Postfix(EntityPlayerLocal __instance)
    {
        Controller.OnPlayerUpdate(__instance);
    }

    internal static bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
        => Controller.TrySetHighlightMode(token, out mode, out message);

    internal static bool TrySetOpacity(string token, out string message)
        => Controller.TrySetOpacity(token, out message);

    internal static bool TrySetSize(string token, out string message)
        => Controller.TrySetSize(token, out message);

    internal static bool TrySetColor(string token, out string message)
        => Controller.TrySetColor(token, out message);

    internal static bool TryAdjustRange(string token, out string message)
        => Controller.TryAdjustRange(token, out message);

    internal static bool TrySetSystemState(string token, out string message)
        => Controller.TrySetSystemState(token, out message);

    internal static bool TrySetScanningState(string token, out string message)
        => Controller.TrySetScanningState(token, out message);

    internal static bool TrySetRenderingState(string token, out string message)
        => Controller.TrySetRenderingState(token, out message);

    internal static bool TrySetProfilerState(string token, out string message)
        => Controller.TrySetProfilerState(token, out message);

    internal static string GetHighlightModeSummary()
        => Controller.GetHighlightModeSummary();

    internal static string GetConfigDump()
        => Controller.GetConfigDump();
}
