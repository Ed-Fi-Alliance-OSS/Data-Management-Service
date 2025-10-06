// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides database connection strings for DMS instances
/// </summary>
public interface IConnectionStringProvider
{
    /// <summary>
    /// Gets the database connection string for the specified DMS instance ID
    /// </summary>
    /// <param name="dmsInstanceId">The DMS instance ID</param>
    /// <returns>The connection string for the instance, or null if not found</returns>
    string? GetConnectionString(long dmsInstanceId);

    /// <summary>
    /// Gets a database connection string for health check purposes.
    /// This is a compromise - we just use the first available instance since health checks
    /// need some connection string to test database connectivity.
    /// </summary>
    /// <returns>The connection string for health check, or null if no instances available</returns>
    string? GetHealthCheckConnectionString();
}
