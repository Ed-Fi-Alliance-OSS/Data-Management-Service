// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;

namespace EdFi.DmsConfigurationService.Backend;

public interface ITokenManager
{
    public Task<TokenResult> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials);

    // For JWKS endpoint
    Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>> GetPublicKeysAsync();
    Task<bool> ValidateTokenAsync(string rawToken);
}

public record TokenResult
{
    public record Success(string Token) : TokenResult;

    // Service-owned OAuth error: both values are trusted local constants, never provider content.
    public record FailureAuthentication(string Error, string ErrorDescription) : TokenResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : TokenResult;

    /// <summary>
    /// The client already holds <paramref name="Limit"/> active access tokens, the configured
    /// maximum, so no further token was issued.
    /// </summary>
    public record FailureTokenLimitExceeded(int Limit) : TokenResult;

    /// <summary>
    /// A database lock wait while storing the grant ran out, or the store was chosen as a
    /// deadlock victim, before the identity provider's contention budget did - usually behind
    /// another grant in flight for the same client, but possibly behind another writer on the
    /// token store. Nothing is wrong with the request
    /// and the client may be nowhere near its token limit, so this is retriable as it stands.
    /// </summary>
    public record FailureLockTimeout : TokenResult;

    public record FailureUnknown(string FailureMessage) : TokenResult;
}
