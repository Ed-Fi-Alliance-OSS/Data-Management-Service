// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.ApiSchemaDownloader.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Tests.Unit;

/// <summary>
/// Proves every published ApiSchema package carries the snapshot OpenAPI contract (DMS-1369), by
/// downloading each one from the feed the way a deployment does and reading the payload it delivers.
/// </summary>
/// <remarks>
/// <para>
/// This is the file-based intake mode. <c>SCHEMA_PACKAGES</c> names packages that are downloaded and
/// staged at deployment time, and five of the seven supported families reach a running DMS only that
/// way: they carry no direct package reference, so no build restores them and no unit test in the
/// Core project can see them. Coverage limited to the bundled families would pass while the majority
/// of served documents lacked the contract.
/// </para>
/// <para>
/// Responsibilities are split deliberately. This asserts what the package payloads contain; the Core
/// fixtures assert what DMS assembles and serves from them. So this reads the payload with
/// System.Text.Json alone and takes no dependency on the Core assembly, which also keeps the CLI test
/// project's reference graph as it was.
/// </para>
/// </remarks>
[TestFixture]
public class SnapshotOpenApiPackagePayloadTests
{
    private const string FeedUrl =
        "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

    /// <summary>
    /// The pinned version, which every active version-selection surface in this repository selects.
    /// </summary>
    private const string SnapshotContractVersion = "1.0.335";

    /// <summary>
    /// The last version published before the upstream snapshot work (DMS-1371). Used to prove these
    /// checks detect the gap rather than passing over whatever they are handed.
    /// </summary>
    private const string PreSnapshotContractVersion = "1.0.333";

    private const string UseSnapshotParameterName = "Use-Snapshot";
    private const string SnapshotNotFoundResponseName = "SnapshotNotFound";
    private const string SnapshotMethodNotAllowedResponseName = "SnapshotMethodNotAllowed";
    private const string ProblemDetailsSchemaName = "ProblemDetails";

    private const string UseSnapshotParameterReference = "#/components/parameters/Use-Snapshot";
    private const string SnapshotNotFoundResponseReference = "#/components/responses/SnapshotNotFound";
    private const string SnapshotMethodNotAllowedResponseReference =
        "#/components/responses/SnapshotMethodNotAllowed";

    private const string ResourcesDocument = "resources";
    private const string DescriptorsDocument = "descriptors";
    private const string ChangeQueriesDocument = "changeQueries";

    /// <summary>
    /// The standalone Change Queries route, matched exactly rather than by suffix so the assertion
    /// binds to this operation and cannot be satisfied by a different one in the same document.
    /// </summary>
    private const string AvailableChangeVersionsPath = "/availableChangeVersions";

    private static readonly string[] _httpMethodNames = ["get", "put", "post", "delete", "patch"];

    /// <summary>
    /// Every supported family. Data Standard 6.1 folds TPDM into core, so it has no TPDM package and
    /// none is expected.
    /// </summary>
    private static readonly PackageFamily[] _families =
    [
        new("EdFi.DataStandard52.ApiSchema", "5.2", true),
        new("EdFi.DataStandard52.TPDM.ApiSchema", "5.2", false),
        new("EdFi.DataStandard52.Sample.ApiSchema", "5.2", false),
        new("EdFi.DataStandard52.Homograph.ApiSchema", "5.2", false),
        new("EdFi.DataStandard61.ApiSchema", "6.1", true),
        new("EdFi.DataStandard61.Sample.ApiSchema", "6.1", false),
        new("EdFi.DataStandard61.Homograph.ApiSchema", "6.1", false),
    ];

    private static string _workspace = null!;
    private static Dictionary<string, PackagePayload> _payloads = null!;
    private static PackagePayload _preSnapshotCorePayload = null!;

    public static IEnumerable<string> AllPackageIds => _families.Select(family => family.PackageId);

