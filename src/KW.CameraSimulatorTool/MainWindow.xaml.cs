using System.Windows;
using KW.CameraSimulatorTool.ViewModels;

namespace KW.CameraSimulatorTool;

public partial class MainWindow : Window
{
    private readonly SimulatorViewModel _vm;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new SimulatorViewModel();
        DataContext = _vm;
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        await _vm.StartServerAsync();
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        await _vm.StopServerAsync();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        IsEnabled = false;

        try
        {
            await _vm.StopServerAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The simulator could not shut down cleanly:\n{ex.Message}",
                "Shutdown error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}