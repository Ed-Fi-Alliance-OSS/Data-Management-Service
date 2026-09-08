// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

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
)
{
    /// <summary>
    /// Securable element metadata extracted from ApiSchema.json for authorization path resolution.
    /// </summary>
    public ResourceSecurableElements SecurableElements { get; init; } = ResourceSecurableElements.Empty;

    /// <summary>
    /// Query-field metadata extracted from ApiSchema.json for relational GET-many compilation.
    /// </summary>
#pragma warning disable IDE0055
    public IReadOnlyDictionary<
        string,
        RelationalQueryFieldMapping
    > QueryFieldMappingsByQueryField { get; init; } =
        new Dictionary<string, RelationalQueryFieldMapping>(StringComparer.Ordinal);
#pragma warning restore IDE0055

    /// <summary>
    /// The superclass resource identity for subclass resources, extracted from ApiSchema.json
    /// (<c>superclassProjectName</c>/<c>superclassResourceName</c>). Null for non-subclass resources.
    /// Reference-identity alias query compilation uses this to validate superclass-derived
    /// query field paths against schema metadata.
    /// </summary>
    public QualifiedResourceName? SuperclassResource { get; init; }
}

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
/// <param name="IsDescriptorReference">
/// Whether the output column stores a descriptor document id that must be projected as the descriptor URI when
/// used as an API identity value.
/// </param>
public sealed record AbstractUnionViewOutputColumn(
    DbColumnName ColumnName,
    RelationalScalarType ScalarType,
    JsonPathExpression? SourceJsonPath,
    QualifiedResourceName? TargetResource,
    bool IsDescriptorReference = false
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
    /// <param name="SourceType">
    /// The source column's scalar type on the concrete member table. When provided and different
    /// from the view's canonical output type, the emitter wraps the column in an explicit CAST
    /// to ensure cross-member type normalization in the <c>UNION ALL</c>.
    /// </param>
    public sealed record SourceColumn(DbColumnName ColumnName, RelationalScalarType? SourceType = null)
        : AbstractUnionViewProjectionExpression;

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

    /// <summary>
    /// Index required by relationship-based or namespace-based authorization (see auth.md).
    /// </summary>
    Authorization,
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
/// <param name="IncludeColumns">
/// Optional non-key columns to include in the index leaf pages (INCLUDE clause).
/// </param>
public sealed record DbIndexInfo(
    DbIndexName Name,
    DbTableName Table,
    IReadOnlyList<DbColumnName> KeyColumns,
    bool IsUnique,
    DbIndexKind Kind,
    IReadOnlyList<DbColumnName>? IncludeColumns = null
);

/// <summary>
/// Discriminated union for trigger-kind-specific parameters. Each subtype carries exactly
/// the fields required by its trigger kind, providing compile-time type safety instead of
/// nullable optional parameters.
/// </summary>
public abstract record TriggerKindParameters
{
    private TriggerKindParameters() { }

    /// <summary>
    /// Parameters for triggers that stamp document representation/identity versions.
    /// </summary>
    /// <param name="ChangeTracking">
    /// The change-tracking attachment when this stamping trigger also populates a tracked-change table
    /// (tombstones and key-change rows). Non-null on root-table stamping triggers whose resource has a
    /// derived tracked-change table, and on the shared <c>dms.Descriptor</c> stamping trigger when a
    /// shared descriptor tracked-change table is derived. Null for child / collection / <c>_ext</c>
    /// stamping triggers and on models with no corresponding tracked-change table. The key-change
    /// workset is not duplicated here; it is the owning trigger's
    /// <see cref="DbTriggerInfo.IdentityProjectionColumns"/>.
    /// </param>
    public sealed record DocumentStamping(TrackedChangeAttachment? ChangeTracking = null)
        : TriggerKindParameters;

    /// <summary>
    /// Parameters for triggers that maintain referential identity for concrete resources.
    /// </summary>
    /// <param name="ResourceKeyId">The resource key ID for UUIDv5 computation.</param>
    /// <param name="ProjectName">The project name for UUIDv5 computation.</param>
    /// <param name="ResourceName">The resource name for UUIDv5 computation.</param>
    /// <param name="IdentityElements">Identity element mappings for UUIDv5 computation.</param>
    /// <param name="SuperclassAlias">
    /// Superclass alias information for subclass resources. <c>null</c> for non-subclass resources.
    /// </param>
    public sealed record ReferentialIdentityMaintenance(
        short ResourceKeyId,
        string ProjectName,
        string ResourceName,
        IReadOnlyList<IdentityElementMapping> IdentityElements,
        SuperclassAliasInfo? SuperclassAlias = null
    ) : TriggerKindParameters;

    /// <summary>
    /// Parameters for triggers that maintain abstract identity tables from concrete roots.
    /// </summary>
    /// <param name="TargetTable">The abstract identity table being maintained.</param>
    /// <param name="TargetColumnMappings">Column mappings from the source table to the target table.</param>
    /// <param name="DiscriminatorValue">The discriminator value written to the abstract identity table.</param>
    public sealed record AbstractIdentityMaintenance(
        DbTableName TargetTable,
        IReadOnlyList<TriggerColumnMapping> TargetColumnMappings,
        string DiscriminatorValue
    ) : TriggerKindParameters;

    /// <summary>
    /// Parameters for triggers that maintain the <c>auth.EducationOrganizationIdToEducationOrganizationId</c>
    /// hierarchy table. One trigger is emitted per (entity, event) pair.
    /// </summary>
    /// <param name="Entity">The concrete EducationOrganization entity metadata.</param>
    /// <param name="TriggerEvent">The DML event this trigger fires on.</param>
    public sealed record AuthHierarchyMaintenance(
        AuthEdOrgEntity Entity,
        AuthHierarchyTriggerEvent TriggerEvent
    ) : TriggerKindParameters;
}

/// <summary>
/// Represents a physical database trigger name.
/// </summary>
/// <param name="Value">The trigger identifier.</param>
public readonly record struct DbTriggerName(string Value);

/// <summary>
/// Maps a root table column to its identity JSON path for UUIDv5 computation.
/// </summary>
/// <param name="Column">The physical column on the root table.</param>
/// <param name="IdentityJsonPath">The canonical JSON path label used in the UUIDv5 hash string.</param>
/// <param name="ScalarType">The scalar type metadata for type-aware string formatting in hash expressions.</param>
/// <param name="IsDescriptorReference">
/// Indicates that <paramref name="Column"/> stores a descriptor document ID that must be converted back
/// to the descriptor URI before UUIDv5 hash computation.
/// </param>
public sealed record IdentityElementMapping(
    DbColumnName Column,
    string IdentityJsonPath,
    RelationalScalarType ScalarType,
    bool IsDescriptorReference = false
);

/// <summary>
/// Superclass alias information for subclass resources that must also maintain referential identity
/// under their superclass resource key.
/// </summary>
/// <param name="ResourceKeyId">The superclass resource key ID.</param>
/// <param name="ProjectName">The superclass project name.</param>
/// <param name="ResourceName">The superclass resource name.</param>
/// <param name="IdentityElements">Identity element mappings for the superclass identity.</param>
public sealed record SuperclassAliasInfo(
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    IReadOnlyList<IdentityElementMapping> IdentityElements
);

/// <summary>
/// Maps a source column on the trigger's owning table to a target column on the maintenance table.
/// </summary>
/// <param name="SourceColumn">The column on the trigger's owning table.</param>
/// <param name="TargetColumn">The corresponding column on the target table.</param>
public sealed record TriggerColumnMapping(DbColumnName SourceColumn, DbColumnName TargetColumn);

/// <summary>
/// Derived trigger inventory entry.
/// </summary>
/// <param name="Name">The trigger name.</param>
/// <param name="Table">The owning table.</param>
/// <param name="KeyColumns">Key columns used to identify the affected <c>DocumentId</c>.</param>
/// <param name="IdentityProjectionColumns">
/// Columns whose change affects the resource identity projection. For
/// <see cref="TriggerKindParameters.DocumentStamping"/> triggers on root tables, these are the columns
/// that should additionally bump <c>IdentityVersion</c>. For
/// <see cref="TriggerKindParameters.ReferentialIdentityMaintenance"/> and
/// <see cref="TriggerKindParameters.AbstractIdentityMaintenance"/> triggers, these are the columns that
/// trigger recomputation. Empty for child/extension table stamping triggers.
/// </param>
/// <param name="Parameters">The trigger-kind-specific parameters.</param>
/// <param name="MirrorStampTargetTable">
/// The table whose <c>ContentVersion</c> / <c>ContentLastModifiedAt</c> mirror columns this trigger
/// stamps. Non-null for every <see cref="TriggerKindParameters.DocumentStamping"/> entry: the owning
/// resource root table (the source table itself for root-table triggers; the owning resource root for
/// child / collection / <c>_ext</c> triggers). Null for non-stamping trigger kinds.
/// </param>
public sealed record DbTriggerInfo(
    DbTriggerName Name,
    DbTableName Table,
    IReadOnlyList<DbColumnName> KeyColumns,
    IReadOnlyList<DbColumnName> IdentityProjectionColumns,
    TriggerKindParameters Parameters,
    DbTableName? MirrorStampTargetTable = null
);

/// <summary>
/// Change-tracking attachment for a <see cref="TriggerKindParameters.DocumentStamping"/> trigger that
/// also writes tombstones and key-change rows. References the tracked-change table by name (mirroring
/// <see cref="DbTriggerInfo.MirrorStampTargetTable"/>) rather than embedding the
/// <see cref="TrackedChangeTableInfo"/>, keeping the manifest acyclic.
/// </summary>
/// <param name="TrackedChangeTable">The <c>tracked_changes_*</c> table this trigger populates.</param>
public sealed record TrackedChangeAttachment(DbTableName TrackedChangeTable);

/// <summary>
/// Fixed-by-role system column on a tracked-change table (<c>Id</c>, <c>ChangeVersion</c>,
/// <c>CreatedAt</c>, and—on the shared descriptor table—<c>Discriminator</c>). These are determined by
/// role rather than by ApiSchema value metadata; dialect emitters render the appropriate type/default.
/// </summary>
/// <param name="Role">The system column role.</param>
/// <param name="ColumnName">The physical column name.</param>
/// <param name="ScalarType">
/// The scalar type metadata, or <c>null</c> when the type is determined entirely by
/// <paramref name="Role"/> and has no <see cref="ScalarKind"/> representation. This is the case for
/// <see cref="TrackedChangeSystemColumnRole.Id"/>, whose type is PostgreSQL <c>uuid</c> / SQL Server
/// <c>uniqueidentifier</c>; dialect emitters render it by role.
/// </param>
/// <param name="IsNullable">Whether the column is nullable.</param>
/// <param name="IsPrimaryKey">Whether the column participates in the table's primary key.</param>
public sealed record TrackedChangeSystemColumnInfo(
    TrackedChangeSystemColumnRole Role,
    DbColumnName ColumnName,
    RelationalScalarType? ScalarType,
    bool IsNullable,
    bool IsPrimaryKey
);

/// <summary>
/// A tracked old/new value column pair on a tracked-change table. Each tracked value contributes one
/// entry that materializes as an <c>Old*</c> column and a <c>New*</c> column; tombstones populate only
/// the old values while key-change rows populate the new values when present.
/// </summary>
/// <param name="OldColumnName">The <c>Old*</c> column name.</param>
/// <param name="NewColumnName">The <c>New*</c> column name.</param>
/// <param name="SourceJsonPath">The canonical JSONPath of the tracked source value.</param>
/// <param name="CanonicalStorageColumn">
/// The canonical storage column when the source participates in key unification; null otherwise.
/// </param>
/// <param name="IsOldColumnNullable">
/// Nullability of the <c>Old*</c> column, following the tracked source value's nullability.
/// </param>
/// <param name="IsNewColumnNullable">
/// Nullability of the <c>New*</c> column. Normally <c>true</c> because delete tombstones leave
/// <c>New*</c> columns null; key-change rows populate them when present.
/// </param>
/// <param name="ScalarType">The scalar type metadata shared by the old and new columns.</param>
/// <param name="Role">The materialization shape of the column (scalar, descriptor part, or person id).</param>
/// <param name="Origin">The authorization purpose(s) this column serves (identity and/or securable element).</param>
/// <param name="DescriptorJoinName">
/// The <see cref="TrackedChangeDescriptorJoinInfo.DescriptorJoinName"/> this column resolves through,
/// when <paramref name="Role"/> is a descriptor part; null otherwise.
/// </param>
/// <param name="PersonJoinName">
/// The <see cref="TrackedChangePersonJoinInfo.PersonJoinName"/> this column resolves through, when
/// <paramref name="Role"/> is <see cref="TrackedChangeColumnRole.PersonDocumentId"/>; null otherwise.
/// </param>
public sealed record TrackedChangeColumnInfo(
    DbColumnName OldColumnName,
    DbColumnName NewColumnName,
    string SourceJsonPath,
    DbColumnName? CanonicalStorageColumn,
    bool IsOldColumnNullable,
    bool IsNewColumnNullable,
    RelationalScalarType ScalarType,
    TrackedChangeColumnRole Role,
    TrackedChangeColumnOrigin Origin,
    string? DescriptorJoinName = null,
    string? PersonJoinName = null
);

/// <summary>
/// A table-level join from a tracked-change table to <c>dms.Descriptor</c> used by trigger emitters to
/// materialize a descriptor reference's <c>Namespace</c> and <c>CodeValue</c> for the old and new row
/// images. Tracked-change value columns reference this join by <paramref name="DescriptorJoinName"/>
/// rather than duplicating the join definition.
/// </summary>
/// <param name="DescriptorJoinName">The stable join name referenced by descriptor value columns.</param>
/// <param name="SourceColumn">The descriptor FK column on the live source table (e.g. <c>*_DescriptorId</c>).</param>
/// <param name="DescriptorResource">The descriptor resource type expected at this join.</param>
public sealed record TrackedChangeDescriptorJoinInfo(
    string DescriptorJoinName,
    DbColumnName SourceColumn,
    QualifiedResourceName DescriptorResource
);

/// <summary>
/// A table-level join path from a tracked-change table to a person (Student/Contact/Staff) resource
/// root, used by trigger emitters to materialize the person <c>DocumentId</c> for the old and new row
/// images. The person <c>DocumentId</c> value column references this join by
/// <paramref name="PersonJoinName"/> rather than duplicating the join definition.
/// </summary>
/// <param name="PersonJoinName">The stable join name referenced by the person <c>DocumentId</c> column.</param>
/// <param name="PersonKind">The kind of person resource this join reaches.</param>
/// <param name="JoinPath">The resource-table join chain reaching the person resource root.</param>
public sealed record TrackedChangePersonJoinInfo(
    string PersonJoinName,
    SecurableElementKind PersonKind,
    IReadOnlyList<ColumnPathStep> JoinPath
);

/// <summary>
/// Derived tracked-change table inventory entry (<c>tracked_changes_*</c>). Carries the SQL-free
/// semantics dialect emitters and runtime Change Query planners consume mechanically.
/// </summary>
/// <param name="Table">The tracked-change table (<c>tracked_changes_&lt;project&gt;.&lt;resource&gt;</c>).</param>
/// <param name="Kind">Whether this is a resource, concrete-abstract, or shared descriptor table.</param>
/// <param name="SourceTable">The live source table whose changes this table tracks.</param>
/// <param name="ValueColumnsInTableOrder">The tracked old/new value columns in table order.</param>
/// <param name="SystemColumns">
/// The fixed-by-role system columns (<c>Id</c>, <c>ChangeVersion</c>, <c>CreatedAt</c>, and
/// <c>Discriminator</c> for the shared descriptor table).
/// </param>
/// <param name="PrimaryKeyColumns">
/// The primary-key columns. <c>[ChangeVersion]</c> in the current design; carried here so renderers do
/// not hardcode it.
/// </param>
/// <param name="DescriptorJoins">Table-level descriptor joins referenced by descriptor value columns.</param>
/// <param name="PersonJoins">Table-level person joins referenced by person <c>DocumentId</c> columns.</param>
public sealed record TrackedChangeTableInfo(
    DbTableName Table,
    TrackedChangeTableKind Kind,
    DbTableName SourceTable,
    IReadOnlyList<TrackedChangeColumnInfo> ValueColumnsInTableOrder,
    IReadOnlyList<TrackedChangeSystemColumnInfo> SystemColumns,
    IReadOnlyList<DbColumnName> PrimaryKeyColumns,
    IReadOnlyList<TrackedChangeDescriptorJoinInfo> DescriptorJoins,
    IReadOnlyList<TrackedChangePersonJoinInfo> PersonJoins
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
/// <param name="AuthEdOrgHierarchy">
/// Optional EducationOrganization hierarchy for auth DDL emission. <c>null</c> when no abstract
/// EducationOrganization resource exists in the schema.
/// </param>
/// <param name="TrackedChangeTablesInNameOrder">
/// Tracked-change table inventory (<c>tracked_changes_*</c>) in canonical deterministic order by
/// table schema and name. The constructor argument is optional; the exposed property is never null —
/// it normalizes to an empty list when omitted, so consumers (DMS-1177 rendering, runtime planners)
/// can iterate it without a null check.
/// </param>
public sealed record DerivedRelationalModelSet(
    EffectiveSchemaInfo EffectiveSchema,
    SqlDialect Dialect,
    IReadOnlyList<ProjectSchemaInfo> ProjectSchemasInEndpointOrder,
    IReadOnlyList<ConcreteResourceModel> ConcreteResourcesInNameOrder,
    IReadOnlyList<AbstractIdentityTableInfo> AbstractIdentityTablesInNameOrder,
    IReadOnlyList<AbstractUnionViewInfo> AbstractUnionViewsInNameOrder,
    IReadOnlyList<DbIndexInfo> IndexesInCreateOrder,
    IReadOnlyList<DbTriggerInfo> TriggersInCreateOrder,
    AuthEdOrgHierarchy? AuthEdOrgHierarchy = null,
    IReadOnlyList<TrackedChangeTableInfo>? TrackedChangeTablesInNameOrder = null
)
{
    /// <summary>
    /// Tracked-change table inventory in canonical deterministic order by table schema and name.
    /// Never null — empty when no resources or descriptors require tracked-change tables.
    /// </summary>
    public IReadOnlyList<TrackedChangeTableInfo> TrackedChangeTablesInNameOrder { get; init; } =
        TrackedChangeTablesInNameOrder ?? [];
}
