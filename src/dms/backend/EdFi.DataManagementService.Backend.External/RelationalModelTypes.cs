// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Identifies a resource by project and resource name.
/// </summary>
/// <param name="ProjectName">The logical project name (e.g., <c>Ed-Fi</c>).</param>
/// <param name="ResourceName">The logical resource name (e.g., <c>School</c>).</param>
public readonly record struct QualifiedResourceName(string ProjectName, string ResourceName);

/// <summary>
/// Represents a physical database schema name.
/// </summary>
/// <param name="Value">The normalized schema identifier.</param>
public readonly record struct DbSchemaName(string Value);

/// <summary>
/// Represents a fully qualified physical table name.
/// </summary>
/// <param name="Schema">The schema containing the table.</param>
/// <param name="Name">The unqualified table name.</param>
public readonly record struct DbTableName(DbSchemaName Schema, string Name)
{
    /// <summary>
    /// Returns the qualified table name as <c>schema.table</c>.
    /// </summary>
    public override string ToString() => $"{Schema.Value}.{Name}";
}

/// <summary>
/// Represents a physical database column name.
/// </summary>
/// <param name="Value">The column identifier.</param>
public readonly record struct DbColumnName(string Value);

/// <summary>
/// Classifies the storage strategy for a resource.
/// </summary>
public enum ResourceStorageKind
{
    /// <summary>
    /// Default: per-project schema tables (root + child + _ext).
    /// </summary>
    RelationalTables,

    /// <summary>
    /// Descriptor resources stored in shared <c>dms.Descriptor</c>.
    /// </summary>
    SharedDescriptorTable,
}

/// <summary>
/// Classifies the semantic role of a derived column within a table.
/// </summary>
public enum ColumnKind
{
    /// <summary>
    /// A scalar value projected from the request JSON document.
    /// </summary>
    Scalar,

    /// <summary>
    /// A foreign key to another document (stored as <c>DocumentId</c>).
    /// </summary>
    DocumentFk,

    /// <summary>
    /// A foreign key to <c>dms.Descriptor</c> (stored as <c>DescriptorId</c> / <c>DocumentId</c>).
    /// </summary>
    DescriptorFk,

    /// <summary>
    /// The array ordering column used to preserve element order within collection tables.
    /// </summary>
    Ordinal,

    /// <summary>
    /// A key-part column inherited from an ancestor scope (e.g., root document id and parent ordinals).
    /// </summary>
    ParentKeyPart,
}

/// <summary>
/// The dialect-neutral scalar type categories used by the derived relational model.
/// </summary>
public enum ScalarKind
{
    /// <summary>
    /// A string value (<c>varchar</c>/<c>nvarchar</c>) with optional max length metadata.
    /// </summary>
    String,

    /// <summary>
    /// A 32-bit integer (<c>int</c>).
    /// </summary>
    Int32,

    /// <summary>
    /// A 64-bit integer (<c>bigint</c>).
    /// </summary>
    Int64,

    /// <summary>
    /// A fixed-precision decimal (<c>decimal(p,s)</c>).
    /// </summary>
    Decimal,

    /// <summary>
    /// A boolean value.
    /// </summary>
    Boolean,

    /// <summary>
    /// A date-only value.
    /// </summary>
    Date,

    /// <summary>
    /// A date-time value.
    /// </summary>
    DateTime,

    /// <summary>
    /// A time-only value.
    /// </summary>
    Time,
}

/// <summary>
/// Supported referential actions for derived foreign keys (a constrained cross-dialect subset).
/// </summary>
public enum ReferentialAction
{
    /// <summary>
    /// No action on update/delete.
    /// </summary>
    NoAction,

    /// <summary>
    /// Cascading behavior on update/delete.
    /// </summary>
    Cascade,
}

/// <summary>
/// Describes the storage type metadata for a scalar column.
/// </summary>
/// <param name="Kind">The scalar kind category.</param>
/// <param name="MaxLength">Maximum string length when <paramref name="Kind"/> is <see cref="ScalarKind.String"/>.</param>
/// <param name="Decimal">Precision and scale when <paramref name="Kind"/> is <see cref="ScalarKind.Decimal"/>.</param>
public sealed record RelationalScalarType(
    ScalarKind Kind,
    int? MaxLength = null,
    (int Precision, int Scale)? Decimal = null
);

