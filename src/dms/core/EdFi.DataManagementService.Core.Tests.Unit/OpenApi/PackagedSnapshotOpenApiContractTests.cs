// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiSnapshotContractAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Asserts the snapshot OpenAPI contract (DMS-1369) on the documents DMS actually serves, assembled
/// from the pinned ApiSchema packages through the production assembly and metadata-injection path.
/// </summary>
/// <remarks>
/// <para>
/// These are the bundled intake mode: the packages a build restores and stages. The file-based
/// <c>SCHEMA_PACKAGES</c> mode and the remaining package families are covered separately, because this
/// project can only reach the three families its own build brings in.
/// </para>
/// <para>
/// Everything asserted here is served rather than authored. The documents come out of
/// <c>ApiService</c>, so they have been through fragment merging, cursor-paging augmentation, and the
/// <c>servers</c> and OAuth2 injection, which is the last point at which a component could be dropped
/// or overwritten before a client sees it.
/// </para>
/// </remarks>
public abstract class PackagedSnapshotOpenApiContractTests
{
    private const string DataServerUrl = "https://example.org/data";
    private const string ChangeQueriesServerUrl = "https://example.org/changeQueries/v1";
    private const string ProblemDetailsSchemaReference = "#/components/schemas/ProblemDetails";

    /// <summary>
    /// Names this package configuration in failure messages.
    /// </summary>
    protected abstract string ConfigurationName { get; }

    /// <summary>
    /// The core and extension schemas this configuration serves.
    /// </summary>
    protected abstract ApiSchemaDocumentNodes LoadApiSchemaNodes();

    /// <summary>
    /// Path prefixes the served resource document must contain, so a configuration cannot pass by
    /// silently serving fewer projects than it claims.
    /// </summary>
    protected abstract IReadOnlyList<string> ExpectedPathPrefixes { get; }

    private JsonNode _resources = null!;
    private JsonNode _descriptors = null!;
    private JsonNode _changeQueries = null!;

    private JsonNode? _packagedResourceComponents;
    private JsonNode? _packagedDescriptorComponents;
    private JsonNode? _packagedChangeQueriesComponents;

    private IReadOnlyList<Operation> _resourceOperations = null!;
    private IReadOnlyList<Operation> _descriptorOperations = null!;
    private IReadOnlyList<Operation> _changeQueriesOperations = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        ApiSchemaDocumentNodes nodes = LoadApiSchemaNodes();

        // Read straight off the package before assembly, so the injection assertion compares what DMS
        // serves against what MetaEd published rather than against another DMS-produced document.
        _packagedResourceComponents = PackagedBaseDocumentComponents(nodes, "resources");
        _packagedDescriptorComponents = PackagedBaseDocumentComponents(nodes, "descriptors");
        _packagedChangeQueriesComponents = PackagedBaseDocumentComponents(nodes, "changeQueries");

        ApiService apiService = ApiServiceOpenApiTests.CreateApiService(nodes);

        _resources = apiService.GetResourceOpenApiSpecification(Servers(DataServerUrl));
        _descriptors = apiService.GetDescriptorOpenApiSpecification(Servers(DataServerUrl));
        _changeQueries =
            apiService.GetChangeQueriesOpenApiSpecification(Servers(ChangeQueriesServerUrl))
            ?? throw new InvalidOperationException(
                $"{ConfigurationName} served no standalone Change Queries document."
            );

