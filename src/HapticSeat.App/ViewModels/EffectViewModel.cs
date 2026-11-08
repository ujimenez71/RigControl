using CommunityToolkit.Mvvm.ComponentModel;
using HapticSeat.Effects;
using HapticSeat.Profiles;

namespace HapticSeat.App.ViewModels;

/// <summary>Una fila de la lista de efectos: activo, intensidad y botón Probar.</summary>
public sealed partial class EffectViewModel(IEffect effect, Action<EffectViewModel> changed) : ObservableObject
{
    private bool _loading;

    public string Id => effect.Id;
    public string Name => effect.Name;
    public string Description => effect.Description;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    /// <summary>Intensidad en %, 0..200.</summary>
    [ObservableProperty]
    public partial int Gain { get; set; } = 100;

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    /// <summary>Carga los valores del perfil sin avisar de cambios (no es una edición del usuario).</summary>
    public void Load(EffectSetting setting)
    {
        _loading = true;
        Enabled = setting.Enabled;
        Gain = setting.Gain;
        _loading = false;
    }

    partial void OnEnabledChanged(bool value) => Notify();

    partial void OnGainChanged(int value) => Notify();

    private void Notify()
    {
        if (!_loading) changed(this);
    }
}
