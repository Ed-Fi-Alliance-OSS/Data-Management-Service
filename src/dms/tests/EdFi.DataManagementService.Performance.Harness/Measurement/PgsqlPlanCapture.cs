// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// How one hydration-batch statement is replayed: setup statements (temp-table DDL) execute
/// as-is so later statements can run, and explained statements (the DML and SELECTs that do
/// the batch's work) run under EXPLAIN and contribute to the metrics.
/// </summary>
public enum PgsqlStatementKind
{
    Setup,
    Explained,
}

/// <summary>
/// One statement split out of the hydration batch: its 1-based position, its replay
/// classification, and its SQL text.
/// </summary>
public sealed record PgsqlBatchStatement(int StatementNumber, PgsqlStatementKind Kind, string Sql);

/// <summary>
/// One explained statement together with the raw EXPLAIN JSON PostgreSQL returned for it.
/// </summary>
public sealed record PgsqlExplainedStatement(PgsqlBatchStatement Statement, string ExplainJson);

/// <summary>
/// The captured PostgreSQL plan evidence for one cell: the plan artifact retaining every
/// statement of the replayed hydration batch (with the raw EXPLAIN JSON of each explained
/// statement), and the batch-aggregated metrics for the results row.
/// </summary>
public sealed record PgsqlPlanCaptureResult(string PlanArtifactJson, Results.PerfDatabaseMetrics Metrics);

/// <summary>
/// Replays the full recorded hydration batch — the one DbCommand the measured request
/// executed — on an out-of-band connection to the same warm database, with the same bound
/// parameter values. The batch is multi-statement, so it is split and classified: temp-table
/// setup statements execute as-is, and each DML/SELECT statement runs under
/// EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON). The replay shares one explicit transaction because
/// the production batch executes under one implicit transaction (a single protocol sync), and
/// the ON COMMIT DROP temp table would otherwise vanish after each statement's auto-commit.
/// Root-node shared buffer counters are cumulative over children, so summing them across the
/// explained statements yields the batch totals.
/// </summary>
public static class PgsqlPlanCapture
{
    private const int CommandTimeoutSeconds = 600;

    public static string ExplainSql(string statementSql) =>
        "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\n" + statementSql;

    /// <summary>
    /// Splits the generated hydration batch into classified statements. The splitter handles
    /// exactly the constructs the batch generator emits — double-quoted identifiers and
    /// semicolon terminators — and refuses anything it cannot split or classify safely
    /// (string literals, dollar quoting, comments, unexpected leading keywords), because a
    /// missplit batch would silently produce wrong plan evidence.
    /// </summary>
    public static IReadOnlyList<PgsqlBatchStatement> SplitHydrationBatch(string hydrationBatchSql)
    {
        List<PgsqlBatchStatement> statements = [];
        StringBuilder current = new();
        bool inQuotedIdentifier = false;

        void FlushStatement()
        {
            string sql = current.ToString().Trim();
            current.Clear();
            if (sql.Length == 0)
            {
                return;
            }

            statements.Add(new PgsqlBatchStatement(statements.Count + 1, ClassifyStatement(sql), sql));
        }

        for (int index = 0; index < hydrationBatchSql.Length; index++)
        {
            char character = hydrationBatchSql[index];
            if (inQuotedIdentifier)
            {
                current.Append(character);
                if (character == '"')
                {
                    inQuotedIdentifier = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotedIdentifier = true;
                    current.Append(character);
                    break;
                case '\'':
                    throw Unsupported("a string literal");
                case '$':
                    throw Unsupported("dollar quoting");
                case '-' when Peek(hydrationBatchSql, index + 1) == '-':
                    throw Unsupported("a line comment");
                case '/' when Peek(hydrationBatchSql, index + 1) == '*':
                    throw Unsupported("a block comment");
                case ';':
                    FlushStatement();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (inQuotedIdentifier)
        {
            throw Unsupported("an unterminated quoted identifier");
        }

        FlushStatement();
        if (!statements.Exists(statement => statement.Kind == PgsqlStatementKind.Explained))
        {
            throw new PerfObservationException(
                "The hydration batch contains no explainable DML/SELECT statement."
            );
        }

        return statements;
    }

    public static async Task<PgsqlPlanCaptureResult> CaptureAsync(
        DbConnection connection,
        string hydrationBatchSql,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        IReadOnlyList<PgsqlBatchStatement> statements = SplitHydrationBatch(hydrationBatchSql);
        List<PgsqlExplainedStatement> explained = [];

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            foreach (PgsqlBatchStatement statement in statements)
            {
                if (statement.Kind == PgsqlStatementKind.Setup)
                {
                    await using DbCommand setup = CreateStatementCommand(
                        connection,
                        transaction,
                        statement.Sql,
                        parameterValues
                    );
                    await setup.ExecuteNonQueryAsync();
                    continue;
                }

                await using DbCommand command = CreateStatementCommand(
                    connection,
                    transaction,
                    ExplainSql(statement.Sql),
                    parameterValues
                );
                object? scalar = await command.ExecuteScalarAsync();
                string explainJson =
                    scalar as string
                    ?? throw new PerfObservationException(
                        $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) returned no JSON document for "
                            + $"hydration batch statement {statement.StatementNumber}."
                    );
                explained.Add(new PgsqlExplainedStatement(statement, explainJson));
            }

            await transaction.CommitAsync();
        }

        return AssembleResult(statements, explained);
    }

    /// <summary>
    /// Builds the cell's plan artifact and batch-aggregated metrics from the replayed
    /// statements. Every explained statement must carry EXPLAIN evidence — a batch replay
    /// that skipped a statement is not full-batch evidence.
    /// </summary>
    public static PgsqlPlanCaptureResult AssembleResult(
        IReadOnlyList<PgsqlBatchStatement> statements,
        IReadOnlyList<PgsqlExplainedStatement> explained
    )
    {
        int explainableCount = statements.Count(statement => statement.Kind == PgsqlStatementKind.Explained);
        if (explained.Count == 0 || explained.Count != explainableCount)
        {
            throw new PerfObservationException(
                $"Expected EXPLAIN evidence for all {explainableCount} explainable hydration batch "
                    + $"statements; got {explained.Count}."
            );
        }

        Dictionary<int, string> explainByStatementNumber = explained.ToDictionary(
            entry => entry.Statement.StatementNumber,
            entry => entry.ExplainJson
        );

        long buffersHit = 0;
        long buffersRead = 0;
        double executionMs = 0;
        JsonArray statementsJson = [];
        foreach (PgsqlBatchStatement statement in statements)
        {
            JsonObject entry = new()
            {
                ["statementNumber"] = statement.StatementNumber,
                ["kind"] = statement.Kind == PgsqlStatementKind.Setup ? "setup" : "explained",
                ["sql"] = statement.Sql,
            };
            if (explainByStatementNumber.TryGetValue(statement.StatementNumber, out string? explainJson))
            {
                Results.PerfDatabaseMetrics metrics = ParseMetrics(explainJson);
                buffersHit += metrics.BuffersHit!.Value;
                buffersRead += metrics.BuffersRead!.Value;
                executionMs += metrics.DbExecutionMs!.Value;
                entry["explain"] =
                    JsonNode.Parse(explainJson)
                    ?? throw new PerfObservationException("EXPLAIN JSON parsed to null.");
            }

            statementsJson.Add(entry);
        }

        JsonObject artifact = new()
        {
            ["replay"] =
                "full hydration batch on an out-of-band connection; setup statements execute "
                + "unexplained, DML/SELECT statements run under EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)",
            ["statements"] = statementsJson,
        };

        return new PgsqlPlanCaptureResult(
            artifact.ToJsonString(
                // LF-only like every other artifact, so runs diff identically across platforms.
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }
            ),
            new Results.PerfDatabaseMetrics(
                BuffersHit: buffersHit,
                BuffersRead: buffersRead,
                DbExecutionMs: executionMs,
                LogicalReads: null,
                PhysicalReads: null,
                DbCpuMs: null,
                DbElapsedMs: null
            )
        );
    }

