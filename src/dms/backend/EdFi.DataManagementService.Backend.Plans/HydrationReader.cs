// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Consumes <see cref="DbDataReader"/> result sets produced by a hydration batch command,
/// returning typed metadata rows and raw row buffers aligned to table column ordinals.
/// </summary>
public static class HydrationReader
{
    /// <summary>
    /// Reads a single-row, single-column total count from the current result set.
    /// </summary>
    /// <param name="reader">The data reader positioned at the total count result set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total count value.</returns>
    public static async Task<long> ReadTotalCountAsync(DbDataReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Expected a total count result set row but none was returned."
            );
        }

        // SQL Server COUNT() returns int; PostgreSQL COUNT() returns bigint.
        // Convert.ToInt64 handles both without an InvalidCastException.
        return Convert.ToInt64(reader.GetValue(0));
    }

    /// <summary>
    /// Expected column count for the document metadata result set:
    /// DocumentId (ordinal 0) + the columns defined in <see cref="DocumentMetadataColumns.ColumnsInOrdinalOrder"/>.
    /// </summary>
    private static readonly int ExpectedDocumentMetadataColumnCount =
        1 + DocumentMetadataColumns.ColumnsInOrdinalOrder.Length;

    /// <summary>
    /// Reads <c>dms.Document</c> metadata rows from the current result set.
    /// </summary>
    /// <remarks>
    /// Expects columns at fixed ordinals aligned to <see cref="DocumentMetadataColumns"/>:
    /// 0=DocumentId, 1=DocumentUuid, 2=ContentVersion, 3=IdentityVersion,
    /// 4=ContentLastModifiedAt, 5=IdentityLastModifiedAt.
    /// </remarks>
    /// <param name="reader">The data reader positioned at the document metadata result set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of document metadata rows ordered by DocumentId.</returns>
    public static async Task<List<DocumentMetadataRow>> ReadDocumentMetadataAsync(
        DbDataReader reader,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        var rows = new List<DocumentMetadataRow>();

        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == 0 && reader.FieldCount != ExpectedDocumentMetadataColumnCount)
            {
                throw new InvalidOperationException(
                    $"Document metadata result set has {reader.FieldCount} columns but expected {ExpectedDocumentMetadataColumnCount}."
                );
            }

            rows.Add(
                new DocumentMetadataRow(
                    DocumentId: reader.GetInt64(0),
                    DocumentUuid: reader.GetGuid(1),
                    ContentVersion: reader.GetInt64(2),
                    IdentityVersion: reader.GetInt64(3),
                    ContentLastModifiedAt: reader.GetFieldValue<DateTimeOffset>(4),
                    IdentityLastModifiedAt: reader.GetFieldValue<DateTimeOffset>(5)
                )
            );
        }

        return rows;
    }

    /// <summary>
    /// Reads all rows from the current result set into <c>object?[]</c> buffers
    /// aligned to the table's column count.
    /// </summary>
    /// <param name="reader">The data reader positioned at a table hydration result set.</param>
    /// <param name="tablePlan">The table read plan describing the expected column shape.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hydrated table rows with raw value buffers.</returns>
    public static async Task<HydratedTableRows> ReadTableRowsAsync(
        DbDataReader reader,
        TableReadPlan tablePlan,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(tablePlan);

        var columnCount = tablePlan.TableModel.Columns.Count;
        var rows = new List<object?[]>();

        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == 0 && reader.FieldCount != columnCount)
            {
                throw new InvalidOperationException(
                    $"Table '{tablePlan.TableModel.Table}' result set has {reader.FieldCount} columns but expected {columnCount}."
                );
            }

            var row = new object?[columnCount];

            for (var i = 0; i < columnCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new HydratedTableRows(tablePlan.TableModel, rows);
    }
}
