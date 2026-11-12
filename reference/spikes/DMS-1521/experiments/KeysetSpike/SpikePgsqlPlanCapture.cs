// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KeysetSpike;

public enum SpikePgsqlStatementKind
{
    /// <summary>SELECT, WITH, or INSERT: runs under EXPLAIN ANALYZE.</summary>
    Explained,

    /// <summary>CREATE, DROP, or ANALYZE: executes timed, and its duration is a statement row.</summary>
    Timed,
}

public sealed record SpikePgsqlStatement(
    int Number,
    SpikePgsqlStatementKind Kind,
    string Sql,
    string Leading
);

/// <summary>
/// Replays a captured PostgreSQL batch statement by statement, in one transaction, binding each
/// statement's referenced parameters from the captured specifications. Unlike the harness
/// <c>PgsqlPlanCapture</c>, the splitter understands single-quoted literals (with <c>''</c>),
/// double-quoted identifiers, and <c>--</c> and <c>/* */</c> comments, so the descriptor batch,
/// the <c>pg-analyze</c> variant, and the <c>inline</c> carrier can all be replayed. Dollar
/// quoting and anything unclassifiable are refused rather than missplit.
/// </summary>
public static class SpikePgsqlPlanCapture
{
    private const int CommandTimeoutSeconds = 600;

    public const string ExplainPrefix = "EXPLAIN (ANALYZE, BUFFERS, WAL, SETTINGS, FORMAT JSON)\n";

    public static IReadOnlyList<SpikePgsqlStatement> Split(string batch)
    {
        List<SpikePgsqlStatement> statements = [];
        StringBuilder current = new();
        int index = 0;

        void Flush()
        {
            string sql = current.ToString().Trim();
            current.Clear();
            string leading = LeadingKeyword(sql);
            if (leading.Length == 0)
            {
                // Only whitespace and comments: nothing to execute.
                return;
            }

            statements.Add(
                new SpikePgsqlStatement(statements.Count + 1, Classify(leading, sql), sql, leading)
            );
        }

        while (index < batch.Length)
        {
            char character = batch[index];
            switch (character)
            {
                case '\'':
                    index = CopyQuoted(batch, index, '\'', current);
                    break;
                case '"':
                    index = CopyQuoted(batch, index, '"', current);
                    break;
                case '-' when Peek(batch, index + 1) == '-':
                {
                    int end = batch.IndexOf('\n', index);
                    end = end < 0 ? batch.Length : end;
                    current.Append(batch, index, end - index);
                    index = end;
                    break;
                }
                case '/' when Peek(batch, index + 1) == '*':
                {
                    int end = batch.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Unsupported("an unterminated block comment");
                    }

                    current.Append(batch, index, end + 2 - index);
                    index = end + 2;
                    break;
                }
                case '$' when Peek(batch, index + 1) is char next && (next == '$' || char.IsLetter(next)):
                    throw Unsupported("dollar quoting");
                case ';':
                    Flush();
                    index++;
                    break;
                default:
                    current.Append(character);
                    index++;
                    break;
            }
        }

