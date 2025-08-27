// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;

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
