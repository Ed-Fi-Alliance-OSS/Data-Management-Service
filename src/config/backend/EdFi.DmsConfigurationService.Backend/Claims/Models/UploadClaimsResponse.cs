// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// Response model for claims upload operations
/// </summary>
public record UploadClaimsResponse
{
    /// <summary>
    /// Indicates whether the upload was successful
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The reload ID of the claims after successful upload
    /// </summary>
    public Guid? ReloadId { get; init; }

    /// <summary>
    /// Any errors that occurred during the upload
    /// </summary>
    public List<ClaimsUploadError>? Errors { get; init; }

    /// <summary>
    /// Creates a successful response
    /// </summary>
    public static UploadClaimsResponse Successful(Guid reloadId) =>
        new() { Success = true, ReloadId = reloadId };

    /// <summary>
    /// Creates a failure response
    /// </summary>
    public static UploadClaimsResponse Failed(List<ClaimsUploadError> errors) =>
        new() { Success = false, Errors = errors };
}

/// <summary>
/// Represents an error that occurred during claims upload
/// </summary>
public record ClaimsUploadError
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
    /// The JSON path where the error occurred, if applicable
    /// </summary>
    public string? Path { get; init; }
}
