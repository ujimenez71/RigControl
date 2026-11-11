using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class SnapshotsView : UserControl
{
    public SnapshotsView() => InitializeComponent();

    private SnapshotsViewModel Vm => (SnapshotsViewModel)DataContext;

    private void Create_Click(object sender, RoutedEventArgs e) => Vm.Create(all: false);
    private void CreateAll_Click(object sender, RoutedEventArgs e) => Vm.Create(all: true);
    private void Compare_Click(object sender, RoutedEventArgs e) => Vm.Compare();

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedGroup is not { } g || Vm.SelectedSnapshot is not { } s) return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"¿Restaurar {g.Name} a como estaba el {s.When}{(s.Name.Length > 0 ? $" («{s.Name}»)" : "")}?\n\n" +
            "Se sobrescriben sus archivos; los que se hayan creado después se quedan. " +
            "Antes se guarda lo actual como instantánea automática, así que se puede deshacer.",
            "Restaurar instantánea", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Vm.Restore();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedSnapshot is not { } s) return;
        var answer = MessageBox.Show(Window.GetWindow(this), $"¿Borrar la instantánea del {s.When}? No se puede deshacer.",
                                     "Borrar instantánea", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) Vm.Delete();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Vm.FolderOfSelectedGroup();
        Directory.CreateDirectory(dir);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
