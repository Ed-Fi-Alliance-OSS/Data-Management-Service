// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
public class EffectiveApiSchemaProviderTests
{
    private static EffectiveApiSchemaProvider CreateProvider()
    {
        return new EffectiveApiSchemaProvider(
            NullLogger<EffectiveApiSchemaProvider>.Instance,
            A.Fake<ICompiledSchemaCache>()
        );
    }

    private static JsonNode GetResourceSchema(EffectiveApiSchemaProvider provider, string resourceName)
    {
        return provider
            .Documents.GetCoreProjectSchema()
            .GetAllResourceSchemaNodes()
            .First(n => n.GetRequiredNode("resourceName").GetValue<string>() == resourceName);
    }

    private static JsonObject BuildCoreContactsJsonSchemaForInsert()
    {
        return JsonNode
            .Parse(
                """
                {
                    "additionalProperties": false,
                    "properties": {
                        "addresses": {
                            "type": "array",
                            "minItems": 0,
                            "items": {
                                "additionalProperties": false,
                                "type": "object",
                                "properties": {
                                    "streetNumberName": { "maxLength": 150, "type": "string" },
                                    "city": { "maxLength": 30, "type": "string" },
                                    "postalCode": { "maxLength": 17, "type": "string" },
                                    "stateAbbreviationDescriptor": { "maxLength": 306, "type": "string" },
                                    "addressTypeDescriptor": { "maxLength": 306, "type": "string" },
                                    "periods": {
                                        "type": "array",
                                        "minItems": 0,
                                        "items": {
                                            "additionalProperties": false,
                                            "type": "object",
                                            "properties": {
                                                "beginDate": { "format": "date", "type": "string" },
                                                "endDate": { "format": "date", "type": "string" }
                                            },
                                            "required": ["beginDate"]
                                        }
                                    }
                                },
                                "required": [
                                    "streetNumberName", "city",
                                    "stateAbbreviationDescriptor", "postalCode",
                                    "addressTypeDescriptor"
                                ]
                            }
                        },
                        "contactUniqueId": { "maxLength": 32, "type": "string" },
                        "firstName": { "maxLength": 75, "type": "string" },
                        "lastSurname": { "maxLength": 75, "type": "string" }
                    },
                    "required": ["contactUniqueId", "firstName", "lastSurname"],
                    "type": "object"
                }
                """
            )!
            .AsObject();
    }

    private static JsonObject BuildSampleExtensionProperties()
    {
        return JsonNode
            .Parse(
                """
                {
                    "_ext": {
                        "additionalProperties": true,
                        "type": "object",
                        "properties": {
                            "sample": {
                                "additionalProperties": true,
                                "type": "object",
                                "properties": {
                                    "isSportsFan": { "type": "boolean" },
                                    "favoriteBookTitles": {
                                        "type": "array",
                                        "minItems": 1,
                                        "items": {
                                            "properties": {
                                                "favoriteBookTitle": { "maxLength": 100, "type": "string" }
                                            },
                                            "required": ["favoriteBookTitle"],
                                            "type": "object"
                                        }
                                    }
                                }
                            }
                        }
                    },
                    "addresses": {
                        "type": "array",
                        "minItems": 0,
                        "items": {
                            "additionalProperties": false,
                            "type": "object",
                            "properties": {
                                "_ext": {
                                    "additionalProperties": false,
                                    "type": "object",
                                    "properties": {
                                        "sample": {
                                            "additionalProperties": false,
                                            "type": "object",
                                            "properties": {
                                                "complex": { "maxLength": 255, "type": "string" },
                                                "onBusRoute": { "type": "boolean" },
                                                "schoolDistricts": {
                                                    "type": "array",
                                                    "minItems": 1,
                                                    "items": {
                                                        "properties": {
                                                            "schoolDistrict": { "maxLength": 250, "type": "string" }
                                                        },
                                                        "required": ["schoolDistrict"],
                                                        "type": "object"
                                                    }
                                                }
                                            },
                                            "required": ["onBusRoute", "schoolDistricts"]
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                """
            )!
            .AsObject();
    }

