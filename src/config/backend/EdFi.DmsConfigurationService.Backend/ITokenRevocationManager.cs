// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// Provider-neutral RFC 7009 revocation. The implementation owns the ordering
/// <c>authenticate → (token type) → ownership → mutate</c> for its identity provider; the endpoint
/// owns request shape and the HTTP status and challenge rules. One method rather than a separate
/// authentication step because Keycloak authenticates the caller inside the delegated revoke
/// request, so a separate authentication call would be a second provider round-trip and an
/// authentication side channel. See reference/design/configuration-service/DMS-1327-cms-token-revocation.md (D-02).
/// </summary>
public interface ITokenRevocationManager
{
    Task<TokenRevocationResult> RevokeTokenAsync(
        TokenRevocationRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>The caller's <c>token_type_hint</c>, reduced to the values RFC 7009 defines; anything else is <see cref="None"/>.</summary>
public enum TokenTypeHint
{
    None,
    AccessToken,
    RefreshToken,
}

/// <summary>
/// The caller's credentials plus the target token, after the endpoint's request-shape validation
/// has passed. The credentials are the caller's own, never the service's.
/// </summary>
public sealed record TokenRevocationRequest(
    string ClientId,
    string ClientSecret,
    string Token,
    TokenTypeHint TokenTypeHint
);

/// <summary>
/// The outcome of one revocation attempt. Token outcomes and authentication outcomes are kept
/// apart from operational failures so that an outage can never be answered as "revoked" and a
/// token the caller does not own can never be answered as an error (D-13).
/// </summary>
public abstract record TokenRevocationResult
{
    private TokenRevocationResult() { }

    /// <summary>
    /// RFC 7009 success: the token was revoked, or it was unknown, invalid, expired, already
    /// revoked, or belongs to another client. All of these are the same empty 200 to the caller.
    /// </summary>
    public sealed record Completed : TokenRevocationResult;

    /// <summary>The caller failed client authentication: unknown, wrong secret, unapproved, or a public client.</summary>
    public sealed record InvalidClient : TokenRevocationResult;

    /// <summary>The provider recognised the token as a type it does not revoke.</summary>
    public sealed record UnsupportedTokenType : TokenRevocationResult;

    /// <summary>The provider rejected the request shape for a reason the endpoint could not pre-empt.</summary>
    public sealed record InvalidRequest : TokenRevocationResult;

    /// <summary>
    /// An operational failure: database, signing-key retrieval, or provider timeout, unreachable,
    /// 5xx or malformed answer. The final state of the token is not known. <paramref name="Reason"/>
    /// is a fixed boundary label for logs only and never carries caller or provider content.
    /// </summary>
    public sealed record TemporarilyUnavailable(string Reason) : TokenRevocationResult;
}
