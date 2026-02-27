// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Table-local reference-object projection metadata consumed by ordinal access over hydration select lists.
/// </summary>
/// <param name="Table">The table whose hydration result-set ordinals this metadata targets.</param>
/// <param name="BindingsInOrder">
/// Reference-object projection bindings in deterministic authoritative order.
/// </param>
public sealed record ReferenceIdentityProjectionTablePlan
{
    public ReferenceIdentityProjectionTablePlan(
        DbTableName Table,
        IEnumerable<ReferenceIdentityProjectionBinding> BindingsInOrder
    )
    {
        this.Table = Table;
        this.BindingsInOrder = PlanContractCollectionCloner.ToImmutableArray(
            BindingsInOrder,
            nameof(BindingsInOrder)
        );
    }

    public DbTableName Table { get; init; }

    public ImmutableArray<ReferenceIdentityProjectionBinding> BindingsInOrder { get; init; }
}

/// <summary>
/// One reference-object projection binding derived from <c>DocumentReferenceBindings</c>.
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
public sealed record ReferenceIdentityProjectionBinding
{
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
        this.IdentityFieldOrdinalsInOrder = PlanContractCollectionCloner.ToImmutableArray(
            IdentityFieldOrdinalsInOrder,
            nameof(IdentityFieldOrdinalsInOrder)
        );
    }

    public bool IsIdentityComponent { get; init; }

    public JsonPathExpression ReferenceObjectPath { get; init; }

    public QualifiedResourceName TargetResource { get; init; }

    public int FkColumnOrdinal { get; init; }

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
/// <param name="SelectByKeysetSql">
/// Parameterized SQL that emits descriptor projection rows for the current page keyset.
/// </param>
/// <param name="ResultShape">
/// Ordinal contract for descriptor projection result rows.
/// </param>
/// <param name="SourcesInOrder">
/// Descriptor FK source metadata in deterministic authoritative order.
/// </param>
public sealed record DescriptorProjectionPlan
{
    public DescriptorProjectionPlan(
        string SelectByKeysetSql,
        DescriptorProjectionResultShape ResultShape,
        IEnumerable<DescriptorProjectionSource> SourcesInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(SelectByKeysetSql);
        ArgumentNullException.ThrowIfNull(ResultShape);

        this.SelectByKeysetSql = SelectByKeysetSql;
        this.ResultShape = ResultShape;
        this.SourcesInOrder = PlanContractCollectionCloner.ToImmutableArray(
            SourcesInOrder,
            nameof(SourcesInOrder)
        );
    }

    public string SelectByKeysetSql { get; init; }

    public DescriptorProjectionResultShape ResultShape { get; init; }

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
