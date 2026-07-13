// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
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

        // Allow /health endpoint without tenant header (health probes must be tenant-agnostic).
        //   Matched exactly (not by segment prefix) so lookalike paths keep requiring a valid tenant.
        // Allow /connect endpoints without tenant header (for system administrator authentication)
        // Allow /v3/tenants endpoints without tenant header (for tenant management before tenants exist)
        // Allow /.well-known endpoints without tenant header (standard OIDC discovery endpoints)
        if (
            IsHealthPath(context.Request.Path)
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
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = "Bad Request",
                    message = $"The '{TenantHeaderName}' header is required when multi-tenancy is enabled",
                }
            );
            return;
        }

        var sanitizedTenantName = SanitizeForLog(tenantName.ToString());

        // Validate that the tenant exists
        var tenantResult = await tenantRepository.GetTenantByName(tenantName.ToString());

        if (tenantResult is TenantGetByNameResult.FailureNotFound)
        {
            logger.LogWarning("Tenant not found: {TenantName}", sanitizedTenantName);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new { error = "Bad Request", message = $"Invalid tenant: {sanitizedTenantName}" }
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
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new { error = "Internal Server Error", message = "Failed to validate tenant" }
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

        // Handle unexpected result type
        logger.LogError("Unexpected tenant lookup result type: {ResultType}", tenantResult.GetType().Name);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { error = "Bad Request", message = "Failed to validate tenant" }
        );
    }

    /// <summary>
    /// Determines whether the request targets the health endpoint, which must be reachable without a
    /// tenant header so health probes remain tenant-agnostic. Matches only "/health" and "/health/"
    /// (case-insensitive), leaving lookalike paths such as "/health/foo" or "/healthcheck" subject to
    /// tenant enforcement. Path base is already stripped by UsePathBase, so "/mt-config/health" arrives
    /// here as "/health".
    /// </summary>
    private static bool IsHealthPath(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/health/", StringComparison.OrdinalIgnoreCase);

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
