using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Acciones: parser de K:/B:/L:/key:.</summary>
internal static class ActionTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        void Good(string text, ActionKind kind, string name = "", double? value = null, string? keys = null)
        {
            var a = ActionSpec.Parse(text, out var err);
            var ok = a is not null && err is null && a.Kind == kind && a.Name == name
                     && (value is null || (a.HasValue && a.Value == value)) && (keys is null || a.Keys?.Text == keys);
            Check(ok, $"{text,-28} -> {a?.Kind} {a?.Name} {(a?.HasValue == true ? a.Value : "")} {a?.Keys?.Text} " +
                      (a?.Keys is { } k ? "[" + string.Join(" ", k.ScanCodes.Select((s, i) => $"{s:X2}{(k.Extended[i] ? "e" : "")}")) + "]" : "") + err);
        }
        void Bad(string text)
        {
            var a = ActionSpec.Parse(text, out var err);
            Check(a is null && err is not null, $"rechaza {text,-22}: {err}");
        }

        Good("AP HDG", ActionKind.Print);
        Good("K:gear_up", ActionKind.SimEvent, "GEAR_UP");
        Good("K:HEADING_BUG_SET=180", ActionKind.SimEvent, "HEADING_BUG_SET", 180);
        Good("K:AXIS_FLAPS_SET=-16383", ActionKind.SimEvent, "AXIS_FLAPS_SET", -16383);
        Good("B:AUTOPILOT_HEADING_HOLD", ActionKind.InputEvent, "AUTOPILOT_HEADING_HOLD", 1);
        Good("B:LIGHTING_LANDING_1=0", ActionKind.InputEvent, "LIGHTING_LANDING_1", 0);
        Good("L:A32NX_Efis_Mode=2.5", ActionKind.LVar, "A32NX_Efis_Mode", 2.5);
        Good("key:G", ActionKind.Key, keys: "G");
        Good("key:ctrl+shift+g", ActionKind.Key, keys: "CTRL+SHIFT+G");
        Good("key:F12", ActionKind.Key, keys: "F12");
        Good("key:RALT+UP", ActionKind.Key, keys: "RALT+UP");
        Good("key:NUM+", ActionKind.Key, keys: "NUM+");
        Good("key:CTRL+NUM+", ActionKind.Key, keys: "CTRL+NUM+");
        Good("key:NUM5", ActionKind.Key, keys: "NUM5");
        Good("key:CTRL+NUM/", ActionKind.Key, keys: "CTRL+NUM/");

        Bad("K:");
        Bad("K:GEAR UP");
        Bad("K:HEADING_BUG_SET=1.5");
        Bad("K:X=abc");
        Bad("L:MiVar");
        Bad("key:");
        Bad("key:CTRL+");
        Bad("key:CTRL+PATATA");
        Bad("key:G+CTRL");
        Bad("key:F25");

        return fails;
    }
}
