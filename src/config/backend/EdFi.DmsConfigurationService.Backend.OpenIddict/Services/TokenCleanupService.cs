// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// Periodically deletes expired OpenIddict tokens so the token store does not grow unbounded.
/// </summary>
public class TokenCleanupService(
    IOptions<IdentityOptions> identityOptions,
    ILogger<TokenCleanupService> logger,
    IOpenIddictTokenRepository tokenRepository
) : BackgroundService
{
    private const int DefaultIntervalMinutes = 30;

    // PeriodicTimer rejects periods above uint.MaxValue - 1 milliseconds; an interval past
    // this bound would throw in the constructor and fault the whole host at startup.
    private static readonly int MaxIntervalMinutes = (int)
        TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalMinutes;

    private readonly IOptions<IdentityOptions> _identityOptions = identityOptions;
    private readonly ILogger<TokenCleanupService> _logger = logger;
    private readonly IOpenIddictTokenRepository _tokenRepository = tokenRepository;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_identityOptions.Value.TokenCleanupEnabled)
        {
            _logger.LogInformation("Token cleanup is disabled; the sweep will not run.");
            return;
        }

        int intervalMinutes = _identityOptions.Value.TokenCleanupIntervalMinutes;
        if (intervalMinutes < 1 || intervalMinutes > MaxIntervalMinutes)
        {
            _logger.LogWarning(
                "IdentitySettings:TokenCleanupIntervalMinutes must be between 1 and {MaxIntervalMinutes}; using default of {DefaultIntervalMinutes}.",
                MaxIntervalMinutes,
                DefaultIntervalMinutes
            );
            intervalMinutes = DefaultIntervalMinutes;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        try
        {
            // Sweep once at startup so a pre-existing backlog does not wait a full interval,
            // and so instances restarting more often than the interval still clean up.
            await RunSweepAsync();

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSweepAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown; the loop exits quietly.
        }
    }

    private async Task RunSweepAsync()
    {
        try
        {
            // The validator still accepts tokens for TokenValidationClockSkew past their
            // expiration; rows in that window must survive or those tokens would fail
            // their status lookup.
            int deletedCount = await _tokenRepository.DeleteExpiredTokensAsync(
                DateTimeOffset.UtcNow - JwtTokenValidator.TokenValidationClockSkew
            );
            if (deletedCount > 0)
            {
                _logger.LogInformation("Deleted {DeletedCount} expired OpenIddict tokens.", deletedCount);
            }
            else
            {
                _logger.LogDebug("Expired OpenIddict token sweep found no tokens to delete.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete expired OpenIddict tokens; will retry next interval.");
        }
    }
}
