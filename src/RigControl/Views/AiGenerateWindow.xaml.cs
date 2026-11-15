using System.Windows;
using RigControl.ViewModels;

namespace RigControl.Views;

/// <summary>Ventana "Generar con IA". DialogResult = true si se pide abrir el borrador en el editor.</summary>
public partial class AiGenerateWindow : Window
{
    public AiGenerateWindow(AiGenerateViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += (_, _) => vm.Cancel(); // cerrar a mitad = dejar de esperar
    }

    private AiGenerateViewModel Vm => (AiGenerateViewModel)DataContext;

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password.Trim().Length == 0) return;
        Vm.SaveKey(KeyBox.Password);
        KeyBox.Clear();
    }

    private void ForgetKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "¿Borrar la clave guardada?", "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Vm.ForgetKey();
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) => await Vm.DetectAircraftAsync();

    private async void Generate_Click(object sender, RoutedEventArgs e) => await Vm.GenerateAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Vm.Cancel();

    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
