// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;

/// <summary>
/// Service responsible for loading initial claims data into the database during application startup
/// </summary>
public interface IClaimsDataLoader
{
    /// <summary>
    /// Loads initial claims data from Claims.json into the database if tables are empty
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
    Task<ClaimsDataLoadResult> UpdateClaimsAsync(ClaimsDocumentNodes claimsNodes);
}

/// <summary>
/// Result of claims data loading operation
/// </summary>
public abstract record ClaimsDataLoadResult
{
    /// <summary>
    /// Claims data was successfully loaded
    /// </summary>
    /// <param name="ClaimSetsLoaded">Number of claim sets loaded</param>
    /// <param name="HierarchyLoaded">Whether hierarchy was loaded</param>
    public record Success(int ClaimSetsLoaded, bool HierarchyLoaded) : ClaimsDataLoadResult;

    /// <summary>
    /// Claims data already exists, no loading performed
    /// </summary>
    public record AlreadyLoaded() : ClaimsDataLoadResult;

    /// <summary>
    /// Failed to load claims data due to validation errors
    /// </summary>
    /// <param name="Errors">List of validation errors</param>
    public record ValidationFailure(IReadOnlyList<string> Errors) : ClaimsDataLoadResult;

    /// <summary>
    /// Failed to load claims data due to database errors
    /// </summary>
    /// <param name="ErrorMessage">Database error message</param>
    public record DatabaseFailure(string ErrorMessage) : ClaimsDataLoadResult;

    /// <summary>
    /// Failed to load claims data due to unexpected error
    /// </summary>
    /// <param name="ErrorMessage">Error message</param>
    /// <param name="Exception">The exception that occurred</param>
    public record UnexpectedFailure(string ErrorMessage, Exception? Exception = null) : ClaimsDataLoadResult;
}
