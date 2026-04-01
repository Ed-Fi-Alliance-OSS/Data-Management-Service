// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Detects duplicate visible collection items that share the same compiled
/// semantic identity within the same stable parent address. Duplicates are
/// rejected with C8 category-3 typed failures before backend receives the contract.
/// </summary>
public static class DuplicateCollectionItemDetector
{
    /// <summary>
    /// Checks for duplicate visible collection items and returns category-3
    /// failures for any collisions found.
    /// </summary>
    public static ImmutableArray<WritableProfileValidationFailure> Detect(
        ImmutableArray<VisibleRequestCollectionItem> items,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        if (items.Length <= 1)
        {
            return [];
        }

        // Group items by their CollectionRowAddress using structural equality
        var groups = new Dictionary<CollectionRowAddress, List<VisibleRequestCollectionItem>>(
            CollectionRowAddressComparer.Instance
        );

        foreach (var item in items)
        {
            if (!groups.TryGetValue(item.Address, out var group))
            {
                group = [];
                groups[item.Address] = group;
            }
            group.Add(item);
        }

        ImmutableArray<WritableProfileValidationFailure>.Builder? failures = null;

        foreach (var (address, group) in groups)
        {
            if (group.Count > 1)
            {
                failures ??= ImmutableArray.CreateBuilder<WritableProfileValidationFailure>();

                IEnumerable<string> requestPaths = group.Select(item => item.RequestJsonPath);

                failures.Add(
                    ProfileFailures.DuplicateVisibleCollectionItemCollision(
                        profileName: profileName,
                        resourceName: resourceName,
                        method: method,
                        operation: operation,
                        jsonScope: address.JsonScope,
                        stableParentAddress: address.ParentAddress,
                        semanticIdentityPartsInOrder: address.SemanticIdentityInOrder,
                        requestJsonPaths: requestPaths
                    )
                );
            }
        }

        return failures?.ToImmutable() ?? [];
    }
}
