// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class MetadataModuleTests
{
    [TestFixture]
    public class When_Requesting_Tenant_Only_Metadata_With_Required_Route_Qualifiers
    {
        [TestCase("/tenant1/metadata/dependencies")]
        [TestCase("/tenant1/metadata/specifications")]
        public async Task It_returns_not_found_instead_of_matching_discovery(string requestPath)
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.Configure<FrontendAppSettings>(options =>
                    {
                        options.MultiTenancy = true;
                        options.RouteQualifierSegments = "districtId,schoolYear";
                    });
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync(requestPath);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class When_Mapping_Qualified_Metadata_Routes
    {
        private sealed class RejectingMetadataRouteValidator : IMetadataRouteValidator
        {
            public int CallCount { get; private set; }

            public Task<bool> ValidateAsync(
                HttpContext httpContext,
                CancellationToken cancellationToken = default
            )
            {
                CallCount++;
                httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.FromResult(false);
            }
        }

        [TestCase("/tenant1/255901/2024/metadata")]
        [TestCase("/tenant1/255901/2024/metadata/dependencies")]
        [TestCase("/tenant1/255901/2024/metadata/specifications")]
        [TestCase("/tenant1/255901/2024/metadata/specifications/resources-spec.json")]
        [TestCase("/tenant1/255901/2024/metadata/specifications/descriptors-spec.json")]
        [TestCase("/tenant1/255901/2024/metadata/changequeries/v1/swagger.json")]
        [TestCase("/tenant1/255901/2024/metadata/specifications/discovery-spec.json")]
        [TestCase("/tenant1/255901/2024/metadata/specifications/profiles/StudentProfile/resources-spec.json")]
        public async Task It_short_circuits_invalid_qualified_metadata_requests(string requestPath)
        {
            // Arrange
            var metadataRouteValidator = new RejectingMetadataRouteValidator();
            var apiService = A.Fake<IApiService>();
            var contentProvider = A.Fake<IContentProvider>();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(_ => apiService);
                    collection.AddTransient(_ => contentProvider);
                    collection.AddTransient<IMetadataRouteValidator>(_ => metadataRouteValidator);
                    collection.Configure<FrontendAppSettings>(options =>
                    {
                        options.MultiTenancy = true;
                        options.RouteQualifierSegments = "districtId,schoolYear";
                    });
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync(requestPath);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            metadataRouteValidator.CallCount.Should().Be(1);
        }

        [Test]
        public async Task It_preserves_the_unqualified_metadata_root_without_route_validation()
        {
            // Arrange
            var metadataRouteValidator = new RejectingMetadataRouteValidator();
            var apiService = A.Fake<IApiService>();

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(_ => apiService);
                    collection.AddTransient<IMetadataRouteValidator>(_ => metadataRouteValidator);
                    collection.Configure<FrontendAppSettings>(options =>
                    {
                        options.MultiTenancy = true;
                        options.RouteQualifierSegments = "districtId,schoolYear";
                    });
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            metadataRouteValidator.CallCount.Should().Be(0);
        }
    }

    [TestFixture]
    public class When_Validating_Qualified_Metadata_Routes
    {
        private static IOptions<FrontendAppSettings> RouteOptions(params string[] routeQualifierSegments)
        {
            return Options.Create(
                new FrontendAppSettings
                {
                    AuthenticationService = "http://localhost/oauth",
                    CorrelationIdHeader = "X-Correlation-Id",
                    Datastore = "postgresql",
                    RouteQualifierSegments = string.Join(',', routeQualifierSegments),
                }
            );
        }

        private static DataStore DataStoreWithRouteContext(
            long id,
            params (string Key, string Value)[] routeContext
        )
        {
            return new DataStore(
                id,
                "Test",
                $"TestInstance{id}",
                "test-connection-string",
                routeContext.ToDictionary(
                    item => new RouteQualifierName(item.Key),
                    item => new RouteQualifierValue(item.Value)
                )
            );
        }

        [Test]
        public async Task It_allows_unqualified_metadata_requests()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            var tenantValidator = A.Fake<ITenantValidator>();
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            var validator = new MetadataRouteValidator(tenantValidator, dataStoreProvider, RouteOptions());

            // Act
            bool result = await validator.ValidateAsync(httpContext);

            // Assert
            result.Should().BeTrue();
            A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>._)).MustNotHaveHappened();
        }

        [TestCase("tenant", "")]
        [TestCase("tenant", " ")]
        [TestCase("tenant", null)]
        [TestCase("districtId", "")]
        [TestCase("districtId", " ")]
        [TestCase("districtId", null)]
        public async Task It_rejects_present_but_blank_route_values(string key, string? value)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues[key] = value;
            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>._)).Returns(true);
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            var validator = new MetadataRouteValidator(
                tenantValidator,
                dataStoreProvider,
                RouteOptions(key == "tenant" ? [] : ["districtId"])
            );

            bool result = await validator.ValidateAsync(httpContext);

            result.Should().BeFalse();
            httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        }

        [TestCase("tenant")]
        [TestCase("districtId")]
        [TestCase("schoolYear")]
        public async Task It_rejects_incomplete_qualified_contexts(string missingKey)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["tenant"] = "Tenant_255901";
            httpContext.Request.RouteValues["districtId"] = "255901";
            httpContext.Request.RouteValues["schoolYear"] = "2024";
            httpContext.Request.RouteValues.Remove(missingKey);
            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync(A<string>._)).Returns(true);
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            var options = RouteOptions("districtId", "schoolYear");
            options.Value.MultiTenancy = true;
            var validator = new MetadataRouteValidator(tenantValidator, dataStoreProvider, options);

            bool result = await validator.ValidateAsync(httpContext);

            result.Should().BeFalse();
            httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_allows_unqualified_metadata_with_dynamic_values_and_configured_qualifiers()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["section"] = "ed-fi";
            httpContext.Request.RouteValues["fileName"] = "Ed-Fi-Core";
            var options = RouteOptions("districtId", "schoolYear");
            options.Value.MultiTenancy = true;
            var validator = new MetadataRouteValidator(
                A.Fake<ITenantValidator>(),
                A.Fake<IDataStoreProvider>(),
                options
            );

            bool result = await validator.ValidateAsync(httpContext);

            result.Should().BeTrue();
        }

        [Test]
        public async Task It_allows_matching_tenant_and_route_context()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["tenant"] = "Tenant_255901";
            httpContext.Request.RouteValues["districtId"] = "255901";
            httpContext.Request.RouteValues["schoolYear"] = "2024";

            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync("Tenant_255901")).Returns(true);

            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetAll("Tenant_255901"))
                .Returns([DataStoreWithRouteContext(1, ("districtId", "255901"), ("schoolYear", "2024"))]);

            var validator = new MetadataRouteValidator(
                tenantValidator,
                dataStoreProvider,
                RouteOptions("districtId", "schoolYear")
            );

            // Act
            bool result = await validator.ValidateAsync(httpContext);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public async Task It_rejects_route_context_when_the_cached_tenant_has_no_match()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["tenant"] = "Tenant_255901";
            httpContext.Request.RouteValues["districtId"] = "255901";
            httpContext.Request.RouteValues["schoolYear"] = "2024";

            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync("Tenant_255901")).Returns(true);

            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetAll("Tenant_255901")).Returns([]);

            var validator = new MetadataRouteValidator(
                tenantValidator,
                dataStoreProvider,
                RouteOptions("districtId", "schoolYear")
            );

            // Act
            bool result = await validator.ValidateAsync(httpContext);

            // Assert
            result.Should().BeFalse();
            httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            A.CallTo(() =>
                    dataStoreProvider.RefreshInstancesIfExpiredAsync("Tenant_255901", A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => dataStoreProvider.LoadDataStores("Tenant_255901", A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_rejects_unknown_tenant()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["tenant"] = "UnknownTenant";

            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync("UnknownTenant")).Returns(false);

            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            var validator = new MetadataRouteValidator(tenantValidator, dataStoreProvider, RouteOptions());

            // Act
            bool result = await validator.ValidateAsync(httpContext);

            // Assert
            result.Should().BeFalse();
            httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_rejects_non_matching_route_context()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["tenant"] = "Tenant_255901";
            httpContext.Request.RouteValues["districtId"] = "999999";
            httpContext.Request.RouteValues["schoolYear"] = "2024";

            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync("Tenant_255901")).Returns(true);

            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetAll("Tenant_255901"))
                .Returns([DataStoreWithRouteContext(1, ("districtId", "255901"), ("schoolYear", "2024"))]);

            var validator = new MetadataRouteValidator(
                tenantValidator,
                dataStoreProvider,
                RouteOptions("districtId", "schoolYear")
            );

            // Act
            bool result = await validator.ValidateAsync(httpContext);

            // Assert
            result.Should().BeFalse();
            httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        }

        [TestCase("section", "discovery")]
        [TestCase("profileName", "StudentProfile")]
        public async Task It_ignores_dynamic_metadata_route_values(
            string dynamicRouteValueName,
            string dynamicRouteValue
        )
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.RouteValues["tenant"] = "Tenant_255901";
            httpContext.Request.RouteValues["districtId"] = "255901";
            httpContext.Request.RouteValues["schoolYear"] = "2024";
            httpContext.Request.RouteValues[dynamicRouteValueName] = dynamicRouteValue;

            var tenantValidator = A.Fake<ITenantValidator>();
            A.CallTo(() => tenantValidator.ValidateTenantAsync("Tenant_255901")).Returns(true);

            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetAll("Tenant_255901"))
                .Returns([DataStoreWithRouteContext(1, ("districtId", "255901"), ("schoolYear", "2024"))]);

            var validator = new MetadataRouteValidator(
                tenantValidator,
                dataStoreProvider,
                RouteOptions("districtId", "schoolYear")
            );

            // Act
            bool result = await validator.ValidateAsync(httpContext);

            // Assert
            result.Should().BeTrue();
        }
    }

    [TestFixture]
    public class When_Getting_Profiles_Endpoint
    {
        [Test]
        public async Task It_returns_profile_names_list()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).Returns(false);
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>(["StudentProfile", "SchoolProfile"]));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/specifications");
            var content = await response.Content.ReadAsStringAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;

            var profilesArray = jsonArray!
                .Where(x => x!["prefix"]!.GetValue<string>() == "Profiles")
                .ToArray();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonArray.Should().NotBeNull();
            profilesArray.Should().HaveCount(2);
            profilesArray[0]!["name"]!.GetValue<string>().Should().Be("StudentProfile");
            profilesArray[1]!["name"]!.GetValue<string>().Should().Be("SchoolProfile");
        }

        [Test]
        public async Task It_returns_empty_array_when_no_profiles()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).Returns(false);
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>([]));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/specifications");
            var content = await response.Content.ReadAsStringAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;

            var profilesArray = jsonArray!
                .Where(x => x!["prefix"]!.GetValue<string>() == "Profiles")
                .ToArray();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonArray.Should().NotBeNull();
            profilesArray!.Should().HaveCount(0);
        }
    }

    [TestFixture]
    public class When_Getting_Profile_OpenApi_Specification
    {
        [Test]
        public async Task It_returns_OpenApi_spec_for_valid_profile()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "StudentProfile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .Returns(
                    Task.FromResult<JsonNode?>(
                        JsonNode.Parse(
                            """
                            {
                              "openapi": "3.0.0",
                              "info": { "title": "StudentProfile Resources" },
                              "servers": [{ "url": "http://localhost/data" }]
                            }
                            """
                        )
                    )
                );

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync(
                "/metadata/specifications/profiles/StudentProfile/resources-spec.json"
            );
            var content = await response.Content.ReadAsStringAsync();
            var jsonContent = JsonNode.Parse(content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonContent.Should().NotBeNull();
            jsonContent!["openapi"]!.GetValue<string>().Should().Be("3.0.0");
            jsonContent["info"]!["title"]!.GetValue<string>().Should().Be("StudentProfile Resources");
        }

        [Test]
        public async Task It_returns_404_when_profile_not_found()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "NonExistentProfile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .Returns(Task.FromResult<JsonNode?>(null));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/profiles/NonExistentProfile/resources-spec.json");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task It_handles_case_insensitive_profile_names()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(A<string>._, A<string?>._, A<JsonArray>._)
                )
                .Returns(Task.FromResult<JsonNode?>(JsonNode.Parse("""{"openapi": "3.0.0"}""")));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync(
                "/metadata/specifications/profiles/studentprofile/resources-spec.json"
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "studentprofile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class When_Getting_The_Base_Metadata_Endpoint
    {
        private JsonNode? _jsonContent;
        private HttpResponseMessage? _response;

        [SetUp]
        public void SetUp()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => apiService);
                    }
                );
            });
            using var client = factory.CreateClient();

            // Act
            _response = client.GetAsync("/metadata").GetAwaiter().GetResult();
            var content = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _jsonContent = JsonNode.Parse(content) ?? throw new Exception("JSON parsing failed");
        }

        [TearDownAttribute]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_OK()
        {
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void Then_the_body_contains_the_dependencies_url()
        {
            _jsonContent?["dependencies"]?.ToString().Should().Be("http://localhost/metadata/dependencies");
        }

        [Test]
        public void Then_the_body_contains_the_specifications_url()
        {
            _jsonContent
                ?["specifications"]?.ToString()
                .Should()
                .Be("http://localhost/metadata/specifications");
        }

        [Test]
        public void Then_the_body_contains_the_xsdFiles_url()
        {
            _jsonContent?["discovery"]?.ToString().Should().Be("http://localhost/metadata/xsdFiles");
        }
    }

    [TestFixture]
    public class MetadataSpecificationsListTests
    {
        private WebApplicationFactory<Program> _factory;
        private HttpClient _client;
        private JsonArray? _specificationsJsonArray;

        [SetUp]
        public void SetUp()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetResourceOpenApiSpecification(A<JsonArray>._))
                .Returns(
                    JsonNode.Parse(
                        """
                        {
                          "openapi": "3.0.0",
                          "servers": [
                            {
                              "url": "http://localhost/data"
                            }
                          ]
                        }
                        """
                    )!
                );
            A.CallTo(() => apiService.GetDescriptorOpenApiSpecification(A<JsonArray>._))
                .Returns(
                    JsonNode.Parse(
                        """
                        {
                          "openapi": "3.0.0",
                          "servers": [
                            {
                              "url": "http://localhost/data"
                            }
                          ]
                        }
                        """
                    )!
                );
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).Returns(false);

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");

                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.AddTransient((x) => apiService);
                    }
                );
            });
            _client = _factory.CreateClient();

            // Act
            var response = _client.GetAsync("/metadata/specifications").GetAwaiter().GetResult();
            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var jsonContent = JsonNode.Parse(content);
            _specificationsJsonArray = jsonContent as JsonArray;
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void Metadata_Endpoint_Returns_Specifications_List()
        {
            // Assert
            _specificationsJsonArray.Should().NotBeNull();
            _specificationsJsonArray!.Count.Should().BeGreaterOrEqualTo(3);
            _specificationsJsonArray[0]!["name"]?.GetValue<string>().Should().Be("Resources");
            _specificationsJsonArray[1]!["name"]?.GetValue<string>().Should().Be("Descriptors");
            _specificationsJsonArray[2]!["name"]?.GetValue<string>().Should().Be("Discovery");
        }

        [Test]
        public async Task Api_Spec_Contains_Servers_Array()
        {
            // Assert
            _specificationsJsonArray.Should().NotBeNull();
            foreach (var item in _specificationsJsonArray!)
            {
                var endpointUri = item?["endpointUri"]?.GetValue<string>();
                endpointUri.Should().NotBeNullOrEmpty();
                var response = await _client.GetAsync(endpointUri);
                var content = await response.Content.ReadAsStringAsync();
                var jsonContent = JsonNode.Parse(content);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                jsonContent.Should().NotBeNull();
                var servers = jsonContent?["servers"];
                servers.Should().NotBeNull();
                servers.Should().BeOfType<JsonArray>();
                servers!.AsArray().Count.Should().Be(1);
                var server = servers[0];
                server.Should().NotBeNull();
                server?["url"]?.GetValue<string>().Should().Be("http://localhost/data");
            }
        }
    }

    [TestFixture]
    public class When_Building_OpenApi_Server_Urls
    {
        private static JsonObject OpenApiWithServers(JsonArray servers)
        {
            return new JsonObject { ["openapi"] = "3.0.0", ["servers"] = servers.DeepClone() };
        }

        private static DataStore DataStoreWithRouteContext(
            long id,
            params (string Key, string Value)[] routeContext
        )
        {
            return new DataStore(
                id,
                "Test",
                $"TestInstance{id}",
                "test-connection-string",
                routeContext.ToDictionary(
                    item => new RouteQualifierName(item.Key),
                    item => new RouteQualifierValue(item.Value)
                )
            );
        }

        [Test]
        public async Task It_uses_the_data_route_base_for_resource_descriptor_and_profile_documents()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetResourceOpenApiSpecification(A<JsonArray>._))
                .ReturnsLazily((JsonArray servers) => OpenApiWithServers(servers));
            A.CallTo(() => apiService.GetDescriptorOpenApiSpecification(A<JsonArray>._))
                .ReturnsLazily((JsonArray servers) => OpenApiWithServers(servers));
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "StudentProfile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .ReturnsLazily(
                    (string profileName, string? tenantId, JsonArray servers) =>
                        Task.FromResult<JsonNode?>(OpenApiWithServers(servers))
                );

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            string[] endpointUris =
            [
                "/metadata/specifications/resources-spec.json",
                "/metadata/specifications/descriptors-spec.json",
                "/metadata/specifications/profiles/StudentProfile/resources-spec.json",
            ];

            foreach (string endpointUri in endpointUris)
            {
                // Act
                var response = await client.GetAsync(endpointUri);
                var content = await response.Content.ReadAsStringAsync();
                var jsonContent = JsonNode.Parse(content);

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                jsonContent!["servers"]![0]!["url"]!.GetValue<string>().Should().Be("http://localhost/data");
            }
        }

        [Test]
        public async Task It_includes_PathBase_once_for_metadata_OpenApi_documents()
        {
            // Arrange
            string pathBase = "dms-api";
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetResourceOpenApiSpecification(A<JsonArray>._))
                .ReturnsLazily((JsonArray servers) => OpenApiWithServers(servers));
            A.CallTo(() => apiService.GetDescriptorOpenApiSpecification(A<JsonArray>._))
                .ReturnsLazily((JsonArray servers) => OpenApiWithServers(servers));
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .ReturnsLazily((JsonArray servers) => OpenApiWithServers(servers));
            A.CallTo(() =>
                    apiService.GetProfileOpenApiSpecificationAsync(
                        "StudentProfile",
                        A<string?>._,
                        A<JsonArray>._
                    )
                )
                .ReturnsLazily(
                    (string profileName, string? tenantId, JsonArray servers) =>
                        Task.FromResult<JsonNode?>(OpenApiWithServers(servers))
                );

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?> { ["AppSettings:PathBase"] = pathBase }
                        );
                    }
                );
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            var endpointExpectedServers = new Dictionary<string, string>
            {
                [$"/{pathBase}/metadata/specifications/resources-spec.json"] =
                    $"http://localhost/{pathBase}/data",
                [$"/{pathBase}/metadata/specifications/descriptors-spec.json"] =
                    $"http://localhost/{pathBase}/data",
                [$"/{pathBase}/metadata/specifications/profiles/StudentProfile/resources-spec.json"] =
                    $"http://localhost/{pathBase}/data",
                [$"/{pathBase}/metadata/changequeries/v1/swagger.json"] =
                    $"http://localhost/{pathBase}/changeQueries/v1",
            };

            foreach ((string endpointUri, string expectedServerUrl) in endpointExpectedServers)
            {
                // Act
                var response = await client.GetAsync(endpointUri);
                var content = await response.Content.ReadAsStringAsync();
                var jsonContent = JsonNode.Parse(content);

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                jsonContent!["servers"]![0]!["url"]!.GetValue<string>().Should().Be(expectedServerUrl);
            }
        }

        [TestCase(false, "", "", "http://localhost/changeQueries/v1")]
        [TestCase(true, "", "", "http://localhost/{tenant}/changeQueries/v1")]
        [TestCase(
            false,
            "districtId,schoolYear",
            "",
            "http://localhost/{districtId}/{schoolYear}/changeQueries/v1"
        )]
        [TestCase(
            true,
            "districtId,schoolYear",
            "",
            "http://localhost/{tenant}/{districtId}/{schoolYear}/changeQueries/v1"
        )]
        [TestCase(
            true,
            "districtId,schoolYear",
            "dms-api",
            "http://localhost/dms-api/{tenant}/{districtId}/{schoolYear}/changeQueries/v1"
        )]
        public async Task It_uses_the_change_queries_route_base_with_configured_prefixes(
            bool multiTenancy,
            string routeQualifierSegments,
            string pathBase,
            string expectedServerUrl
        )
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .ReturnsLazily((JsonArray servers) => OpenApiWithServers(servers));

            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            var tenantADataStore = DataStoreWithRouteContext(
                1,
                ("districtId", "255901"),
                ("schoolYear", "2024")
            );
            var tenantBDataStore = DataStoreWithRouteContext(
                2,
                ("districtId", "255902"),
                ("schoolYear", "2025")
            );
            A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>.Ignored, A<CancellationToken>._))
                .Returns([tenantADataStore, tenantBDataStore]);
            A.CallTo(() => dataStoreProvider.LoadTenants())
                .Returns(new List<string> { "tenantA", "tenantB" });
            A.CallTo(() => dataStoreProvider.GetById(A<long>.Ignored, A<string?>.Ignored))
                .Returns(tenantADataStore);
            A.CallTo(() => dataStoreProvider.IsLoaded(A<string?>.Ignored)).Returns(true);
            A.CallTo(() => dataStoreProvider.TenantExists(A<string>.That.IsNotNull())).Returns(true);

            if (multiTenancy)
            {
                A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys())
                    .Returns(new List<string> { "tenantB", "tenantA" }.AsReadOnly());
                A.CallTo(() => dataStoreProvider.GetAll("tenantA")).Returns([tenantADataStore]);
                A.CallTo(() => dataStoreProvider.GetAll("tenantB")).Returns([tenantBDataStore]);
            }
            else
            {
                A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys())
                    .Returns(new List<string> { "" }.AsReadOnly());
                A.CallTo(() => dataStoreProvider.GetAll(null)).Returns([tenantADataStore, tenantBDataStore]);
            }

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:MultiTenancy"] = multiTenancy.ToString(),
                                ["AppSettings:RouteQualifierSegments"] = routeQualifierSegments,
                                ["AppSettings:PathBase"] = pathBase,
                            }
                        );
                    }
                );
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => dataStoreProvider);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();
            string requestPath = string.IsNullOrWhiteSpace(pathBase)
                ? "/metadata/changequeries/v1/swagger.json"
                : $"/{pathBase}/metadata/changequeries/v1/swagger.json";

            // Act
            var response = await client.GetAsync(requestPath);
            var content = await response.Content.ReadAsStringAsync();
            var jsonContent = JsonNode.Parse(content);
            var server = jsonContent!["servers"]![0]!;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            server["url"]!.GetValue<string>().Should().Be(expectedServerUrl);

            if (multiTenancy)
            {
                var tenantVariable = server["variables"]!["tenant"]!;
                tenantVariable["default"]!.GetValue<string>().Should().Be("tenantA");
                tenantVariable["enum"]!
                    .AsArray()
                    .Select(value => value!.GetValue<string>())
                    .Should()
                    .Equal("tenantA", "tenantB");
            }

            if (!string.IsNullOrWhiteSpace(routeQualifierSegments))
            {
                var variables = server["variables"]!;
                variables["districtId"]!["default"]!.GetValue<string>().Should().Be("255902");
                variables["districtId"]!["enum"]!
                    .AsArray()
                    .Select(value => value!.GetValue<string>())
                    .Should()
                    .Equal("255902", "255901");
                variables["schoolYear"]!["default"]!.GetValue<string>().Should().Be("2025");
                variables["schoolYear"]!["enum"]!
                    .AsArray()
                    .Select(value => value!.GetValue<string>())
                    .Should()
                    .Equal("2025", "2024");
            }
        }
    }

    [TestFixture]
    public class When_Getting_Change_Queries_OpenApi_Metadata
    {
        [Test]
        public async Task It_returns_the_standalone_OpenApi_document_when_present()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns(
                    JsonNode.Parse(
                        """
                        {
                          "openapi": "3.0.0",
                          "info": {
                            "title": "Ed-Fi Change Queries API"
                          },
                          "paths": {
                            "/availableChangeVersions": {}
                          },
                          "servers": [
                            {
                              "url": "http://localhost/data"
                            }
                          ]
                        }
                        """
                    )
                );

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/changequeries/v1/swagger.json");
            var content = await response.Content.ReadAsStringAsync();
            var jsonContent = JsonNode.Parse(content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            jsonContent.Should().NotBeNull();
            jsonContent!["openapi"]!.GetValue<string>().Should().Be("3.0.0");
            jsonContent["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi Change Queries API");
            jsonContent["paths"]!.AsObject().Should().ContainKey("/availableChangeVersions");
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_returns_404_when_the_standalone_document_is_absent()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns((JsonNode?)null);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/changequeries/v1/swagger.json");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class When_Getting_Specifications_With_Change_Queries_Metadata
    {
        [Test]
        public async Task It_lists_Change_Queries_when_the_standalone_document_is_present()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).Returns(true);
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>([]));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/specifications");
            var content = await response.Content.ReadAsStringAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;
            var changeQueries = jsonArray!.SingleOrDefault(node =>
                node!["name"]!.GetValue<string>() == "Change-Queries"
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            changeQueries.Should().NotBeNull();
            changeQueries!["prefix"]!.GetValue<string>().Should().Be("Other");
            changeQueries["endpointUri"]!
                .GetValue<string>()
                .Should()
                .Be("http://localhost/metadata/changequeries/v1/swagger.json");
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).MustHaveHappenedOnceExactly();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .MustNotHaveHappened();
        }

        [TestCase("/tenant1/255901/2024/metadata/specifications", "", "/tenant1/255901/2024/metadata")]
        [TestCase(
            "/tenant1/255901/2024/MeTaDaTa/SpEcIfIcAtIoNs/",
            "/dms",
            "/dms/tenant1/255901/2024/MeTaDaTa"
        )]
        [TestCase("/metadata/SPECIFICATIONS/", "", "/metadata")]
        [TestCase("/metadata/specifications", "/dms", "/dms/metadata")]
        [TestCase("", "", "/metadata")]
        [TestCase("/", "/dms", "/dms/metadata")]
        [TestCase("/other", "/dms", "/dms/metadata")]
        public async Task It_preserves_the_metadata_prefix_for_Change_Queries(
            string path,
            string pathBase,
            string expectedPrefix
        )
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).Returns(true);
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>([]));

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("localhost");
            httpContext.Request.Path = path;
            httpContext.Request.PathBase = pathBase;
            httpContext.Response.Body = new MemoryStream();

            // Act
            await MetadataEndpointModule.GetSections(httpContext, apiService);
            httpContext.Response.Body.Position = 0;
            var content = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;
            var changeQueries = jsonArray!.Single(node =>
                node!["name"]!.GetValue<string>() == "Change-Queries"
            );

            // Assert
            changeQueries!["endpointUri"]!
                .GetValue<string>()
                .Should()
                .Be($"http://localhost{expectedPrefix}/changequeries/v1/swagger.json");
        }

        [Test]
        public async Task It_omits_Change_Queries_when_the_standalone_document_is_absent()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).Returns(false);
            A.CallTo(() => apiService.GetProfileNamesAsync(A<string?>._))
                .Returns(Task.FromResult<IReadOnlyList<string>>([]));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => apiService);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/specifications");
            var content = await response.Content.ReadAsStringAsync();
            var jsonArray = JsonNode.Parse(content) as JsonArray;
            var changeQueries = jsonArray!.SingleOrDefault(node =>
                node!["name"]!.GetValue<string>() == "Change-Queries"
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            changeQueries.Should().BeNull();
            A.CallTo(() => apiService.HasChangeQueriesOpenApiSpecification()).MustHaveHappenedOnceExactly();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_does_not_expose_the_specifications_alias()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                });
            });
            using var client = factory.CreateClient();

            // Act
            var response = await client.GetAsync("/metadata/specifications/changequeries-spec.json");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Metadata_Returns_Descriptors_Content()
    {
        // Arrange
        var contentProvider = A.Fake<IContentProvider>();

        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetDescriptorOpenApiSpecification(A<JsonArray>._))
            .Returns(
                JsonNode.Parse(
                    """
                    {
                      "openapi": "3.0.0",
                      "servers": [
                        {
                          "url": "http://localhost/data"
                        }
                      ],
                      "paths": {
                        "/ed-fi/absenceEventCategoryDescriptors": {}
                      }
                    }
                    """
                )!
            );

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient((x) => contentProvider);
                    collection.AddTransient((x) => apiService);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/specifications/descriptors-spec.json");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var openapiVersion = jsonContent?["openapi"]?.GetValue<string>();
        var paths = jsonContent?["paths"]?.AsObject();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        openapiVersion.Should().Be("3.0.0");
        paths.Should().NotBeNull();
        paths?["/ed-fi/absenceEventCategoryDescriptors"].Should().NotBeNull();
    }

    [Test]
    public async Task Metadata_Returns_Invalid_Resource_Error()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/swagger.json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Metadata_Returns_Dependencies()
    {
        // Arrange
        var httpContext = A.Fake<HttpContext>();

        var apiService = A.Fake<IApiService>();
        var dependenciesJson = JsonNode
            .Parse(
                """
                [
                  {
                    "resource": "/ed-fi/absenceEventCategoryDescriptors",
                    "order": 1
                  }
                ]
                """
            )!
            .AsArray();
        A.CallTo(() => apiService.GetDependencies()).Returns(dependenciesJson);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                    collection.AddTransient(x => httpContext);
                    collection.AddTransient((x) => apiService);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/dependencies");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        jsonContent
            ?[0]!["resource"]
            ?.GetValue<string>()
            .Should()
            .Be("/ed-fi/absenceEventCategoryDescriptors");
        jsonContent?[0]!["order"]?.GetValue<int>().Should().Be(1);
    }
}

