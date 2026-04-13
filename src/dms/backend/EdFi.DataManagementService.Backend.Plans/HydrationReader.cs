// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
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

        if (reader.FieldCount != ExpectedDocumentMetadataColumnCount)
        {
            throw new InvalidOperationException(
                $"Document metadata result set has {reader.FieldCount} columns but expected {ExpectedDocumentMetadataColumnCount}."
            );
        }

        var rows = new List<DocumentMetadataRow>();

        while (await reader.ReadAsync(ct))
        {
            rows.Add(
                new DocumentMetadataRow(
                    DocumentId: reader.GetInt64(0),
                    DocumentUuid: reader.GetGuid(1),
                    ContentVersion: reader.GetInt64(2),
                    IdentityVersion: reader.GetInt64(3),
                    ContentLastModifiedAt: ReadDateTimeOffset(reader, 4),
                    IdentityLastModifiedAt: ReadDateTimeOffset(reader, 5)
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

        if (reader.FieldCount != columnCount)
        {
            throw new InvalidOperationException(
                $"Table '{tablePlan.TableModel.Table}' result set has {reader.FieldCount} columns but expected {columnCount}."
            );
        }

        var rows = new List<object?[]>();

        while (await reader.ReadAsync(ct))
        {
            var row = new object?[columnCount];

            for (var i = 0; i < columnCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new HydratedTableRows(tablePlan.TableModel, rows);
    }

    /// <summary>
    /// Reads normalized descriptor URI rows from the current descriptor projection result set.
    /// </summary>
    /// <param name="reader">The data reader positioned at a descriptor projection result set.</param>
    /// <param name="descriptorPlan">The descriptor projection plan describing the expected ordinals.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Descriptor URI rows in result-set order.</returns>
    public static async Task<HydratedDescriptorRows> ReadDescriptorRowsAsync(
        DbDataReader reader,
        DescriptorProjectionPlan descriptorPlan,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(descriptorPlan);

        var expectedColumnCount =
            Math.Max(descriptorPlan.ResultShape.DescriptorIdOrdinal, descriptorPlan.ResultShape.UriOrdinal)
            + 1;

        if (reader.FieldCount != expectedColumnCount)
        {
            throw new InvalidOperationException(
                "Descriptor projection result set has "
                    + $"{reader.FieldCount} columns but expected {expectedColumnCount}."
            );
        }

        var rows = new List<DescriptorUriRow>();

        while (await reader.ReadAsync(ct))
        {
            rows.Add(
                new DescriptorUriRow(
                    DescriptorId: reader.GetInt64(descriptorPlan.ResultShape.DescriptorIdOrdinal),
                    Uri: reader.GetString(descriptorPlan.ResultShape.UriOrdinal)
                )
            );
        }

        return new HydratedDescriptorRows(rows);
    }

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Expected DateTimeOffset-compatible value at ordinal {ordinal}, but received '{value.GetType().Name}'."
            ),
        };
    }
}
