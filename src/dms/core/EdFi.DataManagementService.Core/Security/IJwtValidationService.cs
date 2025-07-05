// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Service for validating JWT tokens and extracting client authorizations
/// </summary>
internal interface IJwtValidationService
{
    /// <summary>
    /// Validates a JWT token and extracts the client authorizations
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the claims principal and client authorizations, or nulls if validation fails</returns>
    Task<(
        ClaimsPrincipal? Principal,
        ClientAuthorizations? ClientAuthorizations
    )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken);
}
