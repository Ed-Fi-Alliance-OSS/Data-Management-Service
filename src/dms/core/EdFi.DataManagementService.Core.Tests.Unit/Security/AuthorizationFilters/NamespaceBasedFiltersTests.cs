// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security.AuthorizationFilters;

public class NamespaceBasedFiltersTests
{
    [TestFixture]
    public class Given_Claim_Has_NamespacePrefixes
    {
        private AuthorizationStrategyEvaluator? _expectedResult;

        [SetUp]
        public void Setup()
        {
            var filters = new NamespaceBasedFiltersProvider();
            _expectedResult = filters.GetFilters(
                [new JsonPath("$.namespace")],
                new ApiClientDetails(
                    "",
                    "",
                    [],
                    [new NamespacePrefix("uri://namespace1"), new NamespacePrefix("uri://namespace2")]
                )
            );
        }

        [Test]
        public void Should_Return_Expected_NamespacePrefixes()
        {
            _expectedResult.Should().NotBeNull();
            _expectedResult!.Operator.Should().Be(FilterOperator.Or);
            _expectedResult!.Filters.Should().HaveCount(2);
            _expectedResult!.Filters[0].Value.Should().Be("uri://namespace1");
            _expectedResult!.Filters[1].Value.Should().Be("uri://namespace2");
        }
    }
}
