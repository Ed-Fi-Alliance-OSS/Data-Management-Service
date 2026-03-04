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
    /// Normalizes SQL text by converting all line endings to LF.
    /// This ensures the hash and statement count match the on-disk .sql file
    /// (which is always written with LF via WriteFileWithUnixLineEndings).
    /// </summary>
    internal static string NormalizeSql(string sqlText)
    {
        ArgumentNullException.ThrowIfNull(sqlText);

        return sqlText.ReplaceLineEndings("\n");
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
    /// <para><b>MSSQL:</b> Splits by standalone <c>GO</c> lines. The first segment (plain DDL)
    /// counts lines ending with <c>;</c>. Each subsequent non-empty segment (trigger batch)
    /// counts as 1.</para>
    /// <para><b>PostgreSQL:</b> Tracks <c>$$</c> dollar-quote state using line-boundary heuristics
    /// (only <c>$$</c> at start or end of a line toggles state). Counts lines ending with <c>;</c>
    /// only outside <c>$$</c> blocks.</para>
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
    /// Counts MSSQL statements by scanning for standalone GO lines.
    /// Before the first GO: counts lines ending with semicolons (plain DDL/DML).
    /// After each GO: counts the batch as one statement (trigger definition).
    /// Uses span-based line enumeration to avoid per-line string allocations.
    /// </summary>
    private static int CountMssqlStatements(string sqlText)
    {
        int count = 0;
        bool pastFirstGo = false;
        bool currentBatchHasContent = false;

        foreach (var rawLine in sqlText.AsSpan().EnumerateLines())
        {
            var trimmed = rawLine.TrimEnd('\r').Trim();

            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (pastFirstGo && currentBatchHasContent)
                {
                    count++;
                }

                pastFirstGo = true;
                currentBatchHasContent = false;
                continue;
            }

            if (!pastFirstGo)
            {
                if (trimmed.Length > 0 && trimmed[^1] == ';')
                {
                    count++;
                }
            }
            else
            {
                if (!trimmed.IsEmpty)
                {
                    currentBatchHasContent = true;
                }
            }
        }

        if (pastFirstGo && currentBatchHasContent)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Counts PostgreSQL statements by tracking dollar-quote (<c>$$</c>) state.
    /// Only <c>$$</c> at line boundaries (start or end of trimmed line) toggles state,
    /// avoiding false matches like <c>'$$.schoolId='</c> inside string literals.
    /// </summary>
    /// <remarks>
    /// The heuristic supports exactly three dollar-quote exit patterns produced by the DDL emitters:
    /// <list type="bullet">
    ///   <item><c>END $$;</c> — PL/pgSQL function/trigger body end</item>
    ///   <item><c>$$ LANGUAGE plpgsql;</c> (or any <c>$$</c>-prefixed line) — alternative body close</item>
    ///   <item><c>$$</c> alone on a line — bare dollar-quote close (no trailing semicolon)</item>
    /// </list>
    /// If the emitter produces a new pattern, this method must be updated and re-validated.
    /// </remarks>
    private static int CountPgsqlStatements(string sqlText)
    {
        int count = 0;
        bool insideDollarQuote = false;

        foreach (var rawLine in sqlText.AsSpan().EnumerateLines())
        {
            // Trim all whitespace to match $$-delimiters on indented lines
            var trimmed = rawLine.TrimEnd('\r').Trim();

            if (insideDollarQuote)
            {
                if (trimmed.SequenceEqual("END $$;") || trimmed.StartsWith("$$"))
                {
                    insideDollarQuote = false;
                }
            }
            else if (trimmed.EndsWith("$$"))
            {
                insideDollarQuote = true;
            }

            if (!insideDollarQuote && trimmed.Length > 0 && trimmed[^1] == ';')
            {
                count++;
            }
        }

        return count;
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
