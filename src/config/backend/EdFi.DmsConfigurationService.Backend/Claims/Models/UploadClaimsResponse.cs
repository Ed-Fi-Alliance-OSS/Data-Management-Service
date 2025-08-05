// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// Response model for claims upload operations
/// </summary>
/// <param name="Success">Indicates whether the upload was successful</param>
/// <param name="ReloadId">The reload ID of the claims after successful upload</param>
/// <param name="Errors">Any errors that occurred during the upload</param>
public record UploadClaimsResponse(
    bool Success,
    Guid? ReloadId = null,
    List<ClaimsUploadError>? Errors = null
)
{
    /// <summary>
    /// Creates a successful response
    /// </summary>
    public static UploadClaimsResponse Successful(Guid reloadId) => new(true, reloadId);

    /// <summary>
    /// Creates a failure response
    /// </summary>
    public static UploadClaimsResponse Failed(List<ClaimsUploadError> errors) => new(false, Errors: errors);
}
