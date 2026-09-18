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
        /// Revokes a token by setting its status to 'revoked', provided the token belongs to the
        /// calling client. A token the caller does not own is left untouched: callers must not be
        /// able to revoke each other's tokens, and per RFC 7009 the outcome is reported to the
        /// HTTP caller the same way as "token not found", so nothing is leaked about the token's
        /// existence or owner.
        /// </summary>
        /// <param name="token">The token to revoke</param>
        /// <param name="callerClientId">
        /// The <c>client_id</c> claim of the authenticated caller. Revocation proceeds only when the
        /// target token carries the same <c>client_id</c>.
        /// </param>
        /// <returns>True if the token was successfully revoked, false otherwise</returns>
        Task<bool> RevokeTokenAsync(string token, string callerClientId);
    }
}
