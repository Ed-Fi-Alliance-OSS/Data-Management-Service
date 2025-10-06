// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Represents the application context retrieved from the Configuration Management Service
/// </summary>
public record ApplicationContext(
    /// <summary>
    /// The API client ID
    /// </summary>
    long Id,
    /// <summary>
    /// The application ID this client belongs to
    /// </summary>
    long ApplicationId,
    /// <summary>
    /// The client identifier
    /// </summary>
    string ClientId,
    /// <summary>
    /// The client UUID
    /// </summary>
    Guid ClientUuid,
    /// <summary>
    /// List of DMS instance IDs this application is authorized to access
    /// </summary>
    List<long> DmsInstanceIds
);
