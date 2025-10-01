// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Represents a DMS instance configuration fetched from the Configuration Service
/// </summary>
public record DmsInstance(
    /// <summary>
    /// The unique identifier for the DMS instance
    /// </summary>
    long Id,
    /// <summary>
    /// The type/category of the DMS instance
    /// </summary>
    string InstanceType,
    /// <summary>
    /// The name of the DMS instance
    /// </summary>
    string InstanceName,
    /// <summary>
    /// The database connection string for this instance
    /// </summary>
    string? ConnectionString
);
