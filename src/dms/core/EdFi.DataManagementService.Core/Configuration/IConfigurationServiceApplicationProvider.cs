// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Interface for retrieving application context directly from the Configuration Service API.
/// This is the underlying provider that CachedApplicationContextProvider wraps.
/// </summary>
public interface IConfigurationServiceApplicationProvider
{
    /// <summary>
    /// Retrieves application context for a client ID from the Configuration Service API.
    /// </summary>
    /// <param name="clientId">The client ID to look up.</param>
    /// <param name="tenant">The optional tenant context for the request.</param>
    /// <returns>The typed application-context lookup outcome.</returns>
    Task<ApplicationContextResult> GetApplicationByClientIdAsync(string clientId, string? tenant);

    /// <summary>
    /// Forces a reload of application context from the Configuration Service API.
    /// Use this when the application may have been recently created or modified.
    /// </summary>
    /// <param name="clientId">The client ID to reload.</param>
    /// <param name="tenant">The optional tenant context for the request.</param>
    /// <returns>The typed application-context reload outcome.</returns>
    Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(string clientId, string? tenant);
}