        _resourceOperations = EnumerateOperations(_resources);
        _descriptorOperations = EnumerateOperations(_descriptors);
        _changeQueriesOperations = EnumerateOperations(_changeQueries);
    }

    [Test]
    public void It_serves_every_expected_project()
    {
        IReadOnlyList<string> pathKeys = [.. _resourceOperations.Select(operation => operation.PathKey)];

        foreach (string prefix in ExpectedPathPrefixes)
        {
            pathKeys
                .Should()
                .Contain(
                    pathKey => pathKey.StartsWith(prefix, StringComparison.Ordinal),
                    "{0} must serve paths under {1}, otherwise this configuration asserts the contract "
                        + "over fewer projects than it claims",
                    ConfigurationName,
                    prefix
                );
        }
    }

    [Test]
    public void It_declares_the_use_snapshot_parameter_in_every_served_document()
    {
        AssertUseSnapshotParameterShape(_resources, $"{ConfigurationName} resources");
        AssertUseSnapshotParameterShape(_descriptors, $"{ConfigurationName} descriptors");
        AssertUseSnapshotParameterShape(_changeQueries, $"{ConfigurationName} Change Queries");
    }

    [Test]
    public void It_serves_the_snapshot_parameter_on_every_snapshot_eligible_read()
    {
        AssertEverySnapshotEligibleRead(
            _resourceOperations,
            "resources",
            operation => operation.ReferencesUseSnapshotParameter,
            "the Use-Snapshot parameter"
        );
        AssertEverySnapshotEligibleRead(
            _descriptorOperations,
            "descriptors",
            operation => operation.ReferencesUseSnapshotParameter,
            "the Use-Snapshot parameter"
        );
    }

    [Test]
    public void It_serves_the_snapshot_not_found_response_on_every_snapshot_eligible_read()
    {
        AssertEverySnapshotEligibleRead(
            _resourceOperations,
            "resources",
            operation => operation.ReferencesSnapshotNotFound,
            "the Snapshot Not Found 404"
        );
        AssertEverySnapshotEligibleRead(
            _descriptorOperations,
            "descriptors",
            operation => operation.ReferencesSnapshotNotFound,
            "the Snapshot Not Found 404"
        );
    }

    [Test]
    public void It_advertises_the_header_on_get_many_consistently_with_get_by_id()
    {
        AssertGetManyMatchesGetById(_resourceOperations, "resources");
        AssertGetManyMatchesGetById(_descriptorOperations, "descriptors");
    }

    [Test]
    public void It_serves_the_snapshot_method_not_allowed_response_on_every_mutation()
    {
        AssertEveryMutation(_resourceOperations, "resources");
        AssertEveryMutation(_descriptorOperations, "descriptors");
    }

    [Test]
    public void It_declares_the_snapshot_responses_with_problem_json_and_the_allow_header()
    {
        foreach ((JsonNode document, string documentName) in DocumentsDeclaringBothResponses())
        {
            JsonNode? notFound = Component(document, "responses", SnapshotNotFoundResponseName);
            ProblemJsonSchemaReferenceOf(notFound)
                .Should()
                .Be(
                    ProblemDetailsSchemaReference,
                    "the {0} {1} response must carry the shared ProblemDetails envelope as {2}",
                    documentName,
                    SnapshotNotFoundResponseName,
                    ProblemJsonContentType
                );

            JsonNode? methodNotAllowed = Component(
                document,
                "responses",
                SnapshotMethodNotAllowedResponseName
            );
            ProblemJsonSchemaReferenceOf(methodNotAllowed)
                .Should()
                .Be(
                    ProblemDetailsSchemaReference,
                    "the {0} {1} response must carry the shared ProblemDetails envelope as {2}",
                    documentName,
                    SnapshotMethodNotAllowedResponseName,
                    ProblemJsonContentType
                );

            TextAt(methodNotAllowed?["headers"]?["Allow"]?["example"])
                .Should()
                .Be(
                    "GET",
                    "the {0} snapshot 405 must declare the Allow header, and GET is what is permitted "
                        + "against a read-only snapshot target",
                    documentName
                );
        }
    }

    [Test]
    public void It_serves_a_self_contained_change_queries_document()
    {
        Component(_changeQueries, "parameters", UseSnapshotParameterName)
            .Should()
            .NotBeNull(
                "the standalone Change Queries document is served independently, so it cannot resolve "
                    + "the {0} parameter out of the {1} resources document",
                UseSnapshotParameterName,
                ConfigurationName
            );
        Component(_changeQueries, "responses", SnapshotNotFoundResponseName).Should().NotBeNull();
        Component(_changeQueries, "schemas", ProblemDetailsSchemaName).Should().NotBeNull();

        Operation availableChangeVersions = _changeQueriesOperations
            .Should()
            .ContainSingle(operation =>
                operation.PathKind == OperationPathKind.AvailableChangeVersions && operation.Method == "get"
            )
            .Subject;

        availableChangeVersions.ReferencesUseSnapshotParameter.Should().BeTrue();
        availableChangeVersions.ReferencesSnapshotNotFound.Should().BeTrue();
    }

    [Test]
    public void It_serves_documents_that_resolve_every_reference_within_themselves()
    {
        AssertSelfResolves(_resources, $"{ConfigurationName} resources");
        AssertSelfResolves(_descriptors, $"{ConfigurationName} descriptors");
        AssertSelfResolves(_changeQueries, $"{ConfigurationName} Change Queries");
    }

    [Test]
    public void It_would_report_the_snapshot_parameter_going_missing_from_a_served_document()
    {
        // The self-resolution assertion above passes, and on a document this size that is only
        // meaningful if the check would have caught a loss. Dropping the shared parameter from a copy
        // of the real served document is the failure this story exists to prevent, so the check has to
        // find it - once for every operation that referenced it.
        JsonNode probe = _resources.DeepClone();
        probe["components"]!["parameters"]!.AsObject().Remove(UseSnapshotParameterName);

        IReadOnlyList<UnresolvedReference> unresolved = FindUnresolvedReferences(probe);

        unresolved
            .Should()
            .NotBeEmpty(
                "removing {0} from the served {1} resources document must be detected",
                UseSnapshotParameterName,
                ConfigurationName
            );
        unresolved
            .Should()
            .OnlyContain(reference => reference.Reference == UseSnapshotParameterReference)
            .And.HaveCount(
                _resourceOperations.Count(operation => operation.ReferencesUseSnapshotParameter),
                "every operation that referenced the parameter must be reported once"
            );
    }

    [Test]
    public void It_preserves_the_published_snapshot_components_through_endpoint_metadata_injection()
    {
        AssertComponentsMatchPackage(_resources, _packagedResourceComponents, "resources", true);
        AssertComponentsMatchPackage(_descriptors, _packagedDescriptorComponents, "descriptors", true);
        AssertComponentsMatchPackage(
            _changeQueries,
            _packagedChangeQueriesComponents,
            "Change Queries",
            false
        );
    }

    [Test]
    public void It_injects_servers_and_security_alongside_the_snapshot_components()
    {
        foreach ((JsonNode document, string documentName, string serverUrl) in AllDocumentsWithServerUrl())
        {
            TextAt(document["servers"]?[0]?["url"])
                .Should()
                .Be(serverUrl, "the {0} document is served with a route-qualified server", documentName);
            Component(document, "securitySchemes", "oauth2_client_credentials")
                .Should()
                .NotBeNull("the {0} document is served with the OAuth2 security scheme", documentName);
            document["security"]
                .Should()
                .NotBeNull("the {0} document is served with a root security requirement", documentName);
        }
    }

    [Test]
    public void It_adds_no_read_replica_parameter_or_response()
    {
        // Read-replica selection is automatic and carries no request or response surface, so nothing in
        // a served document may mention it.
        foreach ((JsonNode document, string documentName) in AllDocuments())
        {
            ComponentNames(document, "parameters")
                .Concat(ComponentNames(document, "responses"))
                .Where(name => name.Contains("replica", StringComparison.OrdinalIgnoreCase))
                .Should()
                .BeEmpty("read-replica routing adds no OpenAPI surface to the {0} document", documentName);
        }
    }

    private void AssertEverySnapshotEligibleRead(
        IReadOnlyList<Operation> operations,
        string documentName,
        Func<Operation, bool> predicate,
        string requirement
    )
    {
        IReadOnlyList<Operation> eligibleReads = [.. operations.Where(IsSnapshotEligibleRead)];

        eligibleReads
            .Should()
            .NotBeEmpty(
                "the {0} {1} document must serve snapshot-eligible reads",
                ConfigurationName,
                documentName
            );

        IReadOnlyList<Operation> missing = [.. eligibleReads.Where(operation => !predicate(operation))];

        missing
            .Should()
            .BeEmpty(
                "every snapshot-eligible read in the {0} {1} document must serve {2}; missing on {3}",
                ConfigurationName,
                documentName,
                requirement,
                Describe(missing)
            );
    }

    private void AssertEveryMutation(IReadOnlyList<Operation> operations, string documentName)
    {
        IReadOnlyList<Operation> mutations = [.. operations.Where(IsMutation)];

        mutations
            .Should()
            .NotBeEmpty("the {0} {1} document must serve mutations", ConfigurationName, documentName);

        IReadOnlyList<Operation> missing =
        [
            .. mutations.Where(operation => !operation.ReferencesSnapshotMethodNotAllowed),
        ];

        missing
            .Should()
            .BeEmpty(
                "every POST, PUT, and DELETE in the {0} {1} document must serve the snapshot 405; "
                    + "missing on {2}",
                ConfigurationName,
                documentName,
                Describe(missing)
            );
    }

    /// <summary>
    /// Pairs each GET-by-id with the GET-many of its own collection and requires the two to agree.
    /// Counting them separately would pass while a resource advertised the header on one and not the
    /// other, which is exactly the older ODS-derived shape this contract diverges from.
    /// </summary>
    private void AssertGetManyMatchesGetById(IReadOnlyList<Operation> operations, string documentName)
    {
        Dictionary<string, Operation> collectionGetsByPath = operations
            .Where(operation =>
                operation.PathKind == OperationPathKind.Collection && operation.Method == "get"
            )
            .ToDictionary(operation => operation.PathKey, StringComparer.Ordinal);

        IReadOnlyList<Operation> itemGets =
        [
            .. operations.Where(operation =>
                operation.PathKind == OperationPathKind.Item && operation.Method == "get"
            ),
        ];

        itemGets
            .Should()
            .NotBeEmpty(
                "the {0} {1} document must serve GET-by-id operations",
                ConfigurationName,
                documentName
            );

        List<string> inconsistent = [];

        foreach (Operation itemGet in itemGets)
        {
            string collectionPath = itemGet.PathKey[..itemGet.PathKey.LastIndexOf('/')];

            if (!collectionGetsByPath.TryGetValue(collectionPath, out Operation? collectionGet))
            {
                inconsistent.Add($"{itemGet} has no GET-many at {collectionPath}");
                continue;
            }

            if (collectionGet.ReferencesUseSnapshotParameter != itemGet.ReferencesUseSnapshotParameter)
            {
                inconsistent.Add(
                    $"{collectionPath} advertises the header on GET-many="
                        + $"{collectionGet.ReferencesUseSnapshotParameter} but on GET-by-id="
                        + $"{itemGet.ReferencesUseSnapshotParameter}"
                );
            }
        }

        inconsistent
            .Should()
            .BeEmpty(
                "GET-many must advertise the header consistently with GET-by-id in the {0} {1} document; "
                    + "{2}",
                ConfigurationName,
                documentName,
                string.Join("; ", inconsistent.Take(10))
            );
    }

    /// <summary>
    /// Compares the served snapshot components against the ones the package published. DMS injects
    /// <c>servers</c>, the OAuth2 security scheme, and the root security requirement into the same
    /// document; this is what proves that injection neither drops nor rewrites what it found.
    /// </summary>
    private void AssertComponentsMatchPackage(
        JsonNode served,
        JsonNode? packagedComponents,
        string documentName,
        bool expectMethodNotAllowed
    )
    {
        packagedComponents
            .Should()
            .NotBeNull(
                "the {0} {1} package document must publish components",
                ConfigurationName,
                documentName
            );

        List<(string Section, string Name)> expected =
        [
            ("parameters", UseSnapshotParameterName),
            ("responses", SnapshotNotFoundResponseName),
            ("schemas", ProblemDetailsSchemaName),
        ];

        if (expectMethodNotAllowed)
        {
            expected.Add(("responses", SnapshotMethodNotAllowedResponseName));
        }

        foreach ((string section, string name) in expected)
        {
            string? publishedJson = packagedComponents![section]?[name]?.ToJsonString();
            string? servedJson = Component(served, section, name)?.ToJsonString();

            publishedJson
                .Should()
                .NotBeNull(
                    "the {0} {1} package document must publish components.{2}.{3}",
                    ConfigurationName,
                    documentName,
                    section,
                    name
                );
            servedJson
                .Should()
                .Be(
                    publishedJson,
                    "the served {0} {1} document must carry components.{2}.{3} exactly as published, "
                        + "so nothing DMS adds drops or overwrites it",
                    ConfigurationName,
                    documentName,
                    section,
                    name
                );
        }
    }

    private static JsonNode? PackagedBaseDocumentComponents(
        ApiSchemaDocumentNodes nodes,
        string baseDocumentKey
    ) =>
        nodes
            .CoreApiSchemaRootNode["projectSchema"]
            ?["openApiBaseDocuments"]
            ?[baseDocumentKey]
            ?["components"];

    private static bool IsSnapshotEligibleRead(Operation operation) =>
        operation.Method == "get"
        && operation.PathKind
            is OperationPathKind.Collection
                or OperationPathKind.Item
                or OperationPathKind.Deletes
                or OperationPathKind.KeyChanges;

    private static bool IsMutation(Operation operation) => operation.Method is "post" or "put" or "delete";

    private static IReadOnlyList<string> ComponentNames(JsonNode document, string section) =>
        document["components"]?[section] is JsonObject entries ? [.. entries.Select(pair => pair.Key)] : [];

    private static string? ProblemJsonSchemaReferenceOf(JsonNode? response) =>
        TextAt(response?["content"]?[ProblemJsonContentType]?["schema"]?["$ref"]);

    private static string? TextAt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string Describe(IReadOnlyList<Operation> operations)
    {
        if (operations.Count == 0)
        {
            return "none";
        }

        const int MaxReported = 10;
        string described = string.Join(", ", operations.Take(MaxReported));

        return operations.Count > MaxReported
            ? $"{described} and {operations.Count - MaxReported} more"
            : described;
    }

    private static JsonArray Servers(string url) => [new JsonObject { ["url"] = url }];

    private IEnumerable<(JsonNode Document, string DocumentName)> AllDocuments()
    {
        yield return (_resources, "resources");
        yield return (_descriptors, "descriptors");
        yield return (_changeQueries, "Change Queries");
    }

    private IEnumerable<(JsonNode Document, string DocumentName)> DocumentsDeclaringBothResponses()
    {
        yield return (_resources, "resources");
        yield return (_descriptors, "descriptors");
    }

    private IEnumerable<(
        JsonNode Document,
        string DocumentName,
        string ServerUrl
    )> AllDocumentsWithServerUrl()
    {
        yield return (_resources, "resources", DataServerUrl);
        yield return (_descriptors, "descriptors", DataServerUrl);
        yield return (_changeQueries, "Change Queries", ChangeQueriesServerUrl);
    }
}

