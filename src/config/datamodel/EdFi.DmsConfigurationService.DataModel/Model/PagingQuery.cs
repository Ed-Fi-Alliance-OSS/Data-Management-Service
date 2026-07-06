// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model;

/// <summary>
/// Used in queries where paging and sorting are supported.
/// </summary>
public class PagingQuery
{
    public int? Offset { get; set; }
    public int? Limit { get; set; }

    public string? OrderBy { get; set; }

    public string? Direction { get; set; }

    public bool IsDescending =>
        Direction is not null
        && (
            Direction.Equals("desc", StringComparison.OrdinalIgnoreCase)
            || Direction.Equals("descending", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// Returns a SQL LIMIT/OFFSET clause only when values are explicitly provided.
    /// Returns empty string when both are null, ensuring no implicit row cap is applied.
    /// When only Offset is provided (no Limit), returns "OFFSET @Offset" — valid in
    /// PostgreSQL but callers should verify their base SQL handles this correctly.
    /// </summary>
    public string BuildPagingClause()
    {
        if (!Limit.HasValue && !Offset.HasValue)
        {
            return string.Empty;
        }
        if (Limit.HasValue && Offset.HasValue)
        {
            return "LIMIT @Limit OFFSET @Offset";
        }
        if (Limit.HasValue)
        {
            return "LIMIT @Limit";
        }
        return "OFFSET @Offset";
    }

    /// <summary>
    /// Returns a SQL Server OFFSET/FETCH paging clause. Callers must place a
    /// deterministic ORDER BY immediately before it — OFFSET/FETCH is invalid without
    /// one. Always emits at least "OFFSET 0 ROWS" so the ORDER BY remains valid inside
    /// derived tables while applying no implicit row cap.
    /// </summary>
    public string BuildSqlServerPagingClause()
    {
        if (Limit.HasValue && Offset.HasValue)
        {
            return "OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY";
        }
        if (Limit.HasValue)
        {
            return "OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY";
        }
        if (Offset.HasValue)
        {
            return "OFFSET @Offset ROWS";
        }
        return "OFFSET 0 ROWS";
    }
}
