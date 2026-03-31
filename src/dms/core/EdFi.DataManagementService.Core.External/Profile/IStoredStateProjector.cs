// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// C6 interface: produces stored-side projection for update/upsert flows.
/// Stub for now — implemented by C6 story (DMS-1118).
/// </summary>
public interface IStoredStateProjector
{
    /// <summary>
    /// Projects the stored document through the writable profile, producing
    /// stored-side scope states, visible stored collection rows, and the
    /// visible stored body. Assembles the complete ProfileAppliedWriteContext.
    /// </summary>
    ProfileAppliedWriteContext ProjectStoredState(
        ProfileAppliedWriteRequest request,
        StoredSideExistenceLookupResult existenceLookupResult
    );
}

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
