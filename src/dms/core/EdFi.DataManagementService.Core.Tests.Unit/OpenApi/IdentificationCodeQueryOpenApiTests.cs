// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// The identification-code query parameters published on the collection GET and partitions operations
/// of the packaged data standards, compared with the packaged fragments they are assembled from and with
/// the query fields the request pipeline accepts.
/// </summary>
public abstract class IdentificationCodeQueryOpenApiTests
{
    protected const string DataStandard52PackageRoot = "DataStandard52ApiSchemaPackageRoot";
    protected const string DataStandard61PackageRoot = "DataStandard61ApiSchemaPackageRoot";
    private const string PartitionsSuffix = "/partitions";

    private static readonly string[] _educationOrganizationNames =
    [
        "educationOrganizationIdentificationSystemDescriptor",
        "identificationCode",
    ];

    /// <summary>
    /// The Data Standard 5.2 collections that publish identification-code parameters and their names.
    /// courseTranscripts offers only two, because its root query fields keep the other two names.
    /// </summary>
    protected static readonly Dictionary<string, string[]> DataStandard52Expected = new()
    {
        ["/ed-fi/assessments"] =
        [
            "assessmentIdentificationSystemDescriptor",
            "assigningOrganizationIdentificationCode",
            "identificationCode",
        ],
        ["/ed-fi/courses"] =
        [
            "assigningOrganizationIdentificationCode",
            "courseCatalogURL",
            "courseIdentificationSystemDescriptor",
            "identificationCode",
        ],
        ["/ed-fi/courseTranscripts"] = ["courseIdentificationSystemDescriptor", "identificationCode"],
        ["/ed-fi/learningStandards"] = ["contentStandardName", "identificationCode"],
        ["/ed-fi/staffs"] =
        [
            "assigningOrganizationIdentificationCode",
            "identificationCode",
            "staffIdentificationSystemDescriptor",
        ],
        ["/ed-fi/studentEducationOrganizationAssociations"] =
        [
            "assigningOrganizationIdentificationCode",
            "identificationCode",
            "studentIdentificationSystemDescriptor",
        ],
        ["/ed-fi/communityOrganizations"] = _educationOrganizationNames,
        ["/ed-fi/communityProviders"] = _educationOrganizationNames,
        ["/ed-fi/educationOrganizationNetworks"] = _educationOrganizationNames,
        ["/ed-fi/educationServiceCenters"] = _educationOrganizationNames,
        ["/ed-fi/localEducationAgencies"] = _educationOrganizationNames,
        ["/ed-fi/organizationDepartments"] = _educationOrganizationNames,
        ["/ed-fi/postSecondaryInstitutions"] = _educationOrganizationNames,
        ["/ed-fi/schools"] = _educationOrganizationNames,
        ["/ed-fi/stateEducationAgencies"] = _educationOrganizationNames,
    };

    /// <summary>
    /// The Data Standard 6.1 collections that publish identification-code parameters and their names.
    /// </summary>
    protected static readonly Dictionary<string, string[]> DataStandard61Expected = new()
    {
        ["/ed-fi/assessments"] = DataStandard52Expected["/ed-fi/assessments"],
        ["/ed-fi/courses"] = DataStandard52Expected["/ed-fi/courses"],
        ["/ed-fi/courseTranscripts"] = DataStandard52Expected["/ed-fi/courseTranscripts"],
        ["/ed-fi/learningStandards"] = DataStandard52Expected["/ed-fi/learningStandards"],
    };

    /// <summary>
    /// A collection path an endpoint-owning resource schema declares, with the packaged path item it was
    /// assembled from.
    /// </summary>
    protected sealed record OwnedCollection(
        string PathKey,
        JsonNode ResourceSchema,
        JsonObject PackagedPathItem
    );

    protected static JsonNode AssembleResourceDocument(JsonNode root, string[]? excludedDomains = null) =>
        new OpenApiDocument(NullLogger.Instance, excludedDomains).CreateDocument(
            new ApiSchemaDocumentNodes(root.DeepClone(), []),
            OpenApiDocument.OpenApiDocumentType.Resource
        );

