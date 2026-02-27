// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Deterministic batching conventions for write-plan bulk inserts.
/// </summary>
public static class PlanWriteBatchingConventions
{
    private const int MssqlMaxParametersPerCommand = 2100;
    private const int PgsqlMaxParametersPerCommand = 65535;
    private const int MssqlMaxValuesRowsPerCommand = 1000;
    private const int PgsqlPolicyMaxRowsPerCommand = 1000;

    /// <summary>
    /// Derives deterministic batching metadata from ordered write-column bindings.
    /// </summary>
    /// <param name="dialect">SQL dialect.</param>
    /// <param name="columnBindings">Stored/writable column bindings in authoritative order.</param>
    public static BulkInsertBatchingInfo DeriveBulkInsertBatchingInfo(
        SqlDialect dialect,
        IReadOnlyList<WriteColumnBinding> columnBindings
    )
    {
        ArgumentNullException.ThrowIfNull(columnBindings);

        return DeriveBulkInsertBatchingInfo(dialect, columnBindings.Count);
    }

    /// <summary>
    /// Derives deterministic batching metadata from per-row parameter width.
    /// </summary>
    /// <param name="dialect">SQL dialect.</param>
    /// <param name="parametersPerRow">
    /// Number of parameters emitted per inserted row. Must be at least 1.
    /// </param>
    public static BulkInsertBatchingInfo DeriveBulkInsertBatchingInfo(
        SqlDialect dialect,
        int parametersPerRow
    )
    {
        if (parametersPerRow < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parametersPerRow),
                parametersPerRow,
                "Parameters per row must be at least 1."
            );
        }

        var limits = GetLimits(dialect);
        var maxRowsByParameterLimit = limits.MaxParametersPerCommand / parametersPerRow;
        var maxRowsPerBatch = Math.Min(limits.RowCap, maxRowsByParameterLimit);

        if (maxRowsPerBatch < 1)
        {
            throw new InvalidOperationException(
                $"Cannot derive bulk-insert batch size for dialect '{dialect}'. "
                    + $"Row width {parametersPerRow} exceeds max parameters per command ({limits.MaxParametersPerCommand})."
            );
        }

        return new BulkInsertBatchingInfo(
            MaxRowsPerBatch: maxRowsPerBatch,
            ParametersPerRow: parametersPerRow,
            MaxParametersPerCommand: limits.MaxParametersPerCommand
        );
    }

    /// <summary>
    /// Returns dialect-specific batching limits used by bulk insert batching calculations.
    /// </summary>
    private static BatchingLimits GetLimits(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Mssql => new(
                MaxParametersPerCommand: MssqlMaxParametersPerCommand,
                RowCap: MssqlMaxValuesRowsPerCommand
            ),
            SqlDialect.Pgsql => new(
                MaxParametersPerCommand: PgsqlMaxParametersPerCommand,
                RowCap: PgsqlPolicyMaxRowsPerCommand
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    /// <summary>
    /// Dialect policy limits used to compute deterministic bulk insert batch sizes.
    /// </summary>
    /// <param name="MaxParametersPerCommand">Maximum parameter placeholders allowed in a single command.</param>
    /// <param name="RowCap">Maximum rows allowed per <c>VALUES</c> clause (or policy cap).</param>
    private readonly record struct BatchingLimits(int MaxParametersPerCommand, int RowCap);
}
