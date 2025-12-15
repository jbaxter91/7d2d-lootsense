using System.Collections.Generic;
using System.Text;

public class ConsoleCmdLootSense : ConsoleCmdAbstract
{
    public override string getDescription() => "Controls PerceptionMastery LootSense highlight visuals.";

    public override string getHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Usage:");
        sb.AppendLine("  pm_lootsense status");
        sb.AppendLine("  pm_lootsense mode <box|icon>");
        sb.AppendLine("  pm_lootsense opacity <0-100>");
        sb.AppendLine("  pm_lootsense size <0-200>");
        sb.AppendLine("  pm_lootsense color <hex>");
        sb.AppendLine("  pm_lootsense range <deltaMeters>");
        return sb.ToString();
    }

    public override string[] getCommands() => new[] { "pm_lootsense", "pmls" };

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        if (_params == null || _params.Count == 0)
        {
            OutputStatus();
            return;
        }

        var action = _params[0].ToLowerInvariant();
        switch (action)
        {
            case "mode":
                if (_params.Count < 2)
                {
                    Output("Missing mode argument. " + GetHelp());
                    return;
                }

                if (LootSense.TrySetHighlightMode(_params[1], out var mode, out var message))
                {
                    Output($"[LootSense] Highlight mode set to {mode.ToString().ToLowerInvariant()}.");
                }
                else
                {
                    Output("[LootSense] " + message);
                }
                break;

            case "opacity":
                if (_params.Count < 2)
                {
                    Output("Missing opacity value. " + GetHelp());
                    return;
                }

                LootSense.TrySetOpacity(_params[1], out var opacityMessage);
                Output("[LootSense] " + opacityMessage);
                break;

            case "size":
                if (_params.Count < 2)
                {
                    Output("Missing size value. " + GetHelp());
                    return;
                }

                LootSense.TrySetSize(_params[1], out var sizeMessage);
                Output("[LootSense] " + sizeMessage);
                break;

            case "color":
                if (_params.Count < 2)
                {
                    Output("Missing color value. " + GetHelp());
                    return;
                }

                LootSense.TrySetColor(_params[1], out var colorMessage);
                Output("[LootSense] " + colorMessage);
                break;

            case "range":
                if (_params.Count < 2)
                {
                    Output("Missing range delta. " + GetHelp());
                    return;
                }

                LootSense.TryAdjustRange(_params[1], out var rangeMessage);
                Output("[LootSense] " + rangeMessage);
                break;

            case "status":
                OutputStatus();
                break;

            default:
                Output($"Unknown argument '{action}'. " + GetHelp());
                break;
        }
    }

    private static void OutputStatus()
    {
        Output($"[LootSense] {LootSense.GetHighlightModeSummary()}");
    }

    private static void Output(string text)
    {
        var console = SdtdConsole.Instance;
        if (console != null)
            console.Output(text);
    }
}
