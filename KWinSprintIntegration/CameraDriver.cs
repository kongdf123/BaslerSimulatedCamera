namespace KW.Capture.Camera;

public enum CameraDriver
{
    Basler = 0,
    Hik = 1,
    Huaray = 2,
    ZCam = 3,
    Simulated = 4,
    Usb = 5,

    /// <summary>
    /// 远程模拟相机：连接独立的 CameraSimulatorTool 桌面应用（TCP），用于无真机调试。
    /// </summary>
    RemoteSimulated = 6,
}