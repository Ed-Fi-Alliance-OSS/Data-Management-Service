// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// Provider-neutral RFC 7662 introspection. The implementation owns the ordering
/// <c>authenticate → verify → ownership → status</c> for its identity provider; the endpoint
/// owns request shape and the HTTP status and challenge rules. <see cref="TokenIntrospectionResult.Inactive"/>
/// covers every token outcome, a token issued to another client included, so the answer is never
/// an oracle. One method rather than a separate authentication step because the endpoint must
/// never see stored client secrets. See reference/design/configuration-service/CS-AUTH.md.
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
/// The outcome of one introspection attempt. Token outcomes and authentication outcomes are kept
/// apart from operational failures so that an outage can never be answered as "inactive".
/// </summary>
public abstract record TokenIntrospectionResult
{
    private TokenIntrospectionResult() { }

    /// <summary>The token is valid, unexpired, unrevoked, and owned by the caller.</summary>
    public sealed record Active(IntrospectedToken Token) : TokenIntrospectionResult;

    /// <summary>
    /// RFC 7662 inactive: the token was unknown, invalid, expired, revoked, or belongs to another
    /// client. All of these are the same <c>active:false</c> to the caller.
    /// </summary>
    public sealed record Inactive : TokenIntrospectionResult;

    /// <summary>The caller failed client authentication: unknown, wrong secret, unapproved, or a public client.</summary>
    public sealed record InvalidClient : TokenIntrospectionResult;

    /// <summary>
    /// An operational failure: database, signing-key retrieval, or provider timeout, unreachable,
    /// 5xx or malformed answer. The state of the token is not known. <paramref name="Reason"/>
    /// is a fixed boundary label for logs only and never carries caller or provider content.
    /// </summary>
    public sealed record TemporarilyUnavailable(string Reason) : TokenIntrospectionResult;
}
