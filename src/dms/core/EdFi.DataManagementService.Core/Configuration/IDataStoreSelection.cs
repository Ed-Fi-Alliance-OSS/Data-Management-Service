// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides the selected data store for the current request
/// This is a scoped service that is populated by middleware and consumed by repositories
/// </summary>
public interface IDataStoreSelection
{
    /// <summary>
    /// Sets the selected data store for the current request
    /// Called by ResolveDataStoreMiddleware
    /// </summary>
    void SetSelectedDataStore(DataStore dataStore);

    /// <summary>
    /// Gets the selected data store for the current request
    /// Called by repository factories
    /// </summary>
    DataStore GetSelectedDataStore();

    /// <summary>
    /// Indicates whether the data store has been set for this request
    /// </summary>
    bool IsSet { get; }
}
