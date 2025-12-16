using HarmonyLib;

/// <summary>
/// Harmony entry point that keeps the controller in sync with each local player's update loop.
/// </summary>
[HarmonyPatch(typeof(EntityPlayerLocal), "Update")]
public static class LootSense
{
    private static readonly LootSenseController Controller = new();

    /// <summary>
    /// Post-update hook invoked after every player Update() call so LootSense can run.
    /// </summary>
    [HarmonyPostfix]
    public static void Postfix(EntityPlayerLocal __instance)
    {
        Controller.OnPlayerUpdate(__instance);
    }

    /// <summary>
    /// Forwards highlight mode changes to the active controller instance.
    /// </summary>
    internal static bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
        => Controller.TrySetHighlightMode(token, out mode, out message);

    /// <summary>
    /// Routes opacity updates through the controller.
    /// </summary>
    internal static bool TrySetOpacity(string token, out string message)
        => Controller.TrySetOpacity(token, out message);

    /// <summary>
    /// Routes size updates through the controller.
    /// </summary>
    internal static bool TrySetSize(string token, out string message)
        => Controller.TrySetSize(token, out message);

    /// <summary>
    /// Routes color updates through the controller.
    /// </summary>
    internal static bool TrySetColor(string token, out string message)
        => Controller.TrySetColor(token, out message);

    /// <summary>
    /// Routes range adjustments through the controller.
    /// </summary>
    internal static bool TryAdjustRange(string token, out string message)
        => Controller.TryAdjustRange(token, out message);

    /// <summary>
    /// Toggles the core system on/off.
    /// </summary>
    internal static bool TrySetSystemState(string token, out string message)
        => Controller.TrySetSystemState(token, out message);

    /// <summary>
    /// Toggles scanning.
    /// </summary>
    internal static bool TrySetScanningState(string token, out string message)
        => Controller.TrySetScanningState(token, out message);

    /// <summary>
    /// Toggles rendering.
    /// </summary>
    internal static bool TrySetRenderingState(string token, out string message)
        => Controller.TrySetRenderingState(token, out message);

    /// <summary>
    /// Toggles the profiler HUD.
    /// </summary>
    internal static bool TrySetProfilerState(string token, out string message)
        => Controller.TrySetProfilerState(token, out message);

    /// <summary>
    /// Returns a compact textual status for console output.
    /// </summary>
    internal static string GetHighlightModeSummary()
        => Controller.GetHighlightModeSummary();

    /// <summary>
    /// Emits a verbose dump of the current configuration and derived ranges.
    /// </summary>
    internal static string GetConfigDump()
        => Controller.GetConfigDump();
}
