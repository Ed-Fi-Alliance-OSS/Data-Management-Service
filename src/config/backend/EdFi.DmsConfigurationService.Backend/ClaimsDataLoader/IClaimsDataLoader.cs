// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;

/// <summary>
/// Service responsible for loading claims data into the database
/// </summary>
public interface IClaimsDataLoader
{
    /// <summary>
    /// Loads claims data from Claims.json into the database if tables are empty
    /// </summary>
    /// <returns>Result indicating success or failure of the load operation</returns>
    Task<ClaimsDataLoadResult> LoadInitialClaimsAsync();

    /// <summary>
    /// Checks if both ClaimSet and ClaimsHierarchy tables are empty
    /// </summary>
    /// <returns>True if both tables are empty, false otherwise</returns>
    Task<bool> AreClaimsTablesEmptyAsync();

    /// <summary>
    /// Updates claims data in the database by replacing existing claims with new claims.
    /// This operation is transactional - either all updates succeed or all are rolled back.
    /// </summary>
    /// <param name="claimsNodes">The new claims data to load, containing both claim sets and claims hierarchy</param>
    /// <returns>Result indicating success or failure of the update operation</returns>
    Task<ClaimsDataLoadResult> UpdateClaimsAsync(ClaimsDocument claimsNodes);
}
