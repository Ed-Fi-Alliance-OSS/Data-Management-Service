// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides access to DMS instance configurations with in-memory storage
/// </summary>
public interface IDmsInstanceProvider
{
    /// <summary>
    /// Loads DMS instances from the Configuration Service and stores them in memory
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    /// <returns>A list of loaded DMS instances</returns>
    Task<IList<DmsInstance>> LoadDmsInstances(string? tenant = null);

    /// <summary>
    /// Gets all stored DMS instances for a tenant
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    /// <returns>A read-only list of all DMS instances for the tenant</returns>
    IReadOnlyList<DmsInstance> GetAll(string? tenant = null);

    /// <summary>
    /// Gets a DMS instance by its ID for a tenant
    /// </summary>
    /// <param name="id">The instance ID</param>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    /// <returns>The DMS instance if found, otherwise null</returns>
    DmsInstance? GetById(long id, string? tenant = null);

    /// <summary>
    /// Indicates whether instances have been loaded for a tenant
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant environments</param>
    /// <returns>True if instances have been loaded for the tenant, otherwise false</returns>
    bool IsLoaded(string? tenant = null);

    /// <summary>
    /// Loads all tenant names from the Configuration Service.
    /// Used at startup when multi-tenancy is enabled to pre-populate the instance cache for all tenants.
    /// </summary>
    /// <returns>A list of tenant names</returns>
    Task<IList<string>> LoadTenants();

    /// <summary>
    /// Checks if a tenant has been loaded into the cache.
    /// </summary>
    /// <param name="tenant">The tenant identifier to check</param>
    /// <returns>True if the tenant exists in the cache, otherwise false</returns>
    bool TenantExists(string tenant);
}
