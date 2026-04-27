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
    /// <param name="UpdateSql">
    /// Optional parameterized <c>UPDATE</c> SQL for 1:1 tables (table key contains no <c>Ordinal</c>).
    /// When provided, executors should treat the table scope as upsertable when the scoped object is present in the
    /// payload (insert when newly present; update when it already exists).
    /// </param>
    /// <param name="DeleteByParentSql">
    /// Optional <c>DELETE</c> SQL used for scope replacement by parent key (non-root tables).
    /// For non-root 1:1 tables (no <c>Ordinal</c>), executors run this when the scoped object is absent from the
    /// payload.
    /// </param>
    /// <param name="BulkInsertBatching">Deterministic bulk-insert batching metadata for this table.</param>
    /// <param name="ColumnBindings">
    /// Stored/writable column bindings in authoritative parameter/value order.
    /// </param>
    /// <param name="CollectionKeyPreallocationPlan">
    /// Optional table-local precompute plan that reserves stable collection-row identities for
    /// <see cref="ColumnKind.CollectionKey" /> bindings before insert DML executes.
    /// </param>
    /// <param name="KeyUnificationPlans">
    /// Per-table key-unification precompute plans that populate key-unification-specific
    /// <see cref="WriteValueSource.Precomputed" /> bindings.
    /// </param>
    /// <param name="CollectionMergePlan">
    /// Optional binding-index-first merge metadata for persisted collection tables.
    /// </param>
    public TableWritePlan(
        DbTableModel TableModel,
        string InsertSql,
        string? UpdateSql,
        string? DeleteByParentSql,
        BulkInsertBatchingInfo BulkInsertBatching,
        IEnumerable<WriteColumnBinding> ColumnBindings,
        IEnumerable<KeyUnificationWritePlan> KeyUnificationPlans,
        CollectionMergePlan? CollectionMergePlan = null,
        CollectionKeyPreallocationPlan? CollectionKeyPreallocationPlan = null
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
        this.CollectionMergePlan = CollectionMergePlan;
        this.CollectionKeyPreallocationPlan = CollectionKeyPreallocationPlan;
        this.KeyUnificationPlans = PlanContractArgumentValidator.RequireImmutableArray(
            KeyUnificationPlans,
            nameof(KeyUnificationPlans)
        );
        var bindingCount = this.ColumnBindings.Length;
        var tableKind = TableModel.IdentityMetadata.TableKind;
        var tableKindParameterName =
            $"{nameof(TableModel)}.{nameof(DbTableModel.IdentityMetadata)}.{nameof(DbTableIdentityMetadata.TableKind)}";

        if (IsCollectionMergeTableKind(tableKind) && CollectionMergePlan is null)
        {
            var detail = DeleteByParentSql is null
                ? $"Neither {nameof(TableWritePlan.CollectionMergePlan)} nor {nameof(TableWritePlan.DeleteByParentSql)} was provided."
                : $"{nameof(TableWritePlan.DeleteByParentSql)} cannot replace {nameof(TableWritePlan.CollectionMergePlan)} for persisted collection tables.";

            throw new ArgumentException(
                $"{tableKindParameterName} '{tableKind}' requires {nameof(TableWritePlan.CollectionMergePlan)}. {detail}",
                nameof(CollectionMergePlan)
            );
        }

        if (CollectionMergePlan is not null)
        {
            if (UpdateSql is not null)
            {
                throw new ArgumentException(
                    $"{nameof(TableWritePlan.CollectionMergePlan)} requires {nameof(TableWritePlan.UpdateSql)} to be null.",
                    nameof(UpdateSql)
                );
            }

            if (DeleteByParentSql is not null)
            {
                throw new ArgumentException(
                    $"{nameof(TableWritePlan.CollectionMergePlan)} requires {nameof(TableWritePlan.DeleteByParentSql)} to be null.",
                    nameof(DeleteByParentSql)
                );
            }

            if (!IsCollectionMergeTableKind(tableKind))
            {
                throw new ArgumentException(
                    $"{nameof(TableWritePlan.CollectionMergePlan)} requires {tableKindParameterName} to be {nameof(DbTableKind.Collection)} or {nameof(DbTableKind.ExtensionCollection)}. Actual value: {tableKind}.",
                    tableKindParameterName
                );
            }

            for (
                var semanticIdentityBindingIndex = 0;
                semanticIdentityBindingIndex < CollectionMergePlan.SemanticIdentityBindings.Length;
                semanticIdentityBindingIndex++
            )
            {
                var semanticIdentityBinding = CollectionMergePlan.SemanticIdentityBindings[
                    semanticIdentityBindingIndex
                ];

                ValidateBindingIndex(
                    semanticIdentityBinding.BindingIndex,
                    bindingCount,
                    $"{nameof(TableWritePlan.CollectionMergePlan)}.{nameof(CollectionMergePlan.SemanticIdentityBindings)}[{semanticIdentityBindingIndex}].{nameof(semanticIdentityBinding.BindingIndex)}",
                    "Collection merge semantic-identity binding"
                );
            }

            ValidateBindingIndex(
                CollectionMergePlan.StableRowIdentityBindingIndex,
                bindingCount,
                $"{nameof(TableWritePlan.CollectionMergePlan)}.{nameof(CollectionMergePlan.StableRowIdentityBindingIndex)}",
                "Collection merge stable-row-identity binding"
            );
            ValidateBindingIndex(
                CollectionMergePlan.OrdinalBindingIndex,
                bindingCount,
                $"{nameof(TableWritePlan.CollectionMergePlan)}.{nameof(CollectionMergePlan.OrdinalBindingIndex)}",
                "Collection merge ordinal binding"
            );

            for (
                var compareBindingIndex = 0;
                compareBindingIndex < CollectionMergePlan.CompareBindingIndexesInOrder.Length;
                compareBindingIndex++
            )
            {
                ValidateBindingIndex(
                    CollectionMergePlan.CompareBindingIndexesInOrder[compareBindingIndex],
                    bindingCount,
                    $"{nameof(TableWritePlan.CollectionMergePlan)}.{nameof(CollectionMergePlan.CompareBindingIndexesInOrder)}[{compareBindingIndex}]",
                    "Collection merge compare binding"
                );
            }

            if (CollectionKeyPreallocationPlan is null)
            {
                throw new ArgumentException(
                    $"{nameof(TableWritePlan.CollectionMergePlan)} requires {nameof(TableWritePlan.CollectionKeyPreallocationPlan)} to be provided.",
                    nameof(CollectionKeyPreallocationPlan)
                );
            }

            ValidateBindingIndex(
                CollectionKeyPreallocationPlan.BindingIndex,
                bindingCount,
                $"{nameof(TableWritePlan.CollectionKeyPreallocationPlan)}.{nameof(CollectionKeyPreallocationPlan.BindingIndex)}",
                "Collection-key preallocation binding"
            );

            if (
                CollectionMergePlan.StableRowIdentityBindingIndex
                != CollectionKeyPreallocationPlan.BindingIndex
            )
            {
                throw new ArgumentException(
                    $"{nameof(TableWritePlan.CollectionMergePlan)}.{nameof(CollectionMergePlan.StableRowIdentityBindingIndex)} must match {nameof(TableWritePlan.CollectionKeyPreallocationPlan)}.{nameof(CollectionKeyPreallocationPlan.BindingIndex)}.",
                    nameof(CollectionKeyPreallocationPlan)
                );
            }

            var stableRowIdentityBinding = this.ColumnBindings[
                CollectionMergePlan.StableRowIdentityBindingIndex
            ];

            if (!stableRowIdentityBinding.Column.ColumnName.Equals(CollectionKeyPreallocationPlan.ColumnName))
            {
                throw new ArgumentException(
                    $"{nameof(TableWritePlan.CollectionMergePlan)}.{nameof(CollectionMergePlan.StableRowIdentityBindingIndex)} resolves to column '{stableRowIdentityBinding.Column.ColumnName.Value}', which must match {nameof(TableWritePlan.CollectionKeyPreallocationPlan)}.{nameof(CollectionKeyPreallocationPlan.ColumnName)} '{CollectionKeyPreallocationPlan.ColumnName.Value}'.",
                    nameof(CollectionKeyPreallocationPlan)
                );
            }

            return;
        }

        if (CollectionKeyPreallocationPlan is null)
        {
            return;
        }

        ValidateBindingIndex(
            CollectionKeyPreallocationPlan.BindingIndex,
            bindingCount,
            $"{nameof(TableWritePlan.CollectionKeyPreallocationPlan)}.{nameof(CollectionKeyPreallocationPlan.BindingIndex)}",
            "Collection-key preallocation binding"
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
    /// Optional canonical parameterized <c>UPDATE</c> SQL for 1:1 tables (table key contains no <c>Ordinal</c>).
    /// </summary>
    /// <remarks>
    /// For non-root 1:1 scopes (including document-scope <c>_ext</c> tables) where <see cref="DeleteByParentSql" /> is
    /// also present:
    /// - when the scoped object is present in the payload, execution uses <see cref="InsertSql" /> for newly present
    ///   rows and <see cref="UpdateSql" /> for existing rows (existence detection is executor-specific), and
    /// - when the scoped object is absent from the payload, execution uses <see cref="DeleteByParentSql" /> to remove
    ///   the scoped row.
    /// </remarks>
    public string? UpdateSql { get; init; }

    /// <summary>
    /// Optional canonical parameterized <c>DELETE</c> SQL used for scope replacement by parent key (non-root tables).
    /// </summary>
    /// <remarks>
    /// - For non-root 1:1 tables (no <c>Ordinal</c>), executors execute this only when the scoped object is absent from
    ///   the payload.
    /// - Persisted collection tables use <see cref="CollectionMergePlan" /> metadata instead of parent-scope replace
    ///   semantics.
    /// </remarks>
    public string? DeleteByParentSql { get; init; }

    /// <summary>
    /// Optional binding-index-first merge metadata for persisted collection tables.
    /// </summary>
    public CollectionMergePlan? CollectionMergePlan { get; init; }

    /// <summary>
    /// Deterministic batching metadata derived from dialect limits and per-row parameter width.
    /// </summary>
    public BulkInsertBatchingInfo BulkInsertBatching { get; init; }

    /// <summary>
    /// Stored/writable column bindings in authoritative parameter/value order.
    /// </summary>
    public ImmutableArray<WriteColumnBinding> ColumnBindings { get; init; }

    /// <summary>
    /// Optional collection-key preallocation metadata for table-local stable row-identity reservation.
    /// </summary>
    public CollectionKeyPreallocationPlan? CollectionKeyPreallocationPlan { get; init; }

    /// <summary>
    /// Key-unification precompute plans that populate key-unification-specific
    /// <see cref="WriteValueSource.Precomputed" /> bindings deterministically.
    /// </summary>
    public ImmutableArray<KeyUnificationWritePlan> KeyUnificationPlans { get; init; }

    private static bool IsCollectionMergeTableKind(DbTableKind tableKind)
    {
        return tableKind is DbTableKind.Collection or DbTableKind.ExtensionCollection;
    }

    private static void ValidateBindingIndex(
        int bindingIndex,
        int bindingCount,
        string parameterName,
        string description
    )
    {
        if (bindingIndex >= 0 && bindingIndex < bindingCount)
        {
            return;
        }

        var validRangeDescription =
            bindingCount == 0
                ? $"{nameof(ColumnBindings)} is empty."
                : $"Expected a value between 0 and {bindingCount - 1}.";

        throw new ArgumentOutOfRangeException(
            parameterName,
            $"{description} must reference a valid {nameof(ColumnBindings)} entry. {validRangeDescription}"
        );
    }
}

/// <summary>
/// Binding-index-first merge metadata for one persisted collection table.
/// </summary>
public sealed record CollectionMergePlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionMergePlan" /> record.
    /// </summary>
    /// <param name="SemanticIdentityBindings">
    /// Ordered semantic-identity bindings that point back into <see cref="TableWritePlan.ColumnBindings" />.
    /// Shared/default compilation may leave this empty for permissive artifacts; strict runtime compilation rejects
    /// that upstream before executable plans are exposed.
    /// </param>
    /// <param name="StableRowIdentityBindingIndex">
    /// Binding index for the stable row identity (for example <c>CollectionItemId</c>).
    /// </param>
    /// <param name="UpdateByStableRowIdentitySql">
    /// Canonical parameterized <c>UPDATE</c> SQL that updates one matched collection row in place using the stable row
    /// identity binding as the <c>WHERE</c> predicate.
    /// </param>
    /// <param name="DeleteByStableRowIdentitySql">
    /// Canonical parameterized <c>DELETE</c> SQL that removes one omitted collection row using the stable row identity
    /// binding as the <c>WHERE</c> predicate.
    /// </param>
    /// <param name="OrdinalBindingIndex">
    /// Binding index for the authoritative persisted ordering column.
    /// </param>
    /// <param name="CompareBindingIndexesInOrder">
    /// Binding indexes used to project current rows into deterministic compare/no-op order.
    /// </param>
    public CollectionMergePlan(
        IEnumerable<CollectionMergeSemanticIdentityBinding> SemanticIdentityBindings,
        int StableRowIdentityBindingIndex,
        string UpdateByStableRowIdentitySql,
        string DeleteByStableRowIdentitySql,
        int OrdinalBindingIndex,
        IEnumerable<int> CompareBindingIndexesInOrder
    )
    {
        this.SemanticIdentityBindings = PlanContractArgumentValidator.RequireImmutableArray(
            SemanticIdentityBindings,
            nameof(SemanticIdentityBindings)
        );
        this.StableRowIdentityBindingIndex = StableRowIdentityBindingIndex;
        this.UpdateByStableRowIdentitySql = PlanContractArgumentValidator.RequireNotNull(
            UpdateByStableRowIdentitySql,
            nameof(UpdateByStableRowIdentitySql)
        );
        this.DeleteByStableRowIdentitySql = PlanContractArgumentValidator.RequireNotNull(
            DeleteByStableRowIdentitySql,
            nameof(DeleteByStableRowIdentitySql)
        );
        this.OrdinalBindingIndex = OrdinalBindingIndex;
        this.CompareBindingIndexesInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            CompareBindingIndexesInOrder,
            nameof(CompareBindingIndexesInOrder)
        );
    }

    /// <summary>
    /// Ordered semantic-identity bindings that point back into <see cref="TableWritePlan.ColumnBindings" />.
    /// </summary>
    public ImmutableArray<CollectionMergeSemanticIdentityBinding> SemanticIdentityBindings { get; init; }

    /// <summary>
    /// Binding index for the stable row identity (for example <c>CollectionItemId</c>).
    /// </summary>
    public int StableRowIdentityBindingIndex { get; init; }

    /// <summary>
    /// Canonical parameterized <c>UPDATE</c> SQL for matched collection rows keyed by stable row identity.
    /// Parameter names align to <see cref="TableWritePlan.ColumnBindings" />.
    /// </summary>
    public string UpdateByStableRowIdentitySql { get; init; }

    /// <summary>
    /// Canonical parameterized <c>DELETE</c> SQL for omitted collection rows keyed by stable row identity.
    /// Parameter names align to <see cref="TableWritePlan.ColumnBindings" />.
    /// </summary>
    public string DeleteByStableRowIdentitySql { get; init; }

    /// <summary>
    /// Binding index for the authoritative persisted ordering column.
    /// </summary>
    public int OrdinalBindingIndex { get; init; }

    /// <summary>
    /// Binding indexes used to project current rows into deterministic compare/no-op order.
    /// </summary>
    public ImmutableArray<int> CompareBindingIndexesInOrder { get; init; }
}

