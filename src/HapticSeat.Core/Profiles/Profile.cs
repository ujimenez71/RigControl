using System.Text.RegularExpressions;

namespace HapticSeat.Profiles;

/// <summary>Ajustes de un efecto dentro de un perfil.</summary>
public sealed class EffectSetting
{
    public bool Enabled { get; set; } = true;

    /// <summary>Intensidad en %, 0..200.</summary>
    public int Gain { get; set; } = 100;
}

/// <summary>
/// Perfil de efectos. Se guarda como JSON en %APPDATA%\HapticSeat\profiles y se elige solo según el avión
/// cuando su nombre encaja con algún patrón de <see cref="Aircraft"/> (comodines * y ?, sin distinguir
/// mayúsculas). El perfil <see cref="DefaultName"/> es el de reserva y no se puede borrar.
/// </summary>
public sealed class Profile
{
    public const string DefaultName = "General";

    public string Name { get; set; } = DefaultName;

    /// <summary>Patrones de nombre de avión tal como los da el simulador, p. ej. "Bf 109*".</summary>
    public List<string> Aircraft { get; set; } = [];

    /// <summary>Intensidad general en %, 0..200.</summary>
    public int MasterGain { get; set; } = 100;

    /// <summary>Por id de efecto (ver IEffect.Id). Los que falten usan los valores por defecto.</summary>
    public Dictionary<string, EffectSetting> Effects { get; set; } = [];

    public bool IsDefault => Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase);

    public EffectSetting For(string effectId)
    {
        if (!Effects.TryGetValue(effectId, out var setting))
            Effects[effectId] = setting = new EffectSetting();
        return setting;
    }

    public bool Matches(string aircraft) =>
        aircraft.Length > 0 && Aircraft.Any(pattern => Wildcard(pattern).IsMatch(aircraft));

    public Profile Clone(string? name = null) => new()
    {
        Name = name ?? Name,
        Aircraft = [.. Aircraft],
        MasterGain = MasterGain,
        Effects = Effects.ToDictionary(kv => kv.Key, kv => new EffectSetting { Enabled = kv.Value.Enabled, Gain = kv.Value.Gain }),
    };

    private static Regex Wildcard(string pattern) => new(
        "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
