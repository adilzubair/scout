using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Scout.Core;

public sealed partial class ScoutStore
{
    public string DatabasePath { get; private set; } = "";
    public string IndexDirectory => DatabasePath + ".search";
    public long IndexGeneration => WithConnection(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT generation FROM index_state WHERE id=1"; return Convert.ToInt64(cmd.ExecuteScalar()); });
    public long IndexJournalFloor => WithConnection(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT generation FROM index_checkpoint WHERE id=1"; return Convert.ToInt64(cmd.ExecuteScalar()); });
    private static void Journal(SqliteConnection c, SqliteTransaction tx, IEnumerable<Candidate> changed, IEnumerable<string> removed)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE index_state SET generation=generation+1 WHERE id=1 RETURNING generation"; var generation = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText = "INSERT OR REPLACE INTO candidate_changes(generation,id,json) VALUES($generation,$id,$json)";
        cmd.Parameters.AddWithValue("$generation", generation); var id = cmd.Parameters.Add("$id", SqliteType.Text); var json = cmd.Parameters.Add("$json", SqliteType.Text);
        foreach (var key in removed.Distinct()) { id.Value = key; json.Value = DBNull.Value; cmd.ExecuteNonQuery(); }
        foreach (var item in changed.GroupBy(c => c.Id).Select(g => g.Last())) { id.Value = item.Id; json.Value = JsonSerializer.Serialize(item); cmd.ExecuteNonQuery(); }
    }
    public (Candidate[] Changed, string[] Removed) ReadIndexChanges(long since) => WithConnection(c =>
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,json FROM candidate_changes WHERE generation>$since ORDER BY generation,rowid"; cmd.Parameters.AddWithValue("$since", since);
        using var reader = cmd.ExecuteReader(); var latest = new Dictionary<string, Candidate?>();
        while (reader.Read()) latest[reader.GetString(0)] = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<Candidate>(reader.GetString(1));
        return (latest.Values.OfType<Candidate>().ToArray(), latest.Where(p => p.Value == null).Select(p => p.Key).ToArray());
    });
    public void PruneIndexJournal(long through) => WithConnection(c =>
    {
        using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM candidate_changes WHERE generation <= $generation"; cmd.Parameters.AddWithValue("$generation", through); cmd.ExecuteNonQuery();
        cmd.CommandText = "UPDATE index_checkpoint SET generation=max(generation,$generation) WHERE id=1"; cmd.ExecuteNonQuery(); tx.Commit(); return 0;
    });
    public IEnumerable<Candidate> StreamCandidates()
    {
        using var c = new SqliteConnection(connectionString); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT json FROM candidates ORDER BY rowid";
        using var reader = cmd.ExecuteReader(); while (reader.Read()) { Candidate? item = null; try { item = JsonSerializer.Deserialize<Candidate>(reader.GetString(0)); } catch (JsonException) { } if (item != null) yield return item; }
    }
}
