using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Kakitome.Application.Search;
using Kakitome.Domain.Recordings;

namespace Kakitome.Storage.Persistence;

/// <summary>
/// FTS5 index with the trigram tokenizer, which finds Japanese substrings without word segmentation. Queries of 3+
/// characters use the index (MATCH); shorter ones (common 2-kanji words like 会議) fall back to a substring scan.
/// The table is created by the <c>AddSearchIndex</c> migration.
/// </summary>
public sealed class SqliteSearchIndex(IDbContextFactory<KakitomeDbContext> contextFactory, KakitomeDatabase database) : ISearchIndex
{
    public const string Table = "SearchSegments";

    public async Task ReplaceAsync(RecordingId id, IReadOnlyList<SearchDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await DeleteAsync(connection, tx, id, cancellationToken).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"INSERT INTO {Table}(recording_id, kind, segment_id, start_seconds, text) VALUES ($r, $k, $s, $t, $x)";
        var r = insert.Parameters.Add("$r", SqliteType.Text);
        var k = insert.Parameters.Add("$k", SqliteType.Integer);
        var s = insert.Parameters.Add("$s", SqliteType.Text);
        var t = insert.Parameters.Add("$t", SqliteType.Real);
        var x = insert.Parameters.Add("$x", SqliteType.Text);
        foreach (var doc in documents)
        {
            r.Value = id.ToString();
            k.Value = (int)doc.Kind;
            s.Value = (object?)doc.SegmentId ?? DBNull.Value;
            t.Value = (object?)doc.StartSeconds ?? DBNull.Value;
            x.Value = doc.Text;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await DeleteAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 100, CancellationToken cancellationToken = default)
    {
        var terms = (query ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .Take(8)
            .ToList();
        if (terms.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var useMatch = terms.All(t => t.Length >= 3);
        if (useMatch)
        {
            // Each term as a quoted phrase (no FTS syntax injection), all required.
            var match = string.Join(" AND ", terms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
            command.CommandText =
                $"SELECT recording_id, kind, segment_id, start_seconds, snippet({Table}, 4, '[', ']', '…', 24), bm25({Table}) " +
                $"FROM {Table} WHERE {Table} MATCH $q ORDER BY bm25({Table}) LIMIT $limit";
            command.Parameters.AddWithValue("$q", match);
        }
        else
        {
            var where = new StringBuilder();
            for (var i = 0; i < terms.Count; i++)
            {
                where.Append(i == 0 ? string.Empty : " AND ").Append(CultureInfo.InvariantCulture, $"instr(lower(text), lower($t{i})) > 0");
                command.Parameters.AddWithValue($"$t{i}", terms[i]);
            }

            command.CommandText = $"SELECT recording_id, kind, segment_id, start_seconds, text, 0 FROM {Table} WHERE {where} LIMIT $limit";
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = reader.GetString(4);
            hits.Add(new SearchHit(
                RecordingId.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                (SearchHitKind)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                useMatch ? text : Highlight(text, terms[0]),
                reader.GetDouble(5)));
        }

        return hits;
    }

    public async Task<int> CountIndexedRecordingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(DISTINCT recording_id) FROM {Table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {Table}";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(SqliteConnection connection, SqliteTransaction? tx, RecordingId id, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = $"DELETE FROM {Table} WHERE recording_id = $r";
        delete.Parameters.AddWithValue("$r", id.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Wraps the first match in [ ] and trims long text around it (substring-scan path).</summary>
    private static string Highlight(string text, string term)
    {
        var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return text.Length > 80 ? text[..80] + "…" : text;
        }

        var start = Math.Max(0, i - 30);
        var end = Math.Min(text.Length, i + term.Length + 30);
        return (start > 0 ? "…" : string.Empty) + text[start..i] + "[" + text.Substring(i, term.Length) + "]" + text[(i + term.Length)..end]
            + (end < text.Length ? "…" : string.Empty);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
