// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// One row of document metadata from <c>dms.Document</c> joined to the page keyset.
/// </summary>
/// <param name="DocumentId">The internal document identity.</param>
/// <param name="DocumentUuid">The public document UUID exposed as <c>id</c> in API responses.</param>
/// <param name="ContentVersion">Stored content-change version stamp.</param>
/// <param name="IdentityVersion">Stored identity-change version stamp.</param>
/// <param name="ContentLastModifiedAt">Timestamp of the last content change.</param>
/// <param name="IdentityLastModifiedAt">Timestamp of the last identity change.</param>
public sealed record DocumentMetadataRow(
    long DocumentId,
    Guid DocumentUuid,
    long ContentVersion,
    long IdentityVersion,
    DateTimeOffset ContentLastModifiedAt,
    DateTimeOffset IdentityLastModifiedAt
);

/// <summary>
/// Hydrated rows for a single table in a resource read plan.
/// </summary>
/// <remarks>
/// Each <c>object?[]</c> in <see cref="Rows"/> is aligned to the <see cref="TableModel"/>'s
/// <c>Columns</c> ordinals. Downstream consumers access values by ordinal using the
/// <see cref="DbTableModel"/> column metadata.
/// </remarks>
/// <param name="TableModel">The table shape model for the hydrated rows.</param>
/// <param name="Rows">Row buffers aligned to the table model's column ordinals.</param>
public sealed record HydratedTableRows(DbTableModel TableModel, IReadOnlyList<object?[]> Rows);

/// <summary>
/// Full hydration result for a page of documents.
/// </summary>
/// <param name="TotalCount">
/// Optional total row count when requested by the caller (e.g., <c>totalCount=true</c>).
/// </param>
/// <param name="DocumentMetadata">
/// Document metadata rows from <c>dms.Document</c> for the page, ordered by <c>DocumentId</c>.
/// </param>
/// <param name="TableRowsInDependencyOrder">
/// Per-table hydrated rows in deterministic dependency order (root table first, then children).
/// </param>
public sealed record HydratedPage(
    long? TotalCount,
    IReadOnlyList<DocumentMetadataRow> DocumentMetadata,
    IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder
);

/// <summary>
/// Discriminated union specifying how the page keyset is materialized for hydration.
/// </summary>
public abstract record PageKeysetSpec
{
    private PageKeysetSpec() { }

    /// <summary>
    /// GET by id: the keyset is a single <c>DocumentId</c>.
    /// </summary>
    /// <param name="DocumentId">The document to hydrate.</param>
    public sealed record Single(long DocumentId) : PageKeysetSpec;

    /// <summary>
    /// GET by query: the keyset comes from a compiled page-selection SQL plan.
    /// </summary>
    /// <param name="Plan">The compiled page document-id SQL plan.</param>
    /// <param name="ParameterValues">
    /// Parameter values keyed by bare parameter name (without <c>@</c>).
    /// </param>
    public sealed record Query(
        PageDocumentIdSqlPlan Plan,
        IReadOnlyDictionary<string, object?> ParameterValues
    ) : PageKeysetSpec;
}
