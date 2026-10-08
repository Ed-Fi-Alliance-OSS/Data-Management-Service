// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that warms up the OIDC discovery/JWKS metadata cache so the first
/// authenticated request does not pay the discovery round-trip. Resolves the OIDC
/// configuration manager lazily so a bypass-authorization deployment never touches
/// authentication services. Fails startup when the metadata issuer does not match the
/// configured authority.
/// </summary>
internal class WarmUpOidcMetadataTask(
    IServiceProvider serviceProvider,
    IOptions<AppSettings> appSettings,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<WarmUpOidcMetadataTask> logger
) : IDmsStartupTask
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly AppSettings _appSettings = appSettings.Value;
    private readonly IOptions<JwtAuthenticationOptions> _jwtAuthenticationOptions = jwtAuthenticationOptions;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public int Order => 400;

    /// <inheritdoc />
    public string Name => "Warm Up OIDC Metadata Cache";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_appSettings.BypassAuthorization)
        {
            _logger.LogInformation("BypassAuthorization is enabled, skipping OIDC metadata cache warm-up");
            return;
        }

        _logger.LogInformation("Warming up OIDC metadata cache");

        var configurationManager = _serviceProvider.GetRequiredService<
            IConfigurationManager<OpenIdConnectConfiguration>
        >();

        OpenIdConnectConfiguration config = await configurationManager.GetConfigurationAsync(
            cancellationToken
        );

        // Same exact comparison as JwtValidationService; the orchestrator logs this at Critical and
        // aborts startup.
        string authority = _jwtAuthenticationOptions.Value.Authority;
        if (!string.Equals(config.Issuer, authority, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"OIDC metadata issuer '{JwtValidationService.SanitizeIssuerForLogging(config.Issuer)}' does not match the configured JwtAuthentication:Authority '{JwtValidationService.SanitizeIssuerForLogging(authority)}'"
            );
        }

        _logger.LogInformation(
            "OIDC metadata cache warmed up successfully. Issuer: {Issuer}, SigningKeys: {SigningKeyCount}",
            config.Issuer,
            config.SigningKeys.Count
        );
    }
}
