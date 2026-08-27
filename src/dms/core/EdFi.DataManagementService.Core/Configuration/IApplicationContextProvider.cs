// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Service for retrieving application context from the Configuration Management Service
/// </summary>
public interface IApplicationContextProvider
{
    /// <summary>
    /// Retrieves application context by client ID
    /// </summary>
    /// <param name="clientId">The client identifier from the JWT token</param>
    /// <param name="tenant">The optional tenant context for the request</param>
    /// <returns>The typed application-context lookup outcome</returns>
    Task<ApplicationContextResult> GetApplicationByClientIdAsync(string clientId, string? tenant);

    /// <summary>
    /// Forces a reload of application data from CMS, bypassing cache
    /// </summary>
    /// <param name="clientId">The client identifier to reload</param>
    /// <param name="tenant">The optional tenant context for the request</param>
    /// <returns>The typed application-context reload outcome</returns>
    Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(string clientId, string? tenant);
}
