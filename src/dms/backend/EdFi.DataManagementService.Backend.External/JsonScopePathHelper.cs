// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Utility methods for JSON scope path manipulation shared across backend components.
/// </summary>
public static class JsonScopePathHelper
{
    /// <summary>
    /// Given an extension-qualified JSON scope (e.g. <c>$.addresses[*]._ext.sample.someScope</c>),
    /// strips all <c>_ext.&lt;extensionName&gt;</c> segments and returns the aligned base scope
    /// (e.g. <c>$.addresses[*].someScope</c>). Returns <c>null</c> when the scope contains no
    /// extension segments or the result would be degenerate.
    /// </summary>
    public static string? ResolveAlignedBaseJsonScope(string jsonScope)
    {
        if (!jsonScope.Contains("._ext.", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = jsonScope.Split('.');
        List<string> baseScopeSegments = [];
        var index = 0;

        while (index < segments.Length)
        {
            if (string.Equals(segments[index], "_ext", StringComparison.Ordinal))
            {
                if (index + 1 >= segments.Length)
                {
                    return null;
                }

                index += 2;
                continue;
            }

            baseScopeSegments.Add(segments[index]);
            index++;
        }

        if (baseScopeSegments.Count == 0 || baseScopeSegments.Count == segments.Length)
        {
            return null;
        }

        return string.Join(".", baseScopeSegments);
    }
}
