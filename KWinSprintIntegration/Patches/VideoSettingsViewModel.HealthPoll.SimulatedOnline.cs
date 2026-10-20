// === VideoSettingsViewModel.HealthPoll.BuildOnlineCameraPollResult 补丁 ===
// 原方法仅枚举 Basler 在线设备，导致 SIM-CAM-XX 行始终显示离线（红叉）。
// 修复：在 Basler 在线序列号集合基础上，补充所有模拟相机序列号（视为始终在线）。

/* 在 BuildOnlineCameraPollResult 方法中，
   在 return new OnlineCameraPollResult(...) 之前插入：

        // 模拟相机（SIM-CAM-XX）不依赖 Basler 枚举，始终视为在线。
        for (int index = 0; index < Devices.Count; index++)
        {
            VideoSettingsDeviceRowModel row = Devices[index];
            if (row.SerialNumber.StartsWith("SIM-CAM-", StringComparison.OrdinalIgnoreCase))
            {
                onlineSerials.Add(row.SerialNumber);
            }
        }

*/