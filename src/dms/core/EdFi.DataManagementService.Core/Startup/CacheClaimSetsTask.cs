// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that pre-caches claim set metadata so the first authorized request
/// does not pay the Configuration Service round-trip. Failures here are logged at
/// Critical level but never abort startup — DMS availability must not depend on the
/// Configuration Service being reachable when the host comes up.
/// </summary>
internal class CacheClaimSetsTask(
    IClaimSetProvider claimSetProvider,
    IDmsInstanceProvider dmsInstanceProvider,
    IOptions<AppSettings> appSettings,
    ILogger<CacheClaimSetsTask> logger
) : IDmsStartupTask
{
    private readonly IClaimSetProvider _claimSetProvider = claimSetProvider;
    private readonly IDmsInstanceProvider _dmsInstanceProvider = dmsInstanceProvider;
    private readonly AppSettings _appSettings = appSettings.Value;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public int Order => 410;

    /// <inheritdoc />
    public string Name => "Cache Claim Sets";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Retrieving and caching required claim sets");

        try
        {
            if (_appSettings.MultiTenancy)
            {
                IList<string> tenants = await _dmsInstanceProvider.LoadTenants();

                foreach (string tenant in tenants)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "Caching claim sets for tenant: {TenantName}",
                        LoggingSanitizer.SanitizeForLogging(tenant)
                    );

                    await _claimSetProvider.GetAllClaimSets(tenant);
                }
            }
            else
            {
                await _claimSetProvider.GetAllClaimSets();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pre-caching claim sets is best-effort; a Configuration Service failure
            // here must not prevent DMS from starting and serving traffic.
            _logger.LogCritical(ex, "Retrieving and caching required claim sets failure");
        }
    }
}
