#if WINDOWS
// Custom in-place updater. Windows only: Android updates through the
// APK, the web build is redeployed from Actions, neither needs this.
// No Velopack, no LocalAppData, no installer. Portable folder: the exe
// updates itself where it sits.
//
// Pattern (same as KikitanTranslator.WinForms/Updater.cs): on startup,
// compare local version.txt (commit SHA, written by Actions) with the
// version.txt asset on the rolling `latest` release. On mismatch,
// download AiVideoEditor-portable.zip in the background and offer
// "Restart to update". Applying extracts the zip to temp, then a batch
// script copies it over after this process exits, excluding config.json
// and the projects/ folder, restarts the exe, and deletes staging.
//
// ponytail: this is the whole updater. No update framework, no service,
// no registry. A failed check is silent; the app keeps working.
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace AiVideoEditor;

public sealed class Updater
{
    private const string ReleaseBase =
        "https://github.com/MLG-SERBUR/ai-video-editor/releases/download/latest";
    private const string ZipName = "AiVideoEditor-portable.zip";
    private const string AppExe = "AiVideoEditor.exe";

    public event Action<string>? StatusChanged;
    public event Action? UpdateReady;

    private string? _downloadedZip;

    public async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AiVideoEditor-updater");

            var local = LocalVersion();
            var remote = (await http.GetStringAsync($"{ReleaseBase}/version.txt")).Trim();
            if (string.IsNullOrEmpty(remote) || remote == local)
                return; // up to date (or no version info); stay silent

            StatusChanged?.Invoke("Downloading update...");
            var zipPath = Path.Combine(Path.GetTempPath(), ZipName);
            using (var resp = await http.GetAsync(
                $"{ReleaseBase}/{ZipName}", HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs);
            }

            _downloadedZip = zipPath;
            UpdateReady?.Invoke();
            StatusChanged?.Invoke("Update downloaded — restart to apply");
        }
        catch
        {
            // Offline, no release yet, GitHub down: stay silent, keep working.
        }
    }

    public void ApplyAndRestart()
    {
        if (_downloadedZip == null || !File.Exists(_downloadedZip)) return;

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var stageDir = Path.Combine(Path.GetTempPath(), "AiVideoEditor-update");
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        ZipFile.ExtractToDirectory(_downloadedZip, stageDir);

        var bat = Path.Combine(Path.GetTempPath(), "AiVideoEditor-apply-update.bat");
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            // /XF config.json: keep local settings. /XD projects: prompts
            // and project files live there and must survive an update.
            $"robocopy \"{stageDir}\" \"{appDir}\" /E /XF config.json /XD projects >nul\r\n" +
            $"start \"\" \"{Path.Combine(appDir, AppExe)}\"\r\n" +
            $"rmdir /s /q \"{stageDir}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true, CreateNoWindow = true });
        Environment.Exit(0);
    }

    private static string LocalVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "version.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
    }
}
#endif
