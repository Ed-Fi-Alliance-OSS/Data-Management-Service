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
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported query parameter role DTO."
            ),
        };
    }
}