    private static JsonArray BuildSampleAddressOverrides()
    {
        return
        [
            new JsonObject
            {
                ["insertionLocations"] = new JsonArray { "$.properties.addresses.items" },
                ["schemaFragment"] = JsonNode.Parse(
                    """
                    {
                        "additionalProperties": false,
                        "type": "object",
                        "properties": {
                            "sample": {
                                "additionalProperties": false,
                                "type": "object",
                                "properties": {
                                    "complex": { "maxLength": 255, "type": "string" },
                                    "onBusRoute": { "type": "boolean" },
                                    "schoolDistricts": {
                                        "type": "array",
                                        "minItems": 1,
                                        "items": {
                                            "properties": {
                                                "schoolDistrict": { "maxLength": 250, "type": "string" }
                                            },
                                            "required": ["schoolDistrict"],
                                            "type": "object"
                                        }
                                    }
                                },
                                "required": ["onBusRoute", "schoolDistricts"]
                            }
                        }
                    }
                    """
                ),
            },
        ];
    }

    /// <summary>
    /// Builds a core schema with one resource.
    /// </summary>
    private static JsonNode BuildCoreSchema(
        string resourceName,
        JsonObject jsonSchemaForInsert,
        JsonObject? documentPathsMapping = null,
        JsonArray? arrayUniquenessConstraints = null
    )
    {
        var resource = new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSchoolYearEnumeration"] = false,
            ["allowIdentityUpdates"] = false,
            ["isSubclass"] = false,
            ["dateTimeJsonPaths"] = new JsonObject(),
            ["booleanJsonPaths"] = new JsonObject(),
            ["numericJsonPaths"] = new JsonObject(),
            ["documentPathsMapping"] = documentPathsMapping ?? new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
            ["equalityConstraints"] = new JsonArray(),
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints ?? new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
        };

