// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Provides audit context information by extracting the current authenticated user from the HTTP context.
/// </summary>
public class AuditContext : IAuditContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Gets the identifier of the current authenticated user or client.
    /// Checks JWT claims in the following order: "sub", "client_id", "name".
    /// Returns "system" if no user context is available.
    /// </summary>
    public string GetCurrentUser()
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;

            if (httpContext?.User?.Identity?.IsAuthenticated != true)
            {
                return "system";
            }

            // Try to get user identifier from various claim types
            var claimsPrincipal = httpContext.User;

            // Check for "sub" claim (subject - standard JWT claim)
            var subClaim = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)
                           ?? claimsPrincipal.FindFirst("sub");
            if (subClaim != null && !string.IsNullOrWhiteSpace(subClaim.Value))
            {
                return TruncateIfNeeded(subClaim.Value);
            }

            // Check for "client_id" claim (OAuth client)
            var clientIdClaim = claimsPrincipal.FindFirst("client_id");
            if (clientIdClaim != null && !string.IsNullOrWhiteSpace(clientIdClaim.Value))
            {
                return TruncateIfNeeded(clientIdClaim.Value);
            }

            // Check for "name" claim
            var nameClaim = claimsPrincipal.FindFirst(ClaimTypes.Name)
                            ?? claimsPrincipal.FindFirst("name");
            if (nameClaim != null && !string.IsNullOrWhiteSpace(nameClaim.Value))
            {
                return TruncateIfNeeded(nameClaim.Value);
            }

            // Fallback to "system" if no identifiable claim is found
            return "system";
        }
        catch
        {
            // If any exception occurs during claim extraction, return "system" to ensure audit logging continues
            return "system";
        }
    }

    /// <summary>
    /// Truncates the user identifier if it exceeds the database column length (256 characters).
    /// </summary>
    private static string TruncateIfNeeded(string value)
    {
        const int maxLength = 256;
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    /// <summary>
    /// Gets the current UTC timestamp for audit tracking.
    /// </summary>
    public DateTime GetCurrentTimestamp()
    {
        return DateTime.UtcNow;
    }
}
