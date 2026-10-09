// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// Obtains and caches the bearer token the projection reader sends to DMS (DMS-1440 spec §5.4): a client-credentials
/// grant with the tenant's configured credential, cached per tenant and client until its expiry minus
/// <c>TokenExpirySafetyMarginSeconds</c>, with one request at a time per tenant and client.
/// </summary>
public interface IProjectionServiceTokenProvider
{
    /// <summary>
    /// A token for <paramref name="tenantName"/>, or a <see cref="ProjectionServiceTokenResult.Failed"/> at the Token
    /// stage. Caller cancellation is checked first and the read deadline second, before a cached token is used and
    /// again after waiting for another caller's request. A token request is bounded by
    /// <c>TokenRequestTimeoutSeconds</c> and by <paramref name="readDeadline"/>; reaching either is a transient
    /// <c>Timeout</c>. Only cancellation of <paramref name="cancellationToken"/> throws
    /// (<see cref="OperationCanceledException"/>), with that token.
    /// </summary>
    /// <param name="tenantName">The tenant, or <c>null</c> in single-tenant mode.</param>
    /// <param name="tokenUrl">The resolved <c>urls.oauth</c>; it is not part of the cache key.</param>
    /// <param name="readDeadline">When the whole logical read times out.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<ProjectionServiceTokenResult> GetTokenAsync(
        string? tenantName,
        Uri tokenUrl,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Drops <paramref name="token"/> from the cache after DMS refused it, unless another read has already replaced it.
    /// </summary>
    void Invalidate(string? tenantName, ProjectionServiceToken token);
}

/// <summary>A bearer token. <see cref="ToString"/> never includes it.</summary>
public sealed class ProjectionServiceToken(string accessToken)
{
    /// <summary>The token: RFC 6750 <c>b64token</c> characters only, so it is safe in an Authorization header.</summary>
    public string AccessToken { get; } = accessToken;

    public override string ToString() => $"{nameof(ProjectionServiceToken)} {{ AccessToken = [redacted] }}";
}

/// <summary>The outcome of <see cref="IProjectionServiceTokenProvider.GetTokenAsync"/>.</summary>
public abstract record ProjectionServiceTokenResult
{
    private ProjectionServiceTokenResult() { }

    public sealed record Issued(ProjectionServiceToken Token) : ProjectionServiceTokenResult;

    public sealed record Failed(EducationOrganizationProjectionFailure Failure)
        : ProjectionServiceTokenResult;
}
