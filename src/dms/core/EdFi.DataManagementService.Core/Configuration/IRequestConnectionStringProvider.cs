// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides the database connection string for the current request
/// This is a scoped service that is populated by middleware and consumed by repositories
/// </summary>
public interface IRequestConnectionStringProvider
{
    /// <summary>
    /// Sets the connection string for the current request
    /// Called by DmsInstanceSelectionMiddleware
    /// </summary>
    void SetConnectionString(string connectionString, long dmsInstanceId);

    /// <summary>
    /// Gets the connection string for the current request
    /// Called by repository factories
    /// </summary>
    string GetConnectionString();

    /// <summary>
    /// Gets the DMS instance ID for the current request
    /// </summary>
    long GetDmsInstanceId();

    /// <summary>
    /// Indicates whether the connection string has been set for this request
    /// </summary>
    bool IsSet { get; }
}
