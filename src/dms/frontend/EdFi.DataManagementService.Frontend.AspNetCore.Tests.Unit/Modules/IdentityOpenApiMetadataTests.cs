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

    [Test]
    public async Task It_serves_the_identity_document_with_root_server_and_security_metadata()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/identity/v2/swagger.json");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(content)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["servers"]![0]!["url"]!.GetValue<string>().Should().Be("http://localhost/identity/v2");
        json["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["type"]!
            .GetValue<string>()
            .Should()
            .Be("oauth2");
        json["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["flows"]!["clientCredentials"]![
            "tokenUrl"
        ]!
            .GetValue<string>()
            .Should()
            .Be(AuthenticationService);
        json["security"]!.AsArray().Should().ContainSingle();
        json["security"]![0]!.AsObject().Should().ContainKey("oauth2_client_credentials");
    }

    [Test]
    public async Task It_serves_the_identity_document_with_the_route_qualified_server_url()
    {
        await using var factory = CreateFactory(
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
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/tenant-a/255901/2026/metadata/identity/v2/swagger.json");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(content)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["servers"]![0]!["url"]!
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

    [Test]
    public async Task Every_ref_in_the_served_identity_document_resolves()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/identity/v2/swagger.json");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode document = JsonNode.Parse(content)!;

        List<string> unresolved = FindUnresolvedRefs(document, document);
        unresolved.Should().BeEmpty();
    }

    [Test]
    public async Task The_metadata_listing_includes_the_identity_entry()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/specifications");
        string content = await response.Content.ReadAsStringAsync();
        JsonArray sections = JsonNode.Parse(content)!.AsArray();

        JsonNode? identitySection = sections.SingleOrDefault(node =>
            node!["name"]!.GetValue<string>() == "Identity"
        );

        identitySection.Should().NotBeNull();
        identitySection!["prefix"]!.GetValue<string>().Should().Be("Other");
        identitySection["endpointUri"]!
            .GetValue<string>()
            .Should()
            .EndWith("/metadata/identity/v2/swagger.json");
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
