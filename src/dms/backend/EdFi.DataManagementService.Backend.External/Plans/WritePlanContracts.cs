// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled write plan for a single resource.
/// </summary>
/// <remarks>
/// This contract carries canonical SQL text plus deterministic binding metadata so runtime execution never relies on
/// SQL text parsing to infer parameter bindings.
/// </remarks>
public sealed record ResourceWritePlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceWritePlan" /> record.
    /// </summary>
    /// <param name="Model">The derived relational model for the resource.</param>
    /// <param name="TablePlansInDependencyOrder">Per-table write plans in deterministic dependency order.</param>
    public ResourceWritePlan(
        RelationalResourceModel Model,
        IEnumerable<TableWritePlan> TablePlansInDependencyOrder
    )
    {
        this.Model = PlanContractArgumentValidator.RequireNotNull(Model, nameof(Model));
        this.TablePlansInDependencyOrder = PlanContractArgumentValidator.RequireImmutableArray(
            TablePlansInDependencyOrder,
            nameof(TablePlansInDependencyOrder)
        );
    }

    /// <summary>
    /// The derived relational model the plan was compiled from.
    /// </summary>
    public RelationalResourceModel Model { get; init; }

    /// <summary>
    /// Per-table write plans in deterministic dependency order.
    /// </summary>
    public ImmutableArray<TableWritePlan> TablePlansInDependencyOrder { get; init; }
}

/// <summary>
/// Compiled write plan for one table.
/// </summary>
/// <remarks>
/// Ordering-sensitive collections (for example <see cref="ColumnBindings" />) are semantically significant and must be
/// preserved exactly as compiled.
/// </remarks>
public sealed record TableWritePlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableWritePlan" /> record.
    /// </summary>
    /// <param name="TableModel">The table shape model.</param>
    /// <param name="InsertSql">Parameterized insert SQL.</param>
    /// <param name="UpdateSql">Optional parameterized update SQL (root table only).</param>
    /// <param name="DeleteByParentSql">
    /// Optional delete SQL used for replace semantics by parent key (non-root tables).
    /// </param>
    /// <param name="BulkInsertBatching">Deterministic bulk-insert batching metadata for this table.</param>
    /// <param name="ColumnBindings">
    /// Stored/writable column bindings in authoritative parameter/value order.
    /// </param>
    /// <param name="KeyUnificationPlans">
    /// Per-table key-unification precompute plans that populate <see cref="WriteValueSource.Precomputed" /> bindings.
    /// </param>
    public TableWritePlan(
        DbTableModel TableModel,
        string InsertSql,
        string? UpdateSql,
        string? DeleteByParentSql,
        BulkInsertBatchingInfo BulkInsertBatching,
        IEnumerable<WriteColumnBinding> ColumnBindings,
        IEnumerable<KeyUnificationWritePlan> KeyUnificationPlans
    )
    {
        this.TableModel = PlanContractArgumentValidator.RequireNotNull(TableModel, nameof(TableModel));
        this.InsertSql = PlanContractArgumentValidator.RequireNotNull(InsertSql, nameof(InsertSql));
        this.UpdateSql = UpdateSql;
        this.DeleteByParentSql = DeleteByParentSql;
        this.BulkInsertBatching = PlanContractArgumentValidator.RequireNotNull(
            BulkInsertBatching,
            nameof(BulkInsertBatching)
        );
        this.ColumnBindings = PlanContractArgumentValidator.RequireImmutableArray(
            ColumnBindings,
            nameof(ColumnBindings)
        );
        this.KeyUnificationPlans = PlanContractArgumentValidator.RequireImmutableArray(
            KeyUnificationPlans,
            nameof(KeyUnificationPlans)
        );
    }

    /// <summary>
    /// The table model the SQL and bindings target.
    /// </summary>
    public DbTableModel TableModel { get; init; }

    /// <summary>
    /// Canonical parameterized <c>INSERT</c> SQL for this table.
    /// </summary>
    public string InsertSql { get; init; }

    /// <summary>
    /// Optional canonical parameterized <c>UPDATE</c> SQL (root table only).
    /// </summary>
    public string? UpdateSql { get; init; }

    /// <summary>
    /// Optional delete SQL used to remove all child rows for a parent key (replace semantics).
    /// </summary>
    public string? DeleteByParentSql { get; init; }

    /// <summary>
    /// Deterministic batching metadata derived from dialect limits and per-row parameter width.
    /// </summary>
    public BulkInsertBatchingInfo BulkInsertBatching { get; init; }

    /// <summary>
    /// Stored/writable column bindings in authoritative parameter/value order.
    /// </summary>
    public ImmutableArray<WriteColumnBinding> ColumnBindings { get; init; }

    /// <summary>
    /// Key-unification precompute plans that populate <see cref="WriteValueSource.Precomputed" /> bindings deterministically.
    /// </summary>
    public ImmutableArray<KeyUnificationWritePlan> KeyUnificationPlans { get; init; }
}

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
    /// Resolves a document-reference FK by binding inventory index.
    /// </summary>
    /// <param name="BindingIndex">The index into the resource's document-reference binding inventory.</param>
    public sealed record DocumentReference(int BindingIndex) : WriteValueSource;

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
public sealed record KeyUnificationWritePlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeyUnificationWritePlan" /> record.
    /// </summary>
    /// <param name="CanonicalColumn">The canonical stored column this plan computes.</param>
    /// <param name="CanonicalBindingIndex">
    /// Binding index in <see cref="TableWritePlan.ColumnBindings" /> for the canonical stored column.
    /// </param>
    /// <param name="MembersInOrder">Candidate member sources in deterministic precedence order.</param>
    public KeyUnificationWritePlan(
        DbColumnName CanonicalColumn,
        int CanonicalBindingIndex,
        IEnumerable<KeyUnificationMemberWritePlan> MembersInOrder
    )
    {
        this.CanonicalColumn = CanonicalColumn;
        this.CanonicalBindingIndex = CanonicalBindingIndex;
        this.MembersInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            MembersInOrder,
            nameof(MembersInOrder)
        );
    }

    /// <summary>
    /// The canonical stored column that this plan computes.
    /// </summary>
    public DbColumnName CanonicalColumn { get; init; }

    /// <summary>
    /// Binding index in <see cref="TableWritePlan.ColumnBindings" /> for <see cref="CanonicalColumn" />.
    /// </summary>
    public int CanonicalBindingIndex { get; init; }

    /// <summary>
    /// Candidate member sources in deterministic precedence order.
    /// </summary>
    public ImmutableArray<KeyUnificationMemberWritePlan> MembersInOrder { get; init; }
}

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
