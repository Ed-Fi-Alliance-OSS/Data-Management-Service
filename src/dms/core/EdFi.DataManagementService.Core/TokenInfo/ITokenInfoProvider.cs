// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.TokenInfo;

/// <summary>
/// Provides token introspection functionality for JWT tokens
/// </summary>
public interface ITokenInfoProvider
{
    /// <summary>
    /// Introspects a JWT token and returns information about its claims and authorization
    /// </summary>
    /// <param name="token">The JWT token to introspect</param>
    /// <returns>Token information response if valid, null if invalid</returns>
    Task<TokenInfoResponse?> GetTokenInfoAsync(string token);
}
