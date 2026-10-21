// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// Provider-neutral RFC 7662 introspection. The implementation owns the ordering
/// <c>authenticate → verify → ownership → status</c> for its identity provider; the endpoint
/// owns request shape and the HTTP status and challenge rules.
/// <see cref="TokenIntrospectionResult.Inactive"/> covers every token outcome, a token issued to
/// another client included, so the answer is never an oracle. One method rather than a separate
/// authentication step because the endpoint must never see stored client secrets. See
/// reference/design/configuration-service/CS-AUTH.md.
/// </summary>
public interface ITokenIntrospectionManager
{
    Task<TokenIntrospectionResult> IntrospectTokenAsync(
        TokenIntrospectionRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The caller's credentials plus the target token, after the endpoint's request-shape validation
/// has passed. The credentials are the caller's own, never the service's.
/// </summary>
public sealed record TokenIntrospectionRequest(
    string ClientId,
    string ClientSecret,
    string Token,
    TokenTypeHint TokenTypeHint
);

/// <summary>
/// The claims of an active token that the caller owns. <paramref name="Exp"/> and
/// <paramref name="Iat"/> are Unix seconds so the endpoint can emit JSON integers.
/// </summary>
public sealed record IntrospectedToken(
    string ClientId,
    string Scope,
    long Exp,
    long Iat,
    string? Sub,
    string? Aud,
    string? Iss
);

/// <summary>
/// The outcome of one introspection attempt. A failure that prevents authenticating the caller is
/// kept apart from the authentication and token outcomes, so it is never answered as rejected
/// credentials. Once the caller is authenticated, every token outcome is
/// <see cref="Inactive"/>, including a token that could not be decided because a dependency
/// failed.
/// </summary>
public abstract record TokenIntrospectionResult
{
    private TokenIntrospectionResult() { }

    /// <summary>The token is valid, unexpired, unrevoked, and owned by the caller.</summary>
    public sealed record Active(IntrospectedToken Token) : TokenIntrospectionResult;

    /// <summary>
    /// RFC 7662 inactive: the token was unknown, invalid, expired, revoked, or belongs to another
    /// client, or it could not be decided because a dependency needed to verify it or read its
    /// status failed after the caller was authenticated. All of these are the same
    /// <c>active:false</c> to the caller.
    /// </summary>
    public sealed record Inactive : TokenIntrospectionResult;

    /// <summary>
    /// The caller failed client authentication, for example an unknown client, a wrong secret or an
    /// unapproved client.
    /// </summary>
    public sealed record InvalidClient : TokenIntrospectionResult;

    /// <summary>
    /// An operational failure that prevented authenticating the caller, such as a client store that
    /// could not be read, so the request was not judged. <paramref name="Reason"/> is a fixed
    /// boundary label for logs only and never carries caller or provider content.
    /// </summary>
    public sealed record TemporarilyUnavailable(string Reason) : TokenIntrospectionResult;
}
