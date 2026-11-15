using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RigControl.Core.Sheets;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    private ProfilesViewModel Vm => (ProfilesViewModel)DataContext;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Vm.OpenFolder();

    private void OpenSelected_Click(object sender, RoutedEventArgs e) => Vm.OpenSelected();

    private void New_Click(object sender, RoutedEventArgs e) => Vm.NewProfile();

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var ai = new AiGenerateViewModel();
        var window = new AiGenerateWindow(ai) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || ai.Result?.Profile is not { } file) return;
        var path = ai.TargetPath ?? Vm.FreeProfilePath(file.Aircraft.FirstOrDefault()?.Replace("dcs:", "") ?? file.Name);
        Vm.OpenDraft(file, path);
    }

    private void Photos_Click(object sender, RoutedEventArgs e) =>
        new DeviceLayoutWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void Sheet_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        try
        {
            var html = ProfileSheet.WriteHtml(p.Path);
            Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, "RigControl", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        PdfButton.IsEnabled = false;
        PdfButton.Content = "Haciendo el PDF…";
        try
        {
            var pdf = await Task.Run(() => ProfileSheet.WritePdf(p.Path));
            Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException
                                       or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, "RigControl", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PdfButton.Content = "PDF";
            PdfButton.IsEnabled = true;
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedHasComments() && MessageBox.Show(Window.GetWindow(this)!,
                "Este perfil tiene comentarios // en el JSON. Si guardas desde el editor, se perderán " +
                "(lo importante puede ir en Notas y en la descripción de cada binding). Queda una copia .json.bak.\n\n¿Abrir el editor?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        if (Vm.BeginEdit() is { } error)
            MessageBox.Show(Window.GetWindow(this)!, error, "RigControl", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"¿Borrar el perfil \"{p.Name}\" ({p.FileName})?\n\nQueda una copia en {p.FileName}.bak.",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Vm.DeleteSelected();
    }
}
