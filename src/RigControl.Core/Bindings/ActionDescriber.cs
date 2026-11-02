namespace RigControl.Core.Bindings;

/// <summary>
/// Convierte una acción ("B:LIGHTING_BEACON_1=1") en una descripción en español ("Luz anticolisión
/// (beacon) 1: encender") para mostrarla. Primero busca nombres conocidos; si no, traduce palabra a
/// palabra lo que sepa y deja el resto tal cual. Solo para la interfaz: nunca en el bucle caliente.
/// En el perfil, "desc" en un binding tiene prioridad sobre esto.
/// </summary>
public static class ActionDescriber
{
    // Nombres completos (sin prefijo ni valor). {n} = número que acompaña al nombre en la acción.
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Autopiloto del G1000 (B:)
        ["AS1000_AUTOPILOT_AP_PFD"] = "Autopiloto: conectar / desconectar (AP)",
        ["AS1000_AUTOPILOT_HEADING_PFD"] = "Autopiloto: modo rumbo (HDG)",
        ["AS1000_AUTOPILOT_NAVIGATION_PFD"] = "Autopiloto: modo navegación (NAV)",
        ["AS1000_AUTOPILOT_APPROACH_PFD"] = "Autopiloto: modo aproximación (APR)",
        ["AS1000_AUTOPILOT_BACKCOURSE_PFD"] = "Autopiloto: rumbo inverso (REV / BC)",
        ["AS1000_AUTOPILOT_ALTITUDE_PFD"] = "Autopiloto: mantener altitud (ALT)",
        ["AS1000_AUTOPILOT_VERTICALSPEED_PFD"] = "Autopiloto: velocidad vertical (VS)",
        ["AS1000_AUTOPILOT_FLIGHTLEVELCHANGE_PFD"] = "Autopiloto: cambio de nivel (FLC / IAS)",
        ["AS1000_HEADING_SYNC_PFD"] = "Rumbo seleccionado = rumbo actual (HDG sync)",
        // Eventos K: frecuentes
        ["AUTOPILOT_OFF"] = "Autopiloto: desconectar",
        ["AUTOPILOT_ON"] = "Autopiloto: conectar",
        ["AP_MASTER"] = "Autopiloto: conectar / desconectar (AP)",
        ["ELEV_TRIM_DN"] = "Trim de profundidad: morro abajo",
        ["ELEV_TRIM_UP"] = "Trim de profundidad: morro arriba",
        ["GEAR_UP"] = "Tren: subir",
        ["GEAR_DOWN"] = "Tren: bajar",
        ["GEAR_TOGGLE"] = "Tren: subir / bajar",
        ["PARKING_BRAKES"] = "Freno de aparcamiento: alternar",
        ["FLAPS_INCR"] = "Flaps: un punto más",
        ["FLAPS_DECR"] = "Flaps: un punto menos",
        ["FLAPS_UP"] = "Flaps: recoger",
        ["FLAPS_DOWN"] = "Flaps: extender del todo",
        ["PAUSE_TOGGLE"] = "Pausa: alternar",
        ["AP_HDG_HOLD"] = "Autopiloto: modo rumbo (HDG)",
        ["AP_NAV1_HOLD"] = "Autopiloto: modo navegación (NAV)",
        ["AP_APR_HOLD"] = "Autopiloto: modo aproximación (APR)",
        ["AP_ALT_HOLD"] = "Autopiloto: mantener altitud (ALT)",
        ["AP_VS_HOLD"] = "Autopiloto: velocidad vertical (VS)",
        ["HEADING_BUG_INC"] = "Rumbo seleccionado: +1°",
        ["HEADING_BUG_DEC"] = "Rumbo seleccionado: -1°",
        ["AP_ALT_VAR_INC"] = "Altitud seleccionada: subir",
        ["AP_ALT_VAR_DEC"] = "Altitud seleccionada: bajar",
        ["AP_VS_VAR_INC"] = "Velocidad vertical seleccionada: subir",
        ["AP_VS_VAR_DEC"] = "Velocidad vertical seleccionada: bajar",
        ["AILERON_TRIM_LEFT"] = "Trim de alerones: izquierda",
        ["AILERON_TRIM_RIGHT"] = "Trim de alerones: derecha",
        ["RUDDER_TRIM_LEFT"] = "Trim de timón: izquierda",
        ["RUDDER_TRIM_RIGHT"] = "Trim de timón: derecha",
        ["BRAKES"] = "Frenos",
        ["SPOILERS_TOGGLE"] = "Aerofrenos: alternar",
        ["MAGNETO_OFF"] = "Magnetos: OFF",
        ["MAGNETO_RIGHT"] = "Magnetos: R (derecho)",
        ["MAGNETO_LEFT"] = "Magnetos: L (izquierdo)",
        ["MAGNETO_BOTH"] = "Magnetos: BOTH (ambos)",
        ["MAGNETO_START"] = "Magnetos: START (arranque)",
        ["TOGGLE_NAV_LIGHTS"] = "Luces de navegación: alternar",
        ["TOGGLE_BEACON_LIGHTS"] = "Luz anticolisión (beacon): alternar",
        ["TOGGLE_TAXI_LIGHTS"] = "Luz de rodaje: alternar",
        ["LANDING_LIGHTS_TOGGLE"] = "Luz de aterrizaje: alternar",
        ["STROBES_TOGGLE"] = "Luces estroboscópicas: alternar",
        ["PANEL_LIGHTS_TOGGLE"] = "Iluminación del panel: alternar",
        ["TOGGLE_MASTER_BATTERY"] = "Batería: alternar",
        ["TOGGLE_MASTER_ALTERNATOR"] = "Alternador: alternar",
        ["TOGGLE_AVIONICS_MASTER"] = "Aviónica: alternar",
        ["PITOT_HEAT_TOGGLE"] = "Calefacción del pitot: alternar",
        // Interruptores B: (con valor: =0 apagar, =1 encender)
        ["ELECTRICAL_BATTERY_{n}"] = "Batería {n}",
        ["ELECTRICAL_ALTERNATOR_{n}"] = "Alternador {n}",
        ["ELECTRICAL_LINE_BUS_{n}_TO_AVIONICS_BUS_{n}"] = "Bus de aviónica {n}",
        ["LIGHTING_BEACON_{n}"] = "Luz anticolisión (beacon) {n}",
        ["LIGHTING_LANDING_{n}"] = "Luz de aterrizaje {n}",
        ["LIGHTING_TAXI_{n}"] = "Luz de rodaje {n}",
        ["LIGHTING_NAV_{n}"] = "Luces de navegación",
        ["LIGHTING_STROBE_{n}"] = "Luces estroboscópicas (strobe)",
        ["LIGHTING_PANEL_{n}"] = "Iluminación del panel {n}",
        ["LIGHTING_CABIN_{n}"] = "Luz de cabina {n}",
        ["LIGHTING_RECOGNITION_{n}"] = "Luz de reconocimiento {n}",
        ["LIGHTING_WING_{n}"] = "Luz de ala {n}",
        ["LIGHTING_LOGO_{n}"] = "Luz del logotipo {n}",
        ["ENGINE_MAGNETO_{n}"] = "Magnetos (motor {n})",
        ["FUEL_PUMP_{n}"] = "Bomba de combustible {n}",
        ["DEICE_PITOT_{n}"] = "Calefacción del pitot {n}",
    };

    // Nombres con valores con nombre propio (en vez de encender/apagar).
    private static readonly Dictionary<string, string[]> ValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENGINE_MAGNETO_{n}"] = ["OFF", "R (derecho)", "L (izquierdo)", "BOTH (ambos)", "START (arranque)"],
    };

    // Traducción palabra a palabra para lo que no está en Known.
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUTOPILOT"] = "autopiloto", ["AP"] = "autopiloto", ["HEADING"] = "rumbo", ["ALTITUDE"] = "altitud",
        ["SPEED"] = "velocidad", ["VERTICAL"] = "vertical", ["APPROACH"] = "aproximación", ["NAVIGATION"] = "navegación",
        ["LIGHTING"] = "luz", ["LIGHT"] = "luz", ["LIGHTS"] = "luces", ["NAV"] = "navegación", ["LANDING"] = "aterrizaje",
        ["TAXI"] = "rodaje", ["BEACON"] = "anticolisión", ["STROBE"] = "estroboscópica", ["STROBES"] = "estroboscópicas",
        ["PANEL"] = "panel", ["CABIN"] = "cabina", ["ELECTRICAL"] = "eléctrico", ["BATTERY"] = "batería",
        ["ALTERNATOR"] = "alternador", ["GENERATOR"] = "generador", ["BUS"] = "bus", ["AVIONICS"] = "aviónica",
        ["MASTER"] = "general", ["ENGINE"] = "motor", ["MAGNETO"] = "magnetos", ["STARTER"] = "arranque",
        ["FUEL"] = "combustible", ["PUMP"] = "bomba", ["VALVE"] = "válvula", ["SELECTOR"] = "selector",
        ["MIXTURE"] = "mezcla", ["THROTTLE"] = "gases", ["PROP"] = "hélice", ["PROPELLER"] = "hélice",
        ["ELEV"] = "profundidad", ["ELEVATOR"] = "profundidad", ["AILERON"] = "alerones", ["RUDDER"] = "timón",
        ["TRIM"] = "trim", ["FLAPS"] = "flaps", ["GEAR"] = "tren", ["BRAKE"] = "freno", ["BRAKES"] = "frenos",
        ["PARKING"] = "aparcamiento", ["PITOT"] = "pitot", ["HEAT"] = "calefacción", ["DEICE"] = "antihielo",
        ["ANTI"] = "anti", ["ICE"] = "hielo", ["DOOR"] = "puerta", ["CANOPY"] = "cabina", ["SYNC"] = "sincronizar",
        ["TOGGLE"] = "alternar", ["SET"] = "fijar", ["INC"] = "subir", ["DEC"] = "bajar", ["INCR"] = "subir",
        ["DECR"] = "bajar", ["UP"] = "arriba", ["DN"] = "abajo", ["DOWN"] = "abajo", ["LEFT"] = "izquierda",
        ["RIGHT"] = "derecha", ["ON"] = "encender", ["OFF"] = "apagar", ["HOLD"] = "mantener", ["LOCK"] = "bloquear",
        ["KNOB"] = "mando", ["SWITCH"] = "interruptor", ["BUTTON"] = "botón", ["PUSH"] = "pulsar", ["RESET"] = "reiniciar",
        ["PFD"] = "(PFD)", ["MFD"] = "(MFD)", ["AS1000"] = "G1000:", ["TO"] = "a",
    };

    /// <summary>Eventos K: frecuentes, para las sugerencias del editor (los que tienen descripción propia).</summary>
    public static IReadOnlyList<string> CommonSimEvents { get; } =
    [
        "AUTOPILOT_OFF", "AUTOPILOT_ON", "AP_MASTER", "AP_HDG_HOLD", "AP_NAV1_HOLD", "AP_APR_HOLD", "AP_ALT_HOLD",
        "AP_VS_HOLD", "HEADING_BUG_INC", "HEADING_BUG_DEC", "AP_ALT_VAR_INC", "AP_ALT_VAR_DEC", "AP_VS_VAR_INC",
        "AP_VS_VAR_DEC", "ELEV_TRIM_DN", "ELEV_TRIM_UP", "AILERON_TRIM_LEFT", "AILERON_TRIM_RIGHT",
        "RUDDER_TRIM_LEFT", "RUDDER_TRIM_RIGHT", "GEAR_UP", "GEAR_DOWN", "GEAR_TOGGLE", "PARKING_BRAKES", "BRAKES",
        "FLAPS_INCR", "FLAPS_DECR", "FLAPS_UP", "FLAPS_DOWN", "SPOILERS_TOGGLE", "TOGGLE_NAV_LIGHTS",
        "TOGGLE_BEACON_LIGHTS", "TOGGLE_TAXI_LIGHTS", "LANDING_LIGHTS_TOGGLE", "STROBES_TOGGLE", "PANEL_LIGHTS_TOGGLE",
        "TOGGLE_MASTER_BATTERY", "TOGGLE_MASTER_ALTERNATOR", "TOGGLE_AVIONICS_MASTER", "PITOT_HEAT_TOGGLE",
        "MAGNETO_OFF", "MAGNETO_RIGHT", "MAGNETO_LEFT", "MAGNETO_BOTH", "MAGNETO_START", "PAUSE_TOGGLE",
    ];

    /// <param name="dcsControls">Controles de DCS-BIOS del avión del perfil (DcsBiosCatalog), para
    /// describir las acciones D: con la descripción del control y el nombre de la posición.</param>
    public static string Describe(string action, IReadOnlyList<DcsControl>? dcsControls = null)
    {
        var text = action.Trim();
        if (text.Length == 0) return "";

        var colon = text.IndexOf(':');
        var prefix = colon > 0 ? text[..colon].ToUpperInvariant() : "";
        switch (prefix)
        {
            case "K":
            case "B":
            {
                var body = text[(colon + 1)..];
                string? value = null;
                var eq = body.IndexOf('=');
                if (eq >= 0) { value = body[(eq + 1)..].Trim(); body = body[..eq].Trim(); }
                return DescribeSim(body, value, isInputEvent: prefix == "B");
            }
            case "L":
                return $"Variable local {text[(colon + 1)..].Replace("=", " = ")}";
            case "KEY":
                return $"Tecla {text[(colon + 1)..].Trim()}";
            case "D":
            {
                var parts = text[(colon + 1)..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return text;
                var control = parts[0].ToUpperInvariant(); // nombres de cada avión: mejor tal cual
                var known = dcsControls?.FirstOrDefault(c => c.Name.Equals(control, StringComparison.OrdinalIgnoreCase));
                var label = known is { Description.Length: > 0 } ? known.Description : control;
                if (parts.Length == 1) return $"DCS: {label} (pulsar)";
                var raw = parts[1].ToUpperInvariant();
                var v = raw switch
                {
                    "INC" => "siguiente posición", "DEC" => "posición anterior", "TOGGLE" => "alternar",
                    _ when known is { Positions.Length: > 0 } && int.TryParse(raw, out var n) && n >= 0 && n < known.Positions.Length
                        => $"→ {known.Positions[n]}",
                    _ => $"= {raw}",
                };
                return v.StartsWith('→') ? $"DCS: {label} {v}" : $"DCS: {label}, {v}";
            }
            default:
                return text; // sin prefijo: el texto ya es la descripción
        }
    }

    private static string DescribeSim(string name, string? value, bool isInputEvent)
    {
        // Busca el nombre tal cual, o con sus números sustituidos por {n}.
        var numbers = new List<string>();
        var pattern = string.Join("_", name.Split('_').Select(p =>
        {
            if (p.Length > 0 && p.All(char.IsDigit)) { numbers.Add(p); return "{n}"; }
            return p;
        }));

        string? label = null;
        if (Known.TryGetValue(name, out var exact)) label = exact;
        else if (Known.TryGetValue(pattern, out var withNumber))
            label = withNumber.Replace("{n}", numbers.Count > 0 ? numbers[0] : "");

        // Genéricos: "TOGGLE_X" / "X_TOGGLE" -> "x: alternar".
        if (label is null && name.StartsWith("TOGGLE_", StringComparison.OrdinalIgnoreCase))
            label = $"{Capitalize(Humanize(name[7..]))}: alternar";
        else if (label is null && name.EndsWith("_TOGGLE", StringComparison.OrdinalIgnoreCase))
            label = $"{Capitalize(Humanize(name[..^7]))}: alternar";
        label ??= Capitalize(Humanize(name));
        label = label.Replace("  ", " ").Trim();

        if (value is null) return label; // K: sin valor o B: pulsador
        if (ValueNames.TryGetValue(pattern, out var names) && int.TryParse(value, out var iv) && iv >= 0 && iv < names.Length)
            return $"{label}: {names[iv]}";
        if (isInputEvent && value == "0") return $"{label}: apagar";
        if (isInputEvent && value == "1") return $"{label}: encender";
        return $"{label} = {value}";
    }

    /// <summary>"LIGHTING_PANEL_1" -> "luz panel 1" (lo que no sabe traducir lo deja en minúsculas).</summary>
    private static string Humanize(string name) =>
        string.Join(" ", name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Words.TryGetValue(w, out var t) ? t : w.All(char.IsDigit) ? w : w.ToLowerInvariant()));

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
