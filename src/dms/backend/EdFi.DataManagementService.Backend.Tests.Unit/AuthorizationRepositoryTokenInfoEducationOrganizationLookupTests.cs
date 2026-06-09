// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_AuthorizationRepositoryTokenInfoEducationOrganizationLookupAdapter
{
    [Test]
    public async Task It_delegates_to_authorization_repository_token_info_lookup()
    {
        var authorizationRepository = A.Fake<IAuthorizationRepository>();
        IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds = [new(255901), new(255902)];
        IEnumerable<TokenInfoEducationOrganization> expectedRows =
        [
            new(
                EducationOrganizationId: 255901,
                NameOfInstitution: "Grand Bend School",
                Discriminator: "School",
                AncestorDiscriminator: "LocalEducationAgency",
                AncestorEducationOrganizationId: 255901001
            ),
        ];

        A.CallTo(() => authorizationRepository.GetTokenInfoEducationOrganizations(educationOrganizationIds))
            .Returns(Task.FromResult(expectedRows));

        var lookup = new AuthorizationRepositoryTokenInfoEducationOrganizationLookupAdapter(
            authorizationRepository
        );

        var actualRows = await lookup.GetEducationOrganizations(educationOrganizationIds);

        actualRows.Should().BeSameAs(expectedRows);
        A.CallTo(() => authorizationRepository.GetTokenInfoEducationOrganizations(educationOrganizationIds))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_delegates_empty_claims_to_authorization_repository()
    {
        var authorizationRepository = A.Fake<IAuthorizationRepository>();
        IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds = [];
        IEnumerable<TokenInfoEducationOrganization> expectedRows = [];

        A.CallTo(() => authorizationRepository.GetTokenInfoEducationOrganizations(educationOrganizationIds))
            .Returns(Task.FromResult(expectedRows));

        var lookup = new AuthorizationRepositoryTokenInfoEducationOrganizationLookupAdapter(
            authorizationRepository
        );

        var actualRows = await lookup.GetEducationOrganizations(educationOrganizationIds);

        actualRows.Should().BeSameAs(expectedRows);
        A.CallTo(() => authorizationRepository.GetTokenInfoEducationOrganizations(educationOrganizationIds))
            .MustHaveHappenedOnceExactly();
    }
}
