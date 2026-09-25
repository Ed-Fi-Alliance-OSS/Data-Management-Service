// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Serves the fixed identity OpenAPI document over HTTP with the toggle on (design.md D2; story D1,
/// D7) and asserts the servers array, the shared OAuth2 security section, self-resolution of every
/// $ref, and the metadata listing entry - the same things
/// <see cref="ChangeQueriesMetadataIntegrationTests" /> asserts for the change-queries document.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdentityOpenApiMetadataTests
{
    private const string AuthenticationService = "https://auth.example.org/oauth/token";

    [TestFixture]
    public class Given_The_Identity_Document_Served_At_Root
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonNode _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory();
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/metadata/identity/v2/swagger.json");
            string content = await _response.Content.ReadAsStringAsync();
            _document = JsonNode.Parse(content)!;
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_serves_the_root_server_url()
        {
            _document["servers"]![0]!["url"]!.GetValue<string>().Should().Be("http://localhost/identity/v2");
        }

        [Test]
        public void It_declares_the_oauth2_client_credentials_scheme_type()
        {
            _document["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["type"]!
                .GetValue<string>()
                .Should()
                .Be("oauth2");
        }

        [Test]
        public void It_declares_the_configured_token_url()
        {
            _document["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["flows"]![
                "clientCredentials"
            ]!["tokenUrl"]!
                .GetValue<string>()
                .Should()
                .Be(AuthenticationService);
        }

        [Test]
        public void It_requires_exactly_one_security_scheme()
        {
            _document["security"]!.AsArray().Should().ContainSingle();
        }

        [Test]
        public void It_requires_the_oauth2_client_credentials_scheme()
        {
            _document["security"]![0]!.AsObject().Should().ContainKey("oauth2_client_credentials");
        }

        [Test]
        public void It_resolves_every_ref()
        {
            List<string> unresolved = FindUnresolvedRefs(_document, _document);
            unresolved.Should().BeEmpty();
        }

        private static List<string> FindUnresolvedRefs(JsonNode root, JsonNode node)
        {
            List<string> unresolved = [];

            switch (node)
            {
                case JsonObject obj:
                    if (obj.TryGetPropertyValue("$ref", out JsonNode? refNode) && refNode is not null)
                    {
                        string reference = refNode.GetValue<string>();
                        if (!TryResolve(root, reference))
                        {
                            unresolved.Add(reference);
                        }
                    }
                    foreach ((string _, JsonNode? value) in obj)
                    {
                        if (value is not null)
                        {
                            unresolved.AddRange(FindUnresolvedRefs(root, value));
                        }
                    }
                    break;
                case JsonArray array:
                    foreach (JsonNode? item in array)
                    {
                        if (item is not null)
                        {
                            unresolved.AddRange(FindUnresolvedRefs(root, item));
                        }
                    }
                    break;
            }

            return unresolved;
        }

        private static bool TryResolve(JsonNode root, string reference)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                return false;
            }

            JsonNode? current = root;
            foreach (string segment in reference[2..].Split('/'))
            {
                current = current?[segment];
                if (current is null)
                {
                    return false;
                }
            }

            return true;
        }
    }

    [TestFixture]
    public class Given_The_Identity_Document_Served_Under_A_Route_Qualified_Prefix
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonNode _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory(
                new Dictionary<string, string?>
                {
                    ["AppSettings:MultiTenancy"] = "true",
                    ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
                },
                configureDataStoreProvider: dataStoreProvider =>
                {
                    A.CallTo(() => dataStoreProvider.GetAll(A<string?>._))
                        .Returns([
                            DataStoreWithRouteContext(1, ("districtId", "255901"), ("schoolYear", "2026")),
                        ]);
                }
            );
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/tenant-a/255901/2026/metadata/identity/v2/swagger.json");
            string content = await _response.Content.ReadAsStringAsync();
            _document = JsonNode.Parse(content)!;
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_serves_the_route_qualified_server_url()
        {
            _document["servers"]![0]!["url"]!
                .GetValue<string>()
                .Should()
                .Be("http://localhost/{tenant}/{districtId}/{schoolYear}/identity/v2");
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
    }

    [TestFixture]
    public class Given_The_Metadata_Specifications_Listing
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonNode? _identitySection;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory();
            _client = _factory.CreateClient();

            _response = await _client.GetAsync("/metadata/specifications");
            string content = await _response.Content.ReadAsStringAsync();
            JsonArray sections = JsonNode.Parse(content)!.AsArray();

            _identitySection = sections.SingleOrDefault(node =>
                node!["name"]!.GetValue<string>() == "Identity"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            _response.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_includes_an_identity_section()
        {
            _identitySection.Should().NotBeNull();
        }

        [Test]
        public void It_uses_the_other_prefix()
        {
            _identitySection!["prefix"]!.GetValue<string>().Should().Be("Other");
        }

        [Test]
        public void It_points_to_the_identity_swagger_endpoint()
        {
            _identitySection!["endpointUri"]!
                .GetValue<string>()
                .Should()
                .EndWith("/metadata/identity/v2/swagger.json");
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?>? configuration = null,
        Action<IDataStoreProvider>? configureDataStoreProvider = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configurationBuilder) =>
                {
                    configurationBuilder.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:AuthenticationService"] = AuthenticationService,
                            ["AppSettings:EnableIdentityManagement"] = "true",
                        }
                    );

                    if (configuration is not null)
                    {
                        configurationBuilder.AddInMemoryCollection(configuration);
                    }
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                services.Replace(ServiceDescriptor.Singleton(CreateApiSchemaProvider()));

                var profileService = A.Fake<IProfileService>();
                A.CallTo(() => profileService.GetProfileNamesAsync(A<string?>._))
                    .Returns(Task.FromResult<IReadOnlyList<string>>([]));
                services.Replace(ServiceDescriptor.Singleton(profileService));

                if (configureDataStoreProvider is not null)
                {
                    var dataStoreProvider = A.Fake<IDataStoreProvider>();
                    var mockInstance = new DataStore(1, "Test", "TestInstance", "test-connection-string", []);
                    A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                        .Returns([mockInstance]);
                    A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                        .Returns(new List<string> { "TestTenant" });
                    A.CallTo(() => dataStoreProvider.GetAll(A<string?>._)).Returns([mockInstance]);
                    A.CallTo(() => dataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(mockInstance);
                    A.CallTo(() => dataStoreProvider.IsLoaded(A<string?>._)).Returns(true);
                    A.CallTo(() => dataStoreProvider.TenantExists(A<string>.That.IsNotNull())).Returns(true);
                    A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys())
                        .Returns(new List<string> { "" }.AsReadOnly());

                    configureDataStoreProvider(dataStoreProvider);

                    services.Replace(ServiceDescriptor.Singleton(dataStoreProvider));
                }
            });
        });
    }

    private static IApiSchemaProvider CreateApiSchemaProvider()
    {
        var apiSchemaProvider = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(CreateApiSchemaNodes());
        A.CallTo(() => apiSchemaProvider.SchemaLoadId).Returns(Guid.NewGuid());
        A.CallTo(() => apiSchemaProvider.IsSchemaValid).Returns(true);
        A.CallTo(() => apiSchemaProvider.ApiSchemaFailures).Returns(new List<ApiSchemaFailure>());
        return apiSchemaProvider;
    }

    private static ApiSchemaDocumentNodes CreateApiSchemaNodes()
    {
        JsonObject minimalDocument = new()
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = "Ed-Fi Resources API", ["version"] = "5.0.0" },
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
            ["tags"] = new JsonArray(),
        };

        JsonObject projectSchema = new()
        {
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["description"] = "Ed-Fi data standard",
            ["domains"] = new JsonArray(),
            ["educationOrganizationHierarchy"] = new JsonObject(),
            ["educationOrganizationTypes"] = new JsonArray(),
            ["isExtensionProject"] = false,
            ["openApiBaseDocuments"] = new JsonObject
            {
                ["resources"] = minimalDocument.DeepClone(),
                ["descriptors"] = minimalDocument.DeepClone(),
            },
            ["projectEndpointName"] = "ed-fi",
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "5.0.0",
            ["resourceNameMapping"] = new JsonObject(),
            ["resourceSchemas"] = new JsonObject(),
        };

        JsonObject coreApiSchema = new()
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = projectSchema,
        };

        return new ApiSchemaDocumentNodes(coreApiSchema, []);
    }
}