        Flush();
        return statements;
    }

    public static async Task<JsonObject> CaptureAsync(
        DbConnection connection,
        string batch,
        IReadOnlyList<SpikeParameterSpec> parameters
    )
    {
        IReadOnlyList<SpikePgsqlStatement> statements = Split(batch);
        JsonArray rows = [];
        double explainedExecutionMs = 0;
        double explainedPlanningMs = 0;
        double timedMs = 0;
        long hit = 0;
        long read = 0;
        long walBytes = 0;

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            foreach (SpikePgsqlStatement statement in statements)
            {
                JsonObject row = new()
                {
                    ["statement"] = statement.Number,
                    ["kind"] = statement.Kind.ToString().ToLowerInvariant(),
                    ["leading"] = statement.Leading,
                    ["sql"] = statement.Sql,
                };
                if (statement.Kind == SpikePgsqlStatementKind.Timed)
                {
                    await using DbCommand command = CreateCommand(
                        connection,
                        transaction,
                        statement.Sql,
                        parameters
                    );
                    long start = Stopwatch.GetTimestamp();
                    await command.ExecuteNonQueryAsync();
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    timedMs += elapsed;
                    row["elapsedMs"] = elapsed;
                }
                else
                {
                    await using DbCommand command = CreateCommand(
                        connection,
                        transaction,
                        ExplainPrefix + statement.Sql,
                        parameters
                    );
                    string json =
                        await command.ExecuteScalarAsync() as string
                        ?? throw new InvalidOperationException(
                            $"EXPLAIN returned no JSON for statement {statement.Number}."
                        );
                    JsonNode explain = JsonNode.Parse(json)!;
                    JsonNode entry = explain[0]!;
                    JsonNode plan = entry["Plan"]!;
                    double execution = entry["Execution Time"]!.GetValue<double>();
                    double planning = entry["Planning Time"]!.GetValue<double>();
                    long statementHit = plan["Shared Hit Blocks"]!.GetValue<long>();
                    long statementRead = plan["Shared Read Blocks"]!.GetValue<long>();
                    long statementWal = plan["WAL Bytes"]?.GetValue<long>() ?? 0;
                    explainedExecutionMs += execution;
                    explainedPlanningMs += planning;
                    hit += statementHit;
                    read += statementRead;
                    walBytes += statementWal;
                    row["executionMs"] = execution;
                    row["planningMs"] = planning;
                    row["sharedHitBlocks"] = statementHit;
                    row["sharedReadBlocks"] = statementRead;
                    row["walBytes"] = statementWal;
                    row["topNode"] = plan["Node Type"]?.GetValue<string>();
                    row["planRows"] = plan["Plan Rows"]?.GetValue<double>();
                    row["actualRows"] = plan["Actual Rows"]?.GetValue<double>();
                    row["explain"] = explain;
                }

                rows.Add(row);
            }

            // The replay must leave nothing behind; the production batch's temp objects drop on commit.
            await transaction.RollbackAsync();
        }

        return new JsonObject
        {
            ["replay"] =
                "statement by statement in one transaction (rolled back); SELECT/WITH/INSERT under "
                + ExplainPrefix.Trim()
                + "; CREATE/DROP/ANALYZE executed and timed; parameters bound from captured specifications",
            ["totals"] = new JsonObject
            {
                ["explainedExecutionMs"] = explainedExecutionMs,
                ["explainedPlanningMs"] = explainedPlanningMs,
                ["timedStatementMs"] = timedMs,
                ["sharedHitBlocks"] = hit,
                ["sharedReadBlocks"] = read,
                ["walBytes"] = walBytes,
            },
            ["statements"] = rows,
        };
    }

    /// <summary>Binds only the parameters a statement references, each from its full specification.</summary>
    public static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        IReadOnlyList<SpikeParameterSpec> parameters
    )
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach (SpikeParameterSpec parameter in parameters)
        {
            if (SpikeCommandCapture.ReferencesParameter(sql, parameter.BareName))
            {
                parameter.Bind(command);
            }
        }

        return command;
    }

    /// <summary>The first keyword after leading whitespace and comments, upper-cased; empty if none.</summary>
    public static string LeadingKeyword(string sql)
    {
        int index = 0;
        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                index++;
            }
            else if (sql[index] == '-' && Peek(sql, index + 1) == '-')
            {
                int end = sql.IndexOf('\n', index);
                index = end < 0 ? sql.Length : end + 1;
            }
            else if (sql[index] == '/' && Peek(sql, index + 1) == '*')
            {
                int end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        int wordEnd = index;
        while (wordEnd < sql.Length && char.IsAsciiLetter(sql[wordEnd]))
        {
            wordEnd++;
        }

        return sql[index..wordEnd].ToUpperInvariant();
    }

    private static SpikePgsqlStatementKind Classify(string leading, string sql) =>
        leading switch
        {
            "SELECT" or "WITH" or "INSERT" => SpikePgsqlStatementKind.Explained,
            "ANALYZE" => SpikePgsqlStatementKind.Timed,
            "DROP" when Regex.IsMatch(sql, @"\bDROP\s+TABLE\b", RegexOptions.IgnoreCase) =>
                SpikePgsqlStatementKind.Timed,
            "CREATE"
                when Regex.IsMatch(sql, @"\bCREATE\s+TEMP(ORARY)?\s+TABLE\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(sql, @"\bSELECT\b", RegexOptions.IgnoreCase) =>
                SpikePgsqlStatementKind.Timed,
            _ => throw Unsupported($"a statement starting with '{leading}'"),
        };

    private static int CopyQuoted(string batch, int start, char quote, StringBuilder current)
    {
        int index = start + 1;
        while (index < batch.Length)
        {
            if (batch[index] == quote)
            {
                if (Peek(batch, index + 1) == quote)
                {
                    index += 2;
                    continue;
                }

                current.Append(batch, start, index + 1 - start);
                return index + 1;
            }

            index++;
        }

        throw Unsupported(
            quote == '\'' ? "an unterminated string literal" : "an unterminated quoted identifier"
        );
    }

    private static char? Peek(string text, int index) => index < text.Length ? text[index] : null;

    private static InvalidOperationException Unsupported(string construct) =>
        new($"The batch contains {construct}, which the spike splitter refuses rather than missplit.");
}
