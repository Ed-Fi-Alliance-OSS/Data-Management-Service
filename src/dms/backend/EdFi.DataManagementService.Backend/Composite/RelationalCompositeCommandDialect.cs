// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// The narrow set of concerns that genuinely differ per provider when composing one command.
/// Orchestration stays provider-neutral; parity means behavior, not identical SQL text.
/// </summary>
internal interface IRelationalCompositeCommandDialect
{
    SqlDialect Dialect { get; }

    /// <summary>
    /// Session-option prologue emitted once at the head of every composite command, or
    /// <see langword="null"/> when the provider needs none.
    /// </summary>
    string? CommandPrologue { get; }

    /// <summary>
    /// Epilogue emitted once after the final logical statement, restoring the caller's option state that
    /// <see cref="CommandPrologue"/> captured, or <see langword="null"/> when the provider needs none.
    /// </summary>
    string? CommandEpilogue { get; }

    /// <summary>
    /// Names the prologue and epilogue occupy, which the parameter allocator must never issue. Separate
    /// from the carrier's reserved names because the prologue exists on every command whereas the carrier
    /// declaration exists only on a command that captures a target.
    /// </summary>
    IReadOnlyList<string> ReservedCommandNames { get; }

    /// <summary>Emits the sentinel select that gives a data-modifying statement its result set.</summary>
    string EmitSentinel(int ordinal);

    /// <summary>The provider's captured-target carrier.</summary>
    IRelationalCompositeTargetCarrier Carrier { get; }

    public static IRelationalCompositeCommandDialect Create(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => PgsqlCompositeCommandDialect.Instance,
            SqlDialect.Mssql => MssqlCompositeCommandDialect.Instance,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
}

/// <summary>
/// Carries the target-or-missing decision made by the locking statement forward to later statements in
/// the same command.
/// </summary>
/// <remarks>
/// Under READ COMMITTED each statement takes its own snapshot on both providers, so one network command is
/// one round trip but not one same-state observation. A later statement that repeats the target predicate
/// is a fresh observation: a concurrent insert landing in between would let a request classified as a
/// create run stored-value authorization against a row it never locked, let hydration observe a target the
/// locking statement never saw, or let a missing-target delete authorize or mutate a newly appeared row.
/// Every statement after the capture must therefore consume the captured outcome.
/// </remarks>
internal interface IRelationalCompositeTargetCarrier
{
    /// <summary>Names the carrier occupies, which the parameter allocator must never issue.</summary>
    IReadOnlyList<string> ReservedNames { get; }

    /// <summary>
    /// Statement text emitted once at the head of a command that declares carrier storage, or
    /// <see langword="null"/> when the provider needs no declaration.
    /// </summary>
    string? DeclarationPrologue { get; }

    /// <summary>
    /// Emits the locking statement that observes the target and captures the outcome, returning the
    /// observed values as its result set. It must run unconditionally, capturing "no target" as well as a
    /// found target.
    /// </summary>
    /// <param name="targetPredicateSql">
    /// Predicate over the aliased <c>dms.Document</c> row, for example
    /// <c>d."DocumentUuid" = @documentUuid</c>.
    /// </param>
    string EmitCaptureTarget(string targetPredicateSql);

    /// <summary>Expression yielding the captured document id, or SQL NULL when no target was observed.</summary>
    string CapturedTargetIdExpression { get; }

    /// <summary>Predicate that is true only when the capture observed a target.</summary>
    string CapturedTargetPresentPredicate { get; }

    /// <summary>Expression yielding the captured content version, or SQL NULL.</summary>
    string CapturedContentVersionExpression { get; }
}

internal sealed class PgsqlCompositeCommandDialect : IRelationalCompositeCommandDialect
{
    public static readonly PgsqlCompositeCommandDialect Instance = new();

    private PgsqlCompositeCommandDialect() { }

    public SqlDialect Dialect => SqlDialect.Pgsql;

    /// <summary>
    /// PostgreSQL needs no prologue: an error always aborts the transaction, so a later statement in the
    /// command cannot execute after a failure.
    /// </summary>
    public string? CommandPrologue => null;

