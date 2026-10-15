// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides database connection strings for data stores
/// </summary>
public interface IConnectionStringProvider
{
    /// <summary>
    /// Gets the database connection string for the specified data store ID
    /// </summary>
    /// <param name="dataStoreId">The data store ID</param>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    /// <returns>The connection string for the instance, or null if not found</returns>
    string? GetConnectionString(long dataStoreId, string? tenant = null);

    /// <summary>
    /// Resolves the database the health check should probe.
    /// This is a compromise - we just use the first available instance since health checks
    /// need some connection string to test database connectivity.
    /// In multi-tenant mode, this searches across all loaded tenant caches.
    /// </summary>
    /// <returns>
    /// The connection string to probe, or which of the two empty states was found: no data stores
    /// loaded yet, or data stores loaded but none with a connection string
    /// </returns>
    HealthCheckTarget GetHealthCheckTarget();
}
