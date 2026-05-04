// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Mode for <see cref="ProfileCollectionWalker.WalkChildren"/>.
/// </summary>
internal enum WalkMode
{
    /// <summary>
    /// Run the planner / decider for each direct child scope and emit merged rows
    /// based on the request, stored, and current state.
    /// </summary>
    Normal,

    /// <summary>
    /// Skip the planner / decider; emit identity merged-rows for every current row
    /// under the parent's physical identity, then recurse into descendants in
    /// <see cref="Preserve"/> mode. Hidden subtree preservation.
    /// </summary>
    Preserve,
}

/// <summary>
/// Per-call context passed through <see cref="ProfileCollectionWalker.WalkChildren"/>.
/// Contains the parent's structural address (the synthetic ScopeInstanceAddress that
/// direct children see as their <c>ParentAddress</c>), the parent's physical row
/// identity values for FK rewriting, and the request-side substructure rooted at
/// this parent.
/// </summary>
/// <param name="ContainingScopeAddress">
/// The synthetic <see cref="ScopeInstanceAddress"/> that direct children of this parent
/// will see as their <c>Address.ParentAddress</c>. For the root, this is
/// <c>($, [])</c>. For a root-extension row, the root-extension's structural address.
/// For a collection row, the parent collection's <c>JsonScope</c> with the existing
/// ancestor chain extended by an <see cref="AncestorCollectionInstance"/> for this row.
/// Address construction is structural — never string append.
/// </param>
/// <param name="ParentPhysicalIdentityValues">
/// The parent row's physical identity values. Used to rewrite FK columns on direct
/// child rows and to key the current-row indexes.
/// </param>
/// <param name="RequestSubstructure">
/// The request-side substructure rooted at this parent: a <see cref="RootWriteRowBuffer"/>,
/// a <see cref="RootExtensionWriteRowBuffer"/>, a <see cref="CollectionWriteCandidate"/>,
/// or a <see cref="CandidateAttachedAlignedScopeData"/>. The walker uses this to find
/// nested collection candidates and aligned scope data attached specifically to this
/// parent. May be <c>null</c> in <see cref="WalkMode.Preserve"/> mode (hidden subtree
/// has no request substructure to walk; child enumeration is topology-driven).
/// </param>
/// <param name="ParentRequestNode">
/// The parent's concrete request body node (the JSON node for this row's request item).
/// Used to derive scoped request nodes for aligned-extension scopes. May be <c>null</c>
/// when no request substructure exists.
/// </param>
internal sealed record ProfileCollectionWalkerContext(
    ScopeInstanceAddress ContainingScopeAddress,
    ImmutableArray<FlattenedWriteValue> ParentPhysicalIdentityValues,
    object? RequestSubstructure,
    JsonNode? ParentRequestNode
);