    protected static IReadOnlyList<OwnedCollection> OwnedCollections(JsonNode root)
    {
        List<OwnedCollection> owned = [];

        foreach ((_, JsonNode? resourceSchema) in root["projectSchema"]!["resourceSchemas"]!.AsObject())
        {
            if (
                resourceSchema is null
                || resourceSchema["isResourceExtension"]?.GetValue<bool>() == true
                || resourceSchema["openApiFragments"]?["resources"]?["paths"] is not JsonObject paths
            )
            {
                continue;
            }

            owned.AddRange(
                paths
                    .Where(path => CursorPagingOpenApiAugmenter.IsCollectionPath(path.Key))
                    .Select(path => new OwnedCollection(path.Key, resourceSchema, path.Value!.AsObject()))
            );
        }

        return owned;
    }

    /// <summary>
    /// The inline query parameters an operation publishes and the packaged collection GET does not, which
    /// is exactly what assembly added inline, because cursor paging adds only references.
    /// </summary>
    protected static IReadOnlyList<JsonObject> AddedParameters(
        JsonNode document,
        OwnedCollection collection,
        string pathKey
    )
    {
        HashSet<string> packagedNames =
        [
            .. InlineQueryParameters(collection.PackagedPathItem["get"])
                .Select(parameter => parameter["name"]!.GetValue<string>()),
        ];

        return
        [
            .. InlineQueryParameters(document["paths"]?[pathKey]?["get"])
                .Where(parameter => !packagedNames.Contains(parameter["name"]!.GetValue<string>())),
        ];
    }

    protected static IReadOnlyList<JsonObject> AddedOnCollection(
        JsonNode document,
        OwnedCollection collection
    ) => AddedParameters(document, collection, collection.PathKey);

    protected static IReadOnlyList<JsonObject> AddedOnPartitions(
        JsonNode document,
        OwnedCollection collection
    ) => AddedParameters(document, collection, collection.PathKey + PartitionsSuffix);

    protected static string NameOf(JsonNode parameter) => parameter["name"]!.GetValue<string>();

    protected static IEnumerable<JsonObject> InlineQueryParameters(JsonNode? operation) =>
        operation?["parameters"] is JsonArray parameters
            ? parameters
                .OfType<JsonObject>()
                .Where(parameter =>
                    parameter["$ref"] is null && parameter["in"]?.GetValue<string>() == "query"
                )
            : [];

    /// <summary>
    /// The item property schemas of the first top-level collection whose items declare an
    /// identificationCode, read straight off the insert schema.
    /// </summary>
    protected static JsonObject IdentificationCodeItemProperties(JsonNode resourceSchema) =>
        resourceSchema["jsonSchemaForInsert"]!["properties"]!
            .AsObject()
            .Select(property => property.Value?["items"]?["properties"] as JsonObject)
            .First(itemProperties => itemProperties?.ContainsKey("identificationCode") == true)!;

    protected static void AssertAddedParametersEqual(
        JsonNode actualDocument,
        JsonNode expectedDocument,
        OwnedCollection collection,
        string context
    )
    {
        foreach (
            (IReadOnlyList<JsonObject> actual, IReadOnlyList<JsonObject> expected, string operation) in new[]
            {
                (
                    AddedOnCollection(actualDocument, collection),
                    AddedOnCollection(expectedDocument, collection),
                    collection.PathKey
                ),
                (
                    AddedOnPartitions(actualDocument, collection),
                    AddedOnPartitions(expectedDocument, collection),
                    collection.PathKey + PartitionsSuffix
                ),
            }
        )
        {
            actual.Should().NotBeEmpty("{0} keeps the parameters of {1}", context, operation);
            actual
                .Select(parameter => parameter.ToJsonString())
                .Should()
                .Equal(
                    expected.Select(parameter => parameter.ToJsonString()),
                    "{0} publishes the same parameters on {1}",
                    context,
                    operation
                );
        }
    }
}

/// <summary>
/// The parameters assembled from one packaged data standard.
/// </summary>
public abstract class PackagedIdentificationCodeQueryOpenApiTests : IdentificationCodeQueryOpenApiTests
{
    protected abstract string PackageRootMetadataKey { get; }

    protected abstract IReadOnlyDictionary<string, string[]> Expected { get; }

