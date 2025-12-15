using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class Init : IModApi
{
    public void InitMod(Mod modInstance)
    {
        var harmony = new Harmony("com.you.perceptionmastery.lootsense.unopenedonly");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Debug.Log("[PerceptionMasteryLootSense] Loaded (Unopened Only)");

        RegisterConsoleCommand();

    }

    private static void RegisterConsoleCommand()
    {
        try
        {
            var console = SdtdConsole.Instance;
            if (console == null)
                return;

            var commandsField = typeof(SdtdConsole).GetField("m_Commands", BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? typeof(SdtdConsole).GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic);

            if (commandsField == null)
            {
                Debug.LogWarning("[PerceptionMasteryLootSense] Unable to find SdtdConsole commands field for registration.");
                return;
            }

            var commands = commandsField.GetValue(console);
            if (commands == null)
            {
                Debug.LogWarning("[PerceptionMasteryLootSense] SdtdConsole commands list was null.");
                return;
            }

            var registerMethod = typeof(SdtdConsole).GetMethod("RegisterCommand",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { commandsField.FieldType, typeof(string), typeof(IConsoleCommand) },
                null);

            if (registerMethod == null)
            {
                Debug.LogWarning("[PerceptionMasteryLootSense] Could not locate SdtdConsole.RegisterCommand method.");
                return;
            }

            var command = new ConsoleCmdLootSense();
            var className = command.GetType().FullName ?? "ConsoleCmdLootSense";
            registerMethod.Invoke(console, new[] { commands, className, (IConsoleCommand)command });
            Debug.Log("[PerceptionMasteryLootSense] Console command 'pm_lootsense' registered.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to register console command: {e.Message}");
        }
    }
}
