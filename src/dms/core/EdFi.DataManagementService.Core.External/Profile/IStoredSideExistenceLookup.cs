// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Address-keyed lookup answering "does a visible stored scope/item exist at
/// this address?" Built by C5, consumed by C4 for creatability decisions.
/// </summary>
public interface IStoredSideExistenceLookup
{
    /// <summary>
    /// Returns true when a visible stored non-collection scope exists at the
    /// given address. Hidden scopes are not reported as existing.
    /// </summary>
    bool VisibleScopeExistsAt(ScopeInstanceAddress address);

    /// <summary>
    /// Returns true when a visible stored collection row exists with the same
    /// compiled semantic identity at the given address. Rows that fail the
    /// profile's item value filter are not visible and not reported.
    /// </summary>
    bool VisibleCollectionRowExistsAt(CollectionRowAddress address);
}
