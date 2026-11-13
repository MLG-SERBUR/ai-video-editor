// Triage import. Reads medal-sorter's clip_data.sqlite READ-ONLY and
// pulls only triage state out of it. Never writes to that database.
// Maps: collection membership -> which clips to import (v1: every kept,
// non-discarded clip; pass a collection name to narrow it), edit_idea ->
// Prompt, note -> Note, friends/game -> tags. Linked edits win over their
// raw source, same as triage itself.
//
// Defensive by design: a medal-sorter DB without any triage_* tables yet
// (never triaged) imports as "everything, unfiltered" instead of crashing.
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AiVideoEditor;

public static class TriageImporter
{
    public sealed record ImportedClip(TimelineClip Clip, string Key, long MtimeNs);

    public static List<ImportedClip> Import(
        string sqlitePath, string medalRoot, string? collectionName = null)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();

        HashSet<string>? wantedKeys = null;
        if (!string.IsNullOrWhiteSpace(collectionName)
            && TableExists(conn, "triage_clip_collections")
            && TableExists(conn, "triage_collections"))
        {
            wantedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT clip_key FROM triage_clip_collections
                JOIN triage_collections ON triage_collections.id = triage_clip_collections.collection_id
                WHERE triage_collections.name = $name
                """;
            cmd.Parameters.AddWithValue("$name", collectionName);
            using var r = cmd.ExecuteReader();
            while (r.Read()) wantedKeys.Add(r.GetString(0));
        }

        var flags = TableExists(conn, "triage_flags") ? ReadFlags(conn) : new();
        var notes = TableExists(conn, "triage_notes") ? ReadNotes(conn) : new();

        var imported = new List<ImportedClip>();
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT clip_path, data FROM records";
        using var reader = cmd2.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (wantedKeys is not null && !wantedKeys.Contains(key)) continue;
            if (flags.TryGetValue(key, out var f) && (f.Discard || !f.Keep))
                continue; // v1: import kept clips only; discard is never imported

            JsonDocument rec;
            try { rec = JsonDocument.Parse(reader.GetString(1)); }
            catch (JsonException) { continue; }
            using (rec)
            {
                var root = rec.RootElement;
                if (!root.TryGetProperty("kind", out var kindEl)) continue;
                var kind = kindEl.GetString();
                if (kind != "source" && kind != "edit") continue;
                // Edit preferred: a source with linked edits is skipped; the
                // edit record itself is imported on its own row.
                if (kind == "source" && HasEdits(root)) continue;

                var rel = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                if (rel.Length == 0) continue;
                var abs = Path.GetFullPath(Path.Combine(medalRoot, rel));
                if (!File.Exists(abs)) continue;

                var mtime = root.TryGetProperty("file", out var fileEl) &&
                    fileEl.TryGetProperty("mtime_ns", out var m) ? m.GetInt64() : 0;

                var clip = new TimelineClip
                {
                    SourcePath = abs,
                    Game = GameOf(rel),
                    Groups = GroupsOf(root),
                    Prompt = notes.TryGetValue(key, out var n) ? n.EditIdea : "",
                    Note = notes.TryGetValue(key, out var n2) ? n2.Note : "",
                };
                imported.Add(new ImportedClip(clip, key, mtime));
            }
        }

        // Chronological, like triage.
        imported.Sort((a, b) =>
        {
            var cmp = a.MtimeNs.CompareTo(b.MtimeNs);
            return cmp != 0 ? cmp : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        });
        return imported;
    }

    private static bool HasEdits(JsonElement rec)
    {
        if (!rec.TryGetProperty("links", out var links)) return false;
        if (!links.TryGetProperty("edits", out var edits)) return false;
        return edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > 0;
    }

    private static string GameOf(string rel)
    {
        var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[1] : "(unknown)";
    }

    private static List<string> GroupsOf(JsonElement rec)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!rec.TryGetProperty("analysis", out var analysis)) return new List<string>();
        if (!analysis.TryGetProperty("usernames", out var usernames)) return new List<string>();
        if (!usernames.TryGetProperty("resolution", out var resolution)) return new List<string>();
        if (!resolution.TryGetProperty("usernames", out var names)) return new List<string>();
        foreach (var u in names.EnumerateArray())
        {
            if (u.TryGetProperty("group", out var g) && g.GetString() is { } s && s.Length > 0)
                groups.Add(s);
        }
        return groups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record Flag(bool Discard, bool Keep);
    private sealed record NoteRow(string Note, string EditIdea);

    private static Dictionary<string, Flag> ReadFlags(SqliteConnection conn)
    {
        var map = new Dictionary<string, Flag>(StringComparer.OrdinalIgnoreCase);
        var cols = ColumnsOf(conn, "triage_flags");
        var hasKeep = cols.Contains("keep");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasKeep
            ? "SELECT clip_key, discard, keep FROM triage_flags"
            : "SELECT clip_key, discard FROM triage_flags";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = new Flag(
                r.GetInt32(1) != 0,
                hasKeep && r.GetInt32(2) != 0);
        return map;
    }

    private static Dictionary<string, NoteRow> ReadNotes(SqliteConnection conn)
    {
        var map = new Dictionary<string, NoteRow>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT clip_key, note, edit_idea FROM triage_notes";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = new NoteRow(r.GetString(1), r.GetString(2));
        return map;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static HashSet<string> ColumnsOf(SqliteConnection conn, string table)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }
}
