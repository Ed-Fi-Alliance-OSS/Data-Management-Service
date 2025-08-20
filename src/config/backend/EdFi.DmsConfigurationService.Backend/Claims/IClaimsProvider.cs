// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Service for managing claims data including loading, providing, and reloading
/// </summary>
public interface IClaimsProvider
{
    /// <summary>
    /// Returns claims document nodes containing claim sets and claims hierarchy
    /// </summary>
    ClaimsDocument GetClaimsDocumentNodes();

    /// <summary>
    /// Gets the current reload identifier.
    /// This identifier changes whenever the claims are reloaded.
    /// </summary>
    Guid ReloadId { get; }

    /// <summary>
    /// Gets whether the currently loaded claims are valid
    /// </summary>
    bool IsClaimsValid { get; }

    /// <summary>
    /// Gets the failures from the last claims operation
    /// </summary>
    List<ClaimsFailure> ClaimsFailures { get; }

    /// <summary>
    /// Loads claims from the configured source
    /// </summary>
    /// <returns>The result of loading claims from source</returns>
    ClaimsLoadResult LoadClaimsFromSource();

    /// <summary>
    /// Updates the in-memory claims state after successful database update
    /// </summary>
    /// <param name="claimsNodes">The new claims document nodes</param>
    /// <param name="newReloadId">The new reload identifier</param>
    void UpdateInMemoryState(ClaimsDocument claimsNodes, Guid newReloadId);
}
