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

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class MetadataModuleTests
{
    [TestFixture]
    public class When_Getting_Profiles_Endpoint
    {
        [Test]
        public async Task It_returns_profile_names_list()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns((JsonNode?)null);
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
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns((JsonNode?)null);
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
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns((JsonNode?)null);

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

        [TestCase(false, "", "http://localhost/changeQueries/v1")]
        [TestCase(true, "", "http://localhost/{tenant}/changeQueries/v1")]
        [TestCase(
            false,
            "districtId,schoolYear",
            "http://localhost/{districtId}/{schoolYear}/changeQueries/v1"
        )]
        [TestCase(
            true,
            "districtId,schoolYear",
            "http://localhost/{tenant}/{districtId}/{schoolYear}/changeQueries/v1"
        )]
        public async Task It_uses_the_change_queries_route_base_with_configured_prefixes(
            bool multiTenancy,
            string routeQualifierSegments,
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
            A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>.Ignored))
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

            // Act
            var response = await client.GetAsync("/metadata/changequeries/v1/swagger.json");
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
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns(JsonNode.Parse("""{"openapi": "3.0.0"}"""));
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
        }

        [Test]
        public async Task It_omits_Change_Queries_when_the_standalone_document_is_absent()
        {
            // Arrange
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.GetChangeQueriesOpenApiSpecification(A<JsonArray>._))
                .Returns((JsonNode?)null);
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
