using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Models;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Thread-safe SQLite-backed vector store.
/// Stores text chunks and their embeddings as BLOBs. 
/// Computes cosine similarity via a full table scan, but keeps memory usage low by streaming.
/// </summary>
public sealed class VectorStore
{
    private readonly string _dbPath;
    private readonly object _lock = new();

    private readonly Channel<Action> _dbQueue = Channel.CreateUnbounded<Action>();

    public VectorStore(string dbPath)
    {
        // Change extension to .db
        _dbPath = Path.ChangeExtension(dbPath, ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitializeSchema();

        // Start the dedicated single-thread database writer
        Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var action in _dbQueue.Reader.ReadAllAsync())
        {
            try
            {
                // Process one DB operation at a time sequentially
                action();
            }
            catch
            {
                // Skip failed operations silently
            }
        }
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        
        return conn;
    }

    private void InitializeSchema()
    {
        lock (_lock)
        {
            using var conn = CreateConnection();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Chunks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    ChunkText TEXT NOT NULL,
                    LastModified TEXT NOT NULL,
                    Embedding BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IDX_Chunks_FilePath ON Chunks(FilePath);
            """;
            cmd.ExecuteNonQuery();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Chunks";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    /// <summary>Returns the set of all distinct file paths currently in the index.</summary>
    public IReadOnlySet<string> GetIndexedPaths()
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT FilePath FROM Chunks";
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                set.Add(reader.GetString(0));
            return set;
        }
    }

    /// <summary>Returns a map of file path → most-recent LastModified stored in the index.</summary>
    public Dictionary<string, DateTime> GetIndexedModificationTimes()
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FilePath, MAX(LastModified) FROM Chunks GROUP BY FilePath";
            var dict = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                if (DateTime.TryParse(reader.GetString(1), out var dt))
                    dict[path] = dt.ToUniversalTime();
            }
            return dict;
        }
    }

    /// <summary>
    /// Replaces all chunks for a given file atomically.
    /// </summary>
    public void Upsert(string filePath, IEnumerable<VectorEntry> entries)
    {
        var entryList = entries.ToList(); // evaluate before queuing

        _dbQueue.Writer.TryWrite(() => 
        {
            lock (_lock)
            {
                using var conn = CreateConnection();
                using var transaction = conn.BeginTransaction();

                // 1. Delete existing
                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = transaction;
                    delCmd.CommandText = "DELETE FROM Chunks WHERE FilePath = @path COLLATE NOCASE";
                    delCmd.Parameters.AddWithValue("@path", filePath);
                    delCmd.ExecuteNonQuery();
                }

                // 2. Insert new
                if (entryList.Count > 0)
                {
                    using var insCmd = conn.CreateCommand();
                    insCmd.Transaction = transaction;
                    insCmd.CommandText = """
                        INSERT INTO Chunks (FilePath, ChunkIndex, ChunkText, LastModified, Embedding)
                        VALUES (@path, @idx, @text, @mod, @emb)
                    """;

                    var pPath = insCmd.Parameters.Add("@path", SqliteType.Text);
                    var pIdx  = insCmd.Parameters.Add("@idx", SqliteType.Integer);
                    var pText = insCmd.Parameters.Add("@text", SqliteType.Text);
                    var pMod  = insCmd.Parameters.Add("@mod", SqliteType.Text);
                    var pEmb  = insCmd.Parameters.Add("@emb", SqliteType.Blob);

                    foreach (var entry in entryList)
                    {
                        pPath.Value = entry.FilePath;
                        pIdx.Value  = entry.ChunkIndex;
                        pText.Value = entry.ChunkText;
                        pMod.Value  = entry.LastModified.ToString("o");
                        pEmb.Value  = MemoryMarshal.AsBytes(entry.Embedding.AsSpan()).ToArray();
                        insCmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        });
    }

    /// <summary>
    /// Deletes all chunks for a given file.
    /// </summary>
    public void Remove(string filePath)
    {
        _dbQueue.Writer.TryWrite(() => 
        {
            lock (_lock)
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Chunks WHERE FilePath = @path COLLATE NOCASE";
                cmd.Parameters.AddWithValue("@path", filePath);
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Deletes all chunks for files under a given directory prefix.
    /// </summary>
    public void RemoveDirectory(string directoryPath)
    {
        string prefix = directoryPath.EndsWith("\\") || directoryPath.EndsWith("/") 
            ? directoryPath 
            : directoryPath + Path.DirectorySeparatorChar;

        _dbQueue.Writer.TryWrite(() => 
        {
            lock (_lock)
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Chunks WHERE FilePath LIKE @prefix";
                // Use LIKE with % wildcard to match any file starting with this directory path
                cmd.Parameters.AddWithValue("@prefix", prefix + "%");
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Synchronously empties the database and reclaims space.
    /// Used by the CLI tool to clear the index even when the file is locked by PowerToys.
    /// </summary>
    public void ClearSync()
    {
        lock (_lock)
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Chunks; VACUUM;";
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SearchResult> Search(float[] queryEmbedding, int topK, float minScore)
    {
        var results = new List<SearchResult>();

        lock (_lock)
        {
            using var conn = CreateConnection();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FilePath, ChunkText, ChunkIndex, Embedding FROM Chunks";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string filePath = reader.GetString(0);
                string text     = reader.GetString(1);
                int chunkIdx    = reader.GetInt32(2);
                byte[] embBytes = (byte[])reader[3];

                float[] embedding = MemoryMarshal.Cast<byte, float>(embBytes.AsSpan()).ToArray();
                float score       = CosineSimilarity(queryEmbedding, embedding);

                if (score >= minScore)
                {
                    results.Add(new SearchResult
                    {
                        FilePath     = filePath,
                        ChunkIndex   = chunkIdx,
                        MatchSnippet = Truncate(text, 120).Replace("\n", " ") + "...",
                        Score        = score
                    });
                }
            }
        }

        // Group by file, taking the best chunk per file, and sort by score
        return results
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : dot / denom;
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen].TrimEnd() + "…";
}
