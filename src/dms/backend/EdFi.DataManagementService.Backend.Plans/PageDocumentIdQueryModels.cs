// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Represents a single value predicate over a root-table column.
/// </summary>
/// <param name="Column">The API-bound predicate column (binding/path column).</param>
/// <param name="Operator">The value-comparison operator.</param>
/// <param name="ParameterName">The bare SQL parameter name that supplies the value.</param>
public sealed record QueryValuePredicate(
    DbColumnName Column,
    QueryComparisonOperator Operator,
    string ParameterName
);

/// <summary>
/// Declares how a unified alias column maps to a canonical storage column and optional presence gate.
/// </summary>
/// <param name="AliasColumn">The API-bound alias/binding column.</param>
/// <param name="CanonicalColumn">The canonical storage column for value predicates.</param>
/// <param name="PresenceColumn">
/// Optional presence gate column. When populated, predicates on the alias imply this column is non-null.
/// </param>
public sealed record UnifiedAliasColumnMapping(
    DbColumnName AliasColumn,
    DbColumnName CanonicalColumn,
    DbColumnName? PresenceColumn
);

/// <summary>
/// Input specification for compiling page-<c>DocumentId</c> query SQL.
/// </summary>
/// <param name="RootTable">The resource root table queried for <c>DocumentId</c>.</param>
/// <param name="Predicates">Value predicates are treated as an unordered set; compiler emits them in deterministic sorted order after rewrite</param>
/// <param name="UnifiedAliasMappings">
/// Unified alias mappings used for canonical-column predicate rewrite.
/// </param>
/// <param name="OffsetParameterName">The bare paging offset parameter name.</param>
/// <param name="LimitParameterName">The bare paging limit parameter name.</param>
/// <param name="IncludeTotalCountSql">
/// Indicates whether the compiler should include total-count SQL in the emitted plan.
/// </param>
public sealed record PageDocumentIdQuerySpec(
    DbTableName RootTable,
    IReadOnlyList<QueryValuePredicate> Predicates,
    IReadOnlyList<UnifiedAliasColumnMapping> UnifiedAliasMappings,
    string OffsetParameterName = "offset",
    string LimitParameterName = "limit",
    bool IncludeTotalCountSql = false
);

/// <summary>
/// Compiled SQL for page keyset selection and total-count evaluation.
/// </summary>
/// <param name="PageDocumentIdSql">Parameterized SQL selecting a page of <c>DocumentId</c>s.</param>
/// <param name="TotalCountSql">
/// Parameterized SQL selecting total row count for the same filter set, when requested.
/// </param>
public sealed record PageDocumentIdSqlPlan(string PageDocumentIdSql, string? TotalCountSql);