/// <summary>
/// The served documents of the pinned Data Standard 5.2 core package.
/// </summary>
[TestFixture]
public class Given_the_served_DataStandard52_documents : PackagedSnapshotOpenApiContractTests
{
    /// <inheritdoc />
    protected override string ConfigurationName => "Data Standard 5.2";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExpectedPathPrefixes => ["/ed-fi/"];

    /// <inheritdoc />
    protected override ApiSchemaDocumentNodes LoadApiSchemaNodes() =>
        new(PackagedApiSchemaContract.LoadPackagedRootNode("DataStandard52ApiSchemaPackageRoot"), []);
}

/// <summary>
/// The served documents of the pinned Data Standard 5.2 core package with the TPDM extension merged in,
/// which is the bundled set the frontend ships. Extension-defined resources reach the served document
/// through fragment merging rather than through the base document, so their snapshot coverage is a
/// separate question from core's.
/// </summary>
[TestFixture]
public class Given_the_served_DataStandard52_documents_with_TPDM : PackagedSnapshotOpenApiContractTests
{
    /// <inheritdoc />
    protected override string ConfigurationName => "Data Standard 5.2 with TPDM";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExpectedPathPrefixes => ["/ed-fi/", "/tpdm/"];

    /// <inheritdoc />
    protected override ApiSchemaDocumentNodes LoadApiSchemaNodes() =>
        new(
            PackagedApiSchemaContract.LoadPackagedRootNode("DataStandard52ApiSchemaPackageRoot"),
            [LoadStagedPackageRootNode("EdFi.DataStandard52.TPDM.ApiSchema")]
        );

    /// <summary>
    /// Reads an extension package out of the ApiSchema workspace the build stages beside the test
    /// binary. The extension packages carry no direct reference here, so there is no restored package
    /// root to read; the staged workspace is what a bundled-mode runtime loads anyway.
    /// </summary>
    private static JsonNode LoadStagedPackageRootNode(string packageId)
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
}

/// <summary>
/// The served documents of the pinned Data Standard 6.1 core package. Data Standard 6.1 folds TPDM into
/// core, so this is a materially larger served surface than 5.2 and is reached only through the
/// file-based intake path in a deployment.
/// </summary>
[TestFixture]
public class Given_the_served_DataStandard61_documents : PackagedSnapshotOpenApiContractTests
{
    /// <inheritdoc />
    protected override string ConfigurationName => "Data Standard 6.1";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExpectedPathPrefixes => ["/ed-fi/"];

    /// <inheritdoc />
    protected override ApiSchemaDocumentNodes LoadApiSchemaNodes() =>
        new(PackagedApiSchemaContract.LoadPackagedRootNode("DataStandard61ApiSchemaPackageRoot"), []);
}
