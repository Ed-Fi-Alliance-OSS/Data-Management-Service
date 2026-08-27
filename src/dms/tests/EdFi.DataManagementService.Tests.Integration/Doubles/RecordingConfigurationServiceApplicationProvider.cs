// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Replaces the singleton <see cref="IConfigurationServiceApplicationProvider"/> underneath the real,
/// scoped <see cref="CachedApplicationContextProvider"/> so a scenario can observe how many times the
/// simulated CMS call fires per HTTP request while the production per-request memoization stays in
/// the request path. Every call is recorded before the configured <paramref name="resolve"/> result
/// is returned.
/// </summary>
internal sealed class RecordingConfigurationServiceApplicationProvider(
    Func<string, string?, ApplicationContextResult> resolve
) : IConfigurationServiceApplicationProvider
{
    private readonly object _lock = new();
    private readonly List<(string ClientId, string? Tenant)> _invocations = [];

    public IReadOnlyList<(string ClientId, string? Tenant)> Invocations
    {
        get
        {
            lock (_lock)
            {
                return [.. _invocations];
            }
        }
    }

    public Task<ApplicationContextResult> GetApplicationByClientIdAsync(string clientId, string? tenant)
    {
        lock (_lock)
        {
            _invocations.Add((clientId, tenant));
        }

        return Task.FromResult(resolve(clientId, tenant));
    }

    public Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(string clientId, string? tenant) =>
        GetApplicationByClientIdAsync(clientId, tenant);
}