/// <summary>
/// File-mode discovery-spec route tests. The /metadata/specifications/discovery-spec.json route
/// calls contentProvider.LoadJsonContent("discovery", rootUrl, oAuthUrl) which in file mode
/// reads the discovery-spec.json from the manifest workspace and applies HOST_URL replacement.
/// The real ContentProvider is built outside DI using FileModeWorkspaceBuilder so AppSettings
/// is not disturbed in the WebApplicationFactory host.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_file_mode_discovery_spec_route
{
    private string _workspaceRoot = string.Empty;
    private IContentProvider _fileModeContentProvider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_fileModeContentProvider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public async Task It_returns_200_with_replaced_urls_for_discovery_spec()
    {
        var fileModeContentProvider = _fileModeContentProvider;

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                var apiService = A.Fake<IApiService>();
                collection.AddTransient(x => apiService);
                // Inject the pre-built file-mode ContentProvider; no AppSettings change.
                collection.AddTransient(x => fileModeContentProvider);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/specifications/discovery-spec.json");
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().NotBeNull();
        // HOST_URL placeholders in the staged file should be replaced with the request base URL
        // The "host" field originally contains "HOST_URL/data/v3"
        json!["host"]!.GetValue<string>().Should().NotContain("HOST_URL");
        // The "token" field originally contains "HOST_URL/oauth/token"
        json["token"]!.GetValue<string>().Should().NotContain("HOST_URL");
    }
}