        return JsonNode.Parse(
            $$"""
            {
                "projectSchema": {
                    "projectName": "ed-fi",
                    "projectVersion": "5.0.0",
                    "isExtensionProject": false,
                    "description": "Core schema",
                    "resourceSchemas": {
                        "{{resourceName}}": {{resource.ToJsonString()}}
                    }
                }
            }
            """
        )!;
    }

    /// <summary>
    /// Builds an extension schema for a resource extension.
    /// </summary>
    private static JsonNode BuildExtensionSchema(
        string resourceName,
        JsonObject jsonSchemaForInsertProperties,
        JsonArray? commonExtensionOverrides = null,
        JsonObject? documentPathsMapping = null,
        JsonArray? arrayUniquenessConstraints = null,
        string projectName = "sample"
    )
    {
        var resource = new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSchoolYearEnumeration"] = false,
            ["allowIdentityUpdates"] = false,
            ["isSubclass"] = false,
            ["dateTimeJsonPaths"] = new JsonObject(),
            ["booleanJsonPaths"] = new JsonObject(),
            ["numericJsonPaths"] = new JsonObject(),
            ["documentPathsMapping"] = documentPathsMapping ?? new JsonObject(),
            ["jsonSchemaForInsert"] = new JsonObject { ["properties"] = jsonSchemaForInsertProperties },
            ["equalityConstraints"] = new JsonArray(),
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints ?? new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
        };

        if (commonExtensionOverrides is not null)
        {
            resource["commonExtensionOverrides"] = commonExtensionOverrides;
        }

        return JsonNode.Parse(
            $$"""
            {
                "projectSchema": {
                    "projectName": "{{projectName}}",
                    "projectVersion": "1.0.0",
                    "isExtensionProject": true,
                    "description": "{{projectName}} extension",
                    "resourceSchemas": {
                        "{{resourceName}}": {{resource.ToJsonString()}}
                    }
                }
            }
            """
        )!;
    }

    [TestFixture]
    public class Given_Sample_Extension_Merging_Into_Core_Contacts : EffectiveApiSchemaProviderTests
    {
        private EffectiveApiSchemaProvider _provider = null!;

        [SetUp]
        public void Setup()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                BuildSampleExtensionProperties(),
                commonExtensionOverrides: BuildSampleAddressOverrides()
            );

            _provider = CreateProvider();
            _provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));
        }

        [Test]
        public void It_adds_root_level_ext_with_sample_properties()
        {
            var contacts = GetResourceSchema(_provider, "contacts").AsObject();
            var sampleExt = contacts["jsonSchemaForInsert"]?["properties"]?["_ext"]?["properties"]?["sample"];

            sampleExt.Should().NotBeNull();
            sampleExt!["properties"]?["isSportsFan"].Should().NotBeNull();
            sampleExt["properties"]?["favoriteBookTitles"].Should().NotBeNull();
        }

        [Test]
        public void It_inserts_ext_at_addresses_items_via_common_extension_overrides()
        {
            var contacts = GetResourceSchema(_provider, "contacts").AsObject();
            var addressExt = contacts["jsonSchemaForInsert"]
                ?["properties"]
                ?["addresses"]
                ?["items"]
                ?["properties"]
                ?["_ext"];

            addressExt.Should().NotBeNull();
            addressExt!["properties"]?["sample"]?["properties"]?["complex"].Should().NotBeNull();
            addressExt["properties"]?["sample"]?["properties"]?["onBusRoute"].Should().NotBeNull();
            addressExt["properties"]?["sample"]?["properties"]?["schoolDistricts"].Should().NotBeNull();
        }

        [Test]
        public void It_preserves_core_address_properties()
        {
            var contacts = GetResourceSchema(_provider, "contacts").AsObject();
            var addressProps = contacts["jsonSchemaForInsert"]
                ?["properties"]
                ?["addresses"]
                ?["items"]
                ?["properties"];

            addressProps?["streetNumberName"].Should().NotBeNull();
            addressProps?["city"].Should().NotBeNull();
            addressProps?["postalCode"].Should().NotBeNull();
            addressProps?["stateAbbreviationDescriptor"].Should().NotBeNull();
            addressProps?["addressTypeDescriptor"].Should().NotBeNull();
            addressProps?["periods"].Should().NotBeNull();
        }

        [Test]
        public void It_preserves_core_required_fields()
        {
            var contacts = GetResourceSchema(_provider, "contacts").AsObject();
            var required = contacts["jsonSchemaForInsert"]?["required"]?.AsArray();
            var values = required!.Select(r => r?.GetValue<string>()).ToList();

            values.Should().Contain("contactUniqueId");
            values.Should().Contain("firstName");
            values.Should().Contain("lastSurname");
        }
    }

    [TestFixture]
    public class Given_Two_Extensions_Merging_Ext_At_Addresses_Items : EffectiveApiSchemaProviderTests
    {
        private JsonNode? _addressExtNode;

        [SetUp]
        public void Setup()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            // First extension: sample with address override (complex, onBusRoute, schoolDistricts)
            var sampleExtension = BuildExtensionSchema(
                "contacts",
                BuildSampleExtensionProperties(),
                commonExtensionOverrides: BuildSampleAddressOverrides()
            );

            // Second extension: "another" with its own address override (zoneCode)
            var anotherExtension = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["another"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                commonExtensionOverrides:
                [
                    new JsonObject
                    {
                        ["insertionLocations"] = new JsonArray { "$.properties.addresses.items" },
                        ["schemaFragment"] = JsonNode.Parse(
                            """
                            {
                                "type": "object",
                                "properties": {
                                    "another": {
                                        "type": "object",
                                        "properties": {
                                            "zoneCode": { "maxLength": 10, "type": "string" }
                                        },
                                        "required": ["zoneCode"]
                                    }
                                }
                            }
                            """
                        ),
                    },
                ],
                projectName: "another"
            );

            var provider = CreateProvider();
            provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [sampleExtension, anotherExtension]));

            var contacts = GetResourceSchema(provider, "contacts").AsObject();
            _addressExtNode = contacts["jsonSchemaForInsert"]
                ?["properties"]
                ?["addresses"]
                ?["items"]
                ?["properties"]
                ?["_ext"];
        }

        [Test]
        public void It_merges_properties_from_both_extensions_at_address_level()
        {
            _addressExtNode.Should().NotBeNull();
            _addressExtNode!["properties"]?["sample"].Should().NotBeNull();
            _addressExtNode["properties"]?["another"].Should().NotBeNull();
        }

        [Test]
        public void It_preserves_extension_fields_within_sample()
        {
            _addressExtNode!["properties"]!["sample"]!["properties"]?["complex"].Should().NotBeNull();
            _addressExtNode["properties"]!["sample"]!["properties"]?["onBusRoute"].Should().NotBeNull();
        }

        [Test]
        public void It_preserves_extension_fields_within_another()
        {
            _addressExtNode!["properties"]!["another"]!["properties"]?["zoneCode"].Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_Extension_With_ArrayUniquenessConstraints_Merging_NestedConstraints
        : EffectiveApiSchemaProviderTests
    {
        private JsonArray? _mergedConstraints;

        [SetUp]
        public void Setup()
        {
            // Core has addresses uniqueness constraint with one nestedConstraint (periods)
            var coreConstraints = JsonNode
                .Parse(
                    """
                    [
                        {
                            "paths": [
                                "$.addresses[*].addressTypeDescriptor",
                                "$.addresses[*].city",
                                "$.addresses[*].postalCode",
                                "$.addresses[*].stateAbbreviationDescriptor",
                                "$.addresses[*].streetNumberName"
                            ],
                            "nestedConstraints": [
                                {
                                    "basePath": "$.addresses[*]",
                                    "paths": ["$.periods[*].beginDate"]
                                }
                            ]
                        }
                    ]
                    """
                )!
                .AsArray();

            var coreSchema = BuildCoreSchema(
                "contacts",
                BuildCoreContactsJsonSchemaForInsert(),
                arrayUniquenessConstraints: coreConstraints
            );

            // Extension adds two more nestedConstraints for the same address paths
            var extConstraints = JsonNode
                .Parse(
                    """
                    [
                        {
                            "paths": [
                                "$.addresses[*].addressTypeDescriptor",
                                "$.addresses[*].city",
                                "$.addresses[*].postalCode",
                                "$.addresses[*].stateAbbreviationDescriptor",
                                "$.addresses[*].streetNumberName"
                            ],
                            "nestedConstraints": [
                                {
                                    "basePath": "$.addresses[*]",
                                    "paths": ["$._ext.sample.schoolDistricts[*].schoolDistrict"]
                                },
                                {
                                    "basePath": "$.addresses[*]",
                                    "paths": ["$._ext.sample.terms[*].termDescriptor"]
                                },
                                {
                                    "basePath": "$.addresses[*]",
                                    "paths": ["$.periods[*].beginDate"]
                                }
                            ]
                        }
                    ]
                    """
                )!
                .AsArray();

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                BuildSampleExtensionProperties(),
                commonExtensionOverrides: BuildSampleAddressOverrides(),
                arrayUniquenessConstraints: extConstraints
            );

            var provider = CreateProvider();
            provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            var contacts = GetResourceSchema(provider, "contacts").AsObject();
            _mergedConstraints = contacts["arrayUniquenessConstraints"]?.AsArray();
        }

        [Test]
        public void It_keeps_single_entry_for_matching_paths()
        {
            // The address constraint should not be duplicated
            var addressConstraints = _mergedConstraints!.Where(c =>
            {
                var paths = c?["paths"]?.AsArray();
                return paths?.Any(p => p?.GetValue<string>() == "$.addresses[*].addressTypeDescriptor")
                    == true;
            });
            addressConstraints.Should().HaveCount(1);
        }

        [Test]
        public void It_merges_nested_constraints_from_extension()
        {
            var addressConstraint = _mergedConstraints!.First(c =>
                c?["paths"]?.AsArray()
                    .Any(p => p?.GetValue<string>() == "$.addresses[*].addressTypeDescriptor") == true
            );

            var nested = addressConstraint!["nestedConstraints"]?.AsArray();
            nested.Should().NotBeNull();

            var allPaths = nested!
                .SelectMany(n => n?["paths"]?.AsArray() ?? [])
                .Select(p => p?.GetValue<string>())
                .ToList();

            allPaths.Should().Contain("$.periods[*].beginDate");
            allPaths.Should().Contain("$._ext.sample.schoolDistricts[*].schoolDistrict");
            allPaths.Should().Contain("$._ext.sample.terms[*].termDescriptor");
        }

        [Test]
        public void It_does_not_duplicate_nested_constraints()
        {
            // periods.beginDate exists in both core and extension — should appear only once
            var addressConstraint = _mergedConstraints!.First(c =>
                c?["paths"]?.AsArray()
                    .Any(p => p?.GetValue<string>() == "$.addresses[*].addressTypeDescriptor") == true
            );

            var nested = addressConstraint!["nestedConstraints"]!.AsArray();
            var serialized = nested.Select(n => n?.ToJsonString()).ToList();
            serialized.Should().OnlyHaveUniqueItems();
        }
    }

    [TestFixture]
    public class Given_Extension_With_Duplicate_Paths_In_ArrayUniquenessConstraints
        : EffectiveApiSchemaProviderTests
    {
        private JsonArray? _mergedConstraints;

        [SetUp]
        public void Setup()
        {
            // Core has no arrayUniquenessConstraints
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            // Extension source has two entries with the SAME paths
            var extConstraints = JsonNode
                .Parse(
                    """
                    [
                        {
                            "paths": ["$.addresses[*].addressTypeDescriptor"],
                            "nestedConstraints": [
                                { "basePath": "$.addresses[*]", "paths": ["$.periods[*].beginDate"] }
                            ]
                        },
                        {
                            "paths": ["$.addresses[*].addressTypeDescriptor"],
                            "nestedConstraints": [
                                { "basePath": "$.addresses[*]", "paths": ["$._ext.sample.terms[*].termDescriptor"] }
                            ]
                        }
                    ]
                    """
                )!
                .AsArray();

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                BuildSampleExtensionProperties(),
                commonExtensionOverrides: BuildSampleAddressOverrides(),
                arrayUniquenessConstraints: extConstraints
            );

            var provider = CreateProvider();
            provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            var contacts = GetResourceSchema(provider, "contacts").AsObject();
            _mergedConstraints = contacts["arrayUniquenessConstraints"]?.AsArray();
        }

        [Test]
        public void It_produces_single_entry_for_same_paths_key()
        {
            var addressConstraints = _mergedConstraints!.Where(c =>
                c?["paths"]?.AsArray()
                    .Any(p => p?.GetValue<string>() == "$.addresses[*].addressTypeDescriptor") == true
            );
            addressConstraints.Should().HaveCount(1);
        }

        [Test]
        public void It_merges_nested_constraints_from_both_source_entries()
        {
            var constraint = _mergedConstraints!.First(c =>
                c?["paths"]?.AsArray()
                    .Any(p => p?.GetValue<string>() == "$.addresses[*].addressTypeDescriptor") == true
            );

            var nested = constraint!["nestedConstraints"]?.AsArray();
            nested.Should().NotBeNull();

            var allPaths = nested!
                .SelectMany(n => n?["paths"]?.AsArray() ?? [])
                .Select(p => p?.GetValue<string>())
                .ToList();

            allPaths.Should().Contain("$.periods[*].beginDate");
            allPaths.Should().Contain("$._ext.sample.terms[*].termDescriptor");
        }
    }

    [TestFixture]
    public class Given_Extension_With_Non_Standard_Ext_Casing : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_treats_wrong_casing_as_separate_key_not_merged_into_ext()
        {
            // Core has "_ext" in its jsonSchemaForInsert
            var coreJsonSchema = JsonNode
                .Parse(
                    """
                    {
                        "additionalProperties": false,
                        "properties": {
                            "_ext": { "type": "object" },
                            "contactUniqueId": { "type": "string" }
                        },
                        "type": "object"
                    }
                    """
                )!
                .AsObject();

            var coreSchema = BuildCoreSchema("contacts", coreJsonSchema);

            // Extension uses "_Ext" (wrong casing) — should NOT be merged into "_ext",
            // instead treated as a separate key
            var extensionSchema = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_Ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                }
            );

            var provider = CreateProvider();
            provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            var contacts = GetResourceSchema(provider, "contacts").AsObject();
            var props = contacts["jsonSchemaForInsert"]?["properties"];

            // Both keys should exist independently
            props?["_ext"].Should().NotBeNull();
            props?["_Ext"].Should().NotBeNull();

            // "_ext" should NOT have "sample" merged into it
            props?["_ext"]?["properties"]?.AsObject().Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Override_With_Invalid_JsonPath : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_throws_when_path_not_found()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            var overrides = new JsonArray
            {
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray { "$.properties.nonExistentCollection.items" },
                    ["schemaFragment"] = JsonNode.Parse(
                        """{ "properties": { "field": { "type": "string" } } }"""
                    ),
                },
            };

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                commonExtensionOverrides: overrides
            );

            var provider = CreateProvider();
            var act = () => provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            act.Should().Throw<InvalidOperationException>().WithMessage("*path*not found*contacts*");
        }
    }

    [TestFixture]
    public class Given_Override_With_Non_Object_SchemaFragment : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_throws_when_schema_fragment_is_not_object()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            var overrides = new JsonArray
            {
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray { "$.properties" },
                    ["schemaFragment"] = JsonNode.Parse("\"not-an-object\""),
                },
            };

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                commonExtensionOverrides: overrides
            );

            var provider = CreateProvider();
            var act = () => provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*schemaFragment*contacts*must be a JSON object*");
        }
    }

    [TestFixture]
    public class Given_Extension_With_Duplicate_Key_And_No_CommonExtensionOverrides
        : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_throws_on_duplicate_key()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            // Extension tries to add "firstName" which already exists in core
            var extensionSchema = BuildExtensionSchema(
                "contacts",
                new JsonObject { ["firstName"] = new JsonObject { ["type"] = "string" } }
            );

            var provider = CreateProvider();
            var act = () => provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate key*firstName*contacts*");
        }
    }

    [TestFixture]
    public class Given_Extension_With_Duplicate_Addresses_Key_And_CommonExtensionOverrides
        : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_skips_duplicate_addresses_key_without_throwing()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            // Extension has "addresses" key (duplicate of core) — this is expected in the
            // real Sample extension because the address-level _ext is also present in the
            // extension's jsonSchemaForInsert.properties alongside commonExtensionOverrides
            var extensionSchema = BuildExtensionSchema(
                "contacts",
                BuildSampleExtensionProperties(),
                commonExtensionOverrides: BuildSampleAddressOverrides()
            );

            var provider = CreateProvider();
            var act = () => provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            act.Should().NotThrow();
        }
    }

    [TestFixture]
    public class Given_Extension_With_Unrelated_Duplicate_Key_And_CommonExtensionOverrides
        : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_throws_on_unrelated_duplicate_key_despite_common_overrides()
        {
            var coreSchema = BuildCoreSchema("contacts", BuildCoreContactsJsonSchemaForInsert());

            // Extension has commonExtensionOverrides targeting "addresses", but also has
            // "firstName" which duplicates a core key NOT targeted by any override.
            var extensionProperties = BuildSampleExtensionProperties();
            extensionProperties["firstName"] = new JsonObject { ["type"] = "string" };

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                extensionProperties,
                commonExtensionOverrides: BuildSampleAddressOverrides()
            );

            var provider = CreateProvider();
            var act = () => provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate key*firstName*contacts*");
        }
    }

    [TestFixture]
    public class Given_Extension_With_Duplicate_Key_In_DocumentPathsMapping : EffectiveApiSchemaProviderTests
    {
        [Test]
        public void It_throws_on_duplicate_even_with_common_extension_overrides()
        {
            var coreSchema = BuildCoreSchema(
                "contacts",
                BuildCoreContactsJsonSchemaForInsert(),
                documentPathsMapping: new JsonObject
                {
                    ["Address.City"] = new JsonObject
                    {
                        ["path"] = "$.addresses[*].city",
                        ["type"] = "string",
                    },
                }
            );

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                BuildSampleExtensionProperties(),
                commonExtensionOverrides: BuildSampleAddressOverrides(),
                documentPathsMapping: new JsonObject
                {
                    ["Address.City"] = new JsonObject
                    {
                        ["path"] = "$.addresses[*].city",
                        ["type"] = "string",
                    },
                }
            );

            var provider = CreateProvider();
            var act = () => provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Duplicate key*Address.City*documentPathsMapping*contacts*");
        }
    }

    [TestFixture]
    public class Given_MergeExtFragment_When_Existing_Ext_Has_No_Properties : EffectiveApiSchemaProviderTests
    {
        private JsonNode? _extNode;

        [SetUp]
        public void Setup()
        {
            // Core has _ext with required but no properties — simulates a partially
            // defined _ext that an extension must fill in
            var coreJsonSchemaForInsert = JsonNode
                .Parse(
                    """
                    {
                        "additionalProperties": false,
                        "properties": {
                            "_ext": { "type": "object", "required": ["existingExt"] },
                            "contactUniqueId": { "maxLength": 32, "type": "string" },
                            "firstName": { "maxLength": 75, "type": "string" },
                            "lastSurname": { "maxLength": 75, "type": "string" }
                        },
                        "required": ["contactUniqueId", "firstName", "lastSurname"],
                        "type": "object"
                    }
                    """
                )!
                .AsObject();

            var coreSchema = BuildCoreSchema("contacts", coreJsonSchemaForInsert);

            var extensionSchema = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["isSportsFan"] = new JsonObject { ["type"] = "boolean" },
                                },
                            },
                        },
                        ["required"] = new JsonArray { "sample" },
                    },
                }
            );

            var provider = CreateProvider();
            provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]));

            var contacts = GetResourceSchema(provider, "contacts").AsObject();
            _extNode = contacts["jsonSchemaForInsert"]?["properties"]?["_ext"];
        }

        [Test]
        public void It_copies_properties_to_existing_ext()
        {
            _extNode!["properties"]?["sample"].Should().NotBeNull();
            _extNode!["properties"]!["sample"]!["properties"]?["isSportsFan"].Should().NotBeNull();
        }

        [Test]
        public void It_merges_required_arrays()
        {
            var required = _extNode?["required"]?.AsArray();
            var values = required!.Select(r => r?.GetValue<string>()).ToList();
            values.Should().Contain("existingExt");
            values.Should().Contain("sample");
        }
    }

    [TestFixture]
    public class Given_Two_Extensions_Merging_Ext_With_AdditionalProperties : EffectiveApiSchemaProviderTests
    {
        private JsonNode? _extNode;

        [SetUp]
        public void Setup()
        {
            var coreJsonSchemaForInsert = JsonNode
                .Parse(
                    """
                    {
                        "additionalProperties": false,
                        "properties": {
                            "_ext": { "type": "object", "additionalProperties": true },
                            "contactUniqueId": { "maxLength": 32, "type": "string" }
                        },
                        "required": ["contactUniqueId"],
                        "type": "object"
                    }
                    """
                )!
                .AsObject();

            var coreSchema = BuildCoreSchema("contacts", coreJsonSchemaForInsert);

            var firstExtension = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true,
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                }
            );

            var secondExtension = BuildExtensionSchema(
                "contacts",
                new JsonObject
                {
                    ["_ext"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["another"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                projectName: "another"
            );

            var provider = CreateProvider();
            provider.Initialize(new ApiSchemaDocumentNodes(coreSchema, [firstExtension, secondExtension]));

            var contacts = GetResourceSchema(provider, "contacts").AsObject();
            _extNode = contacts["jsonSchemaForInsert"]?["properties"]?["_ext"];
        }

        [Test]
        public void It_uses_last_extension_additionalProperties_value()
        {
            // The second extension sets additionalProperties to false, overriding the first's true
            _extNode!["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        }

        [Test]
        public void It_has_properties_from_both_extensions()
        {
            _extNode!["properties"]?["sample"].Should().NotBeNull();
            _extNode!["properties"]?["another"].Should().NotBeNull();
        }
    }
}
