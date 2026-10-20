// === CameraVideoDeviceService.TryApplyBaslerEnvironmentGuardBeforePreviewAsync 补丁 ===
// 原方法在 Basler 运行时不可用时，为所有已配置槽位写入红叉错误。
// 修复：将已配置设备按 Vendor 分为 Basler / 非 Basler（模拟相机）。
//       若全部为非 Basler，直接返回 true，不做 Basler 探测。
//       否则仅对 Basler 子集运行探测并写入错误。

/* 替换原方法体：

    internal async Task<bool> TryApplyBaslerEnvironmentGuardBeforePreviewAsync(
        IReadOnlyList<CameraDeviceSettingsDTO>? configuredDevices = null)
    {
        configuredDevices ??= await GetConfiguredVideoCameraDevicesAsync().ConfigureAwait(false);
        if (configuredDevices.Count == 0)
        {
            return true;
        }

        // 分离模拟相机（Vendor 含 "Simulated"），它们不需要 Basler 运行时。
        var baslerDevices = configuredDevices
            .Where(d => !string.Equals(d.Vendor, "Simulated", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(d.Vendor, "RemoteSimulated", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (baslerDevices.Count == 0)
        {
            // 全部为模拟相机，跳过 Basler 环境探测。
            return true;
        }

        BaslerRuntimeProbeResult probe = BaslerRuntimeProbe.Probe();
        string? message = ResolveBaslerEnvironmentBlockMessage(probe);
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        await UiThreadMarshal.RunAsync(() =>
                ApplyBaslerEnvironmentErrorToConfiguredSlots(message, baslerDevices))
            .ConfigureAwait(false);
        return false;
    }
*/