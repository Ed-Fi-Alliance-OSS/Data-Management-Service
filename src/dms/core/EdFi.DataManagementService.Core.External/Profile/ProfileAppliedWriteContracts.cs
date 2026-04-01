// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// The request-side contract produced by the profile write pipeline.
/// Backend consumes this to execute profile-constrained writes.
/// </summary>
public sealed record ProfileAppliedWriteRequest(
    JsonNode WritableRequestBody,
    bool RootResourceCreatable,
    ImmutableArray<RequestScopeState> RequestScopeStates,
    ImmutableArray<VisibleRequestCollectionItem> VisibleRequestCollectionItems
);

/// <summary>
/// Stored-side state for a non-collection scope, including which members
/// are hidden by the writable profile and must be preserved on update.
/// </summary>
public sealed record StoredScopeState(
    ScopeInstanceAddress Address,
    ProfileVisibilityKind Visibility,
    ImmutableArray<string> HiddenMemberPaths
);

/// <summary>
/// A visible stored collection row with hidden member paths that backend
/// must preserve during matched-row updates.
/// </summary>
public sealed record VisibleStoredCollectionRow(
    CollectionRowAddress Address,
    ImmutableArray<string> HiddenMemberPaths
);

/// <summary>
/// The full context for a profile-constrained write, combining request-side
/// and stored-side state. Produced by C5 for update/upsert flows.
/// </summary>
public sealed record ProfileAppliedWriteContext(
    ProfileAppliedWriteRequest Request,
    JsonNode VisibleStoredBody,
    ImmutableArray<StoredScopeState> StoredScopeStates,
    ImmutableArray<VisibleStoredCollectionRow> VisibleStoredCollectionRows
);

/// <summary>
/// Intermediate result from the stored-side existence lookup step that C6
/// extends rather than reclassifying from scratch. Contains the classified
/// scope/item visibility results from walking the stored document.
/// </summary>
public sealed record StoredSideExistenceLookupResult(
    IStoredSideExistenceLookup Lookup,
    ImmutableArray<StoredScopeState> ClassifiedStoredScopes,
    ImmutableArray<VisibleStoredCollectionRow> ClassifiedStoredCollectionRows
);
