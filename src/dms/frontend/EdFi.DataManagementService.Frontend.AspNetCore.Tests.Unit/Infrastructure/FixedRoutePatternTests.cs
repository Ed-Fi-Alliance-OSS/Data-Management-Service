// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
public class FixedRoutePatternTests
{
    [TestFixture]
    public class Given_No_Multitenancy
    {
        [TestFixture]
        public class Given_No_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = FixedRoutePattern.Build([], multiTenancy: false);
            }

            [Test]
            public void It_should_return_an_empty_prefix()
            {
                _result.Should().Be(string.Empty);
            }
        }

        [TestFixture]
        public class Given_Single_Route_Qualifier
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = FixedRoutePattern.Build(["districtId"], multiTenancy: false);
            }

            [Test]
            public void It_should_return_the_qualifier_segment()
            {
                _result.Should().Be("/{districtId}");
            }
        }

        [TestFixture]
        public class Given_Multiple_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = FixedRoutePattern.Build(["districtId", "schoolYear"], multiTenancy: false);
            }

            [Test]
            public void It_should_return_all_qualifier_segments()
            {
                _result.Should().Be("/{districtId}/{schoolYear}");
            }
        }
    }

    [TestFixture]
    public class Given_Multitenancy_Enabled
    {
        [TestFixture]
        public class Given_No_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = FixedRoutePattern.Build([], multiTenancy: true);
            }

            [Test]
            public void It_should_return_the_tenant_segment()
            {
                _result.Should().Be("/{tenant}");
            }
        }

        [TestFixture]
        public class Given_Single_Route_Qualifier
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = FixedRoutePattern.Build(["districtId"], multiTenancy: true);
            }

            [Test]
            public void It_should_return_the_tenant_before_the_qualifier()
            {
                _result.Should().Be("/{tenant}/{districtId}");
            }
        }

        [TestFixture]
        public class Given_Multiple_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = FixedRoutePattern.Build(["districtId", "schoolYear"], multiTenancy: true);
            }

            [Test]
            public void It_should_return_the_tenant_before_all_qualifiers()
            {
                _result.Should().Be("/{tenant}/{districtId}/{schoolYear}");
            }
        }
    }
}
