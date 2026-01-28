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
/// The derived relational model for a concrete resource.
/// </summary>
/// <param name="ResourceKey">The resource key entry for the resource.</param>
/// <param name="StorageKind">The storage strategy for the resource.</param>
/// <param name="RelationalModel">The relational model inventory for the resource.</param>
public sealed record ConcreteResourceModel(
    ResourceKeyEntry ResourceKey,
    ResourceStorageKind StorageKind,
    RelationalResourceModel RelationalModel
);

/// <summary>
/// Derived identity table metadata for an abstract resource.
/// </summary>
/// <param name="AbstractResourceKey">The abstract resource key entry.</param>
/// <param name="Table">The identity table name.</param>
/// <param name="ColumnsInIdentityOrder">Identity columns in key order.</param>
/// <param name="Constraints">Constraints applied to the identity table.</param>
public sealed record AbstractIdentityTableInfo(
    ResourceKeyEntry AbstractResourceKey,
    DbTableName Table,
    IReadOnlyList<DbColumnModel> ColumnsInIdentityOrder,
    IReadOnlyList<TableConstraint> Constraints
);

/// <summary>
/// Derived union view metadata for an abstract resource.
/// </summary>
/// <param name="AbstractResourceKey">The abstract resource key entry.</param>
/// <param name="ViewName">The union view name.</param>
/// <param name="ColumnsInIdentityOrder">Identity columns in key order.</param>
public sealed record AbstractUnionViewInfo(
    ResourceKeyEntry AbstractResourceKey,
    DbTableName ViewName,
    IReadOnlyList<DbColumnModel> ColumnsInIdentityOrder
);

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
/// Index inventory in canonical deterministic order by schema, table, and name; insertion order is not preserved.
/// </param>
/// <param name="TriggersInCreateOrder">
/// Trigger inventory in canonical deterministic order by schema, table, and name; insertion order is not preserved.
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
