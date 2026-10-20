// [ViewModels/SettingWorkspace] 添加远程模拟相机：直接创建 SIM-CAM-XX 行，无需 Basler 枚举。
// 对应 View/UC：UserControls/SettingWorkspace/VideoSettingsSection
using KW.Database.DataModel.DTO;
using KWinSprint.Models;
using KWinSprint.Services.Video;

namespace KWinSprint.ViewModels;

public sealed partial class VideoSettingsViewModel
{
    private static readonly (string Serial, string Display)[] SimulatedCameraSeeds = new[]
    {
        ("SIM-CAM-01", "Simulated Camera 1"),
        ("SIM-CAM-02", "Simulated Camera 2"),
        ("SIM-CAM-03", "Simulated Camera 3"),
        ("SIM-CAM-04", "Simulated Camera 4"),
    };

    /// <summary>
    /// 添加一台远程模拟相机（Vendor = "RemoteSimulated"）。
    /// 自动选取下一个未使用的 SIM-CAM-XX 序号，直接创建设备行并持久化。
    /// </summary>
    [RelayCommand]
    private async Task AddSimulatedCameraAsync()
    {
        string? serial = SimulatedCameraSeeds
            .Select(s => s.Serial)
            .FirstOrDefault(s => !TryFindDeviceRowBySerial(s, out _));

        if (serial is null)
        {
            return;
        }

        string display = SimulatedCameraSeeds.First(s => s.Serial == serial).Display;

        int nextIndex = Devices.Count + 1;
        string legacyDeviceName = $"REM: {display}";

        var namePayload = new CameraDeviceNameJson
        {
            DisplayName = display,
            DeviceName = legacyDeviceName,
            SerialNumber = serial,
            ModelName = "RemoteSimulatedCamera",
        };
        string columnJson = CameraDeviceNameColumn.Serialize(namePayload);

        DefaultCameraParametersValues defaults = _defaultCameraParametersProvider.GetDefaultCameraParameters();

        CameraDeviceSettingsDTO dto = new()
        {
            DeviceId = $"vc_{serial}",
            DeviceName = columnJson,
            SerialNumber = serial,
            SortOrder = nextIndex,
            Vendor = "RemoteSimulated",
            FrameRate = defaults.FrameRate,
            ExposureTime = defaults.ExposureTime,
            Gain = defaults.Gain,
            Brightness = defaults.Brightness,
            Width = 640,
            Height = 480,
            OffsetX = defaults.OffsetX,
            OffsetY = defaults.OffsetY,
            IsPaused = false,
            IsEnabled = true,
            HasPersistedImagingParameters = false,
            UpdateTime = DateTime.Now,
        };

        _deviceBySerial[dto.SerialNumber] = dto;
        Devices.Add(new VideoSettingsDeviceRowModel(
            nextIndex,
            display,
            columnJson,
            dto.SerialNumber,
            dto.Vendor,
            isEnabled: true));

        RefreshDeviceSequenceNumbers();
        SetActiveRow(Devices[^1]);
        await PersistDevicesAsync().ConfigureAwait(true);
    }
}