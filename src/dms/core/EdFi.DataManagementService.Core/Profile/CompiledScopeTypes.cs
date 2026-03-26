// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Classifies a compiled scope for address derivation.
/// Does not distinguish inlined-vs-separate-table storage topology;
/// that is a backend-only concern.
/// </summary>
public enum ScopeKind
{
    Root,
    NonCollection,
    Collection,
}

/// <summary>
/// Immutable descriptor for one compiled scope in a resource's scope catalog.
/// Built by backend from TableWritePlan / DbTableModel metadata.
/// Core consumes this narrowed adapter for address derivation and canonical vocabulary.
/// </summary>
/// <remarks>
/// <para><strong>ProfileDefinition compatibility assessment (DMS-1111):</strong></para>
/// <para>
/// The existing <see cref="ProfileDefinition"/> tree and this compiled-scope catalog operate
/// at different levels of abstraction. <see cref="ProfileDefinition"/> uses a recursive tree
/// of rules keyed by member name at each level (e.g. <c>CollectionRule.Name = "classPeriods"</c>),
/// while the adapter uses flat compiled <see cref="JsonScope"/> paths
/// (e.g. <c>$.classPeriods[*]</c>).
/// </para>
/// <para>
/// The bridge is straightforward: given a <see cref="JsonScope"/> like <c>$.classPeriods[*]</c>,
/// strip the <c>[*]</c> suffix and take the last dot-separated segment to get <c>"classPeriods"</c>,
/// which maps directly to <c>CollectionRule.Name</c>. Extension scopes follow the same pattern:
/// <c>$._ext.sample</c> navigates to <c>ExtensionRule</c> named <c>"sample"</c>.
/// </para>
/// <para>
/// No structural changes to <see cref="ProfileDefinition"/> are needed. C3 (request-side
/// visibility classification) should own a small utility to correlate adapter
/// <see cref="JsonScope"/> paths with <see cref="ProfileDefinition"/> tree positions by
/// member name extraction. C6 (stored-state projection) reuses the same bridge.
/// </para>
/// </remarks>
/// <param name="JsonScope">
/// Exact compiled scope identifier (e.g. "$", "$.classPeriods[*]").
/// Matches DbTableModel.JsonScope.Canonical.
/// </param>
/// <param name="ScopeKind">Root, NonCollection, or Collection.</param>
/// <param name="ImmediateParentJsonScope">
/// Compiled parent scope. Null for root. Collection-aligned _ext scopes point at
/// the aligned base scope.
/// </param>
/// <param name="CollectionAncestorsInOrder">
/// Collection scopes from root-most to immediate parent collection ancestor.
/// </param>
/// <param name="SemanticIdentityRelativePathsInOrder">
/// Non-empty compiled semantic identity member paths for persisted multi-item
/// collection scopes. Empty for non-collection scopes.
/// </param>
/// <param name="CanonicalScopeRelativeMemberPaths">
/// Canonical vocabulary for SemanticIdentityPart.RelativePath and HiddenMemberPaths.
/// </param>
public sealed record CompiledScopeDescriptor(
    string JsonScope,
    ScopeKind ScopeKind,
    string? ImmediateParentJsonScope,
    ImmutableArray<string> CollectionAncestorsInOrder,
    ImmutableArray<string> SemanticIdentityRelativePathsInOrder,
    ImmutableArray<string> CanonicalScopeRelativeMemberPaths
);
