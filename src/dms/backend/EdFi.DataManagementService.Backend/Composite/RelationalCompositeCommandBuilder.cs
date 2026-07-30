// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// Accumulates ordered logical statements into one composite command.
/// </summary>
/// <remarks>
/// <para>
/// The builder owns three invariants that the composite design depends on and that would otherwise be
/// review-time hopes:
/// </para>
/// <list type="number">
/// <item>
/// Every logical statement emits exactly one result set. Data-modifying statements get a trailing sentinel
/// select, so a provider can never skip past a statement while looking for the next result set.
/// Ordinal-based failure attribution is only sound because of this.
/// </item>
/// <item>
/// Once the captured-target statement is emitted, no later statement may reference the target through a
/// fresh predicate; it must use the carrier's captured expressions. Attempting otherwise is a build-time
/// error, not a silent same-state defect.
/// </item>
/// <item>
/// Parameters come from one allocator, so names cannot collide across co-batched statements and cannot
/// shadow a provider carrier variable or a caller-reserved write-plan binding.
/// </item>
/// </list>
/// </remarks>
internal sealed class RelationalCompositeCommandBuilder
{
    private readonly IRelationalCompositeCommandDialect _dialect;
    private readonly List<RelationalCompositeStatement> _statements = [];
    private readonly List<RelationalParameter> _parameters = [];
    private readonly RelationalCommandBudget _budget;

    private string? _capturedTargetPredicate;
    private bool _sealed;

    public RelationalCompositeCommandBuilder(
        IRelationalCompositeCommandDialect dialect,
        RelationalCommandBudget? budget = null,
        IEnumerable<string>? reservedParameterNames = null
    )
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _budget = budget ?? RelationalCommandBudget.ForDialect(_dialect.Dialect);

        List<string> reserved = [.. _dialect.Carrier.ReservedNames];

        if (reservedParameterNames is not null)
        {
            reserved.AddRange(reservedParameterNames);
        }

