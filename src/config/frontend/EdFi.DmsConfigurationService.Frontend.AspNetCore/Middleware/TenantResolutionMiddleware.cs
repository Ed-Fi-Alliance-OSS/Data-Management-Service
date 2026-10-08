// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

public class TenantResolutionMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private const string TenantHeaderName = "Tenant";

    public async Task Invoke(
        HttpContext context,
        IOptions<AppSettings> appSettings,
        ITenantContextProvider tenantContextProvider,
        ITenantRepository tenantRepository,
        ILogger<TenantResolutionMiddleware> logger
    )
    {
        if (!appSettings.Value.MultiTenancy)
        {
            // Multi-tenancy is disabled, context remains NotMultitenant (the default)
            await _next(context);
            return;
        }

        // Allow tenant-agnostic paths without tenant header: the service root, /health, /tenancy,
        //   /metadata/specifications and /openapi/v1.json. None of them returns tenant-scoped data.
        //   Matched exactly (not by segment prefix) so lookalike paths keep requiring a valid tenant.
        //   A Tenant header sent to one of them is never looked up.
        // Allow /connect endpoints without tenant header (for system administrator authentication)
        // Allow /v3/tenants endpoints without tenant header (for tenant management before tenants exist)
        // Allow /.well-known endpoints without tenant header (standard OIDC discovery endpoints)
        if (
            IsTenantAgnosticPath(context.Request.Path)
            || context.Request.Path.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/v3/tenants", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
        )
        {
            await _next(context);
            return;
        }

        // Multi-tenancy is enabled, validate tenant header
        if (
            !context.Request.Headers.TryGetValue(TenantHeaderName, out var tenantName)
            || string.IsNullOrWhiteSpace(tenantName)
        )
        {
            logger.LogWarning("Tenant header is missing or empty");
            await FailureResponseWriter.WriteAsync(
                context,
                FailureResponse.ForBadRequest(
                    $"The '{TenantHeaderName}' header is required when multi-tenancy is enabled",
                    context.TraceIdentifier
                ),
                context.RequestAborted
            );
            return;
        }

        var sanitizedTenantName = SanitizeForLog(tenantName.ToString());

        // Validate that the tenant exists
        var tenantResult = await tenantRepository.GetTenantByName(tenantName.ToString());

        if (tenantResult is TenantGetByNameResult.FailureNotFound)
        {
            logger.LogWarning("Tenant not found: {TenantName}", sanitizedTenantName);
            await FailureResponseWriter.WriteAsync(
                context,
                FailureResponse.ForBadRequest(
                    $"Invalid tenant: {sanitizedTenantName}",
                    context.TraceIdentifier
                ),
                context.RequestAborted
            );
            return;
        }

        if (tenantResult is TenantGetByNameResult.FailureUnknown failure)
        {
            logger.LogError(
                "Failed to validate tenant: {TenantName}. Error: {Error}",
                sanitizedTenantName,
                SanitizeForLog(failure.FailureMessage)
            );
            await FailureResponseWriter.WriteAsync(
                context,
                FailureResponse.ForUnknown(context.TraceIdentifier),
                context.RequestAborted
            );
            return;
        }

        if (tenantResult is TenantGetByNameResult.Success success)
        {
            // Set tenant context for the request
            tenantContextProvider.Context = new TenantContext.Multitenant(
                success.TenantResponse.Id,
                success.TenantResponse.Name
            );

            logger.LogDebug(
                "Tenant resolved: {TenantName} (Id: {TenantId})",
                sanitizedTenantName,
                success.TenantResponse.Id
            );

            await _next(context);
            return;
        }

        // An unrecognized repository result is a server-side contract failure, not caller input.
        logger.LogError("Unexpected tenant lookup result type: {ResultType}", tenantResult.GetType().Name);
        await FailureResponseWriter.WriteAsync(
            context,
            FailureResponse.ForUnknown(context.TraceIdentifier),
            context.RequestAborted
        );
    }

    // Exact paths (each also accepted with one trailing slash) that never need a Tenant header: health probes,
    // tenant discovery, and the discovery and OpenAPI documents. None of them returns tenant-scoped data.
    private static readonly string[] TenantAgnosticPaths =
    [
        "/health",
        "/tenancy",
        "/metadata/specifications",
        "/openapi/v1.json",
    ];

    /// <summary>
    /// Determines whether the request targets a tenant-agnostic endpoint that must be reachable without a
    /// tenant header: the service root, "/health" (health probes), "/tenancy" (tenant discovery), and
    /// "/metadata/specifications" and "/openapi/v1.json" (discovery documents, including the header-less
    /// inner fetch that "/metadata/specifications" makes of "/openapi/v1.json"). Each is matched exactly,
    /// case-insensitively and optionally with one trailing slash, leaving lookalike paths such as
    /// "/health/foo", "/metadataX" or "/tenancyx" subject to tenant enforcement. Path base is already
    /// stripped by UsePathBase, so "/mt-config/health" arrives here as "/health".
    /// </summary>
    private static bool IsTenantAgnosticPath(PathString path) =>
        IsServiceRoot(path)
        || Array.Exists(
            TenantAgnosticPaths,
            p =>
                path.Equals(p, StringComparison.OrdinalIgnoreCase)
                || path.Equals($"{p}/", StringComparison.OrdinalIgnoreCase)
        );

    // UsePathBase leaves "" for the bare path base ("/mt-config") and "/" for "/mt-config/".
    private static bool IsServiceRoot(PathString path) => !path.HasValue || path.Value == "/";

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        // Whitelist approach: only allow alphanumeric characters and specific safe symbols
        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }
}