/// <summary>
/// Maps one semantic-identity member path to the authoritative write-column binding that carries its value.
/// </summary>
/// <param name="RelativePath">Canonical scope-relative path for the semantic-identity member.</param>
/// <param name="BindingIndex">
/// Binding index in <see cref="TableWritePlan.ColumnBindings" /> for the semantic-identity value.
/// </param>
public sealed record CollectionMergeSemanticIdentityBinding(
    JsonPathExpression RelativePath,
    int BindingIndex
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
/// Table-local precompute metadata for reserving stable collection-row identities.
/// </summary>
/// <param name="ColumnName">The <see cref="ColumnKind.CollectionKey" /> column receiving the reserved value.</param>
/// <param name="BindingIndex">
/// Binding index in <see cref="TableWritePlan.ColumnBindings" /> for the precomputed collection key.
/// </param>
public sealed record CollectionKeyPreallocationPlan(DbColumnName ColumnName, int BindingIndex);

/// <summary>
/// Identifies one logical value derived from a resolved document-reference site.
/// </summary>
/// <param name="BindingIndex">
/// The authoritative index into <see cref="RelationalResourceModel.DocumentReferenceBindings" /> for the reference
/// site.
/// </param>
/// <param name="ReferenceObjectPath">The canonical reference-object path for the site.</param>
/// <param name="IdentityJsonPath">The authoritative target-identity path for the derived logical value.</param>
/// <param name="ReferenceJsonPath">The canonical logical member path under the reference site.</param>
public sealed record ReferenceDerivedValueSourceMetadata
{
    public ReferenceDerivedValueSourceMetadata(
        int BindingIndex,
        JsonPathExpression ReferenceObjectPath,
        JsonPathExpression IdentityJsonPath,
        JsonPathExpression ReferenceJsonPath
    )
    {
        this.BindingIndex = BindingIndex;
        this.ReferenceObjectPath = ReferenceObjectPath;
        this.IdentityJsonPath = IdentityJsonPath;
        this.ReferenceJsonPath = ReferenceJsonPath;
    }

    public int BindingIndex { get; init; }

    public JsonPathExpression ReferenceObjectPath { get; init; }

    public JsonPathExpression IdentityJsonPath { get; init; }

    public JsonPathExpression ReferenceJsonPath { get; init; }
}

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
    /// Uses one logical value derived from an already resolved document-reference site rather than reading request
    /// JSON directly.
    /// </summary>
    /// <param name="ReferenceSource">The authoritative reference-site and logical-member metadata.</param>
    public sealed record ReferenceDerived(ReferenceDerivedValueSourceMetadata ReferenceSource)
        : WriteValueSource;

    /// <summary>
    /// Resolves a descriptor FK from descriptor metadata and a scope-relative path.
    /// </summary>
    /// <param name="DescriptorResource">The descriptor resource type expected at this path.</param>
    /// <param name="RelativePath">Descriptor value path relative to the table scope.</param>
    /// <param name="DescriptorValuePath">
    /// Optional canonical absolute descriptor value path for diagnostics/traceability. Executors SHOULD use
    /// <paramref name="RelativePath" /> for execution and treat <paramref name="DescriptorValuePath" /> as informational
    /// only. When compiled by the DMS plan compiler, this value is always populated; plan decoders MAY omit it for
    /// backward compatibility.
    /// </param>
    public sealed record DescriptorReference(
        QualifiedResourceName DescriptorResource,
        JsonPathExpression RelativePath,
        JsonPathExpression? DescriptorValuePath = null
    ) : WriteValueSource;

    /// <summary>
    /// Value is populated by table-local precompute logic (for example collection-key preallocation or
    /// key-unification).
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

    /// <summary>
    /// Reference-derived member source metadata.
    /// </summary>
    /// <param name="MemberPathColumn">The member-path binding column.</param>
    /// <param name="RelativePath">Member value path relative to the table scope.</param>
    /// <param name="ReferenceSource">
    /// The authoritative reference-site and logical-member metadata for the derived value.
    /// </param>
    /// <param name="PresenceColumn">
    /// Optional presence gate column used to preserve absent-versus-null semantics.
    /// </param>
    /// <param name="PresenceBindingIndex">
    /// Optional binding index in <see cref="TableWritePlan.ColumnBindings" /> for the presence column.
    /// </param>
    /// <param name="PresenceIsSynthetic">Indicates whether the presence column is synthetic.</param>
    public sealed record ReferenceDerivedMember(
        DbColumnName MemberPathColumn,
        JsonPathExpression RelativePath,
        ReferenceDerivedValueSourceMetadata ReferenceSource,
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
