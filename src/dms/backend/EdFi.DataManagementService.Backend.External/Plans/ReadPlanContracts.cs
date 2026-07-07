// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled read plan for a single resource.
/// </summary>
/// <remarks>
/// This contract carries hydration SQL plus deterministic ordinal metadata so executors never infer result shapes or
/// bindings by parsing SQL text.
/// </remarks>
public sealed record ResourceReadPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceReadPlan" /> record.
    /// </summary>
    /// <param name="Model">The derived relational model for the resource.</param>
    /// <param name="KeysetTable">Keyset-table contract used by hydration queries for this dialect/runtime.</param>
    /// <param name="TablePlansInDependencyOrder">Per-table read plans in deterministic dependency order.</param>
    /// <param name="ReferenceIdentityProjectionPlansInDependencyOrder">
    /// Table-local reference-identity projection metadata in deterministic table dependency order.
    /// </param>
    /// <param name="DescriptorProjectionPlansInOrder">
    /// Descriptor projection plans in deterministic execution order.
    /// </param>
    /// <param name="DocumentReferenceLookup">
    /// Optional page-batched document-reference auxiliary lookup plan. Present when the resource
    /// has at least one <see cref="DocumentReferenceBinding"/>; otherwise <see langword="null"/>.
    /// </param>
    public ResourceReadPlan(
        RelationalResourceModel Model,
        KeysetTableContract KeysetTable,
        IEnumerable<TableReadPlan> TablePlansInDependencyOrder,
        IEnumerable<ReferenceIdentityProjectionTablePlan> ReferenceIdentityProjectionPlansInDependencyOrder,
        IEnumerable<DescriptorProjectionPlan> DescriptorProjectionPlansInOrder,
        DocumentReferenceLookupPlan? DocumentReferenceLookup = null
    )
    {
        this.Model = PlanContractArgumentValidator.RequireNotNull(Model, nameof(Model));
        this.KeysetTable = PlanContractArgumentValidator.RequireNotNull(KeysetTable, nameof(KeysetTable));
        this.TablePlansInDependencyOrder = PlanContractArgumentValidator.RequireImmutableArray(
            TablePlansInDependencyOrder,
            nameof(TablePlansInDependencyOrder)
        );
        this.ReferenceIdentityProjectionPlansInDependencyOrder =
            PlanContractArgumentValidator.RequireImmutableArray(
                ReferenceIdentityProjectionPlansInDependencyOrder,
                nameof(ReferenceIdentityProjectionPlansInDependencyOrder)
            );
        this.DescriptorProjectionPlansInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            DescriptorProjectionPlansInOrder,
            nameof(DescriptorProjectionPlansInOrder)
        );
        this.DocumentReferenceLookup = DocumentReferenceLookup;
    }

    /// <summary>
    /// The derived relational model the plan was compiled from.
    /// </summary>
    public RelationalResourceModel Model { get; init; }

    /// <summary>
    /// Dialect-specific contract for the materialized keyset relation consumed by hydration SQL.
    /// </summary>
    public KeysetTableContract KeysetTable { get; init; }

    /// <summary>
    /// Per-table hydration plans in deterministic dependency order.
    /// </summary>
    public ImmutableArray<TableReadPlan> TablePlansInDependencyOrder { get; init; }

    /// <summary>
    /// Reference-object identity projection metadata in deterministic table dependency order.
    /// </summary>
    public ImmutableArray<ReferenceIdentityProjectionTablePlan> ReferenceIdentityProjectionPlansInDependencyOrder { get; init; }

    /// <summary>
    /// Descriptor URI projection plans in deterministic execution order.
    /// </summary>
    public ImmutableArray<DescriptorProjectionPlan> DescriptorProjectionPlansInOrder { get; init; }

    /// <summary>
    /// Optional page-batched document-reference auxiliary lookup plan. Drives the
    /// <c>(DocumentId, DocumentUuid, ResourceKeyId)</c> result set used by link-injection.
    /// <see langword="null"/> when the resource has no <see cref="DocumentReferenceBinding"/>.
    /// </summary>
    public DocumentReferenceLookupPlan? DocumentReferenceLookup { get; init; }
}

