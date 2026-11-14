// Project save/load. Plain JSON, through the platform file pickers so
// the same code works on Windows, Android, and the web build (where a
// save is a browser download). The updater never touches projects/
// (see Updater.cs), so a self-update can never wipe prompts.
using System.Text.Json;
using Windows.Storage;

namespace AiVideoEditor;

public static class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToJson(Project project) =>
        JsonSerializer.Serialize(project, JsonOptions);

    public static Project FromJson(string json) =>
        JsonSerializer.Deserialize<Project>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty project file.");

    public static async Task SaveToFileAsync(Project project, StorageFile file)
    {
        var json = ToJson(project);
        await FileIO.WriteTextAsync(file, json);
    }

    public static async Task<Project> LoadFromFileAsync(StorageFile file)
    {
        var json = await FileIO.ReadTextAsync(file);
        return FromJson(json);
    }

#if WINDOWS
    // ffmpeg concat-demuxer export for the deterministic part only
    // (trim + order). Writes <project>.concat.txt next to the project file
    // plus a <project>.ffmpeg.bat that runs it. -c copy cuts on keyframes;
    // frame-accurate cuts need a re-encode pass (later).
    // Windows only: it needs real file paths and a local ffmpeg.
    public static string ExportFfmpeg(Project project, string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var baseName = Path.GetFileNameWithoutExtension(projectPath);
        var listPath = Path.Combine(dir, baseName + ".concat.txt");
        var batPath = Path.Combine(dir, baseName + ".ffmpeg.bat");
        var outPath = Path.Combine(dir, baseName + ".mp4");

        using var w = new StreamWriter(listPath);
        foreach (var clip in project.Clips)
        {
            w.WriteLine($"file '{clip.SourcePath.Replace("'", "'\\''")}'");
            if (clip.OutSeconds > clip.InSeconds)
            {
                w.WriteLine($"inpoint {clip.InSeconds:0.###}");
                w.WriteLine($"outpoint {clip.OutSeconds:0.###}");
            }
        }

        File.WriteAllText(batPath,
            "@echo off\r\n" +
            $"ffmpeg -f concat -safe 0 -i \"{listPath}\" -c copy \"{outPath}\"\r\n" +
            "pause\r\n");
        return batPath;
    }
#endif
}
