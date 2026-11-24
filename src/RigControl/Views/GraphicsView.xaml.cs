using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class GraphicsView : UserControl
{
    public GraphicsView() => InitializeComponent();

    private GraphicsViewModel Vm => (GraphicsViewModel)DataContext;

    private bool _flightsLoaded;

    /// <summary>Los vuelos se leen la primera vez que se abre su pestaña (leer todos los registros de Pimax lleva un momento).</summary>
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || Tabs.SelectedItem != FlightsTab || _flightsLoaded) return;
        _flightsLoaded = true;
        await Vm.LoadFlightsAsync();
    }

    private async void RefreshFlights_Click(object sender, RoutedEventArgs e) => await Vm.LoadFlightsAsync();

    /// <summary>El registro de progreso sigue a la última línea cuando crece.</summary>
    private void LogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0) LogScroll.ScrollToEnd();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Vm.Reload(Vm.Selected?.Name);

    private void UseDetectedHeadset_Click(object sender, RoutedEventArgs e) => Vm.UseDetectedHeadset();

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Vm.FilePath))
        {
            MessageBox.Show(Window.GetWindow(this)!, "graficos.json aún no existe: se crea al guardar el primer perfil.", "RigControl",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(Vm.FilePath) { UseShellExecute = true });
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Exists(Vm.NewName) && MessageBox.Show(Window.GetWindow(this)!, $"Ya hay un perfil «{Vm.NewName.Trim()}». ¿Sustituirlo por los ajustes actuales?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Vm.Capture();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"¿Aplicar «{p.Name}» a {p.Model.SimName}? Cambia {Vm.Changes.Count} ajuste(s); antes se guarda una copia .bak del archivo.\n\nEl simulador debe estar cerrado.",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Vm.ApplyAsync();
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        var target = Vm.MeasureChoice == GraphicsViewModel.CurrentSettings ? "los ajustes actuales" : $"el perfil «{Vm.MeasureChoice}»";
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"Se va a abrir DCS {(Vm.MeasureVr ? "en VR" : "en el monitor")} para medir {target}: unos 5 minutos sin tocar el ordenador.\n\n" +
                (Vm.MeasureVr ? $"Comprueba que la {Vm.HeadsetName} está encendida y despierta (puesta sin moverte o con el sensor de proximidad tapado).\n\n" : "") +
                "DCS tiene que estar cerrado. Tu configuración queda como está. ¿Empezar?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Vm.MeasureAsync();
    }

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!,
                "Optimizar en VR: DCS se abre una o dos veces y prueba a subir o bajar cada ajuste (sombras, nubes, agua, SSAO...) " +
                "midiendo si llega al refresco del visor. Unos 15-20 minutos sin tocar el ordenador.\n\n" +
                $"La {Vm.HeadsetName} encendida y despierta (puesta sin moverte o con el sensor de proximidad tapado). DCS cerrado.\n\n" +
                "Al final propone un perfil nuevo; no se aplica nada sin que lo decidas. ¿Empezar?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Vm.OptimizeAsync();
    }

    private void CancelMeasure_Click(object sender, RoutedEventArgs e) => Vm.CancelMeasure();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"¿Borrar el perfil «{p.Name}»? (No toca la configuración del simulador.)",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Vm.DeleteSelected();
    }
}
