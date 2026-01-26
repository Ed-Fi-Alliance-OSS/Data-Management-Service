// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

public readonly record struct QualifiedResourceName(string ProjectName, string ResourceName);

public readonly record struct DbSchemaName(string Value);

public readonly record struct DbTableName(DbSchemaName Schema, string Name)
{
    public override string ToString() => $"{Schema.Value}.{Name}";
}

public readonly record struct DbColumnName(string Value);

public enum ColumnKind
{
    Scalar,
    DocumentFk,
    DescriptorFk,
    Ordinal,
    ParentKeyPart,
}

public enum ScalarKind
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Time,
}

public enum ReferentialAction
{
    NoAction,
    Cascade,
}

public sealed record RelationalScalarType(
    ScalarKind Kind,
    int? MaxLength = null,
    (int Precision, int Scale)? Decimal = null
);

public abstract record JsonPathSegment
{
    public sealed record Property(string Name) : JsonPathSegment;

    public sealed record AnyArrayElement : JsonPathSegment;
}

public readonly record struct JsonPathExpression(string Canonical, IReadOnlyList<JsonPathSegment> Segments);

public sealed record RelationalResourceModel(
    QualifiedResourceName Resource,
    DbSchemaName PhysicalSchema,
    DbTableModel Root,
    IReadOnlyList<DbTableModel> TablesInReadDependencyOrder,
    IReadOnlyList<DbTableModel> TablesInWriteDependencyOrder,
    IReadOnlyList<DocumentReferenceBinding> DocumentReferenceBindings,
    IReadOnlyList<DescriptorEdgeSource> DescriptorEdgeSources
);

public sealed record DbTableModel(
    DbTableName Table,
    JsonPathExpression JsonScope,
    TableKey Key,
    IReadOnlyList<DbColumnModel> Columns,
    IReadOnlyList<TableConstraint> Constraints
);

public sealed record TableKey(IReadOnlyList<DbKeyColumn> Columns);

public sealed record DbKeyColumn(DbColumnName ColumnName, ColumnKind Kind);

public sealed record DbColumnModel(
    DbColumnName ColumnName,
    ColumnKind Kind,
    RelationalScalarType? ScalarType,
    bool IsNullable,
    JsonPathExpression? SourceJsonPath,
    QualifiedResourceName? TargetResource
);

public abstract record TableConstraint
{
    public sealed record Unique(string Name, IReadOnlyList<DbColumnName> Columns) : TableConstraint;

    public sealed record ForeignKey(
        string Name,
        IReadOnlyList<DbColumnName> Columns,
        DbTableName TargetTable,
        IReadOnlyList<DbColumnName> TargetColumns,
        ReferentialAction OnDelete = ReferentialAction.NoAction,
        ReferentialAction OnUpdate = ReferentialAction.NoAction
    ) : TableConstraint;
}

public sealed record DocumentReferenceBinding(
    bool IsIdentityComponent,
    JsonPathExpression ReferenceObjectPath,
    DbTableName Table,
    DbColumnName FkColumn,
    QualifiedResourceName TargetResource,
    IReadOnlyList<ReferenceIdentityBinding> IdentityBindings
);

public sealed record ReferenceIdentityBinding(JsonPathExpression ReferenceJsonPath, DbColumnName Column);

public sealed record DescriptorEdgeSource(
    bool IsIdentityComponent,
    JsonPathExpression DescriptorValuePath,
    DbTableName Table,
    DbColumnName FkColumn,
    QualifiedResourceName DescriptorResource
);

public sealed record ExtensionSite(
    JsonPathExpression OwningScope,
    JsonPathExpression ExtensionPath,
    IReadOnlyList<string> ProjectKeys
);
