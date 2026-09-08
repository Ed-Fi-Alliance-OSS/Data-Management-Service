// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Interface for retrieving claim sets directly from the Configuration Service API.
/// This is the underlying provider that CachedClaimSetProvider wraps.
/// </summary>
public interface IConfigurationServiceClaimSetProvider
{
    /// <summary>
    /// Retrieves all claim sets from the Configuration Service API.
    /// </summary>
    /// <param name="tenant">Optional tenant identifier for multi-tenant scenarios.</param>
    /// <returns>List of claim sets from the Configuration Service.</returns>
    Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null);
}