    /// <summary>With nothing established, there is nothing to restore.</summary>
    public string? CommandEpilogue => null;

    public IReadOnlyList<string> ReservedCommandNames => [];

    public string EmitSentinel(int ordinal) =>
        string.Create(CultureInfo.InvariantCulture, $"SELECT {ordinal} AS \"LogicalStatementOrdinal\";");

    public IRelationalCompositeTargetCarrier Carrier => PgsqlCompositeTargetCarrier.Instance;
}

internal sealed class MssqlCompositeCommandDialect : IRelationalCompositeCommandDialect
{
    public static readonly MssqlCompositeCommandDialect Instance = new();

    private const string PriorXactAbortVariable = "dms_composite_prior_xact_abort";
    private const string PriorNoCountVariable = "dms_composite_prior_nocount";

    private MssqlCompositeCommandDialect() { }

    public SqlDialect Dialect => SqlDialect.Mssql;

    /// <summary>
    /// <c>SET XACT_ABORT</c> is session state, not command state. With it off — the default — a constraint
    /// violation in a multi-statement batch aborts only the offending statement and execution continues, so
    /// co-batched DML could leave later statements running after a failure. The options are therefore
    /// established inside the command itself, which costs no extra round trip and leaves no path depending
    /// on a previous command having set them. Their prior values are captured first so
    /// <see cref="CommandEpilogue"/> can put them back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is established on every command, not only on commands holding several logical statements, because
    /// the logical statement count does not bound the emitted statement count: a data-modifying statement
    /// carries an appended sentinel, and the captured-target statement emits a declaration and two selects.
    /// Deterministic packing also makes a command holding one logical statement ordinary at a parameter
    /// budget boundary. Live measurement shows the continuation hazard is what the prologue defends against:
    /// without it a constraint violation in a one-logical-statement batch leaves the transaction
    /// committable. <c>SET NOCOUNT ON</c> travels with it because no write path reads an affected-row count
    /// — delete success is decided from returned rows — and not because the decoder needs it; SqlClient does
    /// not surface a data-modifying statement's row-count completion as a result set.
    /// </para>
    /// <para>
    /// The prior values are captured because an option's lifetime depends on how the client transports the
    /// command. A parameterized command travels through <c>sp_executesql</c>, whose procedure context
    /// restores SET options on exit, so the establishment dies with the command. A parameterless command
    /// travels as a plain batch with no such context, so it would otherwise leave both options set for the
    /// remainder of the session and let a later ordinary command's constraint violation doom the
    /// transaction. Capturing from <c>@@OPTIONS</c> — bit 16384 for <c>XACT_ABORT</c>, 512 for
    /// <c>NOCOUNT</c> — lets <see cref="CommandEpilogue"/> restore exactly what the caller had, including an
    /// ambient ON that must survive.
    /// </para>
    /// </remarks>
    public string? CommandPrologue =>
        $"""
            DECLARE @{PriorXactAbortVariable} BIT = IIF((@@OPTIONS & 16384) = 0, 0, 1);
            DECLARE @{PriorNoCountVariable} BIT = IIF((@@OPTIONS & 512) = 0, 0, 1);
            SET XACT_ABORT ON;
            SET NOCOUNT ON;
            """;

    /// <summary>
    /// Restores each option to its captured prior value after the final logical statement. It deliberately
    /// does not run after an abort: SQL Server skips the rest of the batch, and there the request is already
    /// failing, rollback tolerance covers an already-completed transaction, disposal follows, and the
    /// client's reset of a pooled connection is the outer cleanup boundary. The path that needs restoring is
    /// the successful one, which is exactly the path this reaches.
    /// </summary>
    public string? CommandEpilogue =>
        $"""
            IF @{PriorXactAbortVariable} = 0 SET XACT_ABORT OFF;
            IF @{PriorNoCountVariable} = 0 SET NOCOUNT OFF;
            """;

    public IReadOnlyList<string> ReservedCommandNames => [PriorXactAbortVariable, PriorNoCountVariable];

