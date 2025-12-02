// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security;

public record ConfigurationServiceContext(
    /// <summary>
    /// The client identifier (client ID) used to access the Configuration service endpoints
    /// </summary>
    string clientId,
    /// <summary>
    /// The client secret associated with the client ID for accessing the Configuration service endpoints.
    /// </summary>
    string clientSecret,
    /// <summary>
    /// The authorization scope required for accessing the Configuration service endpoints.
    /// </summary>
    string scope,
    /// <summary>
    /// Optional tenant identifier. When not empty, this value is passed as a "Tenant" header
    /// to all Configuration Service API calls, enabling multi-tenant routing.
    /// </summary>
    string tenant
);
