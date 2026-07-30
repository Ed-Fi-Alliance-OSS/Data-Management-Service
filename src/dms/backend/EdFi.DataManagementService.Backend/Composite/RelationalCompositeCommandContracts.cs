// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// Declared result-set shape of one logical statement in a composite command.
/// </summary>
/// <remarks>
/// Every logical statement emits exactly one result set. That is what makes ordinal-based failure
/// attribution sound: with no result-set-less statement, a provider cannot skip past a statement while
/// searching for the next result set, so the reader position identifies the failing statement. A pure
/// DML statement therefore carries a trailing sentinel select rather than declaring "no results".
/// </remarks>
internal enum RelationalCompositeResultShape
{
    /// <summary>A trailing <c>SELECT &lt;ordinal&gt;</c> appended after a data-modifying statement.</summary>
    Sentinel,

    /// <summary>Exactly one row whose first column is the statement's value.</summary>
    Scalar,

    /// <summary>Zero or more rows consumed by the statement's reader.</summary>
    Rows,
}

/// <summary>
/// One logical statement inside a composite command.
/// </summary>
/// <param name="Ordinal">Zero-based position, and the value a sentinel echoes back.</param>
/// <param name="Label">Diagnostic name used in failure attribution and log scope.</param>
/// <param name="Sql">Statement SQL, excluding any sentinel the builder appends.</param>
/// <param name="Parameters">Parameters contributed by this statement, already uniquely named.</param>
/// <param name="ResultShape">The declared shape the decoder enforces.</param>
/// <param name="Read">
/// Optional reader for <see cref="RelationalCompositeResultShape.Rows"/>. When absent the decoder
/// counts rows.
/// </param>
internal sealed record RelationalCompositeStatement(
    int Ordinal,
    string Label,
    string Sql,
    IReadOnlyList<RelationalParameter> Parameters,
    RelationalCompositeResultShape ResultShape,
    Func<DbDataReader, CancellationToken, Task<object?>>? Read = null
);

/// <summary>
/// A sealed composite command: one <see cref="RelationalCommand"/> plus the ordered logical statements
/// whose result sets the decoder walks.
/// </summary>
internal sealed record RelationalCompositeCommand(
    RelationalCommand Command,
    IReadOnlyList<RelationalCompositeStatement> StatementsInOrder
)
{
    public int ParameterCount => Command.Parameters.Count;
}

/// <summary>
/// Which logical statement a provider failure is attributed to.
/// </summary>
/// <param name="Ordinal">
/// The failing statement's ordinal, or <see langword="null"/> when the failure is not attributable to
/// a statement — a connection-level failure with no server error, or cancellation. There is no
/// fabricated default.
/// </param>
/// <param name="Label">The failing statement's label when the ordinal is known.</param>
/// <param name="Stage">Where the failure surfaced, for diagnostics.</param>
internal sealed record RelationalCompositeFailureContext(
    int? Ordinal,
    string? Label,
    RelationalCompositeFailureStage Stage
);

internal enum RelationalCompositeFailureStage
{
    /// <summary>Thrown while opening the reader, which can only surface statement 0.</summary>
    OpeningReader,

    /// <summary>Thrown while advancing to the next result set.</summary>
    AdvancingResultSet,

    /// <summary>Thrown while reading rows of the current result set.</summary>
    ReadingRows,

    /// <summary>Not attributable to a logical statement.</summary>
    Unattributable,
}

/// <summary>
/// One logical statement's decoded outcome.
/// </summary>
/// <param name="Ordinal">The statement's ordinal.</param>
/// <param name="Label">The statement's label.</param>
/// <param name="Value">
/// The scalar for <see cref="RelationalCompositeResultShape.Scalar"/>, the reader's return value or
/// the row count for <see cref="RelationalCompositeResultShape.Rows"/>, and the echoed ordinal for
/// <see cref="RelationalCompositeResultShape.Sentinel"/>.
/// </param>
internal sealed record RelationalCompositeStatementOutcome(int Ordinal, string Label, object? Value);
