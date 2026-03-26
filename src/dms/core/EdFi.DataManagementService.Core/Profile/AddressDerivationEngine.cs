// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Derives ScopeInstanceAddress and CollectionRowAddress from a pre-indexed compiled
/// scope catalog plus caller-provided JSON context, implementing the normative 7-step
/// algorithm from profiles.md.
/// </summary>
public class AddressDerivationEngine(IReadOnlyList<CompiledScopeDescriptor> scopeCatalog)
{
    private readonly IReadOnlyDictionary<string, CompiledScopeDescriptor> _scopesByJsonScope =
        scopeCatalog.ToDictionary(s => s.JsonScope);

    /// <summary>
    /// Derives a ScopeInstanceAddress for a non-collection scope.
    /// </summary>
    /// <param name="jsonScope">The compiled JsonScope to address.</param>
    /// <param name="ancestorItems">
    /// Caller-provided concrete collection items on the traversal path, one per
    /// ancestor collection scope.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the scope is not found in the catalog or is a Collection scope.
    /// </exception>
    public ScopeInstanceAddress DeriveScopeInstanceAddress(
        string jsonScope,
        IReadOnlyList<AncestorItemContext> ancestorItems
    )
    {
        var descriptor = ResolveDescriptor(jsonScope);

        if (descriptor.ScopeKind == ScopeKind.Collection)
        {
            throw new InvalidOperationException(
                $"Cannot derive ScopeInstanceAddress for Collection scope '{jsonScope}'. "
                    + "Use DeriveCollectionRowAddress instead."
            );
        }

        var ancestors = DeriveAncestorCollectionInstances(
            descriptor.CollectionAncestorsInOrder,
            ancestorItems
        );
        return new ScopeInstanceAddress(jsonScope, ancestors);
    }

    /// <summary>
    /// Derives a CollectionRowAddress for a collection scope item.
    /// </summary>
    /// <param name="jsonScope">The compiled JsonScope of the collection.</param>
    /// <param name="collectionItem">The concrete JSON collection item being addressed.</param>
    /// <param name="ancestorItems">
    /// Caller-provided concrete collection items on the traversal path, one per
    /// ancestor collection scope.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the scope is not found or is not a Collection scope.
    /// </exception>
    public CollectionRowAddress DeriveCollectionRowAddress(
        string jsonScope,
        JsonNode collectionItem,
        IReadOnlyList<AncestorItemContext> ancestorItems
    )
    {
        var descriptor = ResolveDescriptor(jsonScope);

        if (descriptor.ScopeKind != ScopeKind.Collection)
        {
            throw new InvalidOperationException(
                $"Cannot derive CollectionRowAddress for non-Collection scope '{jsonScope}'. "
                    + "Use DeriveScopeInstanceAddress instead."
            );
        }

        var parentAddress = DeriveScopeInstanceAddress(descriptor.ImmediateParentJsonScope!, ancestorItems);

        var semanticIdentity = ReadSemanticIdentity(
            descriptor.SemanticIdentityRelativePathsInOrder,
            collectionItem
        );

        return new CollectionRowAddress(jsonScope, parentAddress, semanticIdentity);
    }

    private CompiledScopeDescriptor ResolveDescriptor(string jsonScope)
    {
        if (!_scopesByJsonScope.TryGetValue(jsonScope, out var descriptor))
        {
            throw new InvalidOperationException(
                $"Compiled scope descriptor not found for JsonScope '{jsonScope}'."
            );
        }
        return descriptor;
    }

    private ImmutableArray<AncestorCollectionInstance> DeriveAncestorCollectionInstances(
        ImmutableArray<string> collectionAncestorsInOrder,
        IReadOnlyList<AncestorItemContext> ancestorItems
    )
    {
        if (collectionAncestorsInOrder.IsEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<AncestorCollectionInstance>(
            collectionAncestorsInOrder.Length
        );

        foreach (var ancestorJsonScope in collectionAncestorsInOrder)
        {
            var ancestorDescriptor = ResolveDescriptor(ancestorJsonScope);

            var ancestorItem = FindAncestorItem(ancestorJsonScope, ancestorItems);

            var identity = ReadSemanticIdentity(
                ancestorDescriptor.SemanticIdentityRelativePathsInOrder,
                ancestorItem
            );

            builder.Add(new AncestorCollectionInstance(ancestorJsonScope, identity));
        }

        return builder.MoveToImmutable();
    }

    private static JsonNode FindAncestorItem(
        string ancestorJsonScope,
        IReadOnlyList<AncestorItemContext> ancestorItems
    )
    {
        for (int i = 0; i < ancestorItems.Count; i++)
        {
            if (ancestorItems[i].JsonScope == ancestorJsonScope)
            {
                return ancestorItems[i].Item;
            }
        }

        throw new InvalidOperationException(
            $"No AncestorItemContext provided for ancestor collection scope '{ancestorJsonScope}'."
        );
    }

    private static ImmutableArray<SemanticIdentityPart> ReadSemanticIdentity(
        ImmutableArray<string> relativePathsInOrder,
        JsonNode item
    )
    {
        if (relativePathsInOrder.IsEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<SemanticIdentityPart>(relativePathsInOrder.Length);

        var obj = item.AsObject();

        foreach (var relativePath in relativePathsInOrder)
        {
            if (obj.TryGetPropertyValue(relativePath, out var value))
            {
                builder.Add(new SemanticIdentityPart(relativePath, value?.DeepClone(), true));
            }
            else
            {
                builder.Add(new SemanticIdentityPart(relativePath, null, false));
            }
        }

        return builder.MoveToImmutable();
    }
}
