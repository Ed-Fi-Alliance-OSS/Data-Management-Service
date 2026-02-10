// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Identifies a configured project schema and its physical database schema.
/// </summary>
/// <param name="ProjectEndpointName">The stable API endpoint name (e.g., <c>ed-fi</c>).</param>
/// <param name="ProjectName">The logical project name (e.g., <c>Ed-Fi</c>).</param>
/// <param name="ProjectVersion">The project version label.</param>
/// <param name="IsExtensionProject">Whether the project is an extension.</param>
/// <param name="PhysicalSchema">The normalized physical schema name.</param>
public sealed record ProjectSchemaInfo(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject,
    DbSchemaName PhysicalSchema
);

/// <summary>
/// Classifies the discriminator strategy for descriptor resources.
/// </summary>
public enum DiscriminatorStrategy
{
    /// <summary>
    /// Use <c>dms.Document.ResourceKeyId</c> as the primary resource-type discriminator.
    /// </summary>
    ResourceKeyId,

    /// <summary>
    /// Use <c>dms.Descriptor.Discriminator</c> column as a secondary discriminator.
    /// </summary>
    DescriptorColumn,

    /// <summary>
    /// Both discriminator strategies are recorded for flexibility.
    /// </summary>
    Both,
}

/// <summary>
/// Defines the canonical descriptor column contract for the shared <c>dms.Descriptor</c> table.
/// </summary>
/// <param name="Namespace">The namespace column name.</param>
/// <param name="CodeValue">The code value column name.</param>
/// <param name="ShortDescription">The short description column name (optional).</param>
/// <param name="Description">The description column name (optional).</param>
/// <param name="EffectiveBeginDate">The effective begin date column name (optional).</param>
/// <param name="EffectiveEndDate">The effective end date column name (optional).</param>
/// <param name="Discriminator">The discriminator column name (optional).</param>
public sealed record DescriptorColumnContract(
    DbColumnName Namespace,
    DbColumnName CodeValue,
    DbColumnName? ShortDescription,
    DbColumnName? Description,
    DbColumnName? EffectiveBeginDate,
    DbColumnName? EffectiveEndDate,
    DbColumnName? Discriminator
);

/// <summary>
/// Metadata for descriptor resources stored in the shared <c>dms.Descriptor</c> table.
/// </summary>
/// <param name="ColumnContract">The descriptor column contract.</param>
/// <param name="DiscriminatorStrategy">The discriminator strategy for resource-type identification.</param>
public sealed record DescriptorMetadata(
    DescriptorColumnContract ColumnContract,
    DiscriminatorStrategy DiscriminatorStrategy
);

/// <summary>
/// The derived relational model for a concrete resource.
/// </summary>
/// <param name="ResourceKey">The resource key entry for the resource.</param>
/// <param name="StorageKind">The storage strategy for the resource.</param>
/// <param name="RelationalModel">The relational model inventory for the resource.</param>
/// <param name="DescriptorMetadata">
/// Descriptor-specific metadata when <paramref name="StorageKind"/> is <see cref="ResourceStorageKind.SharedDescriptorTable"/>.
/// </param>
public sealed record ConcreteResourceModel(
    ResourceKeyEntry ResourceKey,
    ResourceStorageKind StorageKind,
    RelationalResourceModel RelationalModel,
    DescriptorMetadata? DescriptorMetadata = null
);

/// <summary>
/// Derived identity table metadata for an abstract resource.
/// </summary>
/// <param name="AbstractResourceKey">The abstract resource key entry.</param>
/// <param name="TableModel">The identity table model.</param>
public sealed record AbstractIdentityTableInfo(ResourceKeyEntry AbstractResourceKey, DbTableModel TableModel);

/// <summary>
/// Derived union view metadata for an abstract resource.
/// </summary>
/// <param name="AbstractResourceKey">The abstract resource key entry.</param>
/// <param name="ViewName">The union view name.</param>
/// <param name="OutputColumnsInSelectOrder">
/// Output columns in deterministic select-list order.
/// </param>
/// <param name="UnionArmsInOrder">
/// Fully expanded union arms in deterministic <c>UNION ALL</c> order.
/// </param>
public sealed record AbstractUnionViewInfo(
    ResourceKeyEntry AbstractResourceKey,
    DbTableName ViewName,
    IReadOnlyList<AbstractUnionViewOutputColumn> OutputColumnsInSelectOrder,
    IReadOnlyList<AbstractUnionViewArm> UnionArmsInOrder
);

/// <summary>
/// Output-column metadata for an abstract union view.
/// </summary>
/// <param name="ColumnName">The view output column identifier.</param>
/// <param name="ScalarType">The canonical scalar type emitted for this column.</param>
/// <param name="SourceJsonPath">
/// Optional source JSONPath for diagnostics when the column is sourced from resource schema metadata.
/// </param>
/// <param name="TargetResource">
/// Optional referenced resource type for diagnostics when the column models reference/descriptors.
/// </param>
public sealed record AbstractUnionViewOutputColumn(
    DbColumnName ColumnName,
    RelationalScalarType ScalarType,
    JsonPathExpression? SourceJsonPath,
    QualifiedResourceName? TargetResource
);

