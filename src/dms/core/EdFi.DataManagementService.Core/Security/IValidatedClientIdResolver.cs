// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Resolves the client that a request's Authorization header proves, for callers outside Core that
/// need a validated client identity without authenticating the request, such as the frontend rate
/// limiter. Core's own pipelines still authenticate every request independently.
/// </summary>
public interface IValidatedClientIdResolver
{
    /// <summary>
    /// Returns the non-empty <c>client_id</c> of a bearer token that Core's JWT validation accepts.
    /// Returns null when the header is absent or malformed, the token is rejected, the token carries
    /// no <c>client_id</c>, or <c>AppSettings:BypassAuthorization</c> is enabled. A null result is not
    /// an authentication failure.
    /// </summary>
    /// <param name="authorizationHeader">The Authorization header value as received, or null when absent</param>
    /// <param name="cancellationToken">Cancellation token for the validation</param>
    Task<string?> ResolveAsync(string? authorizationHeader, CancellationToken cancellationToken);
}

/// <remarks>
/// The JWT validation service is resolved per call, and only after the bypass and header checks. Its
/// OIDC configuration manager factory throws on missing or invalid metadata settings, and a bypass
/// deployment never warms that metadata up, so a request that cannot carry a valid bearer token, or
/// any request to a bypass deployment, must not construct it.
///
/// A rejected token is logged at Debug here. The limiter resolves every bearer request, including the
/// ones it then rejects, and Core still logs the rejection at Warning when it authenticates a request
/// the limiter admitted.
/// </remarks>
internal sealed class ValidatedClientIdResolver(
    IServiceProvider serviceProvider,
    IOptions<AppSettings> appSettings
) : IValidatedClientIdResolver
{
    public async Task<string?> ResolveAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (authorizationHeader is null || appSettings.Value.BypassAuthorization)
        {
            return null;
        }

        AuthorizationHeaderResult headerResult = AuthorizationHeaderParser.Parse(authorizationHeader);
        if (!headerResult.IsValid)
        {
            return null;
        }

        (_, ClientAuthorizations? clientAuthorizations) = await serviceProvider
            .GetRequiredService<IJwtValidationService>()
            .ValidateAndExtractClientAuthorizationsAsync(
                headerResult.Token!,
                LogLevel.Debug,
                cancellationToken
            );

        string? clientId = clientAuthorizations?.ClientId;
        return string.IsNullOrEmpty(clientId) ? null : clientId;
    }
}
