// Project model. One file on purpose: this is the contract between the
// editor now and the AI renderer later. A project is an ordered list of
// clips, each with trim points and a free-text prompt for the AI.
// SourcePath is absolute on Windows so a project works no matter where
// the videos came from. On the web build the browser gives no real
// path, so SourcePath is just the file name there.
using System.IO;
using System.Text.Json.Serialization;

namespace AiVideoEditor;

public sealed class TimelineClip
{
    // Absolute path to the source video file (file name only on web).
    public string SourcePath { get; set; } = "";
    // Trim in/out in seconds. Out <= In means "full clip" (no trim).
    public double InSeconds { get; set; }
    public double OutSeconds { get; set; }
    // Instruction for the AI edit of this clip. Free text.
    public string Prompt { get; set; } = "";
    // Tags for filtering only.
    public string Game { get; set; } = "";
    public List<string> Groups { get; set; } = new();

    // Full length once the preview has loaded it. Runtime only, never saved.
    [JsonIgnore]
    public double DurationSeconds { get; set; }

    [JsonIgnore]
    public string DisplayName => Path.GetFileName(SourcePath);

    [JsonIgnore]
    public string TrimLabel => OutSeconds > InSeconds
        ? $"{InSeconds:0.0}s–{OutSeconds:0.0}s"
        : "full";

    public TimelineClip Clone() => new()
    {
        SourcePath = SourcePath,
        InSeconds = InSeconds,
        OutSeconds = OutSeconds,
        Prompt = Prompt,
        Game = Game,
        Groups = new List<string>(Groups),
    };
}

public sealed class Project
{
    public string Name { get; set; } = "Untitled";
    public List<TimelineClip> Clips { get; set; } = new();

    // Total trimmed length, where known. Clips with Out <= In count as
    // unknown length here (duration is only known once preview loads).
    [JsonIgnore]
    public double KnownSeconds => Clips.Sum(c =>
        c.OutSeconds > c.InSeconds ? c.OutSeconds - c.InSeconds : 0);
}
