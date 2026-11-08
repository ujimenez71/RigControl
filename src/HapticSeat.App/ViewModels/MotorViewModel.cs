using CommunityToolkit.Mvvm.ComponentModel;
using HapticSeat.Hf8;

namespace HapticSeat.App.ViewModels;

/// <summary>Un motor: intensidad en vivo (dibujo del cojín) y nivel mínimo (calibración).</summary>
public sealed partial class MotorViewModel(int index, int minLevel, Action<MotorViewModel> minLevelChanged) : ObservableObject
{
    private bool _loading;

    public int Index => index;
    public string Name => Hf8Motors.Names[index];

    /// <summary>Intensidad actual 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glow))]
    public partial double Intensity { get; set; }

    /// <summary>Opacidad del color en el dibujo: siempre algo visible para que se vea la zona.</summary>
    public double Glow => 0.08 + 0.92 * Intensity;

    [ObservableProperty]
    public partial int MinLevel { get; set; } = minLevel;

    [ObservableProperty]
    public partial bool IsMeasuring { get; set; }

    public void LoadMinLevel(int level)
    {
        _loading = true;
        MinLevel = level;
        _loading = false;
    }

    partial void OnMinLevelChanged(int value)
    {
        if (!_loading) minLevelChanged(this);
    }
}
