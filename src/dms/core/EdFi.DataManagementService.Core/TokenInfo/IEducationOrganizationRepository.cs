// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.TokenInfo;

/// <summary>
/// Repository for retrieving education organization data from the Configuration Service database
/// </summary>
public interface IEducationOrganizationRepository
{
    /// <summary>
    /// Retrieves education organizations by their IDs from the Config database
    /// </summary>
    /// <param name="ids">Collection of education organization IDs to retrieve</param>
    /// <returns>List of education organizations with their details</returns>
    Task<IReadOnlyList<TokenInfoEducationOrganization>> GetEducationOrganizationsAsync(IEnumerable<long> ids);
}
