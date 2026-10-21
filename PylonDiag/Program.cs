using System.IO;
using System.Reflection;

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "started.txt"), "STARTED");

try
{
    using var sw = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "diag_result.txt"), false);
    void Log(string msg) { Console.WriteLine(msg); sw.WriteLine(msg); sw.Flush(); }

    Log("=== PYLON CAMERA EMULATION DIAGNOSTIC ===");
    Log($"PYLON_CAMEMU (Process) = {Environment.GetEnvironmentVariable("PYLON_CAMEMU", EnvironmentVariableTarget.Process) ?? "<null>"}");
    Log($"PYLON_CAMEMU (User)    = {Environment.GetEnvironmentVariable("PYLON_CAMEMU", EnvironmentVariableTarget.User) ?? "<null>"}");
    Log("");

    string asmPath = @"C:\Program Files\Basler\PylonRuntime\Assemblies\net8.0\x64\Basler.Pylon.dll";
    var asm = Assembly.LoadFrom(asmPath);
    Log($"Loaded Basler.Pylon from: {asmPath}");

    Log("Calling CameraFinder.Enumerate() [with 30s timeout]...");
    var camFinderType = asm.GetType("Basler.Pylon.CameraFinder");
    var enumerateMethod = camFinderType!.GetMethod("Enumerate", Type.EmptyTypes)!;

    var task = Task.Run(() => enumerateMethod.Invoke(null, null));
    if (task.Wait(TimeSpan.FromSeconds(30)))
    {
        var devices = task.Result;
        var devicesList = (System.Collections.IList)devices!;
        Log($"Enumerate returned {devicesList.Count} device(s)!");
        Log("");

        foreach (var dev in devicesList)
        {
            var t = dev!.GetType();
            Log($"  --- Device: {t.FullName} ---");

            var props = new[] { "FriendlyName", "SerialNumber", "ModelName", "VendorName", "FullName" };
            foreach (var name in props)
            {
                var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    try { Log($"    {name} = {prop.GetValue(dev)}"); }
                    catch (Exception ex) { Log($"    {name} ERR: {ex.Message}"); }
                }
            }
            Log("");
        }
    }
    else
    {
        Log("TIMEOUT after 30s — CameraFinder.Enumerate() blocked!");
    }
}
catch (Exception ex)
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.txt"), ex.ToString());
    Console.Error.WriteLine(ex);
}