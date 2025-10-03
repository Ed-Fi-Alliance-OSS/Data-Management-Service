// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security.AuthorizationFilters;

public class RelationshipsWithStudentsOnlyThroughResponsibilityFiltersProviderTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Claim_Has_EducationOrganizations
    {
        private AuthorizationStrategyEvaluator? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var filters = new RelationshipsWithStudentsOnlyThroughResponsibilityFiltersProvider();
            _expectedResult = filters.GetFilters(
                new ClientAuthorizations(
                    "",
                    "",
                    "",
                    [new EducationOrganizationId(255901), new EducationOrganizationId(255902)],
                    []
                )
            );
        }

        [Test]
        public void Should_Return_Expected_EducationOrganizations()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.Operator.Should().Be(FilterOperator.Or);
            _expectedResult!.Filters.Should().HaveCount(2);
            _expectedResult!.Filters[0].Value.Should().Be("255901");
            _expectedResult!.Filters[1].Value.Should().Be("255902");
            _expectedResult!.Filters.Select(x => x.GetType().Name).Should().AllBe("EducationOrganization");
        }

        [Test]
        public void Should_Return_Correct_Authorization_Strategy_Name()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!
                .AuthorizationStrategyName.Should()
                .Be("RelationshipsWithStudentsOnlyThroughResponsibility");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Claim_Has_No_EducationOrganizations
    {
        [Test]
        public void Should_Throw_AuthorizationException()
        {
            // Arrange
            var filtersProvider = new RelationshipsWithStudentsOnlyThroughResponsibilityFiltersProvider();
            var clientAuthorizations = new ClientAuthorizations("", "", "", [], []);

            // Act & Assert
            var exception = Assert.Throws<AuthorizationException>(() =>
                filtersProvider.GetFilters(clientAuthorizations)
            );
            exception.Should().NotBeNull();
            exception!
                .Message.Should()
                .Be(
                    "The API client has been given permissions on a resource that uses the 'RelationshipsWithStudentsOnlyThroughResponsibility' authorization strategy but the client doesn't have any education organizations assigned."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Single_EducationOrganization
    {
        private AuthorizationStrategyEvaluator? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var filters = new RelationshipsWithStudentsOnlyThroughResponsibilityFiltersProvider();
            _expectedResult = filters.GetFilters(
                new ClientAuthorizations("", "", "", [new EducationOrganizationId(123456)], [])
            );
        }

        [Test]
        public void Should_Return_Single_Filter()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.Operator.Should().Be(FilterOperator.Or);
            _expectedResult!.Filters.Should().HaveCount(1);
            _expectedResult!.Filters[0].Value.Should().Be("123456");
            _expectedResult!.Filters[0].GetType().Name.Should().Be("EducationOrganization");
        }
    }
}
