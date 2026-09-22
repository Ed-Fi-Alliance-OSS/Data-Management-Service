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
        /// calling client. A token the caller does not own is left untouched. See
        /// reference/design/configuration-service/CS-AUTH.md for the rationale and the RFC 7009
        /// response behaviour.
        /// </summary>
        /// <param name="token">The token to revoke</param>
        /// <param name="callerClientId">
        /// The <c>client_id</c> claim of the authenticated caller. Revocation proceeds only when the
        /// target token carries the same <c>client_id</c>.
        /// </param>
        /// <returns>True if the token was successfully revoked, false otherwise</returns>
        Task<bool> RevokeTokenAsync(string token, string callerClientId);

        /// <summary>
        /// Authenticates a client_id/client_secret pair, the way RFC 7009 §2.1 requires a
        /// revocation caller to authenticate (per RFC 6749 §2.3) — the same credentials used at
        /// the token endpoint, not a bearer access token.
        /// </summary>
        /// <param name="clientId">The client_id presented by the caller.</param>
        /// <param name="clientSecret">The client_secret presented by the caller.</param>
        /// <returns>
        /// The client's stored canonical <c>client_id</c> when the credentials identify an
        /// approved application, otherwise <c>null</c>. The canonical value is returned rather
        /// than a bare <c>true</c> because tokens are minted from it: passing the caller's own
        /// spelling into <see cref="RevokeTokenAsync"/> would fail the ownership comparison
        /// wherever an engine authenticated a mis-cased id, which SQL Server's default collation
        /// does.
        /// </returns>
        Task<string?> AuthenticateClientAsync(string clientId, string clientSecret);
    }
}
