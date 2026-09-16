// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Utilities;

public static class RouteContextMatcher
{
    public static bool IsMatch(
        Dictionary<RouteQualifierName, RouteQualifierValue> instanceRouteContext,
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers
    )
    {
        if (instanceRouteContext.Count != requestQualifiers.Count)
        {
            return false;
        }

        if (instanceRouteContext.Count == 0)
        {
            return true;
        }

        if (!instanceRouteContext.Keys.All(requestQualifiers.ContainsKey))
        {
            return false;
        }

        foreach (KeyValuePair<RouteQualifierName, RouteQualifierValue> kvp in instanceRouteContext)
        {
            if (
                !requestQualifiers.TryGetValue(kvp.Key, out RouteQualifierValue requestValue)
                || !kvp.Value.Value.Equals(requestValue.Value, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }
        }

        return true;
    }
}
