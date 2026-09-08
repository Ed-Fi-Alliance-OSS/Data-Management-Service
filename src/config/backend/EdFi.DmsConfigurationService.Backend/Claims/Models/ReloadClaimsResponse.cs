// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Claims.Models;

/// <summary>
/// Response model for claims reload operations
/// </summary>
/// <param name="Success">Indicates whether the reload was successful</param>
/// <param name="ReloadId">The new reload ID after successful reload</param>
/// <param name="Errors">Any errors that occurred during the reload</param>
public record ReloadClaimsResponse(
    bool Success,
    Guid? ReloadId = null,
    List<ClaimsReloadError>? Errors = null
)
{
    /// <summary>
    /// Creates a successful response
    /// </summary>
    public static ReloadClaimsResponse Successful(Guid reloadId) => new(true, reloadId);

    /// <summary>
    /// Creates a failure response
    /// </summary>
    public static ReloadClaimsResponse Failed(List<ClaimsReloadError> errors) => new(false, Errors: errors);
}
