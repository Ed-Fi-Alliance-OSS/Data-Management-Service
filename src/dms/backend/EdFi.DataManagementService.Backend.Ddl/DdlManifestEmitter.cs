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

        var sortedEntries = entries
            .OrderBy(e => ToManifestDialect(e.Dialect), StringComparer.Ordinal)
            .ToList();

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
        writer.WriteStartObject();
        writer.WriteString("dialect", ToManifestDialect(entry.Dialect));
        writer.WriteString("normalized_sql_sha256", ComputeSha256(entry.SqlText));
        writer.WriteNumber("statement_count", CountStatements(entry.Dialect, entry.SqlText));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Computes the SHA-256 hash of the SQL text (UTF-8, no BOM) as a lowercase hex string.
    /// </summary>
    internal static string ComputeSha256(string sqlText)
    {
        var bytes = Encoding.UTF8.GetBytes(sqlText);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
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
        return dialect switch
        {
            SqlDialect.Mssql => CountMssqlStatements(sqlText),
            SqlDialect.Pgsql => CountPgsqlStatements(sqlText),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    /// <summary>
    /// Counts MSSQL statements by splitting on standalone GO lines.
    /// Before the first GO: counts lines ending with semicolons (plain DDL/DML).
    /// After each GO: counts the batch as one statement (trigger definition).
    /// </summary>
    private static int CountMssqlStatements(string sqlText)
    {
        var lines = sqlText.Split('\n');
        int count = 0;
        bool pastFirstGo = false;
        bool currentBatchHasContent = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed == "GO")
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
                if (trimmed.Trim().Length > 0)
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
    private static int CountPgsqlStatements(string sqlText)
    {
        var lines = sqlText.Split('\n');
        int count = 0;
        bool insideDollarQuote = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r').Trim();

            if (insideDollarQuote)
            {
                if (trimmed == "END $$;" || trimmed.StartsWith("$$"))
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

    private static string ToManifestDialect(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Mssql => "mssql",
            SqlDialect.Pgsql => "pgsql",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }
}
