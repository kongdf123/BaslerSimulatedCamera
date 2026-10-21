using System.IO;

namespace KW.CameraSimulatorTool.Services;

public static class PylonRuntimeFixer
{
    private static readonly string[] CriticalDlls = new[]
    {
        "PylonCamEmu_v12_TL.dll",
        "PylonC_v10.dll",
        "PylonUsb_v12_TL.dll",
        "PylonCLSer_v12_TL.dll",
        "PylonGtc_v12_TL.dll",
    };

    public record FixResult(
        bool NeedsFix,
        bool FixApplied,
        string RuntimePath,
        string? SourcePath,
        IReadOnlyList<string> MissingDlls,
        string Message);

    public static FixResult CheckAndFix(Action<string>? logger = null)
    {
        logger ??= _ => { };

        string runtimePath = FindPylonRuntimePath();
        string? sourcePath = FindFullPylonRuntimePath();

        logger($"PylonRuntime path: {runtimePath}");
        logger($"Full Pylon path:  {sourcePath ?? "(not found)"}");

        var missing = new List<string>();
        foreach (var dll in CriticalDlls)
        {
            string fullPath = Path.Combine(runtimePath, dll);
            if (!File.Exists(fullPath))
                missing.Add(dll);
        }

        if (missing.Count == 0)
        {
            logger("All critical DLLs present — PylonRuntime already supports camera emulation.");
            return new FixResult(false, false, runtimePath, sourcePath, missing, "OK — camera emulation DLLs present");
        }

        logger($"Missing {missing.Count} DLL(s): {string.Join(", ", missing)}");

        if (sourcePath == null)
        {
            string msg = "Cannot fix: full Pylon installation not found. Install full Basler pylon to enable camera emulation.";
            logger(msg);
            return new FixResult(true, false, runtimePath, null, missing, msg);
        }

        var copied = new List<string>();
        var failed = new List<string>();

        foreach (var dll in missing)
        {
            string src = Path.Combine(sourcePath, dll);
            string dst = Path.Combine(runtimePath, dll);

            if (!File.Exists(src))
            {
                logger($"  SKIP {dll} — not in source");
                failed.Add(dll);
                continue;
            }

            try
            {
                File.Copy(src, dst, overwrite: true);
                copied.Add(dll);
                logger($"  COPIED {dll}");
            }
            catch (Exception ex)
            {
                failed.Add(dll);
                logger($"  FAIL {dll} — {ex.Message}");
            }
        }

        string status;
        if (failed.Count == 0)
            status = $"Fixed — copied {copied.Count} DLL(s) to PylonRuntime";
        else if (copied.Count > 0)
            status = $"Partial — copied {copied.Count}, failed {failed.Count}";
        else
            status = "Failed — could not copy any DLLs";

        logger(status);
        return new FixResult(true, copied.Count > 0, runtimePath, sourcePath, missing, status);
    }

    public static string FindPylonRuntimePath()
    {
        string? env = Environment.GetEnvironmentVariable("PYLON_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return Path.Combine(env, "Runtime", "x64");

        string defaultPath = @"C:\Program Files\Basler\PylonRuntime\Runtime\x64";
        if (Directory.Exists(defaultPath))
            return defaultPath;

        string altRuntime = @"C:\Program Files\Basler\pylon\Runtime\x64";
        if (Directory.Exists(altRuntime))
            return altRuntime;

        return defaultPath;
    }

    public static string? FindFullPylonRuntimePath()
    {
        string[] candidates = new[]
        {
            @"D:\Program Files\Basler\pylon\Runtime\x64",
            @"C:\Program Files\Basler\pylon\Runtime\x64",
            @"D:\Program Files\Basler\PylonRuntime\Runtime\x64",
            @"C:\Program Files\Basler\PylonRuntime\Runtime\x64",
        };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            string camemu = Path.Combine(dir, "PylonCamEmu_v12_TL.dll");
            if (File.Exists(camemu))
                return dir;
        }

        string? fromPath = SearchPATHFor("PylonCamEmu_v12_TL.dll");
        if (fromPath != null)
            return fromPath;

        return null;
    }

    private static string? SearchPATHFor(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            string fullPath = Path.Combine(dir, fileName);
            if (File.Exists(fullPath))
                return dir;
        }
        return null;
    }
}