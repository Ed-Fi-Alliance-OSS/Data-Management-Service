// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Relational token_info education organization lookup contract that receives
/// the selected runtime mapping set for the current DMS instance.
/// </summary>
public interface IRelationalTokenInfoEducationOrganizationLookup : ITokenInfoEducationOrganizationLookup
{
    public Task<IEnumerable<TokenInfoEducationOrganization>> GetEducationOrganizations(
        IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds,
        MappingSet mappingSet
    );
}