    private JsonNode _document = null!;
    private IReadOnlyList<OwnedCollection> _collections = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        JsonNode root = PackagedApiSchemaContract.LoadPackagedRootNode(PackageRootMetadataKey);
        _collections = OwnedCollections(root);
        _document = AssembleResourceDocument(root);
    }

    [Test]
    public void It_publishes_parameters_on_exactly_the_expected_collections()
    {
        Dictionary<string, string[]> published = _collections
            .Select(collection =>
                (collection.PathKey, Names: AddedOnCollection(_document, collection).Select(NameOf).ToArray())
            )
            .Where(entry => entry.Names.Length > 0)
            .ToDictionary(entry => entry.PathKey, entry => entry.Names);

        published.Should().BeEquivalentTo(Expected);
    }

    [Test]
    public void It_publishes_the_same_parameters_on_the_partitions_operation()
    {
        foreach (OwnedCollection collection in _collections)
        {
            AddedOnPartitions(_document, collection)
                .Select(parameter => parameter.ToJsonString())
                .Should()
                .Equal(
                    AddedOnCollection(_document, collection).Select(parameter => parameter.ToJsonString()),
                    "{0} filters partitions as it filters the collection",
                    collection.PathKey
                );
        }
    }

    [Test]
    public void It_describes_each_parameter_from_the_collection_item_property()
    {
        foreach (
            OwnedCollection collection in _collections.Where(owned => Expected.ContainsKey(owned.PathKey))
        )
        {
            JsonObject itemProperties = IdentificationCodeItemProperties(collection.ResourceSchema);
            IReadOnlyList<JsonObject> added = AddedOnCollection(_document, collection);

            added.Should().NotBeEmpty("{0} publishes identification-code parameters", collection.PathKey);

            foreach (JsonObject parameter in added)
            {
                JsonNode item = itemProperties[NameOf(parameter)]!;
                string because = $"{collection.PathKey} {NameOf(parameter)}";

                parameter["in"]!.GetValue<string>().Should().Be("query", because);
                parameter["required"].Should().BeNull(because);
                parameter["description"]!
                    .GetValue<string>()
                    .Should()
                    .Be(item["description"]!.GetValue<string>(), because);

                JsonObject schema = parameter["schema"]!.AsObject();
                schema["type"]!.GetValue<string>().Should().Be(item["type"]!.GetValue<string>(), because);
                schema["maxLength"]!.GetValue<int>().Should().Be(item["maxLength"]!.GetValue<int>(), because);
                (schema["format"]?.ToJsonString()).Should().Be(item["format"]?.ToJsonString(), because);
                schema.Select(member => member.Key).Should().BeSubsetOf(["format", "maxLength", "type"]);
            }
        }
    }

    [Test]
    public void It_publishes_exactly_the_identification_code_fields_the_runtime_accepts()
    {
        foreach (OwnedCollection collection in _collections)
        {
            HashSet<string> mappedNames =
            [
                .. collection.ResourceSchema["queryFieldMapping"]!
                    .AsObject()
                    .Select(queryField => queryField.Key),
            ];

            var accepted = new ResourceSchema(collection.ResourceSchema)
                .QueryFields.Where(queryField => !mappedNames.Contains(queryField.QueryFieldName))
                .Select(queryField =>
                    (
                        queryField.QueryFieldName,
                        queryField.DocumentPathsWithType.Single().Type,
                        queryField.DocumentPathsWithType.Single().MaxLength
                    )
                );

            foreach (
                IReadOnlyList<JsonObject> published in new[]
                {
                    AddedOnCollection(_document, collection),
                    AddedOnPartitions(_document, collection),
                }
            )
            {
                published
                    .Select(parameter =>
                        (
                            NameOf(parameter),
                            parameter["schema"]!["type"]!.GetValue<string>(),
                            parameter["schema"]!["maxLength"]?.GetValue<int>()
                        )
                    )
                    .Should()
                    .BeEquivalentTo(accepted, "{0} publishes what it accepts", collection.PathKey);
            }
        }
    }

    [Test]
    public void It_leaves_the_courseTranscripts_root_parameters_as_packaged()
    {
        OwnedCollection courseTranscripts = _collections.Single(collection =>
            collection.PathKey == "/ed-fi/courseTranscripts"
        );

        foreach (string name in new[] { "assigningOrganizationIdentificationCode", "courseCatalogURL" })
        {
            JsonObject packaged = InlineQueryParameters(courseTranscripts.PackagedPathItem["get"])
                .Single(parameter => NameOf(parameter) == name);

            foreach (
                string pathKey in new[] { "/ed-fi/courseTranscripts", "/ed-fi/courseTranscripts/partitions" }
            )
            {
                InlineQueryParameters(_document["paths"]![pathKey]!["get"])
                    .Where(parameter =>
                        string.Equals(NameOf(parameter), name, StringComparison.OrdinalIgnoreCase)
                    )
                    .Select(parameter => parameter.ToJsonString())
                    .Should()
                    .Equal([packaged.ToJsonString()], "{0} keeps the root {1} filter only", pathKey, name);
            }
        }
    }

    [Test]
    public void It_publishes_no_query_name_twice_on_a_collection_or_partitions_operation()
    {
        JsonObject componentParameters = _document["components"]!["parameters"]!.AsObject();

        foreach (OwnedCollection collection in _collections)
        {
            foreach (string pathKey in new[] { collection.PathKey, collection.PathKey + "/partitions" })
            {
                List<string> names = [];

                foreach (
                    JsonObject parameter in _document["paths"]![pathKey]!["get"]!["parameters"]!
                        .AsArray()
                        .OfType<JsonObject>()
                )
                {
                    JsonNode resolved = parameter["$ref"] is JsonNode reference
                        ? componentParameters[
                            reference.GetValue<string>()["#/components/parameters/".Length..]
                        ]!
                        : parameter;

                    names.Add(NameOf(resolved).ToLowerInvariant());
                }

                names.Should().OnlyHaveUniqueItems("{0} publishes each query name once", pathKey);
            }
        }
    }
}

