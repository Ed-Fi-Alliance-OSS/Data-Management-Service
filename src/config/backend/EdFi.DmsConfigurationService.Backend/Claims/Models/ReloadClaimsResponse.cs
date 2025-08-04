// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// Response model for claims reload operations
/// </summary>
public record ReloadClaimsResponse
{
    /// <summary>
    /// Indicates whether the reload was successful
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The new reload ID after successful reload
    /// </summary>
    public Guid? ReloadId { get; init; }

    /// <summary>
    /// Any errors that occurred during the reload
    /// </summary>
    public List<ClaimsReloadError>? Errors { get; init; }

    /// <summary>
    /// Creates a successful response
    /// </summary>
    public static ReloadClaimsResponse Successful(Guid reloadId) =>
        new() { Success = true, ReloadId = reloadId };

    /// <summary>
    /// Creates a failure response
    /// </summary>
    public static ReloadClaimsResponse Failed(List<ClaimsReloadError> errors) =>
        new() { Success = false, Errors = errors };
}

/// <summary>
/// Represents an error that occurred during claims reload
/// </summary>
public record ClaimsReloadError
{
    /// <summary>
    /// The type of error
    /// </summary>
    public required string ErrorType { get; init; }

    /// <summary>
    /// The error message
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Additional details about the error, if available
    /// </summary>
    public string? Details { get; init; }
}
