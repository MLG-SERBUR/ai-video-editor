// Project save/load. Plain JSON beside the exe, in projects/.
// The updater never touches projects/ (see Updater.cs), so a self-update
// can never wipe prompts.
using System.IO;
using System.Text.Json;

namespace AiVideoEditor;

public static class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ProjectsDir =>
        Path.Combine(AppContext.BaseDirectory, "projects");

    public static void Save(Project project, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(project, JsonOptions);
        // Atomic-ish: write temp, then move over. A crash mid-save must
        // not leave a half-written project file behind.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static Project Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Project>(json, JsonOptions)
            ?? throw new InvalidDataException($"Empty project file: {path}");
    }

    // ffmpeg concat-demuxer export for the deterministic part only
    // (trim + order). Writes <project>.concat.txt next to the project file
    // plus a <project>.ffmpeg.bat that runs it. -c copy cuts on keyframes;
    // frame-accurate cuts need a re-encode pass (later).
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
}
