// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

/// <summary>
/// Repository for atomic updates of the entire claims document (claim sets and hierarchy)
/// </summary>
public interface IClaimsDocumentRepository
{
    /// <summary>
    /// Replaces all non-system-reserved claim sets and the claims hierarchy in a single atomic transaction
    /// </summary>
    /// <param name="claimsNodes">The new claims document containing claim sets and hierarchy</param>
    /// <returns>Result indicating success or failure of the update operation</returns>
    Task<ClaimsDocumentUpdateResult> ReplaceClaimsDocument(ClaimsDocument claimsNodes);
}

public abstract record ClaimsDocumentUpdateResult
{
    /// <summary>
    /// Claims document was successfully updated
    /// </summary>
    /// <param name="ClaimSetsDeleted">Number of claim sets deleted</param>
    /// <param name="ClaimSetsInserted">Number of claim sets inserted</param>
    /// <param name="HierarchyUpdated">Whether hierarchy was updated</param>
    public record Success(int ClaimSetsDeleted, int ClaimSetsInserted, bool HierarchyUpdated)
        : ClaimsDocumentUpdateResult;

    /// <summary>
    /// Failed to update claims document due to validation errors
    /// </summary>
    /// <param name="Errors">List of validation errors</param>
    public record ValidationFailure(IReadOnlyList<string> Errors) : ClaimsDocumentUpdateResult;

    /// <summary>
    /// Failed to update claims document due to database errors
    /// </summary>
    /// <param name="ErrorMessage">Database error message</param>
    public record DatabaseFailure(string ErrorMessage) : ClaimsDocumentUpdateResult;

    /// <summary>
    /// The claims hierarchy was modified by another user
    /// </summary>
    public record MultiUserConflict() : ClaimsDocumentUpdateResult;

    /// <summary>
    /// Failed to update claims document due to unexpected error
    /// </summary>
    /// <param name="ErrorMessage">Error message</param>
    /// <param name="Exception">The exception that occurred</param>
    public record UnexpectedFailure(string ErrorMessage, Exception? Exception = null)
        : ClaimsDocumentUpdateResult;
}
