// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

/// <summary>
/// The identification-code query fields a resource offers, derived from its schema by the ODS/API rule.
/// </summary>
[TestFixture]
public class IdentificationCodeQueryFieldRuleTests
{
    private const string EdOrgSystem = "educationOrganizationIdentificationSystemDescriptor";

    /// <summary>
    /// Each qualifying resource's fields as <c>item path, type, maxLength</c>, in derived order.
    /// </summary>
    private static Dictionary<string, string[]> DeriveByResource(JsonObject resourceSchemas) =>
        resourceSchemas
            .Select(resource =>
                (
                    resource.Key,
                    Fields: IdentificationCodeQueryFieldRule
                        .Derive(resource.Value!)
                        .Select(static field => $"{field.ItemPath.Value} {field.Type} {field.MaxLength}")
                        .ToArray()
                )
            )
            .Where(static resource => resource.Fields.Length > 0)
            .ToDictionary(static resource => resource.Key, static resource => resource.Fields);

    private static string[] Fields(string collection, params (string Name, int MaxLength)[] properties) =>
        [.. properties.Select(property => $"$.{collection}[*].{property.Name} string {property.MaxLength}")];

    private static IReadOnlyList<IdentificationCodeQueryField> Derive(string resourceSchemaJson) =>
        IdentificationCodeQueryFieldRule.Derive(JsonNode.Parse(resourceSchemaJson)!);

    [TestFixture]
    [Parallelizable]
    public class Given_The_Packaged_Data_Standard_52_Schema : IdentificationCodeQueryFieldRuleTests
    {
        private static readonly string[] CollidingNames =
        [
            "assigningOrganizationIdentificationCode",
            "courseCatalogURL",
        ];

        private Dictionary<string, string[]> _fieldsByResource = null!;

        [OneTimeSetUp]
        public void Setup()
        {
            _fieldsByResource = DeriveByResource(
                PackagedApiSchemaContract.LoadPackagedResourceSchemas("DataStandard52ApiSchemaPackageRoot")
            );
        }

        [Test]
        public void It_offers_exactly_the_accepted_parameters_of_the_fifteen_qualifying_resources()
        {
            string[] edOrgFields = Fields(
                "identificationCodes",
                (EdOrgSystem, 306),
                ("identificationCode", 60)
            );

            _fieldsByResource
                .Should()
                .BeEquivalentTo(
                    new Dictionary<string, string[]>
                    {
                        ["assessments"] = Fields(
                            "identificationCodes",
                            ("assessmentIdentificationSystemDescriptor", 306),
                            ("assigningOrganizationIdentificationCode", 60),
                            ("identificationCode", 60)
                        ),
                        ["communityOrganizations"] = edOrgFields,
                        ["communityProviders"] = edOrgFields,
                        ["courseTranscripts"] = Fields(
                            "alternativeCourseIdentificationCodes",
                            ("courseIdentificationSystemDescriptor", 306),
                            ("identificationCode", 60)
                        ),
                        ["courses"] = Fields(
                            "identificationCodes",
                            ("assigningOrganizationIdentificationCode", 60),
                            ("courseCatalogURL", 255),
                            ("courseIdentificationSystemDescriptor", 306),
                            ("identificationCode", 60)
                        ),
                        ["educationOrganizationNetworks"] = edOrgFields,
                        ["educationServiceCenters"] = edOrgFields,
                        ["learningStandards"] = Fields(
                            "identificationCodes",
                            ("contentStandardName", 65),
                            ("identificationCode", 60)
                        ),
                        ["localEducationAgencies"] = edOrgFields,
                        ["organizationDepartments"] = edOrgFields,
                        ["postSecondaryInstitutions"] = edOrgFields,
                        ["schools"] = edOrgFields,
                        ["staffs"] = Fields(
                            "identificationCodes",
                            ("assigningOrganizationIdentificationCode", 60),
                            ("identificationCode", 60),
                            ("staffIdentificationSystemDescriptor", 306)
                        ),
                        ["stateEducationAgencies"] = edOrgFields,
                        ["studentEducationOrganizationAssociations"] = Fields(
                            "studentIdentificationCodes",
                            ("assigningOrganizationIdentificationCode", 60),
                            ("identificationCode", 60),
                            ("studentIdentificationSystemDescriptor", 306)
                        ),
                    },
                    options => options.WithStrictOrdering()
                );
        }

        [Test]
        public void It_leaves_the_course_transcript_names_that_are_root_query_fields_to_those_fields()
        {
            JsonObject courseTranscripts = PackagedApiSchemaContract.LoadPackagedResourceSchemas(
                "DataStandard52ApiSchemaPackageRoot"
            )["courseTranscripts"]!.AsObject();

            // The premise: both names are root query fields of the resource and item properties of its
            // identification-code collection, so offering them would change an existing filter's meaning.
            courseTranscripts["queryFieldMapping"]!
                .AsObject()
                .Select(static queryField => queryField.Key)
                .Should()
                .Contain(CollidingNames);
            courseTranscripts["jsonSchemaForInsert"]!["properties"]!["alternativeCourseIdentificationCodes"]![
                "items"
            ]!["properties"]!
                .AsObject()
                .Select(static property => property.Key)
                .Should()
                .Contain(CollidingNames);

            IdentificationCodeQueryFieldRule
                .Derive(courseTranscripts)
                .Select(static field => field.QueryFieldName)
                .Should()
                .Equal("courseIdentificationSystemDescriptor", "identificationCode");
        }

        [Test]
        public void It_offers_nothing_for_identification_codes_that_are_not_in_a_collection()
        {
            _fieldsByResource.Should().NotContainKeys("assessmentItems", "objectiveAssessments");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Packaged_Data_Standard_61_Schema : IdentificationCodeQueryFieldRuleTests
    {
        [Test]
        public void It_offers_exactly_the_accepted_parameters_of_the_four_qualifying_resources()
        {
            DeriveByResource(
                    PackagedApiSchemaContract.LoadPackagedResourceSchemas(
                        "DataStandard61ApiSchemaPackageRoot"
                    )
                )
                .Should()
                .BeEquivalentTo(
                    new Dictionary<string, string[]>
                    {
                        ["assessments"] = Fields(
                            "identificationCodes",
                            ("assessmentIdentificationSystemDescriptor", 306),
                            ("assigningOrganizationIdentificationCode", 120),
                            ("identificationCode", 120)
                        ),
                        ["courseTranscripts"] = Fields(
                            "alternativeCourseIdentificationCodes",
                            ("courseIdentificationSystemDescriptor", 306),
                            ("identificationCode", 120)
                        ),
                        ["courses"] = Fields(
                            "identificationCodes",
                            ("assigningOrganizationIdentificationCode", 120),
                            ("courseCatalogURL", 255),
                            ("courseIdentificationSystemDescriptor", 306),
                            ("identificationCode", 120)
                        ),
                        ["learningStandards"] = Fields(
                            "identificationCodes",
                            ("contentStandardName", 65),
                            ("identificationCode", 120)
                        ),
                    },
                    options => options.WithStrictOrdering()
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Packaged_Extension_Schemas : IdentificationCodeQueryFieldRuleTests
    {
        private static JsonObject ResourceSchemasOf(JsonNode apiSchemaRoot) =>
            apiSchemaRoot["projectSchema"]?["resourceSchemas"] as JsonObject
            ?? throw new InvalidOperationException("ApiSchema is missing projectSchema.resourceSchemas");

        [TestCase("EdFi.DataStandard52.TPDM.ApiSchema")]
        [TestCase("EdFi.DataStandard52.Sample.ApiSchema")]
        [TestCase("EdFi.DataStandard52.Homograph.ApiSchema")]
        [TestCase("EdFi.DataStandard61.Sample.ApiSchema")]
        [TestCase("EdFi.DataStandard61.Homograph.ApiSchema")]
        public void It_offers_nothing(string extensionPackageId)
        {
            DeriveByResource(
                    ResourceSchemasOf(
                        PackagedApiSchemaContract.LoadStagedExtensionRootNode(extensionPackageId)
                    )
                )
                .Should()
                .BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_With_Two_Qualifying_Collections : IdentificationCodeQueryFieldRuleTests
    {
        private const string Codes = """
            { "type": "array", "items": { "type": "object", "properties": {
              "identificationCode": { "type": "string" }, "{0}": { "type": "string" } } } }
            """;

        private static string Schema(string first, string second) =>
            $$"""
                { "queryFieldMapping": {}, "jsonSchemaForInsert": { "type": "object", "properties": {
                  "{{first}}": {{Codes.Replace("{0}", first + "Only")}},
                  "{{second}}": {{Codes.Replace("{0}", second + "Only")}} } } }
                """;

        [Test]
        public void It_uses_the_first_in_property_order()
        {
            Derive(Schema("primaryCodes", "secondaryCodes"))
                .Select(static field => field.ItemPath.Value)
                .Should()
                .Equal("$.primaryCodes[*].identificationCode", "$.primaryCodes[*].primaryCodesOnly");
        }

        [Test]
        public void It_follows_the_property_order_rather_than_the_names()
        {
            Derive(Schema("secondaryCodes", "primaryCodes"))
                .Select(static field => field.ItemPath.Value)
                .Should()
                .Equal("$.secondaryCodes[*].identificationCode", "$.secondaryCodes[*].secondaryCodesOnly");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Identification_Codes_Outside_A_Top_Level_Collection
        : IdentificationCodeQueryFieldRuleTests
    {
        [Test]
        public void It_offers_nothing()
        {
            Derive(
                    """
                    { "queryFieldMapping": {}, "jsonSchemaForInsert": { "type": "object", "properties": {
                      "identificationCode": { "type": "string" },
                      "parentReference": { "type": "object", "properties": {
                        "identificationCodes": { "type": "array", "items": { "type": "object", "properties": {
                          "identificationCode": { "type": "string" } } } } } },
                      "periods": { "type": "array", "items": { "type": "object", "properties": {
                        "identificationCodes": { "type": "array", "items": { "type": "object", "properties": {
                          "identificationCode": { "type": "string" } } } } } } },
                      "_ext": { "type": "object", "properties": { "sample": { "type": "object", "properties": {
                        "identificationCodes": { "type": "array", "items": { "type": "object", "properties": {
                          "identificationCode": { "type": "string" } } } } } } } },
                      "identification-codes": { "type": "array", "items": { "type": "object", "properties": {
                        "identificationCode": { "type": "string" } } } },
                      "codes": { "type": "array", "items": { "type": "object", "properties": {
                        "IdentificationCode": { "type": "string" } } } }
                    } } }
                    """
                )
                .Should()
                .BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Item_Properties_Of_Each_Schema_Type : IdentificationCodeQueryFieldRuleTests
    {
        private IReadOnlyList<IdentificationCodeQueryField> _fields = null!;

        [SetUp]
        public void Setup()
        {
            _fields = Derive(
                """
                { "queryFieldMapping": {}, "jsonSchemaForInsert": { "type": "object", "properties": {
                  "identificationCodes": { "type": "array", "items": { "type": "object", "properties": {
                    "identificationCode": { "type": "string", "maxLength": 60 },
                    "unbounded": { "type": "string" },
                    "issueDate": { "type": "string", "format": "date", "maxLength": 10 },
                    "issuedAt": { "type": "string", "format": "date-time" },
                    "issueTime": { "type": "string", "format": "time" },
                    "sequence": { "type": "integer", "format": "int32" },
                    "weight": { "type": "number" },
                    "isPrimary": { "type": "boolean" },
                    "issuerReference": { "type": "object", "properties": { "issuerId": { "type": "integer" } } },
                    "aliases": { "type": "array", "items": { "type": "string" } },
                    "untyped": { "maxLength": 5 },
                    "code.part": { "type": "string" }
                  } } } } } }
                """
            );
        }

        [Test]
        public void It_types_each_scalar_as_its_query_field_mapping_type_and_skips_the_rest()
        {
            _fields
                .Select(static field => $"{field.QueryFieldName} {field.Type} {field.MaxLength}")
                .Should()
                .Equal(
                    "identificationCode string 60",
                    "unbounded string ",
                    "issueDate date ",
                    "issuedAt date-time ",
                    "issueTime time ",
                    "sequence number ",
                    "weight number ",
                    "isPrimary boolean "
                );
        }

        [Test]
        public void It_paths_each_field_through_the_collection_items()
        {
            _fields[0].CollectionPath.Should().Be(new JsonPath("$.identificationCodes[*]"));
            _fields[0].ItemPath.Should().Be(new JsonPath("$.identificationCodes[*].identificationCode"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Item_Property_Named_As_A_Root_Query_Field : IdentificationCodeQueryFieldRuleTests
    {
        [Test]
        public void It_leaves_the_name_to_the_root_query_field_whatever_its_case()
        {
            Derive(
                    """
                    { "queryFieldMapping": { "CatalogUrl": [{ "path": "$.catalogUrl", "type": "string" }] },
                      "jsonSchemaForInsert": { "type": "object", "properties": {
                        "catalogUrl": { "type": "string" },
                        "identificationCodes": { "type": "array", "items": { "type": "object", "properties": {
                          "catalogURL": { "type": "string" },
                          "identificationCode": { "type": "string" } } } } } } }
                    """
                )
                .Select(static field => field.QueryFieldName)
                .Should()
                .Equal("identificationCode");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_Schema : IdentificationCodeQueryFieldRuleTests
    {
        [Test]
        public void It_offers_nothing_without_an_insert_schema()
        {
            Derive("""{ "queryFieldMapping": {} }""").Should().BeEmpty();
        }

        [Test]
        public void It_does_not_change_the_schema()
        {
            JsonNode resourceSchema = PackagedApiSchemaContract.LoadPackagedResourceSchemas(
                "DataStandard52ApiSchemaPackageRoot"
            )["schools"]!;
            string before = resourceSchema.ToJsonString();

            IdentificationCodeQueryFieldRule.Derive(resourceSchema).Should().NotBeEmpty();

            resourceSchema.ToJsonString().Should().Be(before);
        }
    }
}
