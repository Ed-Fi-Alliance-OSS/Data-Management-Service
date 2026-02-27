// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled SQL plan for selecting a page of <c>DocumentId</c>s and optional total count.
/// </summary>
/// <param name="PageDocumentIdSql">SQL selecting a page of <c>DocumentId</c>s.</param>
/// <param name="TotalCountSql">Optional SQL selecting a total row count over the same filters.</param>
/// <param name="ParametersInOrder">
/// Deterministic inventory of plan parameters in canonical order (filters as emitted, then paging roles).
/// Executors bind parameters by name; this ordering does not necessarily match placeholder appearance per dialect.
/// </param>
public sealed record PageDocumentIdSqlPlan
{
    public PageDocumentIdSqlPlan(
        string PageDocumentIdSql,
        string? TotalCountSql,
        IEnumerable<QuerySqlParameter> ParametersInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(PageDocumentIdSql);

        this.PageDocumentIdSql = PageDocumentIdSql;
        this.TotalCountSql = TotalCountSql;
        this.ParametersInOrder = PlanContractCollectionCloner.ToImmutableArray(
            ParametersInOrder,
            nameof(ParametersInOrder)
        );
    }

    public string PageDocumentIdSql { get; init; }

    public string? TotalCountSql { get; init; }

    public ImmutableArray<QuerySqlParameter> ParametersInOrder { get; init; }
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
