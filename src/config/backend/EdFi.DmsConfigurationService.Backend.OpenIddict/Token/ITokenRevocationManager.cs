// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    /// <summary>
    /// Interface for token managers that support token revocation.
    /// </summary>
    public interface ITokenRevocationManager
    {
        /// <summary>
        /// Revokes a token by setting its status to 'revoked'
        /// </summary>
        /// <param name="token">The token to revoke</param>
        /// <returns>True if the token was successfully revoked, false otherwise</returns>
        Task<bool> RevokeTokenAsync(string token);
    }
}
