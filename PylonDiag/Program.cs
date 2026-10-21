using System;
using System.IO;
using System.Reflection;

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "started.txt"), "STARTED");

try
{
    using var sw = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "diag_result.txt"), false);
    void Log(string msg) { Console.WriteLine(msg); sw.WriteLine(msg); sw.Flush(); }

    Log("=== PYLON CAMERA EMULATION DIAGNOSTIC (SIMPLE) ===");
    Log("");
    Log($"PYLON_CAMEMU (Process) = {Environment.GetEnvironmentVariable("PYLON_CAMEMU", EnvironmentVariableTarget.Process) ?? "<null>"}");
    Log($"PYLON_CAMEMU (User)    = {Environment.GetEnvironmentVariable("PYLON_CAMEMU", EnvironmentVariableTarget.User) ?? "<null>"}");
    Log("");

    Log("Loading Basler.Pylon...");
    var asm = Assembly.LoadFrom(@"C:\Program Files\Basler\PylonRuntime\Assemblies\net8.0\x64\Basler.Pylon.dll");
    Log($"Loaded OK: {asm.FullName}");

    Log("Calling CameraFinder.Enumerate() [with 30s timeout]...");
    var camFinderType = asm.GetType("Basler.Pylon.CameraFinder");
    var enumerateMethod = camFinderType.GetMethod("Enumerate", Array.Empty<Type>());
    
    var task = Task.Run(() => enumerateMethod.Invoke(null, null));
    if (task.Wait(TimeSpan.FromSeconds(30)))
    {
        var devices = task.Result;
        var devicesList = (System.Collections.IList)devices;
        Log($"Enumerate returned {devicesList.Count} devices!");
        
        foreach (var dev in devicesList)
        {
            var t = dev.GetType();
            Log($"  --- Device type: {t.FullName} ---");
            
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Log($"  PROP {p.Name}");
            }
            
            if (dev is System.Collections.IDictionary dict)
            {
                Log("  IDictionary keys/values:");
                foreach (System.Collections.DictionaryEntry de in dict)
                {
                    Log($"    [{de.Key}] = {de.Value}");
                }
            }
            else
            {
                Log("  Not an IDictionary. Trying indexer...");
                var indexer = t.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
                if (indexer != null && indexer.GetIndexParameters().Length > 0)
                {
                    foreach (var key in new[] { "SerialNumber", "ModelName", "FriendlyName", "DeviceType", "VendorName", "Name" })
                    {
                        try { Log($"    [{key}] = {indexer.GetValue(dev, new object[]{key})}"); }
                        catch (Exception ex) { Log($"    [{key}] ERR: {ex.Message}"); }
                    }
                }
            }
        }
    }
    else
    {
        Log("TIMEOUT after 30s - CameraFinder.Enumerate() blocked!");
    }
}
catch (Exception ex)
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.txt"), ex.ToString());
}