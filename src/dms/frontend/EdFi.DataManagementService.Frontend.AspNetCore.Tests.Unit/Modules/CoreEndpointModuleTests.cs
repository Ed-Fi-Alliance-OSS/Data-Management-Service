// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    [TestFixture]
    [NonParallelizable]
    public class Given_Change_Query_Stub_Routes
    {
        private IApiService _apiService = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;

        [SetUp]
        public void Setup()
        {
            _apiService = A.Fake<IApiService>();
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => _apiService);
                });
            });
            _client = _factory.CreateClient();
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
            _factory.Dispose();
        }

        [TestCase("deletes")]
        [TestCase("keyChanges")]
        public async Task It_should_return_an_empty_json_array_without_calling_the_core_get_path(
            string changeQueryType
        )
        {
            var response = await _client.GetAsync($"/data/ed-fi/surveys/{changeQueryType}");
            string content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Should().Be("[]");
            A.CallTo(() => _apiService.Get(A<FrontendRequest>._)).MustNotHaveHappened();
        }
    }
}
