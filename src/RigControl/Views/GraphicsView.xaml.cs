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

    private void Reload_Click(object sender, RoutedEventArgs e) => Vm.Reload(Vm.Selected?.Name);

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

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"¿Borrar el perfil «{p.Name}»? (No toca la configuración del simulador.)",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Vm.DeleteSelected();
    }
}
