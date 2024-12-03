// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class ClaimSetRepository : IClaimSetRepository
{
    public IEnumerable<AuthorizationStrategy> GetAuthorizationStrategies()
    {
        var authStrategies = new AuthorizationStrategy[]
        {
            new() {
                AuthStrategyId = 1,
                AuthStrategyName = "NoFurtherAuthorizationRequired",
                DisplayName = "No Further Authorization Required"
            },
            new() {
                AuthStrategyId = 2,
                AuthStrategyName = "RelationshipsWithEdOrgsAndPeople",
                DisplayName = "Relationships with Education Organizations and People"
            },
            new() {
                AuthStrategyId = 3,
                AuthStrategyName = "RelationshipsWithEdOrgsOnly",
                DisplayName = "Relationships with Education Organizations only"
            },
            new() {
                AuthStrategyId = 4,
                AuthStrategyName = "NamespaceBased",
                DisplayName = "Namespace Based"
            },
            new() {
                AuthStrategyId = 5,
                AuthStrategyName = "RelationshipsWithPeopleOnly",
                DisplayName = "Relationships with People only"
            },
            new() {
                AuthStrategyId = 6,
                AuthStrategyName = "RelationshipsWithStudentsOnly",
                DisplayName = "Relationships with Students only"
            },
            new() {
                AuthStrategyId = 7,
                AuthStrategyName = "RelationshipsWithStudentsOnlyThroughResponsibility",
                DisplayName = "Relationships with Students only (through StudentEducationOrganizationResponsibilityAssociation)"
            },
            new() {
                AuthStrategyId = 8,
                AuthStrategyName = "OwnershipBased",
                DisplayName = "Ownership Based"
            },
            new() {
                AuthStrategyId = 9,
                AuthStrategyName = "RelationshipsWithEdOrgsAndPeopleIncludingDeletes",
                DisplayName = "Relationships with Education Organizations and People (including deletes)"
            },
            new() {
                AuthStrategyId = 10,
                AuthStrategyName = "RelationshipsWithEdOrgsOnlyInverted",
                DisplayName = "Relationships with Education Organizations only (Inverted)"
            },
            new() {
                AuthStrategyId = 11,
                AuthStrategyName = "RelationshipsWithEdOrgsAndPeopleInverted",
                DisplayName = "Relationships with Education Organizations and People (Inverted)"
            },
            new() {
                AuthStrategyId = 12,
                AuthStrategyName = "RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes",
                DisplayName = "Relationships with Students only (through StudentEducationOrganizationResponsibilityAssociation, including deletes)",
            },
        };
        return authStrategies;
    }
}
