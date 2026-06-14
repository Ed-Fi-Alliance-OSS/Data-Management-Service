// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class CoreEndpointModuleTests
{
    [TestFixture]
    public class Given_BuildRoutePattern_With_No_Multitenancy
    {
        [TestFixture]
        public class Given_No_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern([], multiTenancy: false);
            }

            [Test]
            public void It_should_return_simple_data_path()
            {
                _result.Should().Be("/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Single_Route_Qualifier
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(["districtId"], multiTenancy: false);
            }

            [Test]
            public void It_should_return_path_with_qualifier_segment()
            {
                _result.Should().Be("/{districtId}/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Multiple_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(
                    ["districtId", "schoolYear"],
                    multiTenancy: false
                );
            }

            [Test]
            public void It_should_return_path_with_all_qualifier_segments()
            {
                _result.Should().Be("/{districtId}/{schoolYear}/data/{**dmsPath}");
            }
        }
    }

    [TestFixture]
    public class Given_BuildRoutePattern_With_Multitenancy_Enabled
    {
        [TestFixture]
        public class Given_No_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern([], multiTenancy: true);
            }

            [Test]
            public void It_should_return_path_with_tenant_segment()
            {
                _result.Should().Be("/{tenant}/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Single_Route_Qualifier
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(["districtId"], multiTenancy: true);
            }

            [Test]
            public void It_should_return_path_with_tenant_before_qualifier()
            {
                _result.Should().Be("/{tenant}/{districtId}/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Multiple_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(
                    ["districtId", "schoolYear"],
                    multiTenancy: true
                );
            }

            [Test]
            public void It_should_return_path_with_tenant_before_all_qualifiers()
            {
                _result.Should().Be("/{tenant}/{districtId}/{schoolYear}/data/{**dmsPath}");
            }
        }
    }
}