    public static IEnumerable<string> CorePackageIds =>
        _families.Where(family => family.IsCore).Select(family => family.PackageId);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"dms1369-payloads-{Guid.NewGuid()}");
        Directory.CreateDirectory(_workspace);

        IApiSchemaDownloader downloader = new Services.ApiSchemaDownloader(
            A.Fake<ILogger<Services.ApiSchemaDownloader>>()
        );

        // Cores first: an extension package publishes no base documents, so the components its
        // fragments reference can only be resolved against the core of its own data standard.
        Dictionary<string, IReadOnlyDictionary<string, JsonNode>> coreComponentsByDataStandard = new(
            StringComparer.Ordinal
        );

        _payloads = new Dictionary<string, PackagePayload>(StringComparer.OrdinalIgnoreCase);

        foreach (PackageFamily family in _families.Where(family => family.IsCore))
        {
            PackagePayload payload = await AnalyzeAsync(downloader, family, SnapshotContractVersion);
            _payloads[family.PackageId] = payload;
            coreComponentsByDataStandard[family.DataStandard] = payload.BaseDocumentComponents;
        }

        foreach (PackageFamily family in _families.Where(family => !family.IsCore))
        {
            _payloads[family.PackageId] = await AnalyzeAsync(
                downloader,
                family,
                SnapshotContractVersion,
                coreComponentsByDataStandard[family.DataStandard]
            );
        }

        _preSnapshotCorePayload = await AnalyzeAsync(downloader, _families[0], PreSnapshotContractVersion);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, true);
        }
    }

    [Test]
    [TestCaseSource(nameof(CorePackageIds))]
    public void It_publishes_the_snapshot_components_in_every_independently_served_base_document(
        string packageId
    )
    {
        PackagePayload payload = _payloads[packageId];

        foreach (
            string documentType in new[] { ResourcesDocument, DescriptorsDocument, ChangeQueriesDocument }
        )
        {
            JsonNode components = payload
                .BaseDocumentComponents.Should()
                .ContainKey(documentType, "{0} must publish the {1} base document", packageId, documentType)
                .WhoseValue;

            ComponentOf(components, "parameters", UseSnapshotParameterName)
                .Should()
                .NotBeNull(
                    "{0}'s {1} document is served independently, so it must define {2} rather than "
                        + "resolve it out of a sibling document",
                    packageId,
                    documentType,
                    UseSnapshotParameterName
                );
            ComponentOf(components, "responses", SnapshotNotFoundResponseName).Should().NotBeNull();
            ComponentOf(components, "schemas", ProblemDetailsSchemaName).Should().NotBeNull();

            // Only the documents that serve mutations declare the snapshot 405. The standalone Change
            // Queries document has a single read operation, so requiring it there would demand a
            // component nothing references.
            JsonNode? methodNotAllowed = ComponentOf(
                components,
                "responses",
                SnapshotMethodNotAllowedResponseName
            );

            if (documentType == ChangeQueriesDocument)
            {
                methodNotAllowed
                    .Should()
                    .BeNull("{0}'s {1} document serves no mutation", packageId, documentType);
            }
            else
            {
                methodNotAllowed
                    .Should()
                    .NotBeNull(
                        "{0}'s {1} document serves mutations, so it must declare the snapshot 405",
                        packageId,
                        documentType
                    );
            }
        }
    }

    [Test]
    [TestCaseSource(nameof(CorePackageIds))]
    public void It_publishes_the_snapshot_parameter_as_a_header_boolean_defaulting_to_false(string packageId)
    {
        PackagePayload payload = _payloads[packageId];

        foreach ((string documentType, JsonNode components) in payload.BaseDocumentComponents)
        {
            JsonNode? parameter = ComponentOf(components, "parameters", UseSnapshotParameterName);

            TextAt(parameter?["name"]).Should().Be(UseSnapshotParameterName);
            TextAt(parameter?["in"])
                .Should()
                .Be("header", "{0}'s {1} document must advertise it as a header", packageId, documentType);
            TextAt(parameter?["schema"]?["type"]).Should().Be("boolean");
            BooleanAt(parameter?["schema"]?["default"])
                .Should()
                .BeFalse(
                    "{0}'s {1} document must default the header to false so an unaware client is "
                        + "unaffected",
                    packageId,
                    documentType
                );
        }
    }

    [Test]
    [TestCaseSource(nameof(CorePackageIds))]
    public void It_publishes_base_documents_whose_references_resolve_within_themselves(string packageId)
    {
        _payloads[packageId]
            .UnresolvedBaseDocumentReferences.Should()
            .BeEmpty(
                "every reference in {0}'s independently served base documents must resolve inside the "
                    + "document that carries it",
                packageId
            );
    }

    [Test]
    [TestCaseSource(nameof(CorePackageIds))]
    public void It_publishes_an_available_change_versions_operation_carrying_the_contract(string packageId)
    {
        PackagePayload payload = _payloads[packageId];

        FragmentOperation operation = payload
            .AvailableChangeVersionsOperations.Should()
            .ContainSingle(
                "{0}'s Change Queries document must serve exactly one GET {1}",
                packageId,
                AvailableChangeVersionsPath
            )
            .Subject;

        operation
            .ReferencesParameter.Should()
            .BeTrue(
                "GET {0} must serve {1} in {2}",
                AvailableChangeVersionsPath,
                UseSnapshotParameterName,
                packageId
            );
        operation
            .ReferencesSnapshotNotFound.Should()
            .BeTrue(
                "GET {0} must serve the Snapshot Not Found 404 in {1}",
                AvailableChangeVersionsPath,
                packageId
            );
    }

    [Test]
    [TestCaseSource(nameof(AllPackageIds))]
    public void It_carries_the_snapshot_parameter_on_every_snapshot_eligible_read(string packageId)
    {
        PackagePayload payload = _payloads[packageId];

        payload
            .SnapshotEligibleReadCount.Should()
            .BeGreaterThan(0, "{0} must contribute snapshot-eligible reads", packageId);
        payload
            .ReadsMissingParameter.Should()
            .BeEmpty(
                "every GET-many, GET-by-id, /deletes, and /keyChanges operation {0} contributes must "
                    + "carry {1}; missing on {2}",
                packageId,
                UseSnapshotParameterName,
                Describe(payload.ReadsMissingParameter)
            );
    }

    [Test]
    [TestCaseSource(nameof(AllPackageIds))]
    public void It_carries_the_snapshot_not_found_response_on_every_snapshot_eligible_read(string packageId)
    {
        PackagePayload payload = _payloads[packageId];

        payload
            .ReadsMissingSnapshotNotFound.Should()
            .BeEmpty(
                "every snapshot-eligible read {0} contributes must carry the Snapshot Not Found 404; "
                    + "missing on {1}",
                packageId,
                Describe(payload.ReadsMissingSnapshotNotFound)
            );
    }

    [Test]
    [TestCaseSource(nameof(AllPackageIds))]
    public void It_carries_the_snapshot_method_not_allowed_response_on_every_mutation(string packageId)
    {
        PackagePayload payload = _payloads[packageId];

        payload.MutationCount.Should().BeGreaterThan(0, "{0} must contribute mutations", packageId);
        payload
            .MutationsMissingSnapshotMethodNotAllowed.Should()
            .BeEmpty(
                "every POST, PUT, and DELETE {0} contributes must carry the snapshot 405; missing on {1}",
                packageId,
                Describe(payload.MutationsMissingSnapshotMethodNotAllowed)
            );
    }

    [Test]
    [TestCaseSource(nameof(AllPackageIds))]
    public void It_names_only_snapshot_components_its_own_data_standard_core_publishes(string packageId)
    {
        PackagePayload payload = _payloads[packageId];

        payload
            .SnapshotReferencesUsed.Should()
            .NotBeEmpty("{0} must reference the snapshot contract", packageId);
        payload
            .SnapshotReferencesMissingFromCore.Should()
            .BeEmpty(
                "every snapshot component {0}'s fragments name must be published by its own data "
                    + "standard's core base document, because a served document cannot resolve one out "
                    + "of a sibling; unresolved: {1}",
                packageId,
                string.Join(", ", payload.SnapshotReferencesMissingFromCore)
            );
    }

    [Test]
    public void It_finds_no_snapshot_contract_in_the_package_published_before_the_upstream_change()
    {
        // The proof that everything above looks. Against 1.0.333 the same reads find no parameter in
        // any base document and no coverage on any operation, so these checks fail on a package that
        // lacks the contract rather than passing over whatever they are handed.
        _preSnapshotCorePayload
            .BaseDocumentComponents.Values.Select(components =>
                ComponentOf(components, "parameters", UseSnapshotParameterName)
            )
            .Should()
            .AllSatisfy(parameter =>
                parameter
                    .Should()
                    .BeNull($"{PreSnapshotContractVersion} predates the upstream snapshot contract")
            );

        _preSnapshotCorePayload
            .SnapshotEligibleReadCount.Should()
            .BeGreaterThan(0, "the pre-bump package still serves the same reads");
        _preSnapshotCorePayload
            .ReadsMissingParameter.Should()
            .HaveCount(
                _preSnapshotCorePayload.SnapshotEligibleReadCount,
                "no read in the pre-bump package carries the parameter, so the coverage check must "
                    + "report every one of them"
            );
        _preSnapshotCorePayload
            .MutationsMissingSnapshotMethodNotAllowed.Should()
            .HaveCount(
                _preSnapshotCorePayload.MutationCount,
                "no mutation in the pre-bump package carries the snapshot 405"
            );
        _preSnapshotCorePayload.SnapshotReferencesUsed.Should().BeEmpty();
    }

    private static async Task<PackagePayload> AnalyzeAsync(
        IApiSchemaDownloader downloader,
        PackageFamily family,
        string version,
        IReadOnlyDictionary<string, JsonNode>? coreComponents = null
    )
    {
        string packageWorkspace = Path.Combine(_workspace, $"{family.PackageId}.{version}");
        Directory.CreateDirectory(packageWorkspace);

        string packagePath = await downloader.DownloadNuGetPackageAsync(
            family.PackageId,
            version,
            FeedUrl,
            packageWorkspace
        );
        downloader.ExtractApiSchemaFiles(family.PackageId, packagePath, packageWorkspace);

        string apiSchemaPath = Path.Combine(packageWorkspace, "Packages", family.PackageId, "ApiSchema.json");
        JsonNode root =
            JsonNode.Parse(await File.ReadAllTextAsync(apiSchemaPath))
            ?? throw new InvalidOperationException($"{family.PackageId} ApiSchema.json parsed to null.");

        PackagePayload payload = Analyze(root, coreComponents);

        // The parsed document is the largest thing here and nothing below needs it, so let it go
        // before the next package is downloaded rather than holding all seven at once.
        File.Delete(apiSchemaPath);

        return payload;
    }

    private static PackagePayload Analyze(
        JsonNode root,
        IReadOnlyDictionary<string, JsonNode>? coreComponents
    )
    {
        Dictionary<string, JsonNode> baseDocumentComponents = new(StringComparer.Ordinal);
        List<string> unresolvedBaseDocumentReferences = [];
        List<FragmentOperation> availableChangeVersionsOperations = [];

        if (root["projectSchema"]?["openApiBaseDocuments"] is JsonObject baseDocuments)
        {
            foreach ((string documentType, JsonNode? baseDocument) in baseDocuments)
            {
                if (baseDocument is null)
                {
                    continue;
                }

                if (baseDocument["components"] is JsonNode components)
                {
                    baseDocumentComponents[documentType] = components;
                }

                unresolvedBaseDocumentReferences.AddRange(
                    FindUnresolvedReferences(baseDocument, documentType)
                );

                if (documentType != ChangeQueriesDocument)
                {
                    continue;
                }

                availableChangeVersionsOperations.AddRange(
                    OperationsOf(baseDocument, documentType)
                        .Where(operation =>
                            operation.PathKey.Equals(
                                AvailableChangeVersionsPath,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && operation.Method == "get"
                        )
                );
            }
        }

        List<FragmentOperation> operations = [.. EnumerateFragmentOperations(root)];
        List<FragmentOperation> reads = [.. operations.Where(operation => operation.IsSnapshotEligibleRead)];
        List<FragmentOperation> mutations = [.. operations.Where(operation => operation.IsMutation)];

        // Extension fragments resolve against their core; a core resolves against itself.
        IReadOnlyDictionary<string, JsonNode> resolutionComponents = coreComponents ?? baseDocumentComponents;

        HashSet<(string DocumentType, string Reference)> snapshotReferencesUsed = [];

        foreach (FragmentOperation operation in operations)
        {
            foreach (string reference in operation.SnapshotReferences)
            {
                snapshotReferencesUsed.Add((operation.DocumentType, reference));
            }
        }

        List<string> missingFromCore =
        [
            .. snapshotReferencesUsed
                .Where(used => !ResolvesInComponents(resolutionComponents, used.DocumentType, used.Reference))
                .Select(used => $"{used.DocumentType}: {used.Reference}")
                .Order(StringComparer.Ordinal),
        ];

        return new PackagePayload(
            baseDocumentComponents,
            unresolvedBaseDocumentReferences,
            availableChangeVersionsOperations,
            reads.Count,
            [
                .. reads
                    .Where(operation => !operation.ReferencesParameter)
                    .Select(operation => operation.ToString()),
            ],
            [
                .. reads
                    .Where(operation => !operation.ReferencesSnapshotNotFound)
                    .Select(operation => operation.ToString()),
            ],
            mutations.Count,
            [
                .. mutations
                    .Where(operation => !operation.ReferencesSnapshotMethodNotAllowed)
                    .Select(operation => operation.ToString()),
            ],
            [
                .. snapshotReferencesUsed
                    .Select(used => $"{used.DocumentType}: {used.Reference}")
                    .Order(StringComparer.Ordinal),
            ],
            missingFromCore
        );
    }

    private static bool ResolvesInComponents(
        IReadOnlyDictionary<string, JsonNode> componentsByDocumentType,
        string documentType,
        string reference
    )
    {
        if (!componentsByDocumentType.TryGetValue(documentType, out JsonNode? components))
        {
            return false;
        }

        string[] segments = reference.Split('/');

        return segments.Length == 4 && ComponentOf(components, segments[2], segments[3]) is not null;
    }

    private static IEnumerable<FragmentOperation> EnumerateFragmentOperations(JsonNode root)
    {
        if (root["projectSchema"]?["resourceSchemas"] is not JsonObject resourceSchemas)
        {
            yield break;
        }

        foreach ((string _, JsonNode? resourceSchema) in resourceSchemas)
        {
            if (resourceSchema?["openApiFragments"] is not JsonObject fragments)
            {
                continue;
            }

            foreach ((string documentType, JsonNode? fragment) in fragments)
            {
                if (fragment is null)
                {
                    continue;
                }

                foreach (FragmentOperation operation in OperationsOf(fragment, documentType))
                {
                    yield return operation;
                }
            }
        }
    }

    private static IEnumerable<FragmentOperation> OperationsOf(JsonNode fragment, string documentType)
    {
        if (fragment["paths"] is not JsonObject paths)
        {
            yield break;
        }

        foreach ((string pathKey, JsonNode? pathItem) in paths)
        {
            if (pathItem is not JsonObject pathObject)
            {
                continue;
            }

            foreach ((string method, JsonNode? operation) in pathObject)
            {
                if (
                    !_httpMethodNames.Contains(method, StringComparer.Ordinal)
                    || operation is not JsonObject operationObject
                )
                {
                    continue;
                }

                yield return new FragmentOperation(
                    documentType,
                    pathKey,
                    method,
                    ParameterReferencesOf(operationObject),
                    ResponseReferencesOf(operationObject)
                );
            }
        }
    }

    private static IReadOnlyList<string> ParameterReferencesOf(JsonObject operation) =>
        operation["parameters"] is not JsonArray parameters
            ? []
            :
            [
                .. parameters
                    .OfType<JsonObject>()
                    .Select(parameter => TextAt(parameter["$ref"]))
                    .Where(reference => reference is not null)
                    .Select(reference => reference!),
            ];

    private static IReadOnlyDictionary<string, string> ResponseReferencesOf(JsonObject operation)
    {
        Dictionary<string, string> references = new(StringComparer.Ordinal);

        if (operation["responses"] is not JsonObject responses)
        {
            return references;
        }

        foreach ((string statusCode, JsonNode? response) in responses)
        {
            if (response is JsonObject responseObject && TextAt(responseObject["$ref"]) is string reference)
            {
                references[statusCode] = reference;
            }
        }

        return references;
    }

    /// <summary>
    /// Resolves every local reference in a base document against that same document. The base documents
    /// are small - the resource and descriptor documents carry components and no paths - so this walks
    /// them whole.
    /// </summary>
    private static IEnumerable<string> FindUnresolvedReferences(JsonNode document, string documentType)
    {
        List<string> unresolved = [];
        Walk(document);
        return unresolved;

        void Walk(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    Walk(item);
                }

                return;
            }

            if (node is not JsonObject jsonObject)
            {
                return;
            }

            foreach ((string key, JsonNode? value) in jsonObject)
            {
                if (key == "example")
                {
                    continue;
                }

                if (key != "$ref")
                {
                    Walk(value);
                    continue;
                }

                if (TextAt(value) is not string reference)
                {
                    unresolved.Add($"{documentType}: non-string $ref");
                    continue;
                }

                if (!Resolves(document, reference))
                {
                    unresolved.Add($"{documentType}: {reference}");
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

            if (
                current is not JsonObject currentObject
                || !currentObject.TryGetPropertyValue(token, out current)
            )
            {
                return false;
            }
        }

        return current is not null;
    }

    private static JsonNode? ComponentOf(JsonNode components, string section, string name) =>
        components[section]?[name];

    private static string? TextAt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? BooleanAt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool parsed) ? parsed : null;

    private static string Describe(IReadOnlyList<string> descriptions)
    {
        if (descriptions.Count == 0)
        {
            return "none";
        }

        const int MaxReported = 10;
        string described = string.Join(", ", descriptions.Take(MaxReported));

        return descriptions.Count > MaxReported
            ? $"{described} and {descriptions.Count - MaxReported} more"
            : described;
    }

    private sealed record PackageFamily(string PackageId, string DataStandard, bool IsCore);

    private sealed record FragmentOperation(
        string DocumentType,
        string PathKey,
        string Method,
        IReadOnlyList<string> ParameterReferences,
        IReadOnlyDictionary<string, string> ResponseReferences
    )
    {
        public bool ReferencesParameter =>
            ParameterReferences.Contains(UseSnapshotParameterReference, StringComparer.Ordinal);

        public bool ReferencesSnapshotNotFound =>
            ResponseReferences.TryGetValue("404", out string? reference)
            && reference == SnapshotNotFoundResponseReference;

        public bool ReferencesSnapshotMethodNotAllowed =>
            ResponseReferences.TryGetValue("405", out string? reference)
            && reference == SnapshotMethodNotAllowedResponseReference;

        /// <summary>
        /// GET-many, GET-by-id, <c>/deletes</c>, and <c>/keyChanges</c>. Partitions paths are DMS-generated
        /// cursor-paging metadata rather than package content, so no fragment declares one.
        /// </summary>
        public bool IsSnapshotEligibleRead => Method == "get";

        public bool IsMutation => Method is "post" or "put" or "delete";

        public IEnumerable<string> SnapshotReferences
        {
            get
            {
                foreach (string reference in ParameterReferences.Where(IsSnapshotReference))
                {
                    yield return reference;
                }

                foreach (string reference in ResponseReferences.Values.Where(IsSnapshotReference))
                {
                    yield return reference;
                }
            }
        }

        private static bool IsSnapshotReference(string reference) =>
            reference
                is UseSnapshotParameterReference
                    or SnapshotNotFoundResponseReference
                    or SnapshotMethodNotAllowedResponseReference;

        public override string ToString() => $"{Method.ToUpperInvariant()} {PathKey}";
    }

    private sealed record PackagePayload(
        IReadOnlyDictionary<string, JsonNode> BaseDocumentComponents,
        IReadOnlyList<string> UnresolvedBaseDocumentReferences,
        IReadOnlyList<FragmentOperation> AvailableChangeVersionsOperations,
        int SnapshotEligibleReadCount,
        IReadOnlyList<string> ReadsMissingParameter,
        IReadOnlyList<string> ReadsMissingSnapshotNotFound,
        int MutationCount,
        IReadOnlyList<string> MutationsMissingSnapshotMethodNotAllowed,
        IReadOnlyList<string> SnapshotReferencesUsed,
        IReadOnlyList<string> SnapshotReferencesMissingFromCore
    );
}