/// <summary>
/// A segment of a canonical JSONPath expression used by the relational model builder.
/// </summary>
public abstract record JsonPathSegment
{
    /// <summary>
    /// A property segment (<c>.propertyName</c>).
    /// </summary>
    public sealed record Property(string Name) : JsonPathSegment;

    /// <summary>
    /// An array wildcard segment (<c>[*]</c>).
    /// </summary>
    public sealed record AnyArrayElement : JsonPathSegment;
}

/// <summary>
/// A canonical JSONPath expression along with its structured segment representation.
/// </summary>
/// <param name="Canonical">The canonical JSONPath string.</param>
/// <param name="Segments">The parsed segment sequence.</param>
public readonly record struct JsonPathExpression(string Canonical, IReadOnlyList<JsonPathSegment> Segments);

/// <summary>
/// The derived relational model for a single concrete resource.
/// </summary>
/// <param name="Resource">The logical resource identifier.</param>
/// <param name="PhysicalSchema">
/// The owning project schema. For shared-storage resources (e.g., descriptors), this can differ from
/// <paramref name="Root"/>'s <c>Table.Schema</c>; consumers must use each table's schema when emitting DDL.
/// </param>
/// <param name="StorageKind">The storage strategy for the resource.</param>
/// <param name="Root">The root table (<c>$</c>) for the resource.</param>
/// <param name="TablesInDependencyOrder">
/// Tables ordered in dependency order (root first, then child collection tables).
/// This order is used for both read reconstitution and write flattening.
/// </param>
/// <param name="DocumentReferenceBindings">Document reference bindings derived from metadata.</param>
/// <param name="DescriptorEdgeSources">Descriptor edge bindings derived from metadata.</param>
public sealed record RelationalResourceModel(
    QualifiedResourceName Resource,
    DbSchemaName PhysicalSchema,
    ResourceStorageKind StorageKind,
    DbTableModel Root,
    IReadOnlyList<DbTableModel> TablesInDependencyOrder,
    IReadOnlyList<DocumentReferenceBinding> DocumentReferenceBindings,
    IReadOnlyList<DescriptorEdgeSource> DescriptorEdgeSources
);

/// <summary>
/// The model for a physical table derived from a JSONPath scope.
/// </summary>
/// <param name="Table">The physical table name.</param>
/// <param name="JsonScope">
/// The owning JSONPath scope for rows in this table (e.g., <c>$</c> for the root table or
/// <c>$.addresses[*]</c> for a collection table).
/// </param>
/// <param name="Key">The primary key definition (root document id + ordinals as needed).</param>
/// <param name="Columns">All columns in the table, including key parts and derived scalar/FK columns.</param>
/// <param name="Constraints">Derived constraints (FKs, unique constraints, etc.).</param>
public sealed record DbTableModel(
    DbTableName Table,
    JsonPathExpression JsonScope,
    TableKey Key,
    IReadOnlyList<DbColumnModel> Columns,
    IReadOnlyList<TableConstraint> Constraints
);

/// <summary>
/// Primary key definition for a derived table.
/// </summary>
/// <param name="Columns">The key columns in order.</param>
public sealed record TableKey(IReadOnlyList<DbKeyColumn> Columns);

/// <summary>
/// A primary-key column and its semantic role.
/// </summary>
/// <param name="ColumnName">The physical column name.</param>
/// <param name="Kind">The key column kind.</param>
public sealed record DbKeyColumn(DbColumnName ColumnName, ColumnKind Kind);

/// <summary>
/// A derived table column definition.
/// </summary>
/// <param name="ColumnName">The physical column name.</param>
/// <param name="Kind">The semantic role for the column.</param>
/// <param name="ScalarType">The scalar type metadata (when applicable).</param>
/// <param name="IsNullable">Whether the column allows NULL.</param>
/// <param name="SourceJsonPath">The JSONPath that sources the column value (when applicable).</param>
/// <param name="TargetResource">The referenced resource type for FK columns (when applicable).</param>
public sealed record DbColumnModel(
    DbColumnName ColumnName,
    ColumnKind Kind,
    RelationalScalarType? ScalarType,
    bool IsNullable,
    JsonPathExpression? SourceJsonPath,
    QualifiedResourceName? TargetResource
);

/// <summary>
/// Base type for table constraint models derived from schema and metadata.
/// </summary>
public abstract record TableConstraint
{
    /// <summary>
    /// A UNIQUE constraint over one or more columns.
    /// </summary>
    /// <param name="Name">The physical constraint name.</param>
    /// <param name="Columns">The constrained columns, in key order.</param>
    public sealed record Unique(string Name, IReadOnlyList<DbColumnName> Columns) : TableConstraint;

