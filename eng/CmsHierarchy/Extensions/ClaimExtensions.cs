// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using CmsHierarchy.Model;

namespace CmsHierarchy.Extensions;

public static class ClaimExtensions
{
    public static void RemoveAuthorizationStrategies(
        this IEnumerable<Claim> claims,
        IEnumerable<string> skippedAuths
    )
    {
        var skipSet = new HashSet<string>(skippedAuths, StringComparer.OrdinalIgnoreCase);
        RemoveAuthsRecursive(claims, skipSet);

        void RemoveAuthsRecursive(IEnumerable<Claim> claims, HashSet<string> skipSet)
        {
            foreach (var claim in claims)
            {
                var actions = claim.DefaultAuthorization?.Actions;
                if (actions != null)
                {
                    foreach (var action in actions)
                    {
                        action.AuthorizationStrategies?.RemoveAll(s =>
                            s?.Name != null && skipSet.Contains(s.Name)
                        );
                    }
                }

                if (claim.Claims != null && claim.Claims.Count > 0)
                {
                    RemoveAuthsRecursive(claim.Claims, skipSet);
                }
            }
        }
    }
}