/// <summary>
/// File-mode failure path: when no project in the manifest provides discoverySpecPath,
/// the /metadata/specifications/discovery-spec.json route should produce an error
/// matching DLL-mode behavior (InvalidOperationException surfaced as 500 or similar).
/// The content provider is built standalone with a no-spec manifest so it throws on discovery.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_file_mode_missing_discovery_spec_route
{
    private string _workspaceRoot = string.Empty;
    private IContentProvider _noSpecContentProvider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        // Manifest with no discoverySpecPath on any project
        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "Ed-Fi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/Ed-Fi/ApiSchema.json"
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new CoreAppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = string.Empty,
            }
        );
        var manifestLogger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        var manifestProvider = new ApiSchemaAssetManifestProvider(appSettings, manifestLogger);
        var logger = A.Fake<ILogger<ContentProvider>>();
        _noSpecContentProvider = new ContentProvider(logger, manifestProvider);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public async Task It_returns_non_success_response_when_no_discovery_spec_exists()
    {
        var noSpecContentProvider = _noSpecContentProvider;

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                var apiService = A.Fake<IApiService>();
                collection.AddTransient(x => apiService);
                // Inject the no-spec content provider; LoadJsonContent("discovery",...) throws.
                collection.AddTransient(x => noSpecContentProvider);
            });
        });
        using var client = factory.CreateClient();

        // Use ResponseHeadersRead so we get the status code before reading the body.
        // The server throws InvalidOperationException which surfaces as a 500; some test
        // transports may surface this as an HttpRequestException before the status is readable.
        // Either outcome means the spec is absent — assert non-success.
        try
        {
            var response = await client.GetAsync(
                "/metadata/specifications/discovery-spec.json",
                HttpCompletionOption.ResponseHeadersRead
            );
            // Missing discovery spec surfaces as a server-side error (500) matching DLL-mode failure shape
            response.IsSuccessStatusCode.Should().BeFalse();
        }
        catch (HttpRequestException)
        {
            // Server-side exception surfaced as connection error — also non-success (expected)
        }
    }
}
