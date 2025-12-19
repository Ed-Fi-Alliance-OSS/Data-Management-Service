// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Token;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

/// <summary>
/// Repository for retrieving education organization information for token introspection
/// </summary>
public interface IEducationOrganizationRepository
{
    /// <summary>
    /// Gets education organizations with their hierarchical relationships
    /// </summary>
    /// <param name="educationOrganizationIds">The education organization IDs to retrieve</param>
    /// <returns>List of education organizations with parent relationship information</returns>
    Task<IReadOnlyList<TokenInfoEducationOrganization>> GetEducationOrganizationsAsync(
        IEnumerable<long> educationOrganizationIds
    );
}
