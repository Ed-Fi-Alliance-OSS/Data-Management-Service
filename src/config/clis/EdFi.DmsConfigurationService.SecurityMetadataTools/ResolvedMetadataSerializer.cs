// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using MetadataAction = EdFi.DmsConfigurationService.Backend.AuthorizationMetadata.ClaimSetMetadata.Action;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

/// <summary>
/// Canonical presentation of a shared resolver result for offline commands only.
/// IDs are allocated across the ordinal catalog, then by canonical permission values.
/// Strategy positions remain significant, even though shared resolver equality ignores them.
/// This cannot recover strategy order already discarded by the shared resolver.
/// </summary>
internal static class ResolvedMetadataSerializer
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    private static readonly IComparer<MetadataAction[]> _permissionComparer =
        Comparer<MetadataAction[]>.Create(ComparePermissions);

    internal static string Serialize(AuthorizationMetadataResponse metadata)
    {
        List<ClaimSetMetadata> canonicalClaimSets = [];
        int nextAuthorizationId = 1;

        foreach (var claimSet in metadata.ClaimSets.OrderBy(set => set.ClaimSetName, StringComparer.Ordinal))
        {
            // Incoming IDs are scoped to their containing claim set. Copy action arrays so
            // presentation never mutates the shared response or its strategy sequences.
            var permissionsByOriginalId = claimSet.Authorizations.ToDictionary(
                authorization => authorization.Id,
                authorization =>
                    authorization.Actions.OrderBy(action => action.Name, StringComparer.Ordinal).ToArray()
            );
            SortedDictionary<MetadataAction[], int> canonicalIds = new(_permissionComparer);
            foreach (var permissions in permissionsByOriginalId.Values)
            {
                canonicalIds.TryAdd(permissions, 0);
            }

            foreach (var permissions in canonicalIds.Keys.ToArray())
            {
                canonicalIds[permissions] = nextAuthorizationId++;
            }

            canonicalClaimSets.Add(
                new(
                    claimSet.ClaimSetName,
                    claimSet
                        .Claims.OrderBy(claim => claim.Name, StringComparer.Ordinal)
                        .Select(claim => new ClaimSetMetadata.Claim(
                            claim.Name,
                            canonicalIds[permissionsByOriginalId[claim.AuthorizationId]]
                        ))
                        .ToList(),
                    canonicalIds
                        .Select(pair => new ClaimSetMetadata.Authorization(pair.Value, pair.Key))
                        .ToList()
                )
            );
        }

        return JsonSerializer.Serialize(canonicalClaimSets, _jsonOptions);
    }

    // Compare the actual values, not hashes or the shared order-insensitive record equality.
    // Lexicographic comparison preserves sequence positions and repeated strategy names.
    private static int ComparePermissions(MetadataAction[] left, MetadataAction[] right)
    {
        for (int actionIndex = 0; actionIndex < Math.Min(left.Length, right.Length); actionIndex++)
        {
            var leftAction = left[actionIndex];
            var rightAction = right[actionIndex];
            int comparison = StringComparer.Ordinal.Compare(leftAction.Name, rightAction.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            var leftStrategies = leftAction.AuthorizationStrategies;
            var rightStrategies = rightAction.AuthorizationStrategies;
            for (
                int strategyIndex = 0;
                strategyIndex < Math.Min(leftStrategies.Length, rightStrategies.Length);
                strategyIndex++
            )
            {
                comparison = StringComparer.Ordinal.Compare(
                    leftStrategies[strategyIndex].Name,
                    rightStrategies[strategyIndex].Name
                );
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            comparison = leftStrategies.Length.CompareTo(rightStrategies.Length);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }
}
