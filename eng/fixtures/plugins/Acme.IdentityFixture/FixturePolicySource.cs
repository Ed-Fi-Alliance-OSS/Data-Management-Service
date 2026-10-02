// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Acme.IdentityFixture;

/// <summary>
/// Answers the two questions that may leave the process: whether a client holds a grant, and
/// whether a job has been expired. With no control address configured both are answered from
/// configuration; with one, both are asked of the control channel. A failure to obtain an answer
/// throws and is never treated as a grant.
/// </summary>
public sealed class FixturePolicySource(
    IOptions<IdentityFixtureOptions> options,
    FixtureControlChannel control
)
{
    private readonly IdentityFixtureOptions _options = options.Value;
    private readonly ConcurrentDictionary<
        (string ClientId, string? Tenant, string Namespace),
        CachedAnswer
    > _cache = new(PolicyKeyComparer.Instance);

    public async Task<bool> IsGrantedAsync(
        string clientId,
        string? tenant,
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        if (!control.IsEnabled)
        {
            return _options.Grants.Any(grant =>
                grant.Namespace == namespaceName
                && FixtureContextKey.TenantsEqual(grant.Tenant, tenant)
                && (grant.ClientId == "*" || grant.ClientId == clientId)
            );
        }

        (string, string?, string) key = (clientId, tenant, namespaceName);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        if (_cache.TryGetValue(key, out CachedAnswer? cached) && cached.ExpiresAt > now)
        {
            return cached.Granted;
        }

        string query =
            $"policy?clientId={Uri.EscapeDataString(clientId)}&namespace={Uri.EscapeDataString(namespaceName)}"
            + (tenant is null ? string.Empty : $"&tenant={Uri.EscapeDataString(tenant)}");
        bool granted = FixtureControlChannel.ReadFlag(
            await control.GetAsync(query, cancellationToken),
            "granted"
        );

        if (_options.PolicyCacheSeconds > 0)
        {
            _cache[key] = new CachedAnswer(granted, now.AddSeconds(_options.PolicyCacheSeconds));
        }

        return granted;
    }

    /// <summary>True when the control channel has expired the job. Configuration never expires one.</summary>
    public async Task<bool> IsJobExpiredAsync(string token, CancellationToken cancellationToken) =>
        control.IsEnabled
        && FixtureControlChannel.ReadFlag(
            await control.GetAsync($"jobs/expiry?token={Uri.EscapeDataString(token)}", cancellationToken),
            "expired"
        );

    /// <summary>
    /// The job state the control channel holds: whether the job has failed terminally, and whether the
    /// next poll must fail transiently (consumed by this read). Configuration never fails a job.
    /// </summary>
    public async Task<(bool Failed, bool FailNextPoll)> GetJobStateAsync(
        string token,
        CancellationToken cancellationToken
    )
    {
        if (!control.IsEnabled)
        {
            return (false, false);
        }

        JsonObject body = await control.GetAsync(
            $"jobs/state?token={Uri.EscapeDataString(token)}",
            cancellationToken
        );
        return (
            FixtureControlChannel.ReadFlag(body, "failed"),
            FixtureControlChannel.ReadFlag(body, "failNextPoll")
        );
    }

    private sealed record CachedAnswer(bool Granted, DateTimeOffset ExpiresAt);

    private sealed class PolicyKeyComparer
        : IEqualityComparer<(string ClientId, string? Tenant, string Namespace)>
    {
        public static PolicyKeyComparer Instance { get; } = new();

        public bool Equals(
            (string ClientId, string? Tenant, string Namespace) x,
            (string ClientId, string? Tenant, string Namespace) y
        ) =>
            x.ClientId == y.ClientId
            && FixtureContextKey.TenantsEqual(x.Tenant, y.Tenant)
            && x.Namespace == y.Namespace;

        public int GetHashCode((string ClientId, string? Tenant, string Namespace) obj) =>
            HashCode.Combine(
                obj.ClientId,
                obj.Tenant is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Tenant),
                obj.Namespace
            );
    }
}