    /// <summary>
    /// A foreign key constraint.
    /// </summary>
    /// <param name="Name">The physical constraint name.</param>
    /// <param name="Columns">The local FK columns.</param>
    /// <param name="TargetTable">The referenced table.</param>
    /// <param name="TargetColumns">The referenced columns.</param>
    /// <param name="OnDelete">The delete referential action.</param>
    /// <param name="OnUpdate">The update referential action.</param>
    public sealed record ForeignKey(
        string Name,
        IReadOnlyList<DbColumnName> Columns,
        DbTableName TargetTable,
        IReadOnlyList<DbColumnName> TargetColumns,
        ReferentialAction OnDelete = ReferentialAction.NoAction,
        ReferentialAction OnUpdate = ReferentialAction.NoAction
    ) : TableConstraint;

    /// <summary>
    /// A check constraint that enforces all-or-none nullability for a document reference group.
    /// </summary>
    /// <param name="Name">The physical constraint name.</param>
    /// <param name="FkColumn">The <c>..._DocumentId</c> FK column for the reference.</param>
    /// <param name="DependentColumns">
    /// The identity columns that must be populated when the FK column is populated.
    /// </param>
    public sealed record AllOrNoneNullability(
        string Name,
        DbColumnName FkColumn,
        IReadOnlyList<DbColumnName> DependentColumns
    ) : TableConstraint;
}

/// <summary>
/// Binds a JSON reference object path to a stored <c>..._DocumentId</c> FK column plus projected identity
/// component columns.
/// </summary>
/// <param name="IsIdentityComponent">
/// Indicates whether the reference participates in the resource identity projection.
/// </param>
/// <param name="ReferenceObjectPath">The JSONPath of the reference object in the document.</param>
/// <param name="Table">The table that owns the reference (the scope containing the reference path).</param>
/// <param name="FkColumn">The <c>..._DocumentId</c> FK column representing the reference.</param>
/// <param name="TargetResource">The referenced resource type.</param>
/// <param name="IdentityBindings">Per-identity-part bindings for locally stored reference identity columns.</param>
public sealed record DocumentReferenceBinding(
    bool IsIdentityComponent,
    JsonPathExpression ReferenceObjectPath,
    DbTableName Table,
    DbColumnName FkColumn,
    QualifiedResourceName TargetResource,
    IReadOnlyList<ReferenceIdentityBinding> IdentityBindings
);

/// <summary>
/// Binds a referenced identity JSONPath under a reference object to its stored local column.
/// </summary>
/// <param name="ReferenceJsonPath">The JSONPath to the identity value under the reference object.</param>
/// <param name="Column">The local column that stores the referenced identity value.</param>
public sealed record ReferenceIdentityBinding(JsonPathExpression ReferenceJsonPath, DbColumnName Column);

/// <summary>
/// Records a descriptor value JSONPath and its corresponding FK column for resolution and read-time
/// reconstitution.
/// </summary>
/// <param name="IsIdentityComponent">
/// Indicates whether the descriptor participates in the resource identity projection.
/// </param>
/// <param name="DescriptorValuePath">The JSONPath of the descriptor value in the request document.</param>
/// <param name="Table">The table that stores the descriptor FK column.</param>
/// <param name="FkColumn">The descriptor FK column name.</param>
/// <param name="DescriptorResource">The descriptor resource type expected at this path.</param>
public sealed record DescriptorEdgeSource(
    bool IsIdentityComponent,
    JsonPathExpression DescriptorValuePath,
    DbTableName Table,
    DbColumnName FkColumn,
    QualifiedResourceName DescriptorResource
);

/// <summary>
/// Represents an extension mapping site (<c>_ext</c>) aligned to an owning JSONPath table scope.
/// </summary>
/// <param name="OwningScope">The JSONPath scope of the table that owns the extension site.</param>
/// <param name="ExtensionPath">The JSONPath of the <c>_ext</c> object under the owning scope.</param>
/// <param name="ProjectKeys">The extension project keys present under this site.</param>
public sealed record ExtensionSite(
    JsonPathExpression OwningScope,
    JsonPathExpression ExtensionPath,
    IReadOnlyList<string> ProjectKeys
);
