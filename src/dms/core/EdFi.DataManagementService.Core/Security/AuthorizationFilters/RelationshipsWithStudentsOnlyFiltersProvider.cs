// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for RelationshipsWithStudentsOnly authorization strategy
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly)]
public class RelationshipsWithStudentsOnlyFiltersProvider : AuthorizationFiltersProviderBase
{
    public RelationshipsWithStudentsOnlyFiltersProvider()
        : base(AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly) { }

    public override AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations) =>
        GetRelationshipFilters(authorizations);
}