    public string EmitSentinel(int ordinal) =>
        string.Create(CultureInfo.InvariantCulture, $"SELECT {ordinal} AS [LogicalStatementOrdinal];");

    public IRelationalCompositeTargetCarrier Carrier => MssqlCompositeTargetCarrier.Instance;
}

/// <summary>
/// PostgreSQL carrier built on a transaction-local custom setting. It needs no schema object and no DDL,
/// and PostgreSQL reverts an <c>is_local</c> setting automatically at transaction end on both commit and
/// rollback, so its lifetime is bounded without any cleanup statement. Isolation level is not raised.
/// </summary>
internal sealed class PgsqlCompositeTargetCarrier : IRelationalCompositeTargetCarrier
{
    public static readonly PgsqlCompositeTargetCarrier Instance = new();

    private const string SettingName = "dms.composite_target_documentid";

    private PgsqlCompositeTargetCarrier() { }

    /// <summary>A setting name is not a parameter, so nothing has to be withheld from the allocator.</summary>
    public IReadOnlyList<string> ReservedNames => [];

    public string? DeclarationPrologue => null;

    /// <summary>
    /// The captured value is written by a CTE whose result is referenced from the final select list, so it
    /// is evaluated exactly once whether or not the target CTE produced a row. Writing it from inside the
    /// target CTE would skip the capture entirely on a miss.
    /// </summary>
    public string EmitCaptureTarget(string targetPredicateSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPredicateSql);

        return $"""
            WITH target AS (
                SELECT d."DocumentId", d."ContentVersion"
                FROM dms."Document" d
                WHERE {targetPredicateSql}
                FOR UPDATE
            ),
            captured AS (
                SELECT set_config(
                    '{SettingName}',
                    COALESCE((SELECT "DocumentId"::text FROM target), ''),
                    true
                ) AS "CapturedToken"
            )
            SELECT
                (SELECT "DocumentId" FROM target) AS "DocumentId",
                (SELECT "ContentVersion" FROM target) AS "ContentVersion",
                (SELECT "CapturedToken" FROM captured) AS "CapturedToken";
            """;
    }

    public string CapturedTargetIdExpression => $"NULLIF(current_setting('{SettingName}', true), '')::bigint";

    public string CapturedTargetPresentPredicate =>
        $"COALESCE(current_setting('{SettingName}', true), '') <> ''";

    public string CapturedContentVersionExpression =>
        $"""(SELECT "ContentVersion" FROM dms."Document" WHERE "DocumentId" = {CapturedTargetIdExpression})""";
}

/// <summary>
/// SQL Server carrier built on batch-local variables, which are scoped to the batch by construction and
/// need no cleanup. Their names are reserved because SqlClient rejects a batch-local sharing a name with a
/// bound parameter.
/// </summary>
internal sealed class MssqlCompositeTargetCarrier : IRelationalCompositeTargetCarrier
{
    public static readonly MssqlCompositeTargetCarrier Instance = new();

    private const string DocumentIdVariable = "dms_composite_target_documentid";
    private const string ContentVersionVariable = "dms_composite_target_contentversion";

    private MssqlCompositeTargetCarrier() { }

    public IReadOnlyList<string> ReservedNames => [DocumentIdVariable, ContentVersionVariable];

    public string? DeclarationPrologue =>
        $"DECLARE @{DocumentIdVariable} BIGINT, @{ContentVersionVariable} BIGINT;";

    public string EmitCaptureTarget(string targetPredicateSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPredicateSql);

        return $"""
            SELECT
                @{DocumentIdVariable} = d.[DocumentId],
                @{ContentVersionVariable} = d.[ContentVersion]
            FROM [dms].[Document] d WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            WHERE {targetPredicateSql};

            SELECT
                @{DocumentIdVariable} AS [DocumentId],
                @{ContentVersionVariable} AS [ContentVersion];
            """;
    }

    public string CapturedTargetIdExpression => $"@{DocumentIdVariable}";

    public string CapturedTargetPresentPredicate => $"@{DocumentIdVariable} IS NOT NULL";

    public string CapturedContentVersionExpression => $"@{ContentVersionVariable}";
}
