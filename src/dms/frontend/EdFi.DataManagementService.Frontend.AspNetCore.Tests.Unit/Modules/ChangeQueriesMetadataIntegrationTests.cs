// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
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

[TestFixture]
[NonParallelizable]
public class Given_real_ApiSchema_change_queries_metadata
{
    private const string AuthenticationService = "https://auth.example.org/oauth/token";

    [Test]
    public async Task It_serves_the_core_standalone_document_with_server_and_security_metadata()
    {
        await using var factory = CreateFactory(
            CreateApiSchemaNodes(coreChangeQueries: true, extensionChangeQueries: false)
        );
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/changequeries/v1/swagger.json");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(content)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["info"]!["title"]!.GetValue<string>().Should().Be("Ed-Fi Change Queries API");
        json["paths"]!["/availableChangeVersions"]!["get"]!["summary"]!
            .GetValue<string>()
            .Should()
            .Be("Core available change versions");
        json["servers"]![0]!["url"]!.GetValue<string>().Should().Be("http://localhost/changeQueries/v1");
        json["components"]!["securitySchemes"]!["oauth2_client_credentials"]!["flows"]!["clientCredentials"]![
            "tokenUrl"
        ]!
            .GetValue<string>()
            .Should()
            .Be(AuthenticationService);
        json["security"]!.AsArray().Should().ContainSingle();
    }

