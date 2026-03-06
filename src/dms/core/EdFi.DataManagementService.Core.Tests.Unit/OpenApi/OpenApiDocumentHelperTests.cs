// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.OpenApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiDocumentTestBase;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public class OpenApiDocumentHelperTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_An_Extension_With_Ambiguous_Core_Schema_Name : OpenApiDocumentHelperTests
    {
        private JsonNode _coreSchemaRootNode = null!;
        private JsonNode[] _extensionSchemaRootNodes = null!;

        private static JsonNode CoreSchemaWithAmbiguousContactSchemas()
        {
            JsonObject schemas = new()
            {
                ["EdFi_Contact"] = new JsonObject
                {
                    ["description"] = "EdFi Contact description",
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                        ["addresses"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["$ref"] = "#/components/schemas/EdFi_Contact_Address",
                            },
                        },
                    },
                },
                ["EdFi_Contact_Address"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["streetAddress"] = new JsonObject { ["type"] = "string" },
                    },
                },
                ["TPDM_Contact"] = new JsonObject
                {
                    ["description"] = "TPDM Contact description",
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                        ["addresses"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["$ref"] = "#/components/schemas/TPDM_Contact_Address",
                            },
                        },
                    },
                },
                ["TPDM_Contact_Address"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["streetAddress"] = new JsonObject { ["type"] = "string" },
                    },
                },
            };

            var builder = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionWithCommonOverridesForAmbiguousContact()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["extra"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            _coreSchemaRootNode = CoreSchemaWithAmbiguousContactSchemas();
            _extensionSchemaRootNodes = [ExtensionWithCommonOverridesForAmbiguousContact()];
        }

        [Test]
        public void It_should_throw_invalid_operation_exception()
        {
            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(_coreSchemaRootNode, _extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_should_include_conflicting_schema_names_in_message()
        {
            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(_coreSchemaRootNode, _extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action.Should().Throw<InvalidOperationException>().WithMessage("*matched multiple core schemas*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Core_Schema_Name_Prefix_Mismatch : OpenApiDocumentHelperTests
    {
        private JsonNode _coreSchemaRootNode = null!;
        private JsonNode[] _extensionSchemaRootNodes = null!;

        private static JsonNode CoreSchemaWithWrongPrefixContact()
        {
            JsonObject schemas = new()
            {
                ["WrongPrefix_Contact"] = new JsonObject
                {
                    ["description"] = "Contact with wrong prefix",
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                        ["addresses"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["$ref"] = "#/components/schemas/WrongPrefix_Contact_Address",
                            },
                        },
                    },
                },
                ["WrongPrefix_Contact_Address"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["streetAddress"] = new JsonObject { ["type"] = "string" },
                    },
                },
            };

            var builder = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject(),
                        ["tags"] = new JsonArray(),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionWithCommonOverridesForMismatchedContact()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["extra"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [SetUp]
        public void Setup()
        {
            _coreSchemaRootNode = CoreSchemaWithWrongPrefixContact();
            _extensionSchemaRootNodes = [ExtensionWithCommonOverridesForMismatchedContact()];
        }

        [Test]
        public void It_should_throw_invalid_operation_exception()
        {
            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(_coreSchemaRootNode, _extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_should_include_expected_and_actual_prefix_in_message()
        {
            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(_coreSchemaRootNode, _extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*WrongPrefix*")
                .WithMessage("*EdFi*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ResolveRef_Encounters_Non_JsonObject_Node : OpenApiDocumentHelperTests
    {
        private ApiSchemaDocumentNodes _apiSchemaDocumentNodes = null!;
        private OpenApiDocument _openApiDocument = null!;

        [SetUp]
        public void Setup()
        {
            // Build a core schema where the "addresses" property items resolve to a non-JsonObject value
            // (a JsonValue string rather than a JsonObject) so that ResolveRef throws
            JsonObject contactSchema = new()
            {
                ["description"] = "Contact description",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["addresses"] = new JsonObject
                    {
                        ["type"] = "array",
                        // items is a plain string value, not a JsonObject — will trigger ResolveRef throw
                        ["items"] = JsonValue.Create("not-an-object")!,
                    },
                },
            };

            JsonObject schemas = new() { ["EdFi_Contact"] = contactSchema };

            var coreBuilder = new ApiSchemaBuilder()
                .WithStartProject("ed-fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/contacts"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "contacts get",
                                    ["tags"] = new JsonArray("contacts"),
                                },
                            },
                        },
                        ["tags"] = new JsonArray(
                            new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                        ),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            var coreSchemaRootNode = coreBuilder.AsSingleApiSchemaRootNode();

            var extensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");

            extensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments(
                    "resources",
                    new JsonObject
                    {
                        ["EdFi_Contact"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["extra"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    }
                )
                .WithCommonExtensionOverrides([
                    new JsonObject
                    {
                        // Navigate into items, which is a JsonValue — ResolveRef will throw
                        ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                        ["schemaFragment"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sample"] = new JsonObject { ["type"] = "object" },
                            },
                        },
                    },
                ])
                .WithEndResource();

            var extensionSchemaRootNode = extensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            _apiSchemaDocumentNodes = new ApiSchemaDocumentNodes(
                coreSchemaRootNode,
                [extensionSchemaRootNode]
            );
            _openApiDocument = new OpenApiDocument(NullLogger.Instance);
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            Action act = () =>
                _openApiDocument.CreateDocument(
                    _apiSchemaDocumentNodes,
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Expected a JsonObject node while resolving $ref*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Duplicate_Schema_Name_In_Common_Extension_Overrides : OpenApiDocumentHelperTests
    {
        private ApiSchemaDocumentNodes _apiSchemaDocumentNodes = null!;
        private OpenApiDocument _openApiDocument = null!;

        [SetUp]
        public void Setup()
        {
            var coreSchemaRootNode = CoreSchemaWithContactAndAddress();

            // First extension adds sample_EdFi_Contact_AddressExtension
            var firstExtensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            firstExtensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments(
                    "resources",
                    new JsonObject
                    {
                        ["EdFi_Contact"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["extra"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    }
                )
                .WithCommonExtensionOverrides([
                    new JsonObject
                    {
                        ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                        ["schemaFragment"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sample"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["field1"] = new JsonObject { ["type"] = "string" },
                                    },
                                },
                            },
                        },
                    },
                ])
                .WithEndResource();
            var firstExtension = firstExtensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            // Second extension tries to add the same sample_EdFi_Contact_AddressExtension schema again
            var secondExtensionBuilder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            secondExtensionBuilder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithResourceExtensionFragments(
                    "resources",
                    new JsonObject
                    {
                        ["EdFi_Contact"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["extra2"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    }
                )
                .WithCommonExtensionOverrides([
                    new JsonObject
                    {
                        ["insertionLocations"] = new JsonArray("$.properties.addresses.items"),
                        ["schemaFragment"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sample"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["field2"] = new JsonObject { ["type"] = "string" },
                                    },
                                },
                            },
                        },
                    },
                ])
                .WithEndResource();
            var secondExtension = secondExtensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            _apiSchemaDocumentNodes = new ApiSchemaDocumentNodes(
                coreSchemaRootNode,
                [firstExtension, secondExtension]
            );
            _openApiDocument = new OpenApiDocument(NullLogger.Instance);
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            Action act = () =>
                _openApiDocument.CreateDocument(
                    _apiSchemaDocumentNodes,
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate schema name*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Terminal_Node_Lacks_Component_Ref : OpenApiDocumentHelperTests
    {
        private static JsonNode CoreSchemaWithInlineField()
        {
            // The "someField" property is an inline object definition with no $ref
            JsonObject contactSchema = new()
            {
                ["description"] = "Contact description",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["contactUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["someField"] = new JsonObject
                    {
                        // Inline object — no $ref to a component schema
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string" },
                            ["city"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            };

            JsonObject schemas = new() { ["EdFi_Contact"] = contactSchema };

            var builder = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithOpenApiBaseDocuments(
                    resourcesDoc: new JsonObject
                    {
                        ["openapi"] = "3.0.1",
                        ["info"] = new JsonObject
                        {
                            ["title"] = "Ed-Fi Resources API",
                            ["version"] = "5.0.0",
                        },
                        ["components"] = new JsonObject { ["schemas"] = schemas },
                        ["paths"] = new JsonObject
                        {
                            ["/ed-fi/contacts"] = new JsonObject
                            {
                                ["get"] = new JsonObject
                                {
                                    ["description"] = "contacts get",
                                    ["tags"] = new JsonArray("contacts"),
                                },
                            },
                        },
                        ["tags"] = new JsonArray(
                            new JsonObject { ["name"] = "contacts", ["description"] = "Contacts" }
                        ),
                    }
                )
                .WithSimpleResource("Contact", false)
                .WithEndProject();

            return builder.AsSingleApiSchemaRootNode();
        }

        private static JsonNode ExtensionWithInlineTerminalOverride()
        {
            JsonArray commonOverrides =
            [
                new JsonObject
                {
                    // This path resolves to an inline object (no $ref),
                    // so the method should throw InvalidOperationException
                    ["insertionLocations"] = new JsonArray("$.properties.someField"),
                    ["schemaFragment"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sample"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["extra"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                },
            ];

            var builder = new ApiSchemaBuilder().WithStartProject("sample", "1.0.0");
            builder
                .WithStartResource("Contact", isResourceExtension: true)
                .WithNewExtensionResourceFragments("resources")
                .WithCommonExtensionOverrides(commonOverrides)
                .WithEndResource();

            return builder.WithEndProject().AsSingleApiSchemaRootNode();
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            JsonNode coreSchemaRootNode = CoreSchemaWithInlineField();
            JsonNode[] extensionSchemaRootNodes = [ExtensionWithInlineTerminalOverride()];

            OpenApiDocument doc = new(NullLogger.Instance);
            var action = () =>
                doc.CreateDocument(
                    new(coreSchemaRootNode, extensionSchemaRootNodes),
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            action
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*$.properties.someField*")
                .WithMessage("*Contact*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Duplicate_Project_Extension_Schema_In_Direct_Extension : OpenApiDocumentHelperTests
    {
        private ApiSchemaDocumentNodes _apiSchemaDocumentNodes = null!;
        private OpenApiDocument _openApiDocument = null!;

        [SetUp]
        public void Setup()
        {
            var coreSchemaRootNode = CoreSchemaRootNode();

            JsonObject firstExts = new()
            {
                ["EdFi_AcademicWeek"] = new JsonObject
                {
                    ["description"] = "first ext AcademicWeek description",
                    ["type"] = "string",
                },
            };

            var firstExtensionBuilder = new ApiSchemaBuilder().WithStartProject("tpdm", "1.0.0");
            firstExtensionBuilder
                .WithStartResource("AcademicWeekExtension", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", firstExts)
                .WithEndResource();
            var firstExtension = firstExtensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            JsonObject secondExts = new()
            {
                ["EdFi_AcademicWeek"] = new JsonObject
                {
                    ["description"] = "second ext AcademicWeek description",
                    ["type"] = "string",
                },
            };

            var secondExtensionBuilder = new ApiSchemaBuilder().WithStartProject("tpdm", "1.0.0");
            secondExtensionBuilder
                .WithStartResource("AcademicWeekExtension", isResourceExtension: true)
                .WithResourceExtensionFragments("resources", secondExts)
                .WithEndResource();
            var secondExtension = secondExtensionBuilder.WithEndProject().AsSingleApiSchemaRootNode();

            _apiSchemaDocumentNodes = new ApiSchemaDocumentNodes(
                coreSchemaRootNode,
                [firstExtension, secondExtension]
            );
            _openApiDocument = new OpenApiDocument(NullLogger.Instance);
        }

        [Test]
        public void It_should_throw_InvalidOperationException()
        {
            Action act = () =>
                _openApiDocument.CreateDocument(
                    _apiSchemaDocumentNodes,
                    OpenApiDocument.OpenApiDocumentType.Resource
                );

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Duplicate project extension schema*");
        }
    }
}
