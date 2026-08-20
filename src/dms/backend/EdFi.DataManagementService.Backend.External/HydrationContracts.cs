// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// One row of document metadata from <c>dms.Document</c>, selected for the page being hydrated.
/// </summary>
/// <param name="DocumentId">The internal document identity.</param>
/// <param name="DocumentUuid">The public document UUID exposed as <c>id</c> in API responses.</param>
/// <param name="ContentVersion">Stored content-change version stamp.</param>
/// <param name="IdentityVersion">Stored identity-change version stamp.</param>
/// <param name="ContentLastModifiedAt">Timestamp of the last content change.</param>
/// <param name="IdentityLastModifiedAt">Timestamp of the last identity change.</param>
/// <param name="ResourceKeyId">Stored resource identity.</param>
public sealed record DocumentMetadataRow(
    long DocumentId,
    Guid DocumentUuid,
    long ContentVersion,
    long IdentityVersion,
    DateTimeOffset ContentLastModifiedAt,
    DateTimeOffset IdentityLastModifiedAt,
    short ResourceKeyId
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
/// One resolved descriptor URI row from a descriptor projection result set.
/// </summary>
/// <param name="DescriptorId">The descriptor <c>DocumentId</c> referenced by hydrated rows.</param>
/// <param name="Uri">The canonical descriptor URI.</param>
public sealed record DescriptorUriRow(long DescriptorId, string Uri);

/// <summary>
/// Hydrated descriptor URI rows for a single descriptor projection plan.
/// </summary>
/// <remarks>
/// Instances in <see cref="HydratedPage.DescriptorRowsInPlanOrder"/> align by index with
/// <see cref="ResourceReadPlan.DescriptorProjectionPlansInOrder"/>.
/// </remarks>
/// <param name="Rows">Resolved descriptor URI rows in result-set order.</param>
public sealed record HydratedDescriptorRows(IReadOnlyList<DescriptorUriRow> Rows);

/// <summary>
/// One row from the document-reference auxiliary lookup result set.
/// </summary>
/// <param name="DocumentId">The internal document identity referenced by hydrated rows.</param>
/// <param name="DocumentUuid">The public document UUID rendered into <c>link.href</c>.</param>
/// <param name="ResourceKeyId">The resource-key id used to resolve <c>link.rel</c>.</param>
public sealed record DocumentReferenceLookupRow(long DocumentId, Guid DocumentUuid, short ResourceKeyId);

/// <summary>
/// Hydrated rows from the page-batched document-reference auxiliary lookup. Drives the
/// per-page <c>DocumentId → (DocumentUuid, ResourceKeyId)</c> map used by link injection.
/// </summary>
/// <param name="Rows">Lookup rows in result-set order (sorted by <c>DocumentId</c> ascending).</param>
public sealed record HydratedDocumentReferenceLookup(IReadOnlyList<DocumentReferenceLookupRow> Rows);

/// <summary>
/// Full hydration result for a page of documents.
/// </summary>
/// <param name="TotalCount">
/// Optional total row count when requested by the caller (e.g., <c>totalCount=true</c>).
/// </param>
/// <param name="DocumentMetadata">
/// Document metadata rows from <c>dms.Document</c> for the page, ordered by selected-page
/// ordinal when supplied by the keyset, otherwise by <c>DocumentId</c>.
/// </param>
/// <param name="TableRowsInDependencyOrder">
/// Per-table hydrated rows in deterministic dependency order (root table first, then children).
/// </param>
/// <param name="DescriptorRowsInPlanOrder">
/// Per-plan descriptor URI rows in deterministic compiled-plan order.
/// </param>
public sealed record HydratedPage(
    long? TotalCount,
    IReadOnlyList<DocumentMetadataRow> DocumentMetadata,
    IReadOnlyList<HydratedTableRows> TableRowsInDependencyOrder,
    IReadOnlyList<HydratedDescriptorRows> DescriptorRowsInPlanOrder
)
{
    /// <summary>
    /// Hydrated rows from the optional document-reference auxiliary lookup. Populated when the
    /// resource read plan carries a non-null <c>DocumentReferenceLookup</c>; otherwise
    /// <see langword="null"/>. Drives <c>link.rel</c> / <c>link.href</c> emission in
    /// reconstitution.
    /// </summary>
    public HydratedDocumentReferenceLookup? DocumentReferenceLookup { get; init; }

    /// <summary>
    /// The maximum <c>DocumentId</c> in the selected page keyset, or <see langword="null"/> when page
    /// selection was skipped or selected no keys — including authorization, preprocessing, and planner
    /// early-empty paths, and zero-size pages.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of the hydrated body: every selected row may be deleted before
    /// hydration completes, so this can be non-null while the body is empty. A body-derived boundary
    /// would stall a cursor walk on the last surviving document, or stop it entirely on an empty body.
    /// Populated from the ids the query keyset materialization returned; always null for a
    /// <see cref="PageKeysetSpec.Single"/> keyset, which performs no page selection because its
    /// single id comes from the caller.
    /// </remarks>
    public long? HighestSelectedDocumentId { get; init; }
}

/// <summary>
/// Discriminated union specifying which documents a hydration batch returns, and where their ids come
/// from. Whether those ids are materialized into a keyset table or filtered on directly is a separate
/// decision made when the batch is built.
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
    /// GET by already-selected page: the keyset is the authorized page of <c>DocumentId</c>s selected
    /// before response-body hydration.
    /// </summary>
    /// <param name="DocumentIds">The selected document ids to hydrate.</param>
    public sealed record SelectedPage(IReadOnlyList<long> DocumentIds) : PageKeysetSpec
    {
        public IReadOnlyList<long> DocumentIds { get; init; } =
            DocumentIds ?? throw new ArgumentNullException(nameof(DocumentIds));
    }

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
