// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// SQL-side predicate target for a page-<c>DocumentId</c> query.
/// </summary>
public abstract record QueryPredicateTarget
{
    private QueryPredicateTarget() { }

    /// <summary>
    /// Predicate targets a root-table column.
    /// </summary>
    /// <param name="Column">The root-table column.</param>
    public sealed record RootColumn(DbColumnName Column) : QueryPredicateTarget;

    /// <summary>
    /// Predicate targets <c>dms.Document.DocumentUuid</c> and therefore requires the special-case document join.
    /// </summary>
    public sealed record DocumentUuid : QueryPredicateTarget;

    /// <summary>
    /// Predicate targets a shared <c>dms.Descriptor</c> table column and therefore requires the descriptor join.
    /// </summary>
    /// <param name="Column">The shared descriptor-table column.</param>
    public sealed record DescriptorColumn(DbColumnName Column) : QueryPredicateTarget;
}

/// <summary>
/// Represents a single value predicate over a root-table column.
/// </summary>
/// <param name="Target">The SQL-side predicate target.</param>
/// <param name="Operator">The value-comparison operator.</param>
/// <param name="ParameterName">The bare SQL parameter name that supplies the value.</param>
/// <param name="ScalarKind">
/// Optional scalar-kind metadata for the predicate value. Used by SQL emission for provider-specific string-comparison
/// semantics.
/// </param>
public sealed record QueryValuePredicate(
    QueryPredicateTarget Target,
    QueryComparisonOperator Operator,
    string ParameterName,
    ScalarKind? ScalarKind = null
)
{
    /// <summary>
    /// Initializes a root-column predicate.
    /// </summary>
    public QueryValuePredicate(
        DbColumnName Column,
        QueryComparisonOperator Operator,
        string ParameterName,
        ScalarKind? ScalarKind = null
    )
        : this(new QueryPredicateTarget.RootColumn(Column), Operator, ParameterName, ScalarKind) { }
}

/// <summary>
/// Input specification for compiling page-<c>DocumentId</c> query SQL.
/// </summary>
/// <param name="RootTable">The resource root table queried for <c>DocumentId</c>.</param>
/// <param name="Predicates">Value predicates are treated as an unordered set; compiler emits them in deterministic sorted order after rewrite</param>
/// <param name="UnifiedAliasMappingsByColumn">
/// Unified alias metadata keyed by API-bound alias/binding column for canonical-column predicate rewrite.
/// </param>
/// <param name="OffsetParameterName">The bare paging offset parameter name.</param>
/// <param name="LimitParameterName">The bare paging limit parameter name.</param>
/// <param name="IncludeTotalCountSql">
/// Indicates whether the compiler should include total-count SQL in the emitted plan.
/// </param>
public sealed record PageDocumentIdQuerySpec(
    DbTableName RootTable,
    IReadOnlyList<QueryValuePredicate> Predicates,
    IReadOnlyDictionary<DbColumnName, ColumnStorage.UnifiedAlias> UnifiedAliasMappingsByColumn,
    string OffsetParameterName = "offset",
    string LimitParameterName = "limit",
    bool IncludeTotalCountSql = false
);
