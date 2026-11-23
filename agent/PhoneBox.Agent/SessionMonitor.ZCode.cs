using Microsoft.Data.Sqlite;

namespace PhoneBox.Agent;

/// <summary>
/// ZCode (zcode CLI / Z Code) keeps its own session history in a SQLite database under ~/.zcode/cli/db.
/// Read-only, like the rest of this monitor: nothing is written back, and the database is only opened for one
/// short query per refresh. Its sessions show up on the phone's Sessions widget as the "zcode" tool.
/// </summary>
public sealed partial class SessionMonitor
{
    private static string ZCodeDbPath =>
        Environment.GetEnvironmentVariable("ZCODE_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "cli", "db", "db.sqlite")
            : Path.Combine(Home, ".zcode", "cli", "db", "db.sqlite");

    /// <summary>The most recently used ZCode sessions. Empty when ZCode is not installed or has no history.</summary>
    private IEnumerable<AgentSession> ReadZCode()
    {
        var path = ZCodeDbPath;
        if (!File.Exists(path)) return [];

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = now - (long)TimeSpan.FromHours(12).TotalMilliseconds;
        var list = new List<AgentSession>();
        // Mode=ReadOnly: the ZCode app may be writing while we read; SQLite's WAL lets a reader see a consistent
        // snapshot without locking the writer, and we never touch the file itself.
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            select id, title, directory, time_created, time_updated, last_outcome
            from session
            where time_deleted is null and time_archived is null and time_updated >= $cutoff
            order by time_updated desc
            limit 20
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var id = rows.GetString(0);
            var title = rows.IsDBNull(1) ? "" : rows.GetString(1);
            var cwd = rows.IsDBNull(2) ? "" : rows.GetString(2);
            var created = rows.GetInt64(3);
            var updated = rows.GetInt64(4);
            var outcome = rows.IsDBNull(5) ? null : rows.GetString(5);
            var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (project.Length == 0) project = cwd;
            // ZCode has no hook that tells us a turn is running, so "busy" is recent activity, as for Codex.
            var status = now - updated < 90_000 ? "busy" : "idle";
            list.Add(new AgentSession(
                Id: "zcode:" + id,
                Tool: "zcode",
                Name: id.StartsWith("sess_") && id.Length > 13 ? id[^12..] : id,
                Project: project,
                Status: status,
                WaitingFor: null,
                Since: updated,
                StartedAt: created,
                Outcome: outcome is "completed" or "aborted" or "failed" ? outcome : null,
                Title: string.IsNullOrWhiteSpace(title) ? null : title));
        }
        return list;
    }
}
