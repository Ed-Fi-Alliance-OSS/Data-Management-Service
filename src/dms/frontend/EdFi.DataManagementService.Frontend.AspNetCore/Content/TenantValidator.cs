// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

/// <summary>
/// Whether a tenant exists, with a lookup that could not answer kept apart from a confirmed absence.
/// </summary>
public enum TenantExistence
{
    /// <summary>The tenant is cached, its data stores loaded, or it is in the tenant list.</summary>
    Exists,

    /// <summary>A successfully loaded tenant list does not contain the tenant.</summary>
    Absent,

    /// <summary>The Configuration Service could not be asked, so existence is unknown.</summary>
    Undetermined,
}

/// <summary>
/// Validates that a tenant exists in the system.
/// </summary>
public interface ITenantValidator
{
    /// <summary>
    /// Validates that a tenant exists. Returns true if valid, false if not found.
    /// Handles cache-miss by attempting to reload from Configuration Service.
    /// </summary>
    /// <param name="tenant">The tenant identifier to validate</param>
    /// <returns>True if the tenant exists, false otherwise</returns>
    Task<bool> ValidateTenantAsync(string tenant);

    /// <summary>
    /// Determines whether a tenant exists, answering <see cref="TenantExistence.Absent" /> only when
    /// the Configuration Service's tenant list was loaded and lacks the tenant, and
    /// <see cref="TenantExistence.Undetermined" /> when it could not be loaded. Cancellation of
    /// <paramref name="cancellationToken" /> propagates as an <see cref="OperationCanceledException" />.
    /// </summary>
    Task<TenantExistence> DetermineTenantExistenceAsync(string tenant, CancellationToken cancellationToken);
}

/// <summary>
/// Validates tenants against the Configuration Service.
/// Checks local cache first, then reloads from Configuration Service on cache-miss.
/// </summary>
public class TenantValidator(IDataStoreProvider dataStoreProvider, ILogger<TenantValidator> logger)
    : ITenantValidator
{
    /// <inheritdoc />
    public async Task<bool> ValidateTenantAsync(string tenant)
    {
        if (string.IsNullOrEmpty(tenant))
        {
            return false;
        }

        // Check if tenant exists in cache
        if (dataStoreProvider.TenantExists(tenant))
        {
            logger.LogDebug(
                "Tenant {Tenant} found in cache",
                LoggingSanitizer.SanitizeInternalValueForLogging(tenant)
            );
            return true;
        }

        // Cache miss - attempt to load instances for this tenant from Configuration Service
        // LoadDataStores populates _instancesByTenant, which is what TenantExists checks
        logger.LogDebug(
            "Tenant {Tenant} not found in cache, attempting to load from Configuration Service",
            LoggingSanitizer.SanitizeInternalValueForLogging(tenant)
        );

        try
        {
            // Try to load instances for this specific tenant
            // If the tenant doesn't exist, this will return an empty list or throw
            var instances = await dataStoreProvider.LoadDataStores(tenant);

            // If we got here without an exception, the tenant exists
            // The instances are now cached, so TenantExists will return true
            logger.LogDebug(
                "Tenant {Tenant} loaded successfully with {InstanceCount} instances",
                LoggingSanitizer.SanitizeInternalValueForLogging(tenant),
                instances.Count
            );
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Tenant doesn't exist - 404 from Configuration Service (expected, not an error)
            logger.LogDebug(
                ex,
                "Tenant {Tenant} not found in Configuration Service",
                LoggingSanitizer.SanitizeInternalValueForLogging(tenant)
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load tenant {Tenant} from Configuration Service",
                LoggingSanitizer.SanitizeInternalValueForLogging(tenant)
            );
        }

        logger.LogDebug(
            "Tenant {Tenant} validation failed",
            LoggingSanitizer.SanitizeInternalValueForLogging(tenant)
        );
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A cached tenant exists. On a cache miss the tenant's data stores are loaded, as
    /// <see cref="ValidateTenantAsync" /> does, and success means the tenant exists. A failed load
    /// cannot tell absence from an outage: the Configuration Service answers an unknown tenant with a
    /// 400, and the provider wraps every HTTP failure, so neither the status nor the message is
    /// evidence. The tenant list settles it. A tenant with no data stores is still in that list, so an
    /// empty data-store list is never read as absence. Only exception types are logged, because the
    /// provider's wrapped failure quotes the Configuration Service.
    /// </remarks>
    public async Task<TenantExistence> DetermineTenantExistenceAsync(
        string tenant,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(tenant))
        {
            return TenantExistence.Absent;
        }

        if (dataStoreProvider.TenantExists(tenant))
        {
            return TenantExistence.Exists;
        }

        string sanitizedTenant = LoggingSanitizer.SanitizeInternalValueForLogging(tenant);

        try
        {
            await dataStoreProvider.LoadDataStores(tenant, cancellationToken);
            return TenantExistence.Exists;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
#pragma warning disable S6667 // Deliberate: the exception object is withheld so nothing it carries reaches the log
            logger.LogDebug(
                "Data stores for tenant {Tenant} could not be loaded ({ExceptionType}); checking the tenant list",
                sanitizedTenant,
                ex.GetType().Name
            );
#pragma warning restore S6667
        }

        try
        {
            IList<string> tenants = await dataStoreProvider.LoadTenants(cancellationToken);

            // The Configuration Service matches tenant names case-insensitively, as the cache does.
            return tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase)
                ? TenantExistence.Exists
                : TenantExistence.Absent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
#pragma warning disable S6667 // Deliberate: the exception object is withheld so nothing it carries reaches the log
            logger.LogWarning(
                "Could not determine whether tenant {Tenant} exists: the Configuration Service tenant list could not be loaded ({ExceptionType})",
                sanitizedTenant,
                ex.GetType().Name
            );
#pragma warning restore S6667
            return TenantExistence.Undetermined;
        }
    }
}
