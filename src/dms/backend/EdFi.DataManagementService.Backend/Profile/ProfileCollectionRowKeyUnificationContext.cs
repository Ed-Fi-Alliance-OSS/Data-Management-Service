// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Context for resolving key-unification on a single matched top-level collection row.
/// Parallels <see cref="ProfileRootKeyUnificationContext"/> but sources member visibility
/// from an explicit <see cref="HiddenMemberPaths"/> set (per-row, from the matched
/// <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>) instead of deriving it
/// from scope states.
/// </summary>
/// <param name="RequestItemNode">
/// Concrete request item JSON node, resolved from the writable request body by
/// evaluating the matched <see cref="VisibleRequestCollectionItem.RequestJsonPath"/>
/// (e.g. "$.classPeriods[0]"). NOT the collection-level JsonScope.
/// </param>
/// <param name="CurrentRowByColumnName">
/// Current-row values keyed by <see cref="DbColumnName"/>, from the matched
/// <see cref="CurrentCollectionRowSnapshot.ProjectedCurrentRow"/>.
/// </param>
/// <param name="HiddenMemberPaths">
/// Hidden member paths from the matched <see cref="VisibleStoredCollectionRow.HiddenMemberPaths"/>.
/// Replaces the scope-state-derived hidden-path set used by the root and separate-table resolvers.
/// </param>
/// <param name="ResolvedReferenceLookups">
/// Resolved reference lookups compiled once per synthesis pass, reused across all
/// key-unification plans evaluated for the row.
/// </param>
internal sealed record ProfileCollectionRowKeyUnificationContext(
    JsonNode RequestItemNode,
    IReadOnlyDictionary<DbColumnName, object?> CurrentRowByColumnName,
    ImmutableArray<string> HiddenMemberPaths,
    FlatteningResolvedReferenceLookupSet ResolvedReferenceLookups
);