    [Test]
    public async Task It_starts_and_omits_change_queries_metadata_when_the_core_document_is_missing()
    {
        await using var factory = CreateFactory(
            CreateApiSchemaNodes(coreChangeQueries: false, extensionChangeQueries: false)
        );
        using var client = factory.CreateClient();

        var specificationsResponse = await client.GetAsync("/metadata/specifications");
        string specificationsContent = await specificationsResponse.Content.ReadAsStringAsync();
        var specifications = JsonNode.Parse(specificationsContent)!.AsArray();
        var changeQueries = specifications.SingleOrDefault(node =>
            node!["name"]!.GetValue<string>() == "Change-Queries"
        );

        var changeQueriesResponse = await client.GetAsync("/metadata/changequeries/v1/swagger.json");

        specificationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        changeQueries.Should().BeNull();
        changeQueriesResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_ignores_extension_only_standalone_change_queries_documents()
    {
        await using var factory = CreateFactory(
            CreateApiSchemaNodes(coreChangeQueries: false, extensionChangeQueries: true)
        );
        using var client = factory.CreateClient();

        var specificationsResponse = await client.GetAsync("/metadata/specifications");
        string specificationsContent = await specificationsResponse.Content.ReadAsStringAsync();
        var specifications = JsonNode.Parse(specificationsContent)!.AsArray();
        var changeQueries = specifications.SingleOrDefault(node =>
            node!["name"]!.GetValue<string>() == "Change-Queries"
        );

        var changeQueriesResponse = await client.GetAsync("/metadata/changequeries/v1/swagger.json");

        specificationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        changeQueries.Should().BeNull();
        changeQueriesResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_emits_discovery_change_queries_url_with_the_same_route_prefix_as_data()
    {
        await using var factory = CreateFactory(
            CreateApiSchemaNodes(coreChangeQueries: false, extensionChangeQueries: false),
            new Dictionary<string, string?>
            {
                ["AppSettings:MultiTenancy"] = "true",
                ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
            }
        );
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/tenant-a/255901/2026");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(content)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["urls"]!["dataManagementApi"]!
            .GetValue<string>()
            .Should()
            .Be("http://localhost/tenant-a/255901/2026/data");
        json["urls"]!["changeQueries"]!
            .GetValue<string>()
            .Should()
            .Be("http://localhost/tenant-a/255901/2026/changeQueries/v1/");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        ApiSchemaDocumentNodes apiSchemaNodes,
        Dictionary<string, string?>? configuration = null
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

                services.Replace(ServiceDescriptor.Singleton(CreateApiSchemaProvider(apiSchemaNodes)));

                var profileService = A.Fake<IProfileService>();
                A.CallTo(() => profileService.GetProfileNamesAsync(A<string?>._))
                    .Returns(Task.FromResult<IReadOnlyList<string>>([]));
                services.Replace(ServiceDescriptor.Singleton(profileService));
            });
        });
    }

    private static IApiSchemaProvider CreateApiSchemaProvider(ApiSchemaDocumentNodes apiSchemaNodes)
    {
        var apiSchemaProvider = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(apiSchemaNodes);
        A.CallTo(() => apiSchemaProvider.SchemaLoadId).Returns(Guid.NewGuid());
        A.CallTo(() => apiSchemaProvider.IsSchemaValid).Returns(true);
        A.CallTo(() => apiSchemaProvider.ApiSchemaFailures).Returns(new List<ApiSchemaFailure>());
        return apiSchemaProvider;
    }

    private static ApiSchemaDocumentNodes CreateApiSchemaNodes(
        bool coreChangeQueries,
        bool extensionChangeQueries
    )
    {
        JsonNode coreApiSchema = CreateApiSchemaRoot(
            CreateProjectSchema(
                projectName: "Ed-Fi",
                projectEndpointName: "ed-fi",
                isExtensionProject: false,
                changeQueriesDocument: coreChangeQueries
                    ? CreateChangeQueriesDocument(
                        title: "Ed-Fi Change Queries API",
                        summary: "Core available change versions"
                    )
                    : null
            )
        );

        JsonNode[] extensionApiSchemas = extensionChangeQueries
            ?
            [
                CreateApiSchemaRoot(
                    CreateProjectSchema(
                        projectName: "Sample",
                        projectEndpointName: "sample",
                        isExtensionProject: true,
                        changeQueriesDocument: CreateChangeQueriesDocument(
                            title: "Sample Change Queries API",
                            summary: "Extension available change versions"
                        )
                    )
                ),
            ]
            : [];

        return new ApiSchemaDocumentNodes(coreApiSchema, extensionApiSchemas);
    }

    private static JsonObject CreateApiSchemaRoot(JsonObject projectSchema)
    {
        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema };
    }

    private static JsonObject CreateProjectSchema(
        string projectName,
        string projectEndpointName,
        bool isExtensionProject,
        JsonObject? changeQueriesDocument
    )
    {
        var openApiBaseDocuments = new JsonObject
        {
            ["resources"] = CreateOpenApiDocument($"{projectName} Resources API"),
            ["descriptors"] = CreateOpenApiDocument($"{projectName} Descriptors API"),
        };

        if (changeQueriesDocument is not null)
        {
            openApiBaseDocuments["changeQueries"] = changeQueriesDocument;
        }

        return new JsonObject
        {
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["description"] = $"{projectName} data standard",
            ["domains"] = new JsonArray(),
            ["educationOrganizationHierarchy"] = new JsonObject(),
            ["educationOrganizationTypes"] = new JsonArray(),
            ["isExtensionProject"] = isExtensionProject,
            ["openApiBaseDocuments"] = openApiBaseDocuments,
            ["projectEndpointName"] = projectEndpointName,
            ["projectName"] = projectName,
            ["projectVersion"] = "5.0.0",
            ["resourceNameMapping"] = new JsonObject(),
            ["resourceSchemas"] = new JsonObject(),
        };
    }

    private static JsonObject CreateChangeQueriesDocument(string title, string summary)
    {
        JsonObject document = CreateOpenApiDocument(title);
        document["paths"] = new JsonObject
        {
            ["/availableChangeVersions"] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["summary"] = summary,
                    ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "OK" } },
                },
            },
        };
        document["tags"] = new JsonArray(new JsonObject { ["name"] = "changeQueries" });
        return document;
    }

    private static JsonObject CreateOpenApiDocument(string title)
    {
        return new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = "5.0.0" },
            ["servers"] = new JsonArray(),
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
            ["tags"] = new JsonArray(),
        };
    }
}
