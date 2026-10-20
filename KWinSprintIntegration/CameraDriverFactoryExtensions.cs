namespace KW.Capture.Camera;

public static class CameraDriverFactoryExtensions
{
    public static void RegisterCameraDrivers(this ICameraDriverFactory factory)
    {
        if (factory is null)
            throw new ArgumentNullException(nameof(factory));

        factory.RegisterCameraDriver<BaslerCamera>();

        // 远程模拟相机（连接 CameraSimulatorTool 桌面应用）
        factory.RegisterCameraDriver<RemoteSimulatedCamera>();

        // 进程内模拟相机（本地 Timer 生成帧，无需独立应用）
        factory.RegisterCameraDriver<SimulatedCamera>();

        // 以下驱动已迁入本程序集，默认注释不注册；按需取消注释启用。
        //factory.RegisterCameraDriver<UsbCamera>();
        //factory.RegisterCameraDriver<HikCamera>();
        //factory.RegisterCameraDriver<HuarayCamera>();
        //factory.RegisterCameraDriver<ZCamCamera>();
    }
}