// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using No = EdFi.DataManagementService.Core.Model.No;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProvideApiSchemaMiddlewareTests
{
    // SUT
    private ProvideApiSchemaMiddleware? _provideApiSchemaMiddleware;

    [TestFixture]
    [Parallelizable]
    public class Given_Api_Schema_With_JsonSchemaForInsert_Extensions : ProvideApiSchemaMiddlewareTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("Contact")
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Type(SchemaValueType.Object)
                    .Properties(
                        (
                            "contactUniqueId",
                            new JsonSchemaBuilder()
                                .Description("A unique alphanumeric code assigned to a contact.")
                                .Type(SchemaValueType.String)
                                .MaxLength(32)
                        ),
                        (
                            "firstName",
                            new JsonSchemaBuilder()
                                .Description("A name given to an individual at birth.")
                                .Type(SchemaValueType.String)
                                .MaxLength(75)
                        ),
                        (
                            "addresses",
                            new JsonSchemaBuilder()
                                .Type(SchemaValueType.Array)
                                .Items(
                                    new JsonSchemaBuilder()
                                        .Type(SchemaValueType.Object)
                                        .Properties(
                                            (
                                                "addressTypeDescriptor",
                                                new JsonSchemaBuilder()
                                                    .Description(
                                                        "The type of address listed for an individual or organization."
                                                    )
                                                    .Type(SchemaValueType.String)
                                            ),
                                            (
                                                "city",
                                                new JsonSchemaBuilder()
                                                    .Description(
                                                        "The name of the city in which an address is located."
                                                    )
                                                    .Type(SchemaValueType.String)
                                                    .MaxLength(30)
                                            ),
                                            (
                                                "streetNumberName",
                                                new JsonSchemaBuilder()
                                                    .Description("The street number and street name.")
                                                    .Type(SchemaValueType.String)
                                                    .MaxLength(150)
                                            ),
                                            (
                                                "postalCode",
                                                new JsonSchemaBuilder()
                                                    .Description("The five or nine digit zip code.")
                                                    .Type(SchemaValueType.String)
                                                    .MaxLength(17)
                                            ),
                                            (
                                                "stateAbbreviationDescriptor",
                                                new JsonSchemaBuilder()
                                                    .Description("The abbreviation for the state.")
                                                    .Type(SchemaValueType.String)
                                            )
                                        )
                                )
                        )
                    )
                    .Required("contactUniqueId", "firstName", "addresses")
                    .Build()
            )
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("sample", "1.0.0")
            .WithStartResource("Contact", isResourceExtension: true)
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Type(SchemaValueType.Object)
                    .Properties(
                        (
                            "_ext",
                            new JsonSchemaBuilder()
                                .Type(SchemaValueType.Object)
                                .AdditionalProperties(JsonSchema.True)
                                .Description("optional extension collection")
                                .Properties(
                                    (
                                        "sample",
                                        new JsonSchemaBuilder()
                                            .Type(SchemaValueType.Object)
                                            .AdditionalProperties(JsonSchema.True)
                                            .Description("sample extension properties collection")
                                            .Properties(
                                                (
                                                    "addresses",
                                                    new JsonSchemaBuilder()
                                                        .Type(SchemaValueType.Array)
                                                        .Items(
                                                            new JsonSchemaBuilder()
                                                                .Type(SchemaValueType.Object)
                                                                .Properties(
                                                                    (
                                                                        "_ext",
                                                                        new JsonSchemaBuilder()
                                                                            .Type(SchemaValueType.Object)
                                                                            .AdditionalProperties(
                                                                                JsonSchema.False
                                                                            )
                                                                            .Description(
                                                                                "Extension properties"
                                                                            )
                                                                            .Properties(
                                                                                (
                                                                                    "sample",
                                                                                    new JsonSchemaBuilder()
                                                                                        .Type(
                                                                                            SchemaValueType.Object
                                                                                        )
                                                                                        .AdditionalProperties(
                                                                                            JsonSchema.False
                                                                                        )
                                                                                        .Description(
                                                                                            "sample extension properties"
                                                                                        )
                                                                                        .Properties(
                                                                                            (
                                                                                                "complex",
                                                                                                new JsonSchemaBuilder()
                                                                                                    .Description(
                                                                                                        "The apartment or housing complex name."
                                                                                                    )
                                                                                                    .Type(
                                                                                                        SchemaValueType.String
                                                                                                    )
                                                                                                    .MaxLength(
                                                                                                        255
                                                                                                    )
                                                                                                    .MinLength(
                                                                                                        1
                                                                                                    )
                                                                                            ),
                                                                                            (
                                                                                                "onBusRoute",
                                                                                                new JsonSchemaBuilder()
                                                                                                    .Description(
                                                                                                        "An indicator if the address is on a bus route."
                                                                                                    )
                                                                                                    .Type(
                                                                                                        SchemaValueType.Boolean
                                                                                                    )
                                                                                            ),
                                                                                            (
                                                                                                "schoolDistricts",
                                                                                                new JsonSchemaBuilder()
                                                                                                    .Type(
                                                                                                        SchemaValueType.Array
                                                                                                    )
                                                                                                    .MinItems(
                                                                                                        1
                                                                                                    )
                                                                                                    .Items(
                                                                                                        new JsonSchemaBuilder()
                                                                                                            .Type(
                                                                                                                SchemaValueType.Object
                                                                                                            )
                                                                                                            .Properties(
                                                                                                                (
                                                                                                                    "schoolDistrict",
                                                                                                                    new JsonSchemaBuilder()
                                                                                                                        .Description(
                                                                                                                            "The school district in which the address is located."
                                                                                                                        )
                                                                                                                        .Type(
                                                                                                                            SchemaValueType.String
                                                                                                                        )
                                                                                                                        .MaxLength(
                                                                                                                            250
                                                                                                                        )
                                                                                                                )
                                                                                                            )
                                                                                                    )
                                                                                            ),
                                                                                            (
                                                                                                "terms",
                                                                                                new JsonSchemaBuilder()
                                                                                                    .Type(
                                                                                                        SchemaValueType.Array
                                                                                                    )
                                                                                                    .Items(
                                                                                                        new JsonSchemaBuilder()
                                                                                                            .Type(
                                                                                                                SchemaValueType.Object
                                                                                                            )
                                                                                                            .Properties(
                                                                                                                (
                                                                                                                    "termDescriptor",
                                                                                                                    new JsonSchemaBuilder()
                                                                                                                        .Description(
                                                                                                                            "An Ed-Fi Descriptor"
                                                                                                                        )
                                                                                                                        .Type(
                                                                                                                            SchemaValueType.String
                                                                                                                        )
                                                                                                                )
                                                                                                            )
                                                                                                    )
                                                                                            )
                                                                                        )
                                                                                        .Required(
                                                                                            "onBusRoute",
                                                                                            "schoolDistricts"
                                                                                        )
                                                                                )
                                                                            )
                                                                    ),
                                                                    (
                                                                        "addressTypeDescriptor",
                                                                        new JsonSchemaBuilder()
                                                                            .Description(
                                                                                "The type of address listed for an individual or organization."
                                                                            )
                                                                            .Type(SchemaValueType.String)
                                                                    ),
                                                                    (
                                                                        "city",
                                                                        new JsonSchemaBuilder()
                                                                            .Description(
                                                                                "The name of the city in which an address is located."
                                                                            )
                                                                            .Type(SchemaValueType.String)
                                                                            .MaxLength(30)
                                                                    ),
                                                                    (
                                                                        "streetNumberName",
                                                                        new JsonSchemaBuilder()
                                                                            .Description(
                                                                                "The street number and street name."
                                                                            )
                                                                            .Type(SchemaValueType.String)
                                                                            .MaxLength(150)
                                                                    ),
                                                                    (
                                                                        "postalCode",
                                                                        new JsonSchemaBuilder()
                                                                            .Description(
                                                                                "The five or nine digit zip code."
                                                                            )
                                                                            .Type(SchemaValueType.String)
                                                                            .MaxLength(17)
                                                                    ),
                                                                    (
                                                                        "stateAbbreviationDescriptor",
                                                                        new JsonSchemaBuilder()
                                                                            .Description(
                                                                                "The abbreviation for the state."
                                                                            )
                                                                            .Type(SchemaValueType.String)
                                                                    )
                                                                )
                                                                .Required(
                                                                    "streetNumberName",
                                                                    "city",
                                                                    "stateAbbreviationDescriptor",
                                                                    "postalCode",
                                                                    "addressTypeDescriptor"
                                                                )
                                                        )
                                                ),
                                                (
                                                    "authors",
                                                    new JsonSchemaBuilder()
                                                        .Type(SchemaValueType.Array)
                                                        .Items(
                                                            new JsonSchemaBuilder()
                                                                .Type(SchemaValueType.Object)
                                                                .Properties(
                                                                    (
                                                                        "author",
                                                                        new JsonSchemaBuilder()
                                                                            .Description(
                                                                                "The contact's favorite authors."
                                                                            )
                                                                            .Type(SchemaValueType.String)
                                                                            .MaxLength(100)
                                                                    )
                                                                )
                                                                .Required("author")
                                                        )
                                                ),
                                                (
                                                    "favoriteBookTitle",
                                                    new JsonSchemaBuilder()
                                                        .Description(
                                                            "The title of the contact's favorite book."
                                                        )
                                                        .Type(SchemaValueType.String)
                                                        .MaxLength(100)
                                                ),
                                                (
                                                    "becameParent",
                                                    new JsonSchemaBuilder()
                                                        .Description(
                                                            "The year in which the contact first became a parent."
                                                        )
                                                        .Type(SchemaValueType.Integer)
                                                ),
                                                (
                                                    "isSportsFan",
                                                    new JsonSchemaBuilder()
                                                        .Description(
                                                            "An indication as to whether the contact is a sports fan."
                                                        )
                                                        .Type(SchemaValueType.Boolean)
                                                )
                                            )
                                    )
                                )
                        )
                    )
                    .Required("contactUniqueId", "firstName", "lastSurname")
                    .Build()
            )
            .WithBooleanJsonPaths(["$._ext.sample.isSportsFan"])
            .WithNumericJsonPaths(["$._ext.sample.becameParent"])
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        [SetUp]
        public void Setup()
        {
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => fakeApiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            _provideApiSchemaMiddleware = new ProvideApiSchemaMiddleware(
                fakeApiSchemaProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance,
                new CompiledSchemaCache()
            );
        }

        [Test]
        public async Task Merges_extension_properties_into_existing_array_items()
        {
            // Arrange
            var fakeRequestInfo = A.Fake<RequestInfo>();

            // Act
            await _provideApiSchemaMiddleware!.Execute(fakeRequestInfo, NullNext);

            // Assert
            fakeRequestInfo.ApiSchemaDocuments.Should().NotBeNull();

            var coreContactResource = fakeRequestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("Contact"));

            coreContactResource.Should().NotBeNull();

            // Verify addresses array has _ext.sample merged into items
            var addressesItems = coreContactResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .GetRequiredNode("addresses")
                .GetRequiredNode("items");

            var addressItemProperties = addressesItems.GetRequiredNode("properties").AsObject();

            // Core properties should still exist
            addressItemProperties.Should().ContainKey("addressTypeDescriptor");
            addressItemProperties.Should().ContainKey("city");
            addressItemProperties.Should().ContainKey("streetNumberName");
            addressItemProperties.Should().ContainKey("postalCode");
            addressItemProperties.Should().ContainKey("stateAbbreviationDescriptor");

            // Extension properties should be added to _ext. sample
            addressItemProperties.Should().ContainKey("_ext");

            var extNode = addressItemProperties["_ext"]!;
            extNode["description"]!.GetValue<string>().Should().Be("Extension properties");
            extNode["additionalProperties"]!.GetValue<bool>().Should().BeFalse();

            var sampleExtNode = extNode.GetRequiredNode("properties").GetRequiredNode("sample");

            sampleExtNode["description"]!.GetValue<string>().Should().Be("sample extension properties");
            sampleExtNode["additionalProperties"]!.GetValue<bool>().Should().BeFalse();

            var sampleProperties = sampleExtNode.GetRequiredNode("properties").AsObject();

            // Verify extension properties exist
            sampleProperties.Should().ContainKey("complex");
            sampleProperties.Should().ContainKey("onBusRoute");
            sampleProperties.Should().ContainKey("schoolDistricts");
            sampleProperties.Should().ContainKey("terms");

            // Verify complex property details
            sampleProperties["complex"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be("The apartment or housing complex name.");
            sampleProperties["complex"]!["maxLength"]!.GetValue<int>().Should().Be(255);
            sampleProperties["complex"]!["minLength"]!.GetValue<int>().Should().Be(1);
            sampleProperties["complex"]!["type"]!.GetValue<string>().Should().Be("string");

            // Verify onBusRoute property
            sampleProperties["onBusRoute"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be("An indicator if the address is on a bus route.");
            sampleProperties["onBusRoute"]!["type"]!.GetValue<string>().Should().Be("boolean");

            // Verify schoolDistricts array structure
            sampleProperties["schoolDistricts"]!["type"]!.GetValue<string>().Should().Be("array");
            sampleProperties["schoolDistricts"]!["minItems"]!.GetValue<int>().Should().Be(1);
            sampleProperties["schoolDistricts"]!
                .GetRequiredNode("items")
                .GetRequiredNode("properties")
                .AsObject()
                .Should()
                .ContainKey("schoolDistrict");

            // Verify terms array structure
            sampleProperties["terms"]!["type"]!.GetValue<string>().Should().Be("array");
            sampleProperties["terms"]!
                .GetRequiredNode("items")
                .GetRequiredNode("properties")
                .AsObject()
                .Should()
                .ContainKey("termDescriptor");

            // Verify required fields
            var requiredFields = sampleExtNode["required"]!.AsArray();
            requiredFields.Should().HaveCount(2);
            requiredFields.Select(x => x!.GetValue<string>()).Should().Contain("onBusRoute");
            requiredFields.Select(x => x!.GetValue<string>()).Should().Contain("schoolDistricts");
        }

        [Test]
        public async Task Adds_extension_only_properties_to_root_ext()
        {
            // Arrange
            var fakeRequestInfo = A.Fake<RequestInfo>();

            // Act
            await _provideApiSchemaMiddleware!.Execute(fakeRequestInfo, NullNext);

            // Assert
            fakeRequestInfo.ApiSchemaDocuments.Should().NotBeNull();

            var coreContactResource = fakeRequestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("Contact"));

            var rootProperties = coreContactResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .AsObject();

            // Verify root _ext exists
            rootProperties.Should().ContainKey("_ext");

            var rootExt = rootProperties["_ext"]!;
            rootExt["description"]!.GetValue<string>().Should().Be("optional extension collection");
            rootExt["additionalProperties"]!.GetValue<bool>().Should().BeTrue();

            var sampleExt = rootExt.GetRequiredNode("properties").GetRequiredNode("sample");

            sampleExt["description"]!
                .GetValue<string>()
                .Should()
                .Be("sample extension properties collection");
            sampleExt["additionalProperties"]!.GetValue<bool>().Should().BeTrue();

            var sampleProperties = sampleExt.GetRequiredNode("properties").AsObject();

            // Verify extension-only properties exist at root level
            sampleProperties.Should().ContainKey("authors");
            sampleProperties.Should().ContainKey("favoriteBookTitle");
            sampleProperties.Should().ContainKey("becameParent");
            sampleProperties.Should().ContainKey("isSportsFan");

            // Verify authors array
            var authors = sampleProperties["authors"]!;
            authors["type"]!.GetValue<string>().Should().Be("array");
            var authorItemProps = authors.GetRequiredNode("items").GetRequiredNode("properties").AsObject();
            authorItemProps.Should().ContainKey("author");
            authorItemProps["author"]!["description"]!
                .GetValue<string>()
                .Should()
                .Be("The contact's favorite authors.");
            authorItemProps["author"]!["maxLength"]!.GetValue<int>().Should().Be(100);

            // Verify favoriteBookTitle
            var favoriteBookTitle = sampleProperties["favoriteBookTitle"]!;
            favoriteBookTitle["description"]!
                .GetValue<string>()
                .Should()
                .Be("The title of the contact's favorite book.");
            favoriteBookTitle["maxLength"]!.GetValue<int>().Should().Be(100);
            favoriteBookTitle["type"]!.GetValue<string>().Should().Be("string");

            // Verify becameParent
            var becameParent = sampleProperties["becameParent"]!;
            becameParent["description"]!
                .GetValue<string>()
                .Should()
                .Be("The year in which the contact first became a parent.");
            becameParent["type"]!.GetValue<string>().Should().Be("integer");

            // Verify isSportsFan
            var isSportsFan = sampleProperties["isSportsFan"]!;
            isSportsFan["description"]!
                .GetValue<string>()
                .Should()
                .Be("An indication as to whether the contact is a sports fan.");
            isSportsFan["type"]!.GetValue<string>().Should().Be("boolean");
        }

        [Test]
        public async Task Preserves_core_properties_after_merge()
        {
            // Arrange
            var fakeRequestInfo = A.Fake<RequestInfo>();

            // Act
            await _provideApiSchemaMiddleware!.Execute(fakeRequestInfo, NullNext);

            // Assert
            fakeRequestInfo.ApiSchemaDocuments.Should().NotBeNull();

            var coreContactResource = fakeRequestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("Contact"));

            var rootProperties = coreContactResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .AsObject();

            // Verify all core properties still exist
            rootProperties.Should().ContainKey("contactUniqueId");
            rootProperties.Should().ContainKey("firstName");
            rootProperties.Should().ContainKey("addresses");

            // Verify core property details
            var contactUniqueId = rootProperties["contactUniqueId"]!;
            contactUniqueId["description"]!
                .GetValue<string>()
                .Should()
                .Be("A unique alphanumeric code assigned to a contact.");
            contactUniqueId["maxLength"]!.GetValue<int>().Should().Be(32);
            contactUniqueId["type"]!.GetValue<string>().Should().Be("string");

            var firstName = rootProperties["firstName"]!;
            firstName["description"]!
                .GetValue<string>()
                .Should()
                .Be("A name given to an individual at birth.");
            firstName["maxLength"]!.GetValue<int>().Should().Be(75);
            firstName["type"]!.GetValue<string>().Should().Be("string");

            var addresses = rootProperties["addresses"]!;
            addresses["type"]!.GetValue<string>().Should().Be("array");
        }

        [Test]
        public async Task Merges_other_extension_paths_correctly()
        {
            // Arrange
            var fakeRequestInfo = A.Fake<RequestInfo>();

            // Act
            await _provideApiSchemaMiddleware!.Execute(fakeRequestInfo, NullNext);

            // Assert
            fakeRequestInfo.ApiSchemaDocuments.Should().NotBeNull();

            var coreContactResource = fakeRequestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("Contact"));

            // Verify booleanJsonPaths are merged
            var booleanJsonPaths = coreContactResource!
                .GetRequiredNode("booleanJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>());

            booleanJsonPaths.Should().Contain("$._ext.sample.isSportsFan");

            // Verify numericJsonPaths are merged
            var numericJsonPaths = coreContactResource!
                .GetRequiredNode("numericJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>());

            numericJsonPaths.Should().Contain("$._ext.sample.becameParent");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Api_Schema_With_Resource_Extensions : ProvideApiSchemaMiddlewareTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithJsonSchemaForInsert(new JsonSchemaBuilder().Type(SchemaValueType.Object).Build())
            .WithEqualityConstraints(
                [new(new JsonPath("$.schoolReference.schoolId"), new JsonPath("$.sessionReference.schoolId"))]
            )
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Properties(
                        (
                            "credentialIdentifier",
                            new JsonSchemaBuilder()
                                .Description("Identifier or serial number assigned to the credential.")
                                .Type(SchemaValueType.String)
                        )
                    )
                    .Build()
            )
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("tpdm", "5.0.0")
            .WithStartResource("School", isResourceExtension: true)
            .WithEqualityConstraints(
                [
                    new(
                        new JsonPath("$.evaluationObjectiveRatingReference.evaluationTitle"),
                        new JsonPath("$.evaluationElementReference.evaluationTitle")
                    ),
                ]
            )
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Properties(
                        new Dictionary<string, JsonSchema>
                        {
                            {
                                "_ext",
                                new JsonSchemaBuilder().Properties(
                                    new Dictionary<string, JsonSchema>
                                    {
                                        {
                                            "tpdm",
                                            new JsonSchemaBuilder().Properties(
                                                new Dictionary<string, JsonSchema>
                                                {
                                                    {
                                                        "boardCertificationIndicator",
                                                        new JsonSchemaBuilder()
                                                            .Description("Indicator that the credential")
                                                            .Type(SchemaValueType.Boolean)
                                                    },
                                                }
                                            )
                                        },
                                    }
                                )
                            },
                        }
                    )
                    .Build()
            )
            .WithBooleanJsonPaths(["$._ext.tpdm.gradeLevels[*].isSecondary"])
            .WithNumericJsonPaths(["$._ext.tpdm.schoolId"])
            .WithDateTimeJsonPaths(["$._ext.tpdm.beginDate"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "Person",
                [new("$._ext.tpdm.personId", "$._ext.tpdm.personReference.personId")]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("sample", "1.0.0")
            .WithStartResource("School", isResourceExtension: true)
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Properties(
                        new Dictionary<string, JsonSchema>
                        {
                            {
                                "_ext",
                                new JsonSchemaBuilder().Properties(
                                    new Dictionary<string, JsonSchema>
                                    {
                                        {
                                            "sample",
                                            new JsonSchemaBuilder().Properties(
                                                new Dictionary<string, JsonSchema>
                                                {
                                                    {
                                                        "directlyOwnedBuses",
                                                        new JsonSchemaBuilder().Items(
                                                            new JsonSchemaBuilder().Properties(
                                                                new Dictionary<string, JsonSchema>
                                                                {
                                                                    {
                                                                        "directlyOwnedBusReference",
                                                                        new JsonSchemaBuilder().Properties(
                                                                            new Dictionary<string, JsonSchema>
                                                                            {
                                                                                {
                                                                                    "busId",
                                                                                    new JsonSchemaBuilder()
                                                                                        .Description(
                                                                                            "The unique identifier for the bus"
                                                                                        )
                                                                                        .Type(
                                                                                            SchemaValueType.Boolean
                                                                                        )
                                                                                },
                                                                            }
                                                                        )
                                                                    },
                                                                }
                                                            )
                                                        )
                                                    },
                                                }
                                            )
                                        },
                                    }
                                )
                            },
                        }
                    )
                    .Build()
            )
            .WithBooleanJsonPaths(
                ["$._ext.sample.cteProgramService.primaryIndicator", "$._ext.sample.isExemplary"]
            )
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "DirectlyOwnedBus",
                [new("$.busId", "$._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference.busId")]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        [SetUp]
        public void Setup()
        {
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => fakeApiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            _provideApiSchemaMiddleware = new ProvideApiSchemaMiddleware(
                fakeApiSchemaProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance,
                new CompiledSchemaCache()
            );
        }

        [Test]
        public async Task Copies_paths_to_core()
        {
            // Act
            var fakeRequestInfo = A.Fake<RequestInfo>();
            await _provideApiSchemaMiddleware!.Execute(fakeRequestInfo, NullNext);

            // Assert
            fakeRequestInfo.ApiSchemaDocuments.Should().NotBeNull();

            var coreSchoolResource = fakeRequestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("School"));

            var booleanJsonPaths = coreSchoolResource!
                .GetRequiredNode("booleanJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>());
            booleanJsonPaths.Should().NotBeNull();
            booleanJsonPaths.Should().Contain("$._ext.tpdm.gradeLevels[*].isSecondary");
            booleanJsonPaths.Should().Contain("$._ext.sample.cteProgramService.primaryIndicator");
            booleanJsonPaths.Should().Contain("$._ext.sample.isExemplary");

            coreSchoolResource!
                .GetRequiredNode("numericJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.schoolId");

            coreSchoolResource!
                .GetRequiredNode("dateTimeJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.beginDate");

            coreSchoolResource!
                .GetRequiredNode("documentPathsMapping")
                .AsObject()
                .GetRequiredNode("Person")
                .GetRequiredNode("referenceJsonPaths")[0]!
                .GetRequiredNode("referenceJsonPath")
                .GetValue<string>()
                .Should()
                .Be("$._ext.tpdm.personReference.personId");

            coreSchoolResource!
                .GetRequiredNode("documentPathsMapping")
                .AsObject()
                .GetRequiredNode("DirectlyOwnedBus")
                .GetRequiredNode("referenceJsonPaths")[0]!
                .GetRequiredNode("referenceJsonPath")
                .GetValue<string>()
                .Should()
                .Be("$._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference.busId");

            // check tpdm extension
            coreSchoolResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .GetRequiredNode("_ext")
                .GetRequiredNode("properties")
                .GetRequiredNode("tpdm")
                .GetRequiredNode("properties")
                .GetRequiredNode("boardCertificationIndicator")
                .GetRequiredNode("description")
                .GetValue<string>()
                .Should()
                .Be("Indicator that the credential");

            // check sample extension
            coreSchoolResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .GetRequiredNode("_ext")
                .GetRequiredNode("properties")
                .GetRequiredNode("sample")
                .GetRequiredNode("properties")
                .GetRequiredNode("directlyOwnedBuses")
                .GetRequiredNode("items")
                .GetRequiredNode("properties")
                .GetRequiredNode("directlyOwnedBusReference")
                .GetRequiredNode("properties")
                .GetRequiredNode("busId")
                .GetRequiredNode("description")
                .GetValue<string>()
                .Should()
                .Be("The unique identifier for the bus");

            coreSchoolResource!
                .GetRequiredNode("equalityConstraints")
                .AsArray()
                .Select(node => node!.GetRequiredNode("sourceJsonPath").GetValue<string>())
                .Should()
                .Contain("$.evaluationObjectiveRatingReference.evaluationTitle");

            coreSchoolResource!
                .GetRequiredNode("equalityConstraints")
                .AsArray()
                .Select(node => node!.GetRequiredNode("targetJsonPath").GetValue<string>())
                .Should()
                .Contain("$.evaluationElementReference.evaluationTitle");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class HotReloadScenarios : ProvideApiSchemaMiddlewareTests
    {
        private IApiSchemaProvider _mockProvider = null!;
        private ProvideApiSchemaMiddleware _middleware = null!;

        [SetUp]
        public void Setup()
        {
            _mockProvider = A.Fake<IApiSchemaProvider>();
            _middleware = new ProvideApiSchemaMiddleware(
                _mockProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance,
                new CompiledSchemaCache()
            );
        }

        [Test]
        public async Task Process_AfterSchemaReload_ProvidesNewSchema()
        {
            // Arrange
            var requestInfo = No.RequestInfo();
            var initialVersion = Guid.NewGuid();
            var newVersion = Guid.NewGuid();

            var initialSchema = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("InitialResource")
                .WithIdentityJsonPaths(["$.id"])
                .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            var updatedSchema = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.1.0")
                .WithStartResource("UpdatedResource")
                .WithIdentityJsonPaths(["$.id"])
                .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            // Setup version changes
            A.CallTo(() => _mockProvider.ReloadId).ReturnsNextFromSequence(initialVersion, newVersion);

            A.CallTo(() => _mockProvider.GetApiSchemaNodes())
                .ReturnsNextFromSequence(initialSchema, updatedSchema);

            // Act
            await _middleware.Execute(requestInfo, NullNext);
            var firstSchemaVersion = requestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .ResourceVersion.Value;

            // Reset requestInfo for second execution
            requestInfo = No.RequestInfo();
            await _middleware.Execute(requestInfo, NullNext);
            var secondSchemaVersion = requestInfo
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .ResourceVersion.Value;

            // Assert
            firstSchemaVersion.Should().Be("5.0.0");
            secondSchemaVersion.Should().Be("5.1.0");

            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task Process_MultipleRequestsAfterReload_ConsistentSchema()
        {
            // Arrange
            var contexts = Enumerable.Range(0, 10).Select(_ => No.RequestInfo()).ToList();
            var version = Guid.NewGuid();

            var schema = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("TestResource")
                .WithIdentityJsonPaths(["$.id"])
                .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            A.CallTo(() => _mockProvider.ReloadId).Returns(version);
            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).Returns(schema);

            // Act
            var tasks = contexts.Select(requestInfo => _middleware.Execute(requestInfo, NullNext)).ToArray();
            await Task.WhenAll(tasks);

            // Assert
            var projectVersions = contexts
                .Select(c => c.ApiSchemaDocuments.GetCoreProjectSchema().ResourceVersion.Value)
                .ToList();

            projectVersions
                .Should()
                .OnlyContain(v => v == "5.0.0", "all requests should get the same schema");

            // Should use cache - only called once despite multiple requests
            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task Process_ConcurrentWithSchemaChange_HandlesGracefully()
        {
            // Arrange
            var contexts = Enumerable.Range(0, 20).Select(_ => No.RequestInfo()).ToList();
            var versions = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var currentVersionIndex = 0;

            var schemas = new[]
            {
                new ApiSchemaBuilder()
                    .WithStartProject("Ed-Fi", "5.0.0")
                    .WithStartResource("InitialResource")
                    .WithIdentityJsonPaths(["$.id"])
                    .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                    .WithEndResource()
                    .WithEndProject()
                    .AsApiSchemaNodes(),
                new ApiSchemaBuilder()
                    .WithStartProject("Ed-Fi", "5.1.0")
                    .WithStartResource("UpdatedResource")
                    .WithIdentityJsonPaths(["$.id"])
                    .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                    .WithEndResource()
                    .WithEndProject()
                    .AsApiSchemaNodes(),
            };

            A.CallTo(() => _mockProvider.ReloadId).ReturnsLazily(() => versions[currentVersionIndex]);

            A.CallTo(() => _mockProvider.GetApiSchemaNodes())
                .ReturnsLazily(() => schemas[currentVersionIndex]);

            // Act
            var tasks = contexts
                .Select(
                    async (requestInfo, index) =>
                    {
                        // Change version midway through
                        if (index == 10)
                        {
                            currentVersionIndex = 1;
                        }
                        await _middleware.Execute(requestInfo, NullNext);
                    }
                )
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert
            var projectVersions = contexts
                .Select(c => c.ApiSchemaDocuments.GetCoreProjectSchema().ResourceVersion.Value)
                .ToList();

            // Should have both versions represented
            projectVersions.Should().Contain("5.0.0");
            projectVersions.Should().Contain("5.1.0");

            // Provider should be called at least twice (once per version)
            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).MustHaveHappenedTwiceOrMore();
        }
    }
}
