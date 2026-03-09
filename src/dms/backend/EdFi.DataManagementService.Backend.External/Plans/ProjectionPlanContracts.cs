// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Table-local reference-object projection metadata consumed by ordinal access over hydration select lists.
/// </summary>
/// <remarks>
/// This metadata targets the hydration result-set select-list ordinals for a specific table.
/// </remarks>
public sealed record ReferenceIdentityProjectionTablePlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReferenceIdentityProjectionTablePlan" /> record.
    /// </summary>
    /// <param name="Table">The table whose hydration result-set ordinals this metadata targets.</param>
    /// <param name="BindingsInOrder">Reference-object projection bindings in deterministic authoritative order.</param>
    public ReferenceIdentityProjectionTablePlan(
        DbTableName Table,
        IEnumerable<ReferenceIdentityProjectionBinding> BindingsInOrder
    )
    {
        this.Table = Table;
        this.BindingsInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            BindingsInOrder,
            nameof(BindingsInOrder)
        );
    }

    /// <summary>
    /// The table whose hydration result-set ordinals this metadata targets.
    /// </summary>
    public DbTableName Table { get; init; }

    /// <summary>
    /// Reference-object projection bindings in deterministic authoritative order.
    /// </summary>
    public ImmutableArray<ReferenceIdentityProjectionBinding> BindingsInOrder { get; init; }
}

/// <summary>
/// One reference-object projection binding derived from <c>DocumentReferenceBindings</c>.
/// </summary>
public sealed record ReferenceIdentityProjectionBinding
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReferenceIdentityProjectionBinding" /> record.
    /// </summary>
    /// <param name="IsIdentityComponent">
    /// Indicates whether the reference participates in the resource identity projection.
    /// </param>
    /// <param name="ReferenceObjectPath">Reference-object path to materialize.</param>
    /// <param name="TargetResource">The referenced resource type.</param>
    /// <param name="FkColumnOrdinal">
    /// Zero-based ordinal for the local <c>..._DocumentId</c> column in the hydration select list.
    /// </param>
    /// <param name="IdentityFieldOrdinalsInOrder">
    /// Identity field projection metadata in deterministic field order.
    /// </param>
    public ReferenceIdentityProjectionBinding(
        bool IsIdentityComponent,
        JsonPathExpression ReferenceObjectPath,
        QualifiedResourceName TargetResource,
        int FkColumnOrdinal,
        IEnumerable<ReferenceIdentityProjectionFieldOrdinal> IdentityFieldOrdinalsInOrder
    )
    {
        this.IsIdentityComponent = IsIdentityComponent;
        this.ReferenceObjectPath = ReferenceObjectPath;
        this.TargetResource = TargetResource;
        this.FkColumnOrdinal = FkColumnOrdinal;
        this.IdentityFieldOrdinalsInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            IdentityFieldOrdinalsInOrder,
            nameof(IdentityFieldOrdinalsInOrder)
        );
    }

    /// <summary>
    /// Indicates whether the reference participates in resource identity projection.
    /// </summary>
    public bool IsIdentityComponent { get; init; }

    /// <summary>
    /// The reference-object path to materialize in the reconstituted JSON document.
    /// </summary>
    public JsonPathExpression ReferenceObjectPath { get; init; }

    /// <summary>
    /// The referenced resource type.
    /// </summary>
    public QualifiedResourceName TargetResource { get; init; }

    /// <summary>
    /// Zero-based hydration select-list ordinal for the local <c>..._DocumentId</c> FK column.
    /// </summary>
    public int FkColumnOrdinal { get; init; }

    /// <summary>
    /// Identity field ordinal metadata in deterministic field order.
    /// </summary>
    public ImmutableArray<ReferenceIdentityProjectionFieldOrdinal> IdentityFieldOrdinalsInOrder { get; init; }
}

/// <summary>
/// Ordinal projection metadata for one reference identity field.
/// </summary>
/// <param name="ReferenceJsonPath">Reference identity field path in the materialized JSON object.</param>
/// <param name="ColumnOrdinal">Zero-based hydration select-list ordinal for this field's value column.</param>
public sealed record ReferenceIdentityProjectionFieldOrdinal(
    JsonPathExpression ReferenceJsonPath,
    int ColumnOrdinal
);

/// <summary>
/// Descriptor projection contract for page-batched URI materialization.
/// </summary>
public sealed record DescriptorProjectionPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DescriptorProjectionPlan" /> record.
    /// </summary>
    /// <param name="SelectByKeysetSql">
    /// Parameterized SQL that emits descriptor projection rows for the current page keyset.
    /// </param>
    /// <param name="ResultShape">Ordinal contract for descriptor projection result rows.</param>
    /// <param name="SourcesInOrder">Descriptor FK source metadata in deterministic authoritative order.</param>
    public DescriptorProjectionPlan(
        string SelectByKeysetSql,
        DescriptorProjectionResultShape ResultShape,
        IEnumerable<DescriptorProjectionSource> SourcesInOrder
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
    }

    /// <summary>
    /// Parameterized SQL that emits descriptor projection rows for the current page keyset.
    /// </summary>
    public string SelectByKeysetSql { get; init; }

    /// <summary>
    /// Ordinal contract describing the descriptor projection result row shape.
    /// </summary>
    public DescriptorProjectionResultShape ResultShape { get; init; }

    /// <summary>
    /// Descriptor FK source metadata in deterministic authoritative order.
    /// </summary>
    public ImmutableArray<DescriptorProjectionSource> SourcesInOrder { get; init; }
}

/// <summary>
/// Ordinal contract for descriptor projection result rows.
/// </summary>
/// <param name="DescriptorIdOrdinal">Zero-based <c>DescriptorId</c> ordinal in the descriptor projection result row.</param>
/// <param name="UriOrdinal">Zero-based <c>Uri</c> ordinal in the descriptor projection result row.</param>
public sealed record DescriptorProjectionResultShape(int DescriptorIdOrdinal, int UriOrdinal);

/// <summary>
/// Identifies one descriptor FK source consumed by descriptor URI projection.
/// </summary>
/// <param name="DescriptorValuePath">Descriptor value path associated with the source edge.</param>
/// <param name="Table">Owning table for the descriptor FK source.</param>
/// <param name="DescriptorResource">Descriptor resource type expected at this source path.</param>
/// <param name="DescriptorIdColumnOrdinal">
/// Zero-based ordinal for the source table descriptor-id column in the hydration select list.
/// </param>
public sealed record DescriptorProjectionSource(
    JsonPathExpression DescriptorValuePath,
    DbTableName Table,
    QualifiedResourceName DescriptorResource,
    int DescriptorIdColumnOrdinal
);
