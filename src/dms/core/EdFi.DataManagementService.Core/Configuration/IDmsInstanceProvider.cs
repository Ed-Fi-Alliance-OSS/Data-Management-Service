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
    /// <returns>A list of loaded DMS instances</returns>
    Task<IList<DmsInstance>> LoadDmsInstances();

    /// <summary>
    /// Gets all stored DMS instances
    /// </summary>
    /// <returns>A read-only list of all DMS instances</returns>
    IReadOnlyList<DmsInstance> GetAll();

    /// <summary>
    /// Gets a DMS instance by its ID
    /// </summary>
    /// <param name="id">The instance ID</param>
    /// <returns>The DMS instance if found, otherwise null</returns>
    DmsInstance? GetById(long id);

    /// <summary>
    /// Indicates whether instances have been loaded
    /// </summary>
    bool IsLoaded { get; }
}