        Allocator = new RelationalCompositeParameterAllocator(reserved);
    }

    /// <summary>The single allocator every statement must use to name its parameters.</summary>
    public RelationalCompositeParameterAllocator Allocator { get; }

    /// <summary>Statements appended so far.</summary>
    public int StatementCount => _statements.Count;

    /// <summary>Parameters bound so far.</summary>
    public int ParameterCount => _parameters.Count;

    /// <summary>Parameter slots still available in this command.</summary>
    public int RemainingParameterBudget => _budget.MaxParametersPerCommand - _parameters.Count;

    /// <summary>The ordinal the next appended statement will receive.</summary>
    public int NextOrdinal => _statements.Count;

    /// <summary>
    /// The carrier's captured-target expressions, valid only after <see cref="AppendCaptureTarget"/>.
    /// </summary>
    public IRelationalCompositeTargetCarrier Carrier => _dialect.Carrier;

    /// <summary>
    /// Appends the locking capture statement. Later statements must reference the target only through
    /// <see cref="Carrier"/>'s captured expressions.
    /// </summary>
    public int AppendCaptureTarget(string targetPredicateSql, IReadOnlyList<RelationalParameter> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPredicateSql);

        if (_capturedTargetPredicate is not null)
        {
            throw new InvalidOperationException(
                "A composite command may capture the target only once; the captured decision is what later "
                    + "statements consume."
            );
        }

        if (_statements.Count > 0)
        {
            throw new InvalidOperationException(
                "The captured-target statement must be the first logical statement, so the row lock is held "
                    + "before any statement observes state or authorizes."
            );
        }

        var ordinal = Append(
            "capture-target",
            _dialect.Carrier.EmitCaptureTarget(targetPredicateSql),
            parameters,
            RelationalCompositeResultShape.Scalar
        );

        _capturedTargetPredicate = targetPredicateSql;

        return ordinal;
    }

    /// <summary>Appends a logical statement and returns its ordinal.</summary>
    public int Append(
        string label,
        string sql,
        IReadOnlyList<RelationalParameter> parameters,
        RelationalCompositeResultShape resultShape,
        Func<DbDataReader, CancellationToken, Task<object?>>? read = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_sealed, this);

        if (resultShape is not RelationalCompositeResultShape.Rows && read is not null)
        {
            throw new ArgumentException(
                $"Statement '{label}' supplied a reader but declared shape '{resultShape}'. Only "
                    + $"'{nameof(RelationalCompositeResultShape.Rows)}' consumes a reader.",
                nameof(read)
            );
        }

        GuardAgainstFreshTargetRecheck(label, sql);
        GuardParameterBudget(label, parameters.Count);
        GuardParametersWereAllocated(label, parameters);

        var ordinal = _statements.Count;

        _statements.Add(new RelationalCompositeStatement(ordinal, label, sql, parameters, resultShape, read));
        _parameters.AddRange(parameters);

        return ordinal;
    }

    /// <summary>
    /// True when a statement contributing <paramref name="parameterCount"/> parameters still fits. The
    /// caller seals and opens a new command when this returns false.
    /// </summary>
    public bool Fits(int parameterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parameterCount);

        return parameterCount <= RemainingParameterBudget;
    }

    /// <summary>Assembles the command text and freezes the builder.</summary>
    public RelationalCompositeCommand Seal()
    {
        ObjectDisposedException.ThrowIf(_sealed, this);

        if (_statements.Count == 0)
        {
            throw new InvalidOperationException(
                "A composite command must contain at least one logical statement."
            );
        }

        StringBuilder builder = new();

        // The prologue and any carrier declaration precede every statement and are not logical statements:
        // they produce no result set, so they must not consume an ordinal.
        if (_statements.Count > 1 && _dialect.MultiStatementPrologue is { } prologue)
        {
            builder.AppendLine(prologue);
        }

        if (_capturedTargetPredicate is not null && _dialect.Carrier.DeclarationPrologue is { } declaration)
        {
            builder.AppendLine(declaration);
        }

        foreach (var statement in _statements)
        {
            builder.AppendLine(statement.Sql.TrimEnd());

            if (statement.ResultShape is RelationalCompositeResultShape.Sentinel)
            {
                builder.AppendLine(_dialect.EmitSentinel(statement.Ordinal));
            }
        }

        _sealed = true;

        return new RelationalCompositeCommand(
            new RelationalCommand(builder.ToString().TrimEnd(), _parameters),
            _statements
        );
    }

    /// <summary>
    /// Fails the build when a statement after the capture re-observes the target instead of consuming the
    /// captured decision. This is the mechanical guarantee behind same-state correctness: each statement
    /// takes its own snapshot under READ COMMITTED, so a repeated predicate is a new observation that a
    /// concurrent insert can satisfy.
    /// </summary>
    private void GuardAgainstFreshTargetRecheck(string label, string sql)
    {
        if (
            _capturedTargetPredicate is null
            || !sql.Contains(_capturedTargetPredicate, StringComparison.Ordinal)
        )
        {
            return;
        }

        throw new InvalidOperationException(
            $"Statement '{label}' repeats the captured target predicate '{_capturedTargetPredicate}'. "
                + "Statements after the capture must consume the carrier's captured expressions "
                + $"('{_dialect.Carrier.CapturedTargetIdExpression}' / "
                + $"'{_dialect.Carrier.CapturedTargetPresentPredicate}'), because each statement takes a "
                + "fresh snapshot and a repeated predicate can observe a target the lock never covered."
        );
    }

    private void GuardParameterBudget(string label, int parameterCount)
    {
        if (Fits(parameterCount))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Statement '{label}' needs {parameterCount} parameters but only {RemainingParameterBudget} of "
                + $"{_budget.MaxParametersPerCommand} remain in this command. Seal the command and open the "
                + "next one instead of overflowing it."
        );
    }

    private void GuardParametersWereAllocated(string label, IReadOnlyList<RelationalParameter> parameters)
    {
        var unallocated = parameters.FirstOrDefault(parameter =>
            !Allocator.IssuedNames.Contains(parameter.Name.TrimStart('@'))
        );

        if (unallocated is not null)
        {
            throw new InvalidOperationException(
                $"Statement '{label}' binds parameter '{unallocated.Name}', which this command's allocator "
                    + "did not issue. Every parameter must be allocated through the builder's allocator so "
                    + "names cannot collide across co-batched statements or shadow a carrier variable."
            );
        }
    }
}
