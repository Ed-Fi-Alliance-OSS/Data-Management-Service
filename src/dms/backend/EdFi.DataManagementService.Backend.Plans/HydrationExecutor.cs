// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Executes a compiled <see cref="ResourceReadPlan"/> against a database connection,
/// returning structured hydrated row data for a page of documents.
/// </summary>
/// <remarks>
/// <para>
/// The executor builds a single SQL batch that materializes a keyset temp table, then
/// returns multiple result sets (document metadata, root rows, child rows) consumed
/// sequentially via <see cref="DbDataReader.NextResultAsync"/>.
/// </para>
/// <para>
/// Takes an already-opened <see cref="DbConnection"/> so callers can manage connection
/// lifecycle, transaction scope, and prepend authorization SQL on the same connection.
/// </para>
/// </remarks>
public static class HydrationExecutor
{
    /// <summary>
    /// Executes the hydration batch and returns the structured page result.
    /// </summary>
    /// <param name="connection">An already-opened database connection.</param>
    /// <param name="plan">The compiled resource read plan.</param>
    /// <param name="keyset">The page keyset specification.</param>
    /// <param name="dialect">The SQL dialect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The hydrated page containing document metadata and per-table row data.</returns>
    public static async Task<HydratedPage> ExecuteAsync(
        DbConnection connection,
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        SqlDialect dialect,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(keyset);

        var batchSql = HydrationBatchBuilder.Build(plan, keyset, dialect);

        await using var command = connection.CreateCommand();
        command.CommandText = batchSql;
        HydrationBatchBuilder.AddParameters(command, keyset);

        await using var reader = await command.ExecuteReaderAsync(ct);

        // The batch begins with CREATE TEMP TABLE + INSERT (keyset materialization).
        // Both Npgsql and SqlClient skip DDL/DML statements when advancing result sets,
        // so the reader is positioned at the first SELECT result set automatically.

        // 1. Optional total count
        long? totalCount = null;
        bool hasTotalCount = keyset is PageKeysetSpec.Query { Plan.TotalCountSql: not null };

        if (hasTotalCount)
        {
            totalCount = await HydrationReader.ReadTotalCountAsync(reader, ct);
            if (!await reader.NextResultAsync(ct))
            {
                throw new InvalidOperationException(
                    "Expected document metadata result set after total count but no more result sets available."
                );
            }
        }

        // 2. Document metadata
        var documentMetadata = await HydrationReader.ReadDocumentMetadataAsync(reader, ct);

        // 3. Table rows in dependency order
        var tableRows = new HydratedTableRows[plan.TablePlansInDependencyOrder.Length];

        for (var i = 0; i < plan.TablePlansInDependencyOrder.Length; i++)
        {
            if (!await reader.NextResultAsync(ct))
            {
                throw new InvalidOperationException(
                    $"Expected result set for table '{plan.TablePlansInDependencyOrder[i].TableModel.Table}' "
                        + $"(index {i}) but no more result sets available."
                );
            }
            tableRows[i] = await HydrationReader.ReadTableRowsAsync(
                reader,
                plan.TablePlansInDependencyOrder[i],
                ct
            );
        }

        return new HydratedPage(totalCount, documentMetadata, tableRows);
    }
}
