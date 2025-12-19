// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Token;

namespace EdFi.DmsConfigurationService.Backend.Introspection;

/// <summary>
/// Provides token introspection functionality for OAuth tokens
/// </summary>
public interface ITokenInfoProvider
{
    /// <summary>
    /// Retrieves detailed information about a JWT token including authorized resources and education organizations
    /// </summary>
    /// <param name="token">The JWT token to introspect</param>
    /// <returns>Token information including active status, client details, and authorizations</returns>
    Task<TokenInfoResponse?> GetTokenInfoAsync(string token);
}
