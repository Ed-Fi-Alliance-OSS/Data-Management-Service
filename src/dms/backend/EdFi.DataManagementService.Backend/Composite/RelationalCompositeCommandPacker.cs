// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Composite;

/// <summary>
/// Per-command budget for a composite command.
/// </summary>
/// <remarks>
/// <see cref="BulkInsertBatchingInfo"/> computes a row cap <em>per table</em> against the same dialect
/// ceilings. Co-batching several tables into one command makes a per-table budget unsound, because the
/// ceiling applies to the command, so the budget is tracked here at command level.
/// </remarks>
/// <param name="MaxParametersPerCommand">Usable parameter slots per command.</param>
/// <param name="MaxRowsPerStatement">Policy row cap for a single statement.</param>
internal sealed record RelationalCommandBudget(int MaxParametersPerCommand, int MaxRowsPerStatement)
{
    /// <summary>
    /// SQL Server allows 2098 usable parameters, the documented 2100 less the two slots
    /// <c>sp_executesql</c> consumes for its own arguments; PostgreSQL allows 65535. Both apply the
    /// repository's 1000-row policy cap. The ceilings are read from the plan-layer constants rather than
    /// duplicated, because the write and authorization ceilings previously drifted apart.
    /// </summary>
    public static RelationalCommandBudget ForDialect(SqlDialect dialect) =>
        new(
            PlanWriteBatchingConventions.MaxCommandParameters(dialect),
            PlanWriteBatchingConventions.MaxCommandRows(dialect)
        );
}

/// <summary>
/// One indivisible chunk of work offered to the packer: a single table's contiguous run of rows for one
/// statement kind.
/// </summary>
/// <param name="Label">Diagnostic name, typically <c>kind:schema.table</c>.</param>
/// <param name="RowCount">Rows in the run. Zero is legal and produces no group.</param>
/// <param name="ParametersPerRow">Parameters each row contributes.</param>
/// <param name="FixedParameterCount">Parameters the statement contributes once, regardless of rows.</param>
/// <param name="StartsNewCommand">
/// True when this unit consumes a value the caller does not hold until a previous command has returned —
/// a dependency boundary. The packer always begins a new command here.
/// </param>
internal sealed record RelationalCompositePackUnit(
    string Label,
    int RowCount,
    int ParametersPerRow,
    int FixedParameterCount = 0,
    bool StartsNewCommand = false
);

/// <summary>One row group of a unit, already sized to fit the row cap and the parameter budget.</summary>
/// <param name="Label">The originating unit's label.</param>
/// <param name="RowOffset">First row of the group within the unit.</param>
/// <param name="RowCount">Rows in the group.</param>
/// <param name="ParameterCount">Parameters the group contributes, including fixed parameters.</param>
internal sealed record RelationalCompositePackGroup(
    string Label,
    int RowOffset,
    int RowCount,
    int ParameterCount
);

/// <summary>
/// Deterministic packing of ordered units into as few commands as the budget allows.
/// </summary>
/// <remarks>
/// <para>
/// The rule, applied in the caller's already-resolved dependency order: seal the current command and open
/// the next when appending the next group would exceed the command's remaining parameter budget, when the
/// statement's own row cap forces a split, or at a dependency boundary. A row group is never split across
/// commands, and a unit is never reordered.
/// </para>
/// <para>
/// The invariant that matters is not a closed-form command count — atomic units, row width, row caps, and
/// dependency boundaries all constrain packing — but that the command count never grows merely because
/// more tables were added while their rows still fit one command. Callers assert this algorithm's exact
/// output for fixed inputs rather than a formula.
/// </para>
/// </remarks>
internal static class RelationalCompositeCommandPacker
{
    public static IReadOnlyList<IReadOnlyList<RelationalCompositePackGroup>> Pack(
        IReadOnlyList<RelationalCompositePackUnit> unitsInOrder,
        RelationalCommandBudget budget
    )
    {
        ArgumentNullException.ThrowIfNull(unitsInOrder);
        ArgumentNullException.ThrowIfNull(budget);

        List<IReadOnlyList<RelationalCompositePackGroup>> commands = [];
        List<RelationalCompositePackGroup> current = [];
        var usedParameters = 0;

        foreach (var unit in unitsInOrder)
        {
            ValidateUnit(unit, budget);

            if (unit.StartsNewCommand && current.Count > 0)
            {
                commands.Add(current);
                current = [];
                usedParameters = 0;
            }

            if (unit.RowCount == 0)
            {
                // A zero-row unit still contributes its fixed parameters when it has any; otherwise it
                // contributes nothing and is dropped rather than emitting an empty statement.
                if (unit.FixedParameterCount == 0)
                {
                    continue;
                }

                if (usedParameters + unit.FixedParameterCount > budget.MaxParametersPerCommand)
                {
                    commands.Add(current);
                    current = [];
                    usedParameters = 0;
                }

                current.Add(new RelationalCompositePackGroup(unit.Label, 0, 0, unit.FixedParameterCount));
                usedParameters += unit.FixedParameterCount;
                continue;
            }

            var rowOffset = 0;

            while (rowOffset < unit.RowCount)
            {
                var remainingRows = unit.RowCount - rowOffset;
                var rowsAllowedByCap = Math.Min(remainingRows, budget.MaxRowsPerStatement);
                var remainingParameters = budget.MaxParametersPerCommand - usedParameters;
                var rowsAllowedByBudget =
                    (remainingParameters - unit.FixedParameterCount) / unit.ParametersPerRow;

                if (rowsAllowedByBudget < 1)
                {
                    // Nothing more fits in this command. Sealing an empty command would loop forever, and
                    // ValidateUnit already guarantees at least one row fits an empty command.
                    commands.Add(current);
                    current = [];
                    usedParameters = 0;
                    continue;
                }

                var groupRowCount = Math.Min(rowsAllowedByCap, rowsAllowedByBudget);
                var groupParameterCount = (groupRowCount * unit.ParametersPerRow) + unit.FixedParameterCount;

                current.Add(
                    new RelationalCompositePackGroup(
                        unit.Label,
                        rowOffset,
                        groupRowCount,
                        groupParameterCount
                    )
                );

                usedParameters += groupParameterCount;
                rowOffset += groupRowCount;
            }
        }

        if (current.Count > 0)
        {
            commands.Add(current);
        }

        return commands;
    }

    private static void ValidateUnit(RelationalCompositePackUnit unit, RelationalCommandBudget budget)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentOutOfRangeException.ThrowIfNegative(unit.RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(unit.FixedParameterCount);

        if (unit.RowCount > 0 && unit.ParametersPerRow < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unit),
                unit.ParametersPerRow,
                $"Unit '{unit.Label}' has rows but no parameters per row."
            );
        }

        // A single row that cannot fit an otherwise empty command can never be packed. Fail loudly rather
        // than looping while sealing empty commands.
        if (
            unit.RowCount > 0
            && unit.FixedParameterCount + unit.ParametersPerRow > budget.MaxParametersPerCommand
        )
        {
            throw new InvalidOperationException(
                $"Unit '{unit.Label}' cannot be packed: one row needs "
                    + $"{unit.FixedParameterCount + unit.ParametersPerRow} parameters but a command allows "
                    + $"{budget.MaxParametersPerCommand}."
            );
        }

        if (unit.RowCount == 0 && unit.FixedParameterCount > budget.MaxParametersPerCommand)
        {
            throw new InvalidOperationException(
                $"Unit '{unit.Label}' cannot be packed: its {unit.FixedParameterCount} fixed parameters "
                    + $"exceed the {budget.MaxParametersPerCommand} a command allows."
            );
        }
    }
}
