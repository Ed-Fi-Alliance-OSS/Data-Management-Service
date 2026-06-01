// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Represents a data store configuration fetched from the Configuration Service
/// </summary>
public record DataStore(
    /// <summary>
    /// The unique identifier for the data store
    /// </summary>
    long Id,
    /// <summary>
    /// The type/category of the data store
    /// </summary>
    string DataStoreType,
    /// <summary>
    /// The name of the data store
    /// </summary>
    string Name,
    /// <summary>
    /// The database connection string for this data store
    /// </summary>
    string? ConnectionString,
    /// <summary>
    /// Route qualifier context for this data store, mapping qualifier names to values
    /// (e.g., "district" -> "255901", "schoolYear" -> "2024")
    /// </summary>
    Dictionary<RouteQualifierName, RouteQualifierValue> RouteContext
);
