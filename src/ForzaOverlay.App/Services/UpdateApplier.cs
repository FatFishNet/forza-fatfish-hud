using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ForzaOverlay.App.Services;

public static class UpdateApplier
{
    /// <summary>检测程序目录是否可写；只读目录（如 Program Files）无法自动替换。</summary>
    public static bool CanWriteAppDirectory()
    {
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, ".write-test");
            File.WriteAllText(probe, "test");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string PrepareApplyScript(string zipPath, string extractDirectory)
    {
        string appDirectory = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Environment.ProcessPath ?? Path.Combine(appDirectory, "ForzaFatFishHUD.exe");
        string scriptPath = Path.Combine(UpdateService.GetTempDirectory(), "apply-update.ps1");

        string script = $$"""
            $ErrorActionPreference = 'Stop'
            Start-Sleep -Seconds 2
            $appDir = '{{appDirectory}}'
            $exe = '{{exePath}}'
            $zip = '{{zipPath}}'
            $tmp = '{{extractDirectory}}'
            if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
            Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
            Start-Sleep -Milliseconds 500
            Copy-Item -Path (Join-Path $tmp '*') -Destination $appDir -Recurse -Force
            Start-Process -FilePath $exe
            Start-Sleep -Seconds 1
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item $zip -Force -ErrorAction SilentlyContinue
            Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    public static void LaunchAndExit(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Application.Current.Shutdown();
    }
}
