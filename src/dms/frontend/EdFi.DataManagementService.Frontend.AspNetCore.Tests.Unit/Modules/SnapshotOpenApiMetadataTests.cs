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

/// <summary>
/// Serves the snapshot OpenAPI contract (DMS-1369) over HTTP from the real bundled ApiSchema packages
/// and asserts what a client actually receives.
/// </summary>
/// <remarks>
/// <para>
/// The Core fixtures assert the documents <c>ApiService</c> produces. This asserts the bytes that leave
/// the metadata endpoints, which is the only layer where routing, serialization, and the response
/// pipeline are also in play. Both halves are needed: a document can be assembled correctly and still
/// be served from the wrong route or serialized into something that no longer resolves.
/// </para>
/// <para>
/// No OpenAPI content is authored here. The positive cases are fed the Data Standard 5.2 core and TPDM
/// packages the build stages beside this test binary, and the negative case is the same content with a
/// single component removed.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class SnapshotOpenApiMetadataTests
{
    private const string AuthenticationService = "https://auth.example.org/oauth/token";

    private const string CorePackageId = "EdFi.DataStandard52.ApiSchema";
    private const string ExtensionPackageId = "EdFi.DataStandard52.TPDM.ApiSchema";

    private const string UseSnapshotParameterName = "Use-Snapshot";
    private const string SnapshotNotFoundResponseName = "SnapshotNotFound";
    private const string SnapshotMethodNotAllowedResponseName = "SnapshotMethodNotAllowed";
    private const string ProblemDetailsSchemaName = "ProblemDetails";

    private const string UseSnapshotParameterReference = "#/components/parameters/Use-Snapshot";
    private const string SnapshotNotFoundResponseReference = "#/components/responses/SnapshotNotFound";
    private const string SnapshotMethodNotAllowedResponseReference =
        "#/components/responses/SnapshotMethodNotAllowed";

    private const string ResourcesUrl = "/metadata/specifications/resources-spec.json";
    private const string DescriptorsUrl = "/metadata/specifications/descriptors-spec.json";
    private const string ChangeQueriesUrl = "/metadata/changequeries/v1/swagger.json";

    private JsonNode _resources = null!;
    private JsonNode _descriptors = null!;
    private JsonNode _changeQueries = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(LoadStagedApiSchemaNodes());
        using HttpClient client = factory.CreateClient();

        _resources = await GetDocumentAsync(client, ResourcesUrl);
        _descriptors = await GetDocumentAsync(client, DescriptorsUrl);
        _changeQueries = await GetDocumentAsync(client, ChangeQueriesUrl);
    }

    [Test]
    public void It_serves_the_snapshot_components_in_every_independently_served_document()
    {
        foreach ((JsonNode document, string documentName) in AllDocuments())
        {
            Component(document, "parameters", UseSnapshotParameterName)
                .Should()
                .NotBeNull("the served {0} document must define {1}", documentName, UseSnapshotParameterName);
            Component(document, "responses", SnapshotNotFoundResponseName)
                .Should()
                .NotBeNull(
                    "the served {0} document must define {1}",
                    documentName,
                    SnapshotNotFoundResponseName
                );
            Component(document, "schemas", ProblemDetailsSchemaName)
                .Should()
                .NotBeNull("the served {0} document must define {1}", documentName, ProblemDetailsSchemaName);
        }

        // The snapshot 405 belongs to the documents that serve mutations.
        Component(_resources, "responses", SnapshotMethodNotAllowedResponseName).Should().NotBeNull();
        Component(_descriptors, "responses", SnapshotMethodNotAllowedResponseName).Should().NotBeNull();
    }

    [Test]
    public void It_serves_documents_that_resolve_every_reference_within_themselves()
    {
        foreach ((JsonNode document, string documentName) in AllDocuments())
        {
            FindUnresolvedReferences(document)
                .Should()
                .BeEmpty(
                    "a client fetching {0} on its own cannot resolve a reference out of a sibling document",
                    documentName
                );
        }
    }

    [Test]
    public void It_serves_operations_that_reference_the_snapshot_components()
    {
        // Components nobody references would satisfy the presence assertions above while advertising
        // nothing, so the served operations have to be checked to use them.
        AssertOperationReferences(_resources, "/ed-fi/schools", "get", UseSnapshotParameterReference);
        AssertOperationReferences(_resources, "/ed-fi/schools/{id}", "get", UseSnapshotParameterReference);
        AssertOperationReferences(_resources, "/ed-fi/schools/deletes", "get", UseSnapshotParameterReference);
        AssertOperationReferences(
            _resources,
            "/ed-fi/schools/keyChanges",
            "get",
            UseSnapshotParameterReference
        );
        AssertResponseReferences(
            _resources,
            "/ed-fi/schools",
            "get",
            "404",
            SnapshotNotFoundResponseReference
        );
        AssertResponseReferences(
            _resources,
            "/ed-fi/schools",
            "post",
            "405",
            SnapshotMethodNotAllowedResponseReference
        );

        AssertOperationReferences(
            _descriptors,
            "/ed-fi/absenceEventCategoryDescriptors",
            "get",
            UseSnapshotParameterReference
        );
        AssertResponseReferences(
            _descriptors,
            "/ed-fi/absenceEventCategoryDescriptors",
            "post",
            "405",
            SnapshotMethodNotAllowedResponseReference
        );

        AssertOperationReferences(
            _changeQueries,
            "/availableChangeVersions",
            "get",
            UseSnapshotParameterReference
        );
        AssertResponseReferences(
            _changeQueries,
            "/availableChangeVersions",
            "get",
            "404",
            SnapshotNotFoundResponseReference
        );
    }

    [Test]
    public void It_serves_the_extension_operations_carrying_the_contract()
    {
        // TPDM reaches the served document through fragment merging rather than through the base
        // document, so its coverage is a separate question from core's.
        string[] extensionCollectionPaths =
        [
            .. Paths(_resources)
                .Where(path => path.StartsWith("/tpdm/", StringComparison.Ordinal))
                .Where(IsCollectionPath)
                .Order(StringComparer.Ordinal),
        ];

        extensionCollectionPaths
            .Should()
            .NotBeEmpty("the bundled TPDM extension must contribute collection paths to the served document");

        foreach (string collectionPath in extensionCollectionPaths)
        {
            AssertOperationReferences(_resources, collectionPath, "get", UseSnapshotParameterReference);
            AssertResponseReferences(
                _resources,
                collectionPath,
                "get",
                "404",
                SnapshotNotFoundResponseReference
            );
        }
    }

    [Test]
    public void It_injects_servers_and_security_alongside_the_snapshot_components()
    {
        foreach ((JsonNode document, string documentName) in AllDocuments())
        {
            document["servers"]
                .Should()
                .NotBeNull("the served {0} document carries a route-qualified server", documentName);
            Component(document, "securitySchemes", "oauth2_client_credentials")
                .Should()
                .NotBeNull("the served {0} document carries the OAuth2 security scheme", documentName);
            document["security"]
                .Should()
                .NotBeNull("the served {0} document carries a root security requirement", documentName);
        }

        _changeQueries["servers"]![0]!["url"]!
            .GetValue<string>()
            .Should()
            .EndWith("/changeQueries/v1", "the Change Queries document is served from its own route base");
    }

    [Test]
    public async Task It_serves_a_dangling_reference_when_a_published_response_goes_missing()
    {
        // The clean results above only mean something if this layer would have caught a loss. This is
        // the loss the snapshot design names as the residual hazard: nothing prunes or validates
        // components.responses, so a response that stops being published leaves every operation
        // referencing it pointing at nothing. The loss has to survive assembly, injection,
        // serialization, and the response pipeline to be visible here.
        ApiSchemaDocumentNodes nodes = LoadStagedApiSchemaNodes();
        ResourcesComponents(nodes, "responses").Remove(SnapshotNotFoundResponseName);

        await using WebApplicationFactory<Program> factory = CreateFactory(nodes);
        using HttpClient client = factory.CreateClient();

        JsonNode served = await GetDocumentAsync(client, ResourcesUrl);

        Component(served, "responses", SnapshotNotFoundResponseName).Should().BeNull();
        FindUnresolvedReferences(served)
            .Should()
            .NotBeEmpty(
                "every snapshot-eligible read still answers 404 with a response the document no longer defines"
            )
            .And.OnlyContain(reference =>
                reference.EndsWith(SnapshotNotFoundResponseReference, StringComparison.Ordinal)
            );
    }

    private static JsonObject ResourcesComponents(ApiSchemaDocumentNodes nodes, string section) =>
        nodes.CoreApiSchemaRootNode["projectSchema"]!["openApiBaseDocuments"]!["resources"]!["components"]![
            section
        ]!.AsObject();

    private static void AssertOperationReferences(
        JsonNode document,
        string pathKey,
        string method,
        string parameterReference
    )
    {
        JsonNode? operation = document["paths"]?[pathKey]?[method];

        operation
            .Should()
            .NotBeNull("the served document must serve {0} {1}", method.ToUpperInvariant(), pathKey);

        (operation!["parameters"] as JsonArray ?? [])
            .Select(parameter => parameter?["$ref"]?.GetValue<string>())
            .Should()
            .Contain(
                parameterReference,
                "{0} {1} must reference {2}",
                method.ToUpperInvariant(),
                pathKey,
                parameterReference
            );
    }

    private static void AssertResponseReferences(
        JsonNode document,
        string pathKey,
        string method,
        string statusCode,
        string responseReference
    )
    {
        JsonNode? operation = document["paths"]?[pathKey]?[method];

        operation
            .Should()
            .NotBeNull("the served document must serve {0} {1}", method.ToUpperInvariant(), pathKey);
        operation!
            ["responses"]
            ?[statusCode]?["$ref"]?.GetValue<string>()
            .Should()
            .Be(
                responseReference,
                "{0} {1} must answer {2} with {3}",
                method.ToUpperInvariant(),
                pathKey,
                statusCode,
                responseReference
            );
    }

    private IEnumerable<(JsonNode Document, string DocumentName)> AllDocuments()
    {
        yield return (_resources, "resources");
        yield return (_descriptors, "descriptors");
        yield return (_changeQueries, "Change Queries");
    }

    /// <summary>
    /// A collection path: no id segment, and none of the derived suffixes that hang off a collection.
    /// </summary>
    private static bool IsCollectionPath(string pathKey) =>
        !pathKey.Contains('{')
        && !pathKey.EndsWith("/deletes", StringComparison.Ordinal)
        && !pathKey.EndsWith("/keyChanges", StringComparison.Ordinal)
        && !pathKey.EndsWith("/partitions", StringComparison.Ordinal);

    private static IEnumerable<string> Paths(JsonNode document) =>
        document["paths"] is JsonObject paths ? paths.Select(path => path.Key) : [];

    private static JsonNode? Component(JsonNode document, string section, string name) =>
        document["components"]?[section]?[name];

    private static async Task<JsonNode> GetDocumentAsync(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET {0} must serve a document", url);

        string content = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(content)
            ?? throw new InvalidOperationException($"GET {url} returned null JSON.");
    }

    /// <summary>
    /// Loads the ApiSchema workspace the build stages beside this test binary, which is the bundled set
    /// the frontend ships.
    /// </summary>
    private static ApiSchemaDocumentNodes LoadStagedApiSchemaNodes() =>
        new(LoadStagedPackage(CorePackageId), [LoadStagedPackage(ExtensionPackageId)]);

    private static JsonNode LoadStagedPackage(string packageId)
    {
        string apiSchemaPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ApiSchema",
            "Packages",
            packageId,
            "ApiSchema.json"
        );

        if (!File.Exists(apiSchemaPath))
        {
            throw new FileNotFoundException(
                $"Staged ApiSchema not found for '{packageId}': {apiSchemaPath}. The build stages bundled "
                    + "ApiSchema packages into the test output.",
                apiSchemaPath
            );
        }

        return JsonNode.Parse(File.ReadAllText(apiSchemaPath))
            ?? throw new InvalidOperationException($"Staged ApiSchema parsed to null: {apiSchemaPath}");
    }

    private static WebApplicationFactory<Program> CreateFactory(ApiSchemaDocumentNodes apiSchemaNodes)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configurationBuilder) =>
                    configurationBuilder.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:AuthenticationService"] = AuthenticationService,
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                var apiSchemaProvider = A.Fake<IApiSchemaProvider>();
                A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(apiSchemaNodes);
                A.CallTo(() => apiSchemaProvider.SchemaLoadId).Returns(Guid.NewGuid());
                A.CallTo(() => apiSchemaProvider.IsSchemaValid).Returns(true);
                A.CallTo(() => apiSchemaProvider.ApiSchemaFailures).Returns(new List<ApiSchemaFailure>());
                services.Replace(ServiceDescriptor.Singleton(apiSchemaProvider));

                var profileService = A.Fake<IProfileService>();
                A.CallTo(() => profileService.GetProfileNamesAsync(A<string?>._))
                    .Returns(Task.FromResult<IReadOnlyList<string>>([]));
                services.Replace(ServiceDescriptor.Singleton(profileService));
            });
        });
    }

    /// <summary>
    /// Reports every reference in a served document that does not resolve inside that same document.
    /// </summary>
    /// <remarks>
    /// A deliberately local copy of the walk the Core fixtures use. Sharing one across the two test
    /// projects would mean a single broken implementation could make both sets of fixtures agree that
    /// nothing was wrong; the negative case below is what keeps this copy honest.
    /// </remarks>
    private static IReadOnlyList<string> FindUnresolvedReferences(JsonNode document)
    {
        List<string> unresolved = [];
        Walk(document, "$");
        return unresolved;

        void Walk(JsonNode? node, string location)
        {
            if (node is JsonArray array)
            {
                for (int index = 0; index < array.Count; index++)
                {
                    Walk(array[index], $"{location}[{index}]");
                }

                return;
            }

            if (node is not JsonObject jsonObject)
            {
                return;
            }

            foreach ((string key, JsonNode? value) in jsonObject)
            {
                // An "example" is response data rather than schema, so a "$ref"-shaped key inside one is
                // a value and not a reference.
                if (key == "example")
                {
                    continue;
                }

                if (key != "$ref")
                {
                    Walk(value, $"{location}.{key}");
                    continue;
                }

                string? reference =
                    value is JsonValue referenceValue && referenceValue.TryGetValue(out string? text)
                        ? text
                        : null;

                if (reference is null || !Resolves(document, reference))
                {
                    unresolved.Add($"{location} -> {reference ?? "non-string $ref"}");
                }
            }
        }
    }

    private static bool Resolves(JsonNode document, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        JsonNode? current = document;

        foreach (string segment in reference[2..].Split('/'))
        {
            string token = segment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (current is JsonObject currentObject)
            {
                if (!currentObject.TryGetPropertyValue(token, out current))
                {
                    return false;
                }

                continue;
            }

            if (
                current is JsonArray currentArray
                && int.TryParse(token, out int index)
                && index >= 0
                && index < currentArray.Count
            )
            {
                current = currentArray[index];
                continue;
            }

            return false;
        }

        return current is not null;
    }
}