/// <summary>
/// A single concrete-member arm in an abstract union view.
/// </summary>
/// <param name="ConcreteMemberResourceKey">The concrete member resource key entry.</param>
/// <param name="FromTable">The concrete member root table used in the arm <c>FROM</c> clause.</param>
/// <param name="ProjectionExpressionsInSelectOrder">
/// Projection expressions aligned 1:1 with <see cref="AbstractUnionViewInfo.OutputColumnsInSelectOrder"/>.
/// </param>
public sealed record AbstractUnionViewArm(
    ResourceKeyEntry ConcreteMemberResourceKey,
    DbTableName FromTable,
    IReadOnlyList<AbstractUnionViewProjectionExpression> ProjectionExpressionsInSelectOrder
);

/// <summary>
/// SQL-free projection expression model for abstract union-view arm select lists.
/// </summary>
public abstract record AbstractUnionViewProjectionExpression
{
    /// <summary>
    /// Projects a source column from the arm's <see cref="AbstractUnionViewArm.FromTable"/>.
    /// </summary>
    /// <param name="ColumnName">The concrete source column.</param>
    public sealed record SourceColumn(DbColumnName ColumnName) : AbstractUnionViewProjectionExpression;

    /// <summary>
    /// Projects a string literal.
    /// </summary>
    /// <param name="Value">The literal string value.</param>
    public sealed record StringLiteral(string Value) : AbstractUnionViewProjectionExpression;
}

/// <summary>
/// Classifies the logical intent of a derived index.
/// </summary>
public enum DbIndexKind
{
    /// <summary>
    /// Index implied by a primary key.
    /// </summary>
    PrimaryKey,

    /// <summary>
    /// Index implied by a UNIQUE constraint.
    /// </summary>
    UniqueConstraint,

    /// <summary>
    /// Non-unique index required by the foreign key support policy.
    /// </summary>
    ForeignKeySupport,

    /// <summary>
    /// Explicit non-query indexes defined by the design.
    /// </summary>
    Explicit,
}

/// <summary>
/// Represents a physical database index name.
/// </summary>
/// <param name="Value">The index identifier.</param>
public readonly record struct DbIndexName(string Value);

/// <summary>
/// Derived index inventory entry.
/// </summary>
/// <param name="Name">The index name.</param>
/// <param name="Table">The indexed table.</param>
/// <param name="KeyColumns">Index key columns in order.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
/// <param name="Kind">The index intent classification.</param>
public sealed record DbIndexInfo(
    DbIndexName Name,
    DbTableName Table,
    IReadOnlyList<DbColumnName> KeyColumns,
    bool IsUnique,
    DbIndexKind Kind
);

/// <summary>
/// Classifies the logical intent of a derived trigger.
/// </summary>
public enum DbTriggerKind
{
    /// <summary>
    /// Trigger that stamps document representation/identity versions.
    /// </summary>
    DocumentStamping,

    /// <summary>
    /// Trigger that maintains referential identity for concrete resources.
    /// </summary>
    ReferentialIdentityMaintenance,

    /// <summary>
    /// Trigger that maintains abstract identity tables from concrete roots.
    /// </summary>
    AbstractIdentityMaintenance,

    /// <summary>
    /// Trigger-based fallback for identity propagation when cascade paths are constrained.
    /// </summary>
    IdentityPropagationFallback,
}

/// <summary>
/// Represents a physical database trigger name.
/// </summary>
/// <param name="Value">The trigger identifier.</param>
public readonly record struct DbTriggerName(string Value);

/// <summary>
/// Derived trigger inventory entry.
/// </summary>
/// <param name="Name">The trigger name.</param>
/// <param name="Table">The owning table.</param>
/// <param name="Kind">The trigger intent classification.</param>
/// <param name="KeyColumns">Key columns associated with trigger behavior.</param>
public sealed record DbTriggerInfo(
    DbTriggerName Name,
    DbTableName Table,
    DbTriggerKind Kind,
    IReadOnlyList<DbColumnName> KeyColumns
);

/// <summary>
/// Dialect-aware derived relational model inventory for an effective schema set.
/// </summary>
/// <param name="EffectiveSchema">The effective schema metadata and resource key seed.</param>
/// <param name="Dialect">The target SQL dialect.</param>
/// <param name="ProjectSchemasInEndpointOrder">Project schemas ordered by endpoint name.</param>
/// <param name="ConcreteResourcesInNameOrder">Concrete resources ordered by (project, resource) name.</param>
/// <param name="AbstractIdentityTablesInNameOrder">Abstract identity tables ordered by resource name.</param>
/// <param name="AbstractUnionViewsInNameOrder">Abstract union views ordered by resource name.</param>
/// <param name="IndexesInCreateOrder">
/// Index inventory in canonical deterministic order by schema, table, and name; insertion order is not preserved and
/// the sequence is not a dependency-aware DDL execution order.
/// </param>
/// <param name="TriggersInCreateOrder">
/// Trigger inventory in canonical deterministic order by schema, table, and name; insertion order is not preserved and
/// the sequence is not a dependency-aware DDL execution order.
/// </param>
public sealed record DerivedRelationalModelSet(
    EffectiveSchemaInfo EffectiveSchema,
    SqlDialect Dialect,
    IReadOnlyList<ProjectSchemaInfo> ProjectSchemasInEndpointOrder,
    IReadOnlyList<ConcreteResourceModel> ConcreteResourcesInNameOrder,
    IReadOnlyList<AbstractIdentityTableInfo> AbstractIdentityTablesInNameOrder,
    IReadOnlyList<AbstractUnionViewInfo> AbstractUnionViewsInNameOrder,
    IReadOnlyList<DbIndexInfo> IndexesInCreateOrder,
    IReadOnlyList<DbTriggerInfo> TriggersInCreateOrder
);
