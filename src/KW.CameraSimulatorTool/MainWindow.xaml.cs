using System.Windows;
using KW.CameraSimulatorTool.ViewModels;

namespace KW.CameraSimulatorTool;

public partial class MainWindow : Window
{
    private readonly SimulatorViewModel _vm;

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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _vm.StopServerAsync().Wait(TimeSpan.FromSeconds(3));
        base.OnClosing(e);
    }
}