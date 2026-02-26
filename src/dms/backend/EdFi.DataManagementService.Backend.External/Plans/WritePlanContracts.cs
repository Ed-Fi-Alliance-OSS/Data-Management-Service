// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled write plan for a single resource.
/// </summary>
/// <param name="Model">The derived relational model for the resource.</param>
/// <param name="TablePlansInDependencyOrder">
/// Per-table write plans in deterministic dependency order.
/// </param>
public sealed record ResourceWritePlan(
    RelationalResourceModel Model,
    IReadOnlyList<TableWritePlan> TablePlansInDependencyOrder
);

/// <summary>
/// Compiled write plan for one table.
/// </summary>
/// <param name="TableModel">The table shape model.</param>
/// <param name="InsertSql">Parameterized insert SQL.</param>
/// <param name="UpdateSql">Optional parameterized update SQL (root table only).</param>
/// <param name="DeleteByParentSql">
/// Optional delete SQL used for replace semantics by parent key (non-root tables).
/// </param>
/// <param name="BulkInsertBatching">
/// Deterministic bulk-insert batching metadata for this table.
/// </param>
/// <param name="ColumnBindings">
/// Stored/writable column bindings in authoritative parameter/value order.
/// </param>
/// <param name="KeyUnificationPlans">
/// Per-table key-unification precompute plans that populate <see cref="WriteValueSource.Precomputed" /> bindings.
/// </param>
public sealed record TableWritePlan(
    DbTableModel TableModel,
    string InsertSql,
    string? UpdateSql,
    string? DeleteByParentSql,
    BulkInsertBatchingInfo BulkInsertBatching,
    IReadOnlyList<WriteColumnBinding> ColumnBindings,
    IReadOnlyList<KeyUnificationWritePlan> KeyUnificationPlans
);

/// <summary>
/// Deterministic batching metadata used to chunk bulk insert commands.
/// </summary>
/// <param name="MaxRowsPerBatch">Maximum rows per command for this table plan.</param>
/// <param name="ParametersPerRow">Number of parameters emitted per inserted row.</param>
/// <param name="MaxParametersPerCommand">Dialect max-parameter limit used by the calculator.</param>
public sealed record BulkInsertBatchingInfo(
    int MaxRowsPerBatch,
    int ParametersPerRow,
    int MaxParametersPerCommand
);

/// <summary>
/// Binds a physical column to its write-time value source and authoritative SQL parameter name.
/// </summary>
/// <param name="Column">The column model being written.</param>
/// <param name="Source">The source used to populate the bound value.</param>
/// <param name="ParameterName">The bare SQL parameter name (without <c>@</c>).</param>
public sealed record WriteColumnBinding(DbColumnModel Column, WriteValueSource Source, string ParameterName);

/// <summary>
/// Discriminated union describing where a write-time column value comes from.
/// </summary>
public abstract record WriteValueSource
{
    /// <summary>
    /// Uses the resource root <c>DocumentId</c>.
    /// </summary>
    public sealed record DocumentId : WriteValueSource;

    /// <summary>
    /// Uses one parent-key part by index.
    /// </summary>
    /// <param name="Index">The index in the parent-key parts array.</param>
    public sealed record ParentKeyPart(int Index) : WriteValueSource;

    /// <summary>
    /// Uses the current array element ordinal.
    /// </summary>
    public sealed record Ordinal : WriteValueSource;

    /// <summary>
    /// Reads a scalar value from JSON relative to the table scope.
    /// </summary>
    /// <param name="RelativePath">The canonical relative JSONPath.</param>
    /// <param name="Type">The scalar type metadata for value extraction/coercion.</param>
    public sealed record Scalar(JsonPathExpression RelativePath, RelationalScalarType Type)
        : WriteValueSource;

    /// <summary>
    /// Resolves a document-reference FK by binding inventory index and reference object path.
    /// </summary>
    /// <param name="BindingIndex">The index into the resource's document-reference binding inventory.</param>
    /// <param name="ReferenceObjectPath">The canonical reference object JSONPath.</param>
    public sealed record DocumentReference(int BindingIndex, JsonPathExpression ReferenceObjectPath)
        : WriteValueSource;

    /// <summary>
    /// Resolves a descriptor FK from descriptor metadata and a scope-relative path.
    /// </summary>
    /// <param name="DescriptorResource">The descriptor resource type expected at this path.</param>
    /// <param name="RelativePath">Descriptor value path relative to the table scope.</param>
    /// <param name="DescriptorValuePath">
    /// Optional canonical absolute descriptor value path for diagnostics/traceability.
    /// </param>
    public sealed record DescriptorReference(
        QualifiedResourceName DescriptorResource,
        JsonPathExpression RelativePath,
        JsonPathExpression? DescriptorValuePath = null
    ) : WriteValueSource;

    /// <summary>
    /// Value is populated by precompute logic (for example key-unification).
    /// </summary>
    public sealed record Precomputed : WriteValueSource;
}

/// <summary>
/// Per-table key-unification precompute plan.
/// </summary>
/// <param name="CanonicalColumn">The canonical stored column this plan computes.</param>
/// <param name="CanonicalBindingIndex">
/// Binding index in <see cref="TableWritePlan.ColumnBindings" /> for the canonical stored column.
/// </param>
/// <param name="MembersInOrder">
/// Candidate member sources in deterministic precedence order.
/// </param>
public sealed record KeyUnificationWritePlan(
    DbColumnName CanonicalColumn,
    int CanonicalBindingIndex,
    IReadOnlyList<KeyUnificationMemberWritePlan> MembersInOrder
);

/// <summary>
/// Member source metadata for a key-unification class.
/// </summary>
/// <param name="MemberPathColumn">The member-path binding column.</param>
/// <param name="RelativePath">Member value path relative to the table scope.</param>
/// <param name="PresenceColumn">
/// Optional presence gate column used to preserve absent-versus-null semantics.
/// </param>
/// <param name="PresenceBindingIndex">
/// Optional binding index in <see cref="TableWritePlan.ColumnBindings" /> for the presence column.
/// </param>
/// <param name="PresenceIsSynthetic">Indicates whether the presence column is synthetic.</param>
public abstract record KeyUnificationMemberWritePlan(
    DbColumnName MemberPathColumn,
    JsonPathExpression RelativePath,
    DbColumnName? PresenceColumn,
    int? PresenceBindingIndex,
    bool PresenceIsSynthetic
)
{
    /// <summary>
    /// Scalar member source metadata.
    /// </summary>
    /// <param name="MemberPathColumn">The member-path binding column.</param>
    /// <param name="RelativePath">Member value path relative to the table scope.</param>
    /// <param name="ScalarType">Scalar metadata for the member value extraction.</param>
    /// <param name="PresenceColumn">
    /// Optional presence gate column used to preserve absent-versus-null semantics.
    /// </param>
    /// <param name="PresenceBindingIndex">
    /// Optional binding index in <see cref="TableWritePlan.ColumnBindings" /> for the presence column.
    /// </param>
    /// <param name="PresenceIsSynthetic">Indicates whether the presence column is synthetic.</param>
    public sealed record ScalarMember(
        DbColumnName MemberPathColumn,
        JsonPathExpression RelativePath,
        RelationalScalarType ScalarType,
        DbColumnName? PresenceColumn,
        int? PresenceBindingIndex,
        bool PresenceIsSynthetic
    )
        : KeyUnificationMemberWritePlan(
            MemberPathColumn,
            RelativePath,
            PresenceColumn,
            PresenceBindingIndex,
            PresenceIsSynthetic
        );

    /// <summary>
    /// Descriptor member source metadata.
    /// </summary>
    /// <param name="MemberPathColumn">The member-path binding column.</param>
    /// <param name="RelativePath">Member value path relative to the table scope.</param>
    /// <param name="DescriptorResource">Descriptor resource expected at the member path.</param>
    /// <param name="PresenceColumn">
    /// Optional presence gate column used to preserve absent-versus-null semantics.
    /// </param>
    /// <param name="PresenceBindingIndex">
    /// Optional binding index in <see cref="TableWritePlan.ColumnBindings" /> for the presence column.
    /// </param>
    /// <param name="PresenceIsSynthetic">Indicates whether the presence column is synthetic.</param>
    public sealed record DescriptorMember(
        DbColumnName MemberPathColumn,
        JsonPathExpression RelativePath,
        QualifiedResourceName DescriptorResource,
        DbColumnName? PresenceColumn,
        int? PresenceBindingIndex,
        bool PresenceIsSynthetic
    )
        : KeyUnificationMemberWritePlan(
            MemberPathColumn,
            RelativePath,
            PresenceColumn,
            PresenceBindingIndex,
            PresenceIsSynthetic
        );
}