[TestFixture]
public class Given_the_DataStandard52_resource_document : PackagedIdentificationCodeQueryOpenApiTests
{
    protected override string PackageRootMetadataKey => DataStandard52PackageRoot;

    protected override IReadOnlyDictionary<string, string[]> Expected => DataStandard52Expected;
}

[TestFixture]
public class Given_the_DataStandard61_resource_document : PackagedIdentificationCodeQueryOpenApiTests
{
    protected override string PackageRootMetadataKey => DataStandard61PackageRoot;

    protected override IReadOnlyDictionary<string, string[]> Expected => DataStandard61Expected;
}

/// <summary>
/// A profile document derived from the Data Standard 5.2 document. The profile hides the School
/// identification-code collection from readers, and the parameters are still published, because the
/// request pipeline accepts them whatever the profile.
/// </summary>
[TestFixture]
public class Given_a_profile_document_hiding_the_School_identification_codes
    : IdentificationCodeQueryOpenApiTests
{
    private JsonNode _unprofiled = null!;
    private JsonNode _profiled = null!;
    private IReadOnlyList<OwnedCollection> _collections = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        JsonNode root = PackagedApiSchemaContract.LoadPackagedRootNode(DataStandard52PackageRoot);
        _collections = OwnedCollections(root);
        _unprofiled = AssembleResourceDocument(root);

        ContentTypeDefinition everything = new(MemberSelection.IncludeAll, [], [], [], []);

        // IncludeOnly without the collection hides it both here and in response projection, as the
        // API integration profile does.
        ContentTypeDefinition withoutIdentificationCodes = new(
            MemberSelection.IncludeOnly,
            [new PropertyRule("nameOfInstitution")],
            [],
            [],
            []
        );

        _profiled = new ProfileOpenApiSpecificationFilter(NullLogger.Instance).CreateProfileSpecification(
            AssembleResourceDocument(root),
            new ProfileDefinition(
                "IdentificationCodeProfile",
                [
                    new ResourceProfile("School", null, withoutIdentificationCodes, null),
                    new ResourceProfile("Staff", null, everything, null),
                ]
            )
        );
    }

    [Test]
    public void It_hides_the_collection_from_the_readable_School()
    {
        JsonObject readableProperties = _profiled["components"]!["schemas"]!["EdFi_School_readable"]![
            "properties"
        ]!.AsObject();

        readableProperties.Should().ContainKey("nameOfInstitution");
        readableProperties.Should().NotContainKey("identificationCodes");
    }

    [Test]
    public void It_publishes_the_unprofiled_parameters_on_the_profiled_resources()
    {
        foreach (string pathKey in new[] { "/ed-fi/schools", "/ed-fi/staffs" })
        {
            AssertAddedParametersEqual(
                _profiled,
                _unprofiled,
                _collections.Single(collection => collection.PathKey == pathKey),
                "the profile document"
            );
        }
    }

    [Test]
    public void It_removes_the_resources_the_profile_does_not_cover()
    {
        _profiled["paths"]!
            .AsObject()
            .Select(path => path.Key)
            .Should()
            .NotContain(pathKey => pathKey.StartsWith("/ed-fi/assessments", StringComparison.Ordinal));
    }
}

