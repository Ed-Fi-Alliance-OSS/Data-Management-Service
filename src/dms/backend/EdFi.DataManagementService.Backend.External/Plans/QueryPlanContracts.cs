// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled SQL plan for selecting a page of <c>DocumentId</c>s and optional total count.
/// </summary>
/// <remarks>
/// Carries canonical SQL plus an explicit parameter inventory so executors never infer bindings from SQL-text parsing.
/// </remarks>
public sealed record PageDocumentIdSqlPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PageDocumentIdSqlPlan" /> record.
    /// </summary>
    /// <param name="PageDocumentIdSql">SQL selecting a page of <c>DocumentId</c>s.</param>
    /// <param name="TotalCountSql">Optional SQL selecting a total row count over the same filters.</param>
    /// <param name="PageParametersInOrder">
    /// Deterministic inventory of page-query parameters in canonical order
    /// (filters as emitted, then paging roles).
    /// Executors bind parameters by name; this ordering does not necessarily match placeholder appearance per dialect.
    /// </param>
    /// <param name="TotalCountParametersInOrder">
    /// Optional deterministic inventory of total-count query parameters in canonical order (filters only).
    /// Must be null when <paramref name="TotalCountSql" /> is null, and is required when
    /// <paramref name="TotalCountSql" /> is provided (may be empty when no filter parameters exist).
    /// </param>
    public PageDocumentIdSqlPlan(
        string PageDocumentIdSql,
        string? TotalCountSql,
        IEnumerable<QuerySqlParameter> PageParametersInOrder,
        IEnumerable<QuerySqlParameter>? TotalCountParametersInOrder
    )
    {
        this.PageDocumentIdSql = PlanContractArgumentValidator.RequireNotNull(
            PageDocumentIdSql,
            nameof(PageDocumentIdSql)
        );
        this.TotalCountSql = TotalCountSql;
        this.PageParametersInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            PageParametersInOrder,
            nameof(PageParametersInOrder)
        );
        this.TotalCountParametersInOrder = PlanContractArgumentValidator.RequireImmutableArrayWhenSqlPresent(
            TotalCountSql,
            nameof(TotalCountSql),
            TotalCountParametersInOrder,
            nameof(TotalCountParametersInOrder)
        );
    }

    /// <summary>
    /// SQL that selects a single page of <c>DocumentId</c>s.
    /// </summary>
    public string PageDocumentIdSql { get; init; }

    /// <summary>
    /// Optional SQL that selects a total row count over the same filters.
    /// </summary>
    public string? TotalCountSql { get; init; }

    /// <summary>
    /// Deterministic inventory of page-query parameters in canonical order.
    /// </summary>
    public ImmutableArray<QuerySqlParameter> PageParametersInOrder { get; init; }

    /// <summary>
    /// Optional deterministic inventory of total-count query parameters in canonical order (filters only).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null means the total-count query is absent (<see cref="TotalCountSql" /> is null).
    /// When <see cref="TotalCountSql" /> is provided, this inventory is guaranteed to be materialized and non-default
    /// (but may be empty). Callers should check <c>TotalCountParametersInOrder is null</c> (or <c>.HasValue</c>)
    /// before accessing <c>.Value</c>.
    /// </para>
    /// </remarks>
    public ImmutableArray<QuerySqlParameter>? TotalCountParametersInOrder { get; init; }
}

/// <summary>
/// One query-SQL parameter contract entry.
/// </summary>
/// <param name="Role">The semantic role of the parameter in the plan.</param>
/// <param name="ParameterName">The bare SQL parameter name (without <c>@</c>).</param>
public sealed record QuerySqlParameter(QuerySqlParameterRole Role, string ParameterName);

/// <summary>
/// Classifies the semantic role of a query-SQL parameter in <see cref="PageDocumentIdSqlPlan" />.
/// </summary>
public enum QuerySqlParameterRole
{
    /// <summary>
    /// Parameter for a filter predicate.
    /// </summary>
    Filter,

    /// <summary>
    /// Parameter for page offset.
    /// </summary>
    Offset,

    /// <summary>
    /// Parameter for page size limit.
    /// </summary>
    Limit,
}
