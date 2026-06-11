// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Builds the route prefix for fixed (non-resource) endpoints that sit at the server root rather
/// than under /data, such as /oauth/token_info and /changeQueries/v1/availableChangeVersions.
/// When multitenancy is enabled, prepends {tenant} as the first segment, followed by any
/// configured route-qualifier segments.
/// Examples:
/// - No multitenancy, no qualifiers: ""
/// - No multitenancy, with qualifiers: "/{districtId}/{schoolYear}"
/// - Multitenancy, no qualifiers: "/{tenant}"
/// - Multitenancy, with qualifiers: "/{tenant}/{districtId}/{schoolYear}"
/// </summary>
internal static class FixedRoutePattern
{
    public static string Build(string[] routeQualifierSegments, bool multiTenancy)
    {
        var segments = new List<string>();

        if (multiTenancy)
        {
            segments.Add("{tenant}");
        }

        if (routeQualifierSegments.Length > 0)
        {
            segments.AddRange(routeQualifierSegments.Select(s => $"{{{s}}}"));
        }

        return segments.Count == 0 ? string.Empty : $"/{string.Join("/", segments)}";
    }
}