    public static Results.PerfDatabaseMetrics ParseMetrics(string explainJson)
    {
        JsonNode root =
            JsonNode.Parse(explainJson) ?? throw new PerfObservationException("EXPLAIN JSON parsed to null.");

        if (root is not JsonArray array || array.Count == 0)
        {
            throw new PerfObservationException("EXPLAIN JSON must be a non-empty array.");
        }

        JsonNode entry = array[0] ?? throw new PerfObservationException("EXPLAIN JSON entry is missing.");
        JsonNode plan =
            entry["Plan"] ?? throw new PerfObservationException("EXPLAIN JSON entry carries no Plan node.");

        double executionMs = RequiredDouble(entry, "Execution Time");
        long buffersHit = RequiredLong(plan, "Shared Hit Blocks");
        long buffersRead = RequiredLong(plan, "Shared Read Blocks");

        return new Results.PerfDatabaseMetrics(
            BuffersHit: buffersHit,
            BuffersRead: buffersRead,
            DbExecutionMs: executionMs,
            LogicalReads: null,
            PhysicalReads: null,
            DbCpuMs: null,
            DbElapsedMs: null
        );
    }

    private static PgsqlStatementKind ClassifyStatement(string sql)
    {
        int wordEnd = 0;
        while (wordEnd < sql.Length && char.IsAsciiLetter(sql[wordEnd]))
        {
            wordEnd++;
        }

        string firstWord = sql[..wordEnd].ToUpperInvariant();
        return firstWord switch
        {
            "DROP" or "CREATE" => PgsqlStatementKind.Setup,
            "WITH" or "SELECT" or "INSERT" => PgsqlStatementKind.Explained,
            _ => throw Unsupported($"a statement starting with '{firstWord}'"),
        };
    }

    private static DbCommand CreateStatementCommand(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach ((string name, object? value) in parameterValues)
        {
            // Only the parameters a statement actually references are bound: the batch's
            // statements each use a subset of the request's captured values.
            if (!Regex.IsMatch(commandText, "@" + Regex.Escape(name) + "(?![A-Za-z0-9_])"))
            {
                continue;
            }

            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static double RequiredDouble(JsonNode node, string propertyName) =>
        node[propertyName]?.GetValue<double>()
        ?? throw new PerfObservationException($"EXPLAIN JSON carries no '{propertyName}' value.");

    private static long RequiredLong(JsonNode node, string propertyName) =>
        node[propertyName]?.GetValue<long>()
        ?? throw new PerfObservationException($"EXPLAIN JSON plan carries no '{propertyName}' value.");

    private static char? Peek(string text, int index) => index < text.Length ? text[index] : null;

    private static PerfObservationException Unsupported(string construct) =>
        new(
            $"The hydration batch contains {construct}, which the replay splitter does not "
                + "support; extend the splitter before trusting the plan evidence."
        );
}
