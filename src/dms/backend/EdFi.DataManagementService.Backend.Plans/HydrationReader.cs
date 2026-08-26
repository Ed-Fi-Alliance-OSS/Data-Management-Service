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
    /// Reads the selected page keyset from the current result set and returns the maximum value of its
    /// continuation anchor, or <see langword="null"/> when the selection was empty.
    /// </summary>
    /// <remarks>
    /// Neither <c>RETURNING</c> nor <c>OUTPUT</c> promises an order, so the maximum is taken across
    /// every returned row rather than from the first or last one. That is what makes the widening to
    /// two columns safe: the anchor's maximum is found the same way whichever row carries it.
    /// </remarks>
    /// <param name="reader">The data reader positioned at the selected keyset result set.</param>
    /// <param name="carriesAnchorColumn">
    /// Whether the materialization projected the anchor beside the ids. Supplied by the caller from
    /// <c>HydrationBatchBuilder.CarriesSelectedAnchor</c>, the same predicate the batch emitted its
    /// column list from, rather than inferred from the reader's shape: inferring it would read
    /// <c>DocumentId</c> as the anchor if the projection ever narrowed, which is a wrong continuation
    /// token rather than a failure.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The maximum selected anchor value, or null when nothing was selected.</returns>
    public static async Task<long?> ReadSelectedAnchorMaximumAsync(
        DbDataReader reader,
        bool carriesAnchorColumn,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        var anchorOrdinal = carriesAnchorColumn ? FindSelectedAnchorOrdinal(reader) : DocumentIdColumnOrdinal;

        long? selectedMaximum = null;

        while (await reader.ReadAsync(ct))
        {
            var selectedAnchor = reader.GetInt64(anchorOrdinal);

            if (selectedMaximum is null || selectedAnchor > selectedMaximum)
            {
                selectedMaximum = selectedAnchor;
            }
        }

        return selectedMaximum;
    }

    /// <summary>
    /// Locates the continuation anchor in the selected page keyset result set by the same name the
    /// batch builder projected it under, and reports a shape disagreement when it is absent.
    /// </summary>
    /// <remarks>
    /// By name rather than at a fixed ordinal, matching the read-acceleration twin in the repository:
    /// the anchor's position is a property of the emitted <c>RETURNING</c> and <c>OUTPUT</c> clauses,
    /// and a column added ahead of it would leave a fixed ordinal reading a plausible <c>long</c> that
    /// is not the anchor — a continuation token that names the wrong position in the sequence rather
    /// than a failure. A missing column is a defect in this code, not anything a client did, so it is
    /// named here instead of surfacing as a bare ordinal fault from inside the row loop.
    /// <para>
    /// Scanned rather than resolved through <see cref="DbDataReader.GetOrdinal" />, which reports an
    /// absent name by throwing a type that varies by provider.
    /// </para>
    /// </remarks>
    private static int FindSelectedAnchorOrdinal(DbDataReader reader)
    {
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            if (
                string.Equals(
                    reader.GetName(ordinal),
                    HydrationSqlConventions.SelectedAnchorColumnName,
                    StringComparison.Ordinal
                )
            )
            {
                return ordinal;
            }
        }

        throw new InvalidOperationException(
            "Expected the selected page keyset result set to carry the continuation anchor as its "
                + $"'{HydrationSqlConventions.SelectedAnchorColumnName}' column, but it carries no such "
                + "column. The materialization SQL and this reader disagree about the keyset shape."
        );
    }

    /// <summary>
    /// <c>DocumentId</c>'s ordinal in the selected page keyset result set. Always first, on an anchored
    /// page and an unanchored one alike; the anchor beside it is located by name instead.
    /// </summary>
    private const int DocumentIdColumnOrdinal = 0;

    /// <summary>
    /// Expected column count for the document metadata result set, defined by
    /// <see cref="DocumentMetadataColumns.ColumnsInOrdinalOrder"/>.
    /// </summary>
    private static readonly int ExpectedDocumentMetadataColumnCount = DocumentMetadataColumns
        .ColumnsInOrdinalOrder
        .Length;

    /// <summary>
    /// Reads <c>dms.Document</c> metadata rows from the current result set.
    /// </summary>
    /// <remarks>
    /// Expects columns at fixed ordinals aligned to <see cref="DocumentMetadataColumns"/>:
    /// 0=DocumentId, 1=DocumentUuid, 2=ContentVersion, 3=ContentLastModifiedAt,
    /// 4=ResourceKeyId.
    /// </remarks>
    /// <param name="reader">The data reader positioned at the document metadata result set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// List of document metadata rows ordered by selected-page ordinal when supplied, otherwise by
    /// DocumentId.
    /// </returns>
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
                    ContentLastModifiedAt: ReadDateTimeOffset(reader, 3),
                    ResourceKeyId: reader.GetInt16(4)
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
                var isNull = await reader.IsDBNullAsync(i, ct);
                row[i] = isNull ? null : reader.GetValue(i);
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

    /// <summary>
    /// Reads document-reference auxiliary lookup rows from the current result set.
    /// </summary>
    /// <remarks>
    /// Expected columns at fixed ordinals aligned to
    /// <see cref="DocumentReferenceLookupResultShape"/>:
    /// 0=DocumentId (bigint), 1=DocumentUuid (uuid), 2=ResourceKeyId (smallint).
    /// </remarks>
    /// <param name="reader">The data reader positioned at the lookup result set.</param>
    /// <param name="lookupPlan">The lookup plan describing the expected ordinals.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hydrated lookup rows in result-set order.</returns>
    public static async Task<HydratedDocumentReferenceLookup> ReadDocumentReferenceLookupRowsAsync(
        DbDataReader reader,
        DocumentReferenceLookupPlan lookupPlan,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(lookupPlan);

        var resultShape = lookupPlan.ResultShape;
        var expectedColumnCount =
            Math.Max(
                Math.Max(resultShape.DocumentIdOrdinal, resultShape.DocumentUuidOrdinal),
                resultShape.ResourceKeyIdOrdinal
            ) + 1;

        if (reader.FieldCount != expectedColumnCount)
        {
            throw new InvalidOperationException(
                "Document-reference lookup result set has "
                    + $"{reader.FieldCount} columns but expected {expectedColumnCount}."
            );
        }

        var rows = new List<DocumentReferenceLookupRow>();

        while (await reader.ReadAsync(ct))
        {
            rows.Add(
                new DocumentReferenceLookupRow(
                    DocumentId: reader.GetInt64(resultShape.DocumentIdOrdinal),
                    DocumentUuid: reader.GetGuid(resultShape.DocumentUuidOrdinal),
                    ResourceKeyId: reader.GetInt16(resultShape.ResourceKeyIdOrdinal)
                )
            );
        }

        return new HydratedDocumentReferenceLookup(rows);
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
