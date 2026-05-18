// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides authorization filters for RelationshipsWithEdOrgsOnlyInverted authorization strategy.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted)]
public class RelationshipsWithEdOrgsOnlyInvertedFiltersProvider : AuthorizationFiltersProviderBase
{
    public RelationshipsWithEdOrgsOnlyInvertedFiltersProvider()
        : base(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted) { }

    public override AuthorizationStrategyEvaluator GetFilters(ClientAuthorizations authorizations) =>
        GetRelationshipFilters(authorizations);
}
