// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Describes a single dialect entry for DDL manifest emission.
/// </summary>
/// <param name="Dialect">The SQL dialect.</param>
/// <param name="SqlText">The combined, canonicalized SQL text for this dialect.</param>
public sealed record DdlManifestEntry(SqlDialect Dialect, string SqlText);

/// <summary>
/// Emits a deterministic <c>ddl.manifest.json</c> summarizing the emitted DDL per dialect.
/// The manifest includes a SHA-256 hash of the normalized SQL and a statement count,
/// enabling fast diffs and diagnostics without comparing full SQL files.
/// </summary>
public static class DdlManifestEmitter
{
    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, NewLine = "\n" };

    /// <summary>
    /// Emits the DDL manifest JSON string.
    /// </summary>
    /// <param name="effectiveSchema">The effective schema info (provides hash and mapping version).</param>
    /// <param name="entries">The per-dialect DDL entries. Order does not matter; output is sorted by dialect name.</param>
    /// <returns>A UTF-8 JSON string with <c>\n</c> line endings and a trailing newline.</returns>
    public static string Emit(EffectiveSchemaInfo effectiveSchema, IReadOnlyList<DdlManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(effectiveSchema);
        ArgumentNullException.ThrowIfNull(entries);

        var sortedEntries = entries.OrderBy(e => DialectLabel(e.Dialect), StringComparer.Ordinal).ToList();

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();

            writer.WriteString("effective_schema_hash", effectiveSchema.EffectiveSchemaHash);
            writer.WriteString("relational_mapping_version", effectiveSchema.RelationalMappingVersion);

            writer.WritePropertyName("ddl");
            writer.WriteStartArray();
            foreach (var entry in sortedEntries)
            {
                WriteDdlEntry(writer, entry);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteDdlEntry(Utf8JsonWriter writer, DdlManifestEntry entry)
    {
        var normalized = NormalizeSql(entry.SqlText);

        writer.WriteStartObject();
        writer.WriteString("dialect", DialectLabel(entry.Dialect));
        writer.WriteString("normalized_sql_sha256", ComputeSha256(normalized));
        writer.WriteNumber("statement_count", CountStatements(entry.Dialect, normalized));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Normalizes SQL text by converting all line endings to LF and trimming trailing
    /// whitespace from each line. This ensures the hash and statement count are stable
    /// across non-semantic whitespace differences and match the on-disk .sql file
    /// (which is always written with LF via WriteFileWithUnixLineEndings).
    /// </summary>
    internal static string NormalizeSql(string sqlText)
    {
        ArgumentNullException.ThrowIfNull(sqlText);

        var lines = sqlText.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Computes the SHA-256 hash of the SQL text (UTF-8, no BOM) as a lowercase hex string.
    /// Callers must pass already-normalized SQL (via <see cref="NormalizeSql"/>).
    /// Uses a rented buffer to reduce GC pressure for large SQL texts.
    /// </summary>
    internal static string ComputeSha256(string sqlText)
    {
        ArgumentNullException.ThrowIfNull(sqlText);

        // Rent a pooled buffer instead of allocating via Encoding.UTF8.GetBytes(string)
        // to avoid large byte[] allocations that pressure GC when hashing real-world
        // DDL texts (which can be hundreds of KB per dialect). The 32-byte hash output
        // fits on the stack via stackalloc.
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(sqlText.Length);
        var rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(sqlText, rented);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(rented.AsSpan(0, bytesWritten), hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Counts top-level SQL statements using dialect-aware rules.
    /// </summary>
    /// <remarks>
    /// <para><b>MSSQL:</b> Splits by standalone <c>GO</c> lines. Batches that start with
    /// <c>CREATE</c>/<c>ALTER FUNCTION</c> or <c>CREATE</c>/<c>ALTER TRIGGER</c> count as a
    /// single statement; all other batches count semicolon-terminated statements.</para>
    /// <para><b>PostgreSQL:</b> Tracks dollar-quote state using tag-aware matching for all tags
    /// matching <c>$[A-Za-z0-9_]*$</c> (e.g. <c>$$</c>, <c>$func$</c>, <c>$uuidv5$</c>).
    /// Only tags at line boundaries toggle state. Counts lines ending with <c>;</c>
    /// only outside dollar-quoted blocks.</para>
    /// </remarks>
    internal static int CountStatements(SqlDialect dialect, string sqlText)
    {
        ArgumentNullException.ThrowIfNull(sqlText);

        return dialect switch
        {
            SqlDialect.Mssql => CountMssqlStatements(sqlText),
            SqlDialect.Pgsql => CountPgsqlStatements(sqlText),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    /// <summary>
    /// Counts MSSQL statements by scanning standalone GO lines and classifying each batch.
    /// Function and trigger definition batches count as 1 because their inner semicolons
    /// are part of the definition body, while plain DDL/DML batches count semicolon-terminated
    /// statements line-by-line.
    /// </summary>
    private static int CountMssqlStatements(string sqlText)
    {
        int count = 0;
        List<string> batchLines = [];

        foreach (var rawLine in sqlText.AsSpan().EnumerateLines())
        {
            var line = rawLine.TrimEnd('\r').ToString();
            var trimmed = line.AsSpan().Trim();

            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                count += CountMssqlBatchStatements(batchLines);
                batchLines.Clear();
                continue;
            }

            batchLines.Add(line);
        }

        return count + CountMssqlBatchStatements(batchLines);
    }

    private static int CountMssqlBatchStatements(IReadOnlyList<string> batchLines)
    {
        var statementLines = batchLines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        if (statementLines.Length == 0)
        {
            return 0;
        }

        if (IsSingleStatementMssqlBatch(statementLines[0]))
        {
            return 1;
        }

        return statementLines.Count(static line => line.EndsWith(';'));
    }

    private static bool IsSingleStatementMssqlBatch(string firstStatementLine) =>
        firstStatementLine.StartsWith("CREATE OR ALTER FUNCTION", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("CREATE FUNCTION", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("ALTER FUNCTION", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("CREATE OR ALTER TRIGGER", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("ALTER TRIGGER", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("CREATE OR ALTER PROCEDURE", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("CREATE PROCEDURE", StringComparison.OrdinalIgnoreCase)
        || firstStatementLine.StartsWith("ALTER PROCEDURE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Counts PostgreSQL statements by tracking dollar-quote state with tag-aware matching.
    /// Supports all PostgreSQL dollar-quote tags matching <c>$[A-Za-z0-9_]*$</c> (e.g.
    /// <c>$$</c>, <c>$func$</c>, <c>$uuidv5$</c>). Only dollar-quote tags at line boundaries
    /// (end of trimmed line for entry, start of trimmed line or after <c>END </c> for exit)
    /// toggle state, avoiding false matches inside string literals.
    /// </summary>
    /// <remarks>
    /// The heuristic supports dollar-quote exit patterns produced by the DDL emitters:
    /// <list type="bullet">
    ///   <item><c>END $tag$;</c> — PL/pgSQL function/trigger body end</item>
    ///   <item><c>$tag$ LANGUAGE plpgsql;</c> (or any line starting with the active tag) — alternative body close</item>
    ///   <item><c>$tag$</c> alone on a line — bare dollar-quote close (no trailing semicolon)</item>
    /// </list>
    /// If the emitter produces a new exit pattern, this method must be updated and re-validated.
    /// </remarks>
    private static int CountPgsqlStatements(string sqlText)
    {
        int count = 0;
        string? activeDelimiter = null;

        foreach (var rawLine in sqlText.AsSpan().EnumerateLines())
        {
            var trimmed = rawLine.TrimEnd('\r').Trim();

            if (activeDelimiter != null)
            {
                // Inside a dollar-quoted block — look for exit.
                ReadOnlySpan<char> delim = activeDelimiter.AsSpan();
                if (
                    trimmed.StartsWith(delim)
                    || (
                        trimmed.StartsWith("END ")
                        && trimmed.Length >= 4 + delim.Length
                        && trimmed[4..].StartsWith(delim)
                    )
                )
                {
                    activeDelimiter = null;
                }
            }
            else
            {
                activeDelimiter = TryExtractTrailingDollarQuote(trimmed);
            }

            if (activeDelimiter == null && trimmed.Length > 0 && trimmed[^1] == ';')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Tries to extract a trailing dollar-quote tag (<c>$[A-Za-z0-9_]*$</c>) from the end of a
    /// trimmed line. Returns the tag string if found, <c>null</c> otherwise.
    /// </summary>
    private static string? TryExtractTrailingDollarQuote(ReadOnlySpan<char> trimmedLine)
    {
        if (trimmedLine.Length < 2 || trimmedLine[^1] != '$')
        {
            return null;
        }

        for (int i = trimmedLine.Length - 2; i >= 0; i--)
        {
            char c = trimmedLine[i];
            if (c == '$')
            {
                return trimmedLine[i..].ToString();
            }
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the lowercase label for the given dialect (e.g., <c>"pgsql"</c>, <c>"mssql"</c>).
    /// </summary>
    public static string DialectLabel(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => "pgsql",
            SqlDialect.Mssql => "mssql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
}