/// <summary>
/// The Data Standard 5.2 document with the EducationOrganization domain excluded. Domain filtering
/// removes the collections that belong only to that domain, partitions included, and leaves every other
/// collection's parameters as assembled.
/// </summary>
[TestFixture]
public class Given_the_EducationOrganization_domain_is_excluded : IdentificationCodeQueryOpenApiTests
{
    private static readonly string[] _removedCollections =
    [
        "/ed-fi/communityOrganizations",
        "/ed-fi/communityProviders",
        "/ed-fi/educationOrganizationNetworks",
        "/ed-fi/organizationDepartments",
    ];

    private JsonNode _unfiltered = null!;
    private JsonNode _filtered = null!;
    private IReadOnlyList<OwnedCollection> _collections = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        JsonNode root = PackagedApiSchemaContract.LoadPackagedRootNode(DataStandard52PackageRoot);
        _collections = OwnedCollections(root);
        _unfiltered = AssembleResourceDocument(root);
        _filtered = AssembleResourceDocument(root, ["EducationOrganization"]);
    }

    [Test]
    public void It_removes_the_collections_only_that_domain_owns()
    {
        IEnumerable<string> pathKeys = _filtered["paths"]!.AsObject().Select(path => path.Key);

        foreach (string removed in _removedCollections)
        {
            pathKeys.Should().NotContain([removed, removed + "/partitions"]);
        }
    }

    [Test]
    public void It_keeps_the_parameters_of_every_retained_collection()
    {
        foreach (string pathKey in DataStandard52Expected.Keys.Except(_removedCollections))
        {
            AssertAddedParametersEqual(
                _filtered,
                _unfiltered,
                _collections.Single(collection => collection.PathKey == pathKey),
                "the domain-filtered document"
            );
        }
    }
}

/// <summary>
/// The augmenter refuses to publish a parameter whose name the collection GET already publishes.
/// </summary>
[TestFixture]
public class Given_identification_code_parameters_for_a_hand_built_document
{
    private static Dictionary<string, IReadOnlyList<JsonObject>> Parameters() =>
        new()
        {
            ["/ed-fi/schools"] =
            [
                new JsonObject
                {
                    ["description"] = "code",
                    ["in"] = "query",
                    ["name"] = "identificationCode",
                    ["schema"] = new JsonObject { ["maxLength"] = 60, ["type"] = "string" },
                },
            ],
        };

    private static JsonNode Document(JsonArray? parameters)
    {
        JsonObject get = new() { ["operationId"] = "getSchools" };

        if (parameters is not null)
        {
            get["parameters"] = parameters;
        }

        return new JsonObject
        {
            ["components"] = new JsonObject
            {
                ["parameters"] = new JsonObject
                {
                    ["code"] = new JsonObject { ["in"] = "query", ["name"] = "IDENTIFICATIONCODE" },
                },
            },
            ["paths"] = new JsonObject { ["/ed-fi/schools"] = new JsonObject { ["get"] = get } },
        };
    }

    [Test]
    public void It_appends_the_parameters_to_a_GET_without_parameters()
    {
        JsonNode document = Document(null);

        IdentificationCodeQueryOpenApiAugmenter.Augment(document, Parameters());

        document["paths"]!["/ed-fi/schools"]!["get"]!["parameters"]!
            .ToJsonString()
            .Should()
            .Be(new JsonArray(Parameters()["/ed-fi/schools"][0]).ToJsonString());
    }

    [Test]
    public void It_refuses_an_inline_parameter_of_the_same_name_in_another_case()
    {
        JsonNode document = Document([new JsonObject { ["in"] = "query", ["name"] = "IdentificationCode" }]);

        Action augment = () => IdentificationCodeQueryOpenApiAugmenter.Augment(document, Parameters());

        augment.Should().Throw<InvalidOperationException>().WithMessage("*'identificationCode' parameter*");
    }

    [Test]
    public void It_refuses_a_referenced_parameter_of_the_same_name()
    {
        JsonNode document = Document([new JsonObject { ["$ref"] = "#/components/parameters/code" }]);

        Action augment = () => IdentificationCodeQueryOpenApiAugmenter.Augment(document, Parameters());

        augment.Should().Throw<InvalidOperationException>().WithMessage("*'identificationCode' parameter*");
    }
}
