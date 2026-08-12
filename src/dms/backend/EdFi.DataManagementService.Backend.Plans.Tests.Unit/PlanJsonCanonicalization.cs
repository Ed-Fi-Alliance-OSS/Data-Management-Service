// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class PlanJsonCanonicalization
{
    public static string NormalizeMultilineText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.ReplaceLineEndings("\n").TrimEnd();
    }

    public static string ToQueryParameterRoleToken(QuerySqlParameterRole value)
    {
        return value switch
        {
            QuerySqlParameterRole.Filter => "filter",
            QuerySqlParameterRole.Offset => "offset",
            QuerySqlParameterRole.Limit => "limit",
            QuerySqlParameterRole.CursorInclusiveMinimum => "cursor_inclusive_minimum",
            QuerySqlParameterRole.CursorInclusiveMaximum => "cursor_inclusive_maximum",
            QuerySqlParameterRole.PageSize => "page_size",
            QuerySqlParameterRole.PartitionCount => "partition_count",
            QuerySqlParameterRole.MinimumPartitionSize => "minimum_partition_size",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported query parameter role."
            ),
        };
    }

    public static string ToQueryParameterRoleToken(QuerySqlParameterRoleDto value)
    {
        return value switch
        {
            QuerySqlParameterRoleDto.Filter => "filter",
            QuerySqlParameterRoleDto.Offset => "offset",
            QuerySqlParameterRoleDto.Limit => "limit",
            QuerySqlParameterRoleDto.CursorInclusiveMinimum => "cursor_inclusive_minimum",
            QuerySqlParameterRoleDto.CursorInclusiveMaximum => "cursor_inclusive_maximum",
            QuerySqlParameterRoleDto.PageSize => "page_size",
            QuerySqlParameterRoleDto.PartitionCount => "partition_count",
            QuerySqlParameterRoleDto.MinimumPartitionSize => "minimum_partition_size",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported query parameter role DTO."
            ),
        };
    }

    /// <summary>
    /// Returns the canonical token for a non-traditional candidate mode.
    /// </summary>
    public static string ToPageCandidateModeToken(PageCandidateModeDto value)
    {
        return value switch
        {
            PageCandidateModeDto.Cursor => "cursor",
            PageCandidateModeDto.UnpagedCandidates => "unpaged_candidates",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported page candidate mode DTO."
            ),
        };
    }

    public static string ToQueryParameterBindingKindToken(QuerySqlParameterBindingKind value)
    {
        return value switch
        {
            QuerySqlParameterBindingKind.Scalar => "scalar",
            QuerySqlParameterBindingKind.PgsqlArray => "pgsql_array",
            QuerySqlParameterBindingKind.MssqlStructured => "mssql_structured",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported query parameter binding kind."
            ),
        };
    }

    public static string ToQueryParameterBindingKindToken(QuerySqlParameterBindingKindDto value)
    {
        return value switch
        {
            QuerySqlParameterBindingKindDto.Scalar => "scalar",
            QuerySqlParameterBindingKindDto.PgsqlArray => "pgsql_array",
            QuerySqlParameterBindingKindDto.MssqlStructured => "mssql_structured",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported query parameter binding kind DTO."
            ),
        };
    }
}
