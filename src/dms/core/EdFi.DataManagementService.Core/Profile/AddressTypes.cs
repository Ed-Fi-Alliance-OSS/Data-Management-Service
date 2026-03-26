// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Address for a non-collection scope instance (root or 1:1).
/// </summary>
/// <param name="JsonScope">Compiled JsonScope identifier.</param>
/// <param name="AncestorCollectionInstances">
/// Ancestor collection instances from root-most to immediate parent, each keyed
/// by compiled semantic identity.
/// </param>
public sealed record ScopeInstanceAddress(
    string JsonScope,
    ImmutableArray<AncestorCollectionInstance> AncestorCollectionInstances
);

/// <summary>
/// One ancestor collection instance on the traversal path, identified by compiled
/// semantic identity.
/// </summary>
/// <param name="JsonScope">Compiled JsonScope of the ancestor collection.</param>
/// <param name="SemanticIdentityInOrder">
/// Semantic identity parts in compiled order for the ancestor collection item.
/// </param>
public sealed record AncestorCollectionInstance(
    string JsonScope,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder
);

/// <summary>
/// Address for a visible collection row/item.
/// </summary>
/// <param name="JsonScope">Compiled JsonScope of the collection.</param>
/// <param name="ParentAddress">
/// ScopeInstanceAddress of the immediate containing scope instance.
/// </param>
/// <param name="SemanticIdentityInOrder">
/// Semantic identity parts in compiled order for this collection item.
/// </param>
public sealed record CollectionRowAddress(
    string JsonScope,
    ScopeInstanceAddress ParentAddress,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder
);

/// <summary>
/// One part of a compiled semantic identity.
/// </summary>
/// <param name="RelativePath">
/// Adapter-published canonical scope-relative path for the identity member.
/// </param>
/// <param name="Value">
/// JSON value at that path. Null when the property is missing or explicit JSON null.
/// </param>
/// <param name="IsPresent">
/// True if the property exists in JSON (even if null). False if the property is absent.
/// Preserves missing-vs-explicit-null semantics.
/// </param>
public sealed record SemanticIdentityPart(string RelativePath, JsonNode? Value, bool IsPresent);

/// <summary>
/// Caller-provided context identifying one concrete collection item on the JSON
/// traversal path to the addressed scope.
/// </summary>
/// <param name="JsonScope">Compiled JsonScope of the ancestor collection.</param>
/// <param name="Item">The concrete JSON collection item on the traversal path.</param>
public sealed record AncestorItemContext(string JsonScope, JsonNode Item);