/// <summary>
/// Compiled read SQL for one table in a resource hydration flow.
/// </summary>
public sealed record TableReadPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableReadPlan" /> record.
    /// </summary>
    /// <param name="TableModel">The table shape model.</param>
    /// <param name="SelectByKeysetSql">
    /// Parameterized SQL that joins to a materialized keyset table and returns rows for the page.
    /// </param>
    /// <param name="SelectBySingleDocumentSql">
    /// Optional parameterized SQL that returns rows for one document without materializing a keyset table.
    /// </param>
    public TableReadPlan(
        DbTableModel TableModel,
        string SelectByKeysetSql,
        string? SelectBySingleDocumentSql = null
    )
    {
        ArgumentNullException.ThrowIfNull(TableModel);
        ArgumentNullException.ThrowIfNull(SelectByKeysetSql);

        this.TableModel = TableModel;
        this.SelectByKeysetSql = SelectByKeysetSql;
        this.SelectBySingleDocumentSql = SelectBySingleDocumentSql;
    }

    /// <summary>
    /// The table model the SQL targets.
    /// </summary>
    public DbTableModel TableModel { get; init; }

    /// <summary>
    /// Parameterized SQL that joins to a materialized keyset table and returns rows for the page.
    /// </summary>
    public string SelectByKeysetSql { get; init; }

    /// <summary>
    /// Optional parameterized SQL that returns rows for one document without materializing a keyset table.
    /// </summary>
    public string? SelectBySingleDocumentSql { get; init; }
}

/// <summary>
/// Names the materialized keyset table and its <c>DocumentId</c> column used by hydration SQL.
/// </summary>
/// <param name="Table">Keyset temporary table relation.</param>
/// <param name="DocumentIdColumnName">Keyset table <c>DocumentId</c> column name.</param>
public sealed record KeysetTableContract(SqlRelationRef.TempTable Table, DbColumnName DocumentIdColumnName);

/// <summary>
/// Page-batched document-reference auxiliary lookup plan. Emits a single result set of
/// <c>(DocumentId, DocumentUuid, ResourceKeyId)</c> rows, one per distinct
/// <c>..._DocumentId</c> value reachable from the page's source tables.
/// </summary>
public sealed record DocumentReferenceLookupPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentReferenceLookupPlan" /> record.
    /// </summary>
    /// <param name="SelectByKeysetSql">
    /// Parameterized SQL that emits document-reference lookup rows for the current page keyset.
    /// </param>
    /// <param name="ResultShape">Ordinal contract for the lookup result rows.</param>
    /// <param name="SourcesInOrder">Document-reference FK source metadata in deterministic dedup'd order.</param>
    /// <param name="SelectBySingleDocumentSql">
    /// Optional parameterized SQL that emits document-reference lookup rows for one document.
    /// </param>
    public DocumentReferenceLookupPlan(
        string SelectByKeysetSql,
        DocumentReferenceLookupResultShape ResultShape,
        IEnumerable<DocumentReferenceLookupSource> SourcesInOrder,
        string? SelectBySingleDocumentSql = null
    )
    {
        this.SelectByKeysetSql = PlanContractArgumentValidator.RequireNotNull(
            SelectByKeysetSql,
            nameof(SelectByKeysetSql)
        );
        this.ResultShape = PlanContractArgumentValidator.RequireNotNull(ResultShape, nameof(ResultShape));
        this.SourcesInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            SourcesInOrder,
            nameof(SourcesInOrder)
        );
        this.SelectBySingleDocumentSql = SelectBySingleDocumentSql;
    }

    /// <summary>
    /// Parameterized SQL that emits document-reference lookup rows for the current page keyset.
    /// </summary>
    public string SelectByKeysetSql { get; init; }

    /// <summary>
    /// Optional parameterized SQL that emits document-reference lookup rows for one document.
    /// </summary>
    public string? SelectBySingleDocumentSql { get; init; }

    /// <summary>
    /// Ordinal contract describing the lookup result row shape.
    /// </summary>
    public DocumentReferenceLookupResultShape ResultShape { get; init; }

    /// <summary>
    /// Document-reference FK source metadata in deterministic dedup'd order. Lets validators
    /// check FK columns without parsing SQL.
    /// </summary>
    public ImmutableArray<DocumentReferenceLookupSource> SourcesInOrder { get; init; }
}

/// <summary>
/// Ordinal contract for the document-reference lookup result rows. Always
/// <c>(0, 1, 2)</c> in V1 — exposed for symmetry with descriptor projection.
/// </summary>
/// <param name="DocumentIdOrdinal">Zero-based <c>DocumentId</c> ordinal.</param>
/// <param name="DocumentUuidOrdinal">Zero-based <c>DocumentUuid</c> ordinal.</param>
/// <param name="ResourceKeyIdOrdinal">Zero-based <c>ResourceKeyId</c> ordinal.</param>
public sealed record DocumentReferenceLookupResultShape(
    int DocumentIdOrdinal,
    int DocumentUuidOrdinal,
    int ResourceKeyIdOrdinal
);

/// <summary>
/// Identifies one document-reference FK source consumed by the auxiliary lookup. Mirrors
/// <see cref="DescriptorProjectionSource"/> so the validator can check
/// <c>(Table, FkColumn)</c> against hydration metadata without parsing SQL text.
/// </summary>
/// <param name="Table">Owning table for the document-reference FK source.</param>
/// <param name="FkColumn">Local <c>..._DocumentId</c> column name on the source table.</param>
public sealed record DocumentReferenceLookupSource(DbTableName Table, DbColumnName FkColumn);
