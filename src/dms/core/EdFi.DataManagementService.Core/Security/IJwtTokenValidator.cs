// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Interface for JWT token validation
/// </summary>
internal interface IJwtTokenValidator
{
    /// <summary>
    /// Validates a JWT token against the configured identity provider
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <param name="settings">Identity provider settings</param>
    /// <returns>The validation result containing claims if successful</returns>
    Task<JwtValidationResult> ValidateTokenAsync(string token, IdentitySettings settings);
}

/// <summary>
/// Result of JWT token validation
/// </summary>
internal record JwtValidationResult(bool IsValid, List<Claim> Claims, string ErrorMessage = "");
