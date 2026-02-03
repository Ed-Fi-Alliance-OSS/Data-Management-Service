// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class ApiSchemaInputNormalizerTests
{
    private static JsonNode CreateValidCoreSchema(
        string projectEndpointName = "ed-fi",
        string apiSchemaVersion = "1.0.0",
        bool includeOpenApi = false
    )
    {
        var schema = new JsonObject
        {
            ["apiSchemaVersion"] = apiSchemaVersion,
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = "ed-fi",
                ["projectVersion"] = "5.0.0",
                ["projectEndpointName"] = projectEndpointName,
                ["isExtensionProject"] = false,
                ["resourceSchemas"] = new JsonObject(),
                ["abstractResources"] = new JsonObject(),
            },
        };

        if (includeOpenApi)
        {
            var projectSchema = schema["projectSchema"]!.AsObject();
            projectSchema["openApiBaseDocuments"] = new JsonObject
            {
                ["resources"] = new JsonObject { ["info"] = "test" },
            };
            projectSchema["resourceSchemas"] = new JsonObject
            {
                ["students"] = new JsonObject
                {
                    ["resourceName"] = "Student",
                    ["openApiFragments"] = new JsonObject { ["get"] = new JsonObject() },
                },
            };
            projectSchema["abstractResources"] = new JsonObject
            {
                ["educationOrganization"] = new JsonObject
                {
                    ["resourceName"] = "EducationOrganization",
                    ["openApiFragment"] = new JsonObject { ["schema"] = new JsonObject() },
                },
            };
        }

        return schema;
    }

    private static JsonNode CreateValidExtensionSchema(
        string projectEndpointName,
        string apiSchemaVersion = "1.0.0",
        bool includeOpenApi = false
    )
    {
        var schema = new JsonObject
        {
            ["apiSchemaVersion"] = apiSchemaVersion,
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectEndpointName,
                ["projectVersion"] = "1.0.0",
                ["projectEndpointName"] = projectEndpointName,
                ["isExtensionProject"] = true,
                ["resourceSchemas"] = new JsonObject(),
                ["abstractResources"] = new JsonObject(),
            },
        };

        if (includeOpenApi)
        {
            var projectSchema = schema["projectSchema"]!.AsObject();
            projectSchema["openApiBaseDocuments"] = new JsonObject
            {
                ["resources"] = new JsonObject { ["info"] = "extension test" },
            };
            projectSchema["resourceSchemas"] = new JsonObject
            {
                ["customResource"] = new JsonObject
                {
                    ["resourceName"] = "CustomResource",
                    ["openApiFragments"] = new JsonObject { ["post"] = new JsonObject() },
                },
            };
        }

        return schema;
    }

    [TestFixture]
    public class Given_Valid_Core_Schema_Only : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _inputNodes = new ApiSchemaDocumentNodes(CreateValidCoreSchema(), []);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
        }

        [Test]
        public void It_returns_normalized_nodes()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            success.NormalizedNodes.Should().NotBeNull();
            success.NormalizedNodes.CoreApiSchemaRootNode.Should().NotBeNull();
            success.NormalizedNodes.ExtensionApiSchemaRootNodes.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Empty_Extension_Array : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _inputNodes = new ApiSchemaDocumentNodes(CreateValidCoreSchema(), []);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
        }

        [Test]
        public void It_returns_empty_extension_array_in_result()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            success.NormalizedNodes.ExtensionApiSchemaRootNodes.Should().NotBeNull();
            success.NormalizedNodes.ExtensionApiSchemaRootNodes.Should().BeEmpty();
        }

        [Test]
        public void It_preserves_core_schema()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var coreProjectSchema = success.NormalizedNodes.CoreApiSchemaRootNode["projectSchema"];
            coreProjectSchema?["projectEndpointName"]?.GetValue<string>().Should().Be("ed-fi");
        }

        [Test]
        public void It_does_not_report_collision_with_empty_extensions()
        {
            // Verifies that collision detection handles empty extension list correctly
            _result.Should().NotBeOfType<ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult>();
        }
    }

    [TestFixture]
    public class Given_Valid_Core_And_Extensions : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _inputNodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(),
                [CreateValidExtensionSchema("tpdm"), CreateValidExtensionSchema("sample")]
            );
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
        }

        [Test]
        public void It_returns_all_extensions()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            success.NormalizedNodes.ExtensionApiSchemaRootNodes.Should().HaveCount(2);
        }
    }

    [TestFixture]
    public class Given_Extensions_In_Random_Order : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            // Input extensions in non-alphabetical order
            _inputNodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(),
                [
                    CreateValidExtensionSchema("zebra"),
                    CreateValidExtensionSchema("alpha"),
                    CreateValidExtensionSchema("middle"),
                ]
            );
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
        }

        [Test]
        public void It_sorts_extensions_by_projectEndpointName_ordinally()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var extensions = success.NormalizedNodes.ExtensionApiSchemaRootNodes;

            extensions.Should().HaveCount(3);

            var endpointNames = extensions
                .Select(e => e["projectSchema"]?["projectEndpointName"]?.GetValue<string>())
                .ToList();

            endpointNames.Should().Equal("alpha", "middle", "zebra");
        }

        [Test]
        public void It_uses_ordinal_string_comparison()
        {
            // Verify ordinal sorting by testing case sensitivity
            var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            var nodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(),
                [
                    CreateValidExtensionSchema("Zebra"), // Capital Z
                    CreateValidExtensionSchema("alpha"), // lowercase a
                ]
            );
            var result = normalizer.Normalize(nodes);
            var success = (ApiSchemaNormalizationResult.SuccessResult)result;
            var endpointNames = success
                .NormalizedNodes.ExtensionApiSchemaRootNodes.Select(e =>
                    e["projectSchema"]?["projectEndpointName"]?.GetValue<string>()
                )
                .ToList();

            // In ordinal sort, uppercase comes before lowercase (ASCII order)
            endpointNames.Should().Equal("Zebra", "alpha");
        }
    }

    [TestFixture]
    public class Given_Missing_ProjectSchema : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            var malformedSchema = new JsonObject { ["apiSchemaVersion"] = "1.0.0" };
            // No projectSchema node

            _inputNodes = new ApiSchemaDocumentNodes(malformedSchema, []);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_missing_or_malformed_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult>();
        }

        [Test]
        public void It_provides_schema_source_in_result()
        {
            var failure = (ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult)_result;
            failure.SchemaSource.Should().Be("core");
        }

        [Test]
        public void It_provides_details_in_result()
        {
            var failure = (ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult)_result;
            failure.Details.Should().Contain("projectSchema");
        }
    }

    [TestFixture]
    public class Given_Missing_ApiSchemaVersion : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            var malformedSchema = new JsonObject
            {
                // No apiSchemaVersion
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "ed-fi",
                    ["projectEndpointName"] = "ed-fi",
                },
            };

            _inputNodes = new ApiSchemaDocumentNodes(malformedSchema, []);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_missing_or_malformed_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult>();
        }

        [Test]
        public void It_provides_details_about_missing_version()
        {
            var failure = (ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult)_result;
            failure.Details.Should().Contain("apiSchemaVersion");
        }
    }

    [TestFixture]
    public class Given_Missing_ProjectEndpointName : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            var malformedSchema = new JsonObject
            {
                ["apiSchemaVersion"] = "1.0.0",
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "ed-fi",
                    // No projectEndpointName
                },
            };

            _inputNodes = new ApiSchemaDocumentNodes(malformedSchema, []);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_missing_or_malformed_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult>();
        }

        [Test]
        public void It_provides_details_about_missing_endpoint_name()
        {
            var failure = (ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult)_result;
            failure.Details.Should().Contain("projectEndpointName");
        }
    }

    [TestFixture]
    public class Given_ApiSchemaVersion_Mismatch : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            _inputNodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(apiSchemaVersion: "1.0.0"),
                [CreateValidExtensionSchema("tpdm", apiSchemaVersion: "2.0.0")]
            );
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_version_mismatch_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult>();
        }

        [Test]
        public void It_provides_expected_version()
        {
            var failure = (ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult)_result;
            failure.ExpectedVersion.Should().Be("1.0.0");
        }

        [Test]
        public void It_provides_actual_version()
        {
            var failure = (ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult)_result;
            failure.ActualVersion.Should().Be("2.0.0");
        }

        [Test]
        public void It_provides_schema_source()
        {
            var failure = (ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult)_result;
            failure.SchemaSource.Should().Be("extension[0]");
        }
    }

    [TestFixture]
    public class Given_ProjectEndpointName_Collision : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            // Two extensions with the same projectEndpointName
            _inputNodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(),
                [CreateValidExtensionSchema("tpdm"), CreateValidExtensionSchema("tpdm")]
            );
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_collision_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult>();
        }

        [Test]
        public void It_provides_conflicting_endpoint_name()
        {
            var failure = (ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult)_result;
            failure.Collisions.Should().HaveCount(1);
            failure.Collisions[0].ProjectEndpointName.Should().Be("tpdm");
        }

        [Test]
        public void It_provides_conflicting_sources()
        {
            var failure = (ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult)_result;
            failure.Collisions[0].ConflictingSources.Should().Contain("extension[0]");
            failure.Collisions[0].ConflictingSources.Should().Contain("extension[1]");
        }
    }

    [TestFixture]
    public class Given_Core_Extension_Endpoint_Collision : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            // Extension with same endpoint name as core
            _inputNodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(projectEndpointName: "ed-fi"),
                [CreateValidExtensionSchema("ed-fi")]
            );
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_collision_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult>();
        }

        [Test]
        public void It_includes_core_in_conflicting_sources()
        {
            var failure = (ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult)_result;
            failure.Collisions[0].ConflictingSources.Should().Contain("core");
        }
    }

    [TestFixture]
    public class Given_Multiple_ProjectEndpointName_Collisions : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            // Multiple distinct collisions: "tpdm" appears twice, "sample" appears twice
            _inputNodes = new ApiSchemaDocumentNodes(
                CreateValidCoreSchema(),
                [
                    CreateValidExtensionSchema("tpdm"),
                    CreateValidExtensionSchema("sample"),
                    CreateValidExtensionSchema("tpdm"),
                    CreateValidExtensionSchema("sample"),
                ]
            );
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_collision_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult>();
        }

        [Test]
        public void It_reports_all_collisions()
        {
            var failure = (ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult)_result;
            failure.Collisions.Should().HaveCount(2);
        }

        [Test]
        public void It_includes_both_conflicting_endpoint_names()
        {
            var failure = (ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult)_result;
            var endpointNames = failure.Collisions.Select(c => c.ProjectEndpointName).ToList();
            endpointNames.Should().Contain("tpdm");
            endpointNames.Should().Contain("sample");
        }
    }

    [TestFixture]
    public class Given_Schema_With_OpenApi_Payloads : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private JsonNode _originalCoreSchema = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            _originalCoreSchema = CreateValidCoreSchema(includeOpenApi: true);
            var extensionWithOpenApi = CreateValidExtensionSchema("tpdm", includeOpenApi: true);

            _inputNodes = new ApiSchemaDocumentNodes(_originalCoreSchema, [extensionWithOpenApi]);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
        }

        [Test]
        public void It_strips_openApiBaseDocuments_from_core()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var coreProjectSchema = success.NormalizedNodes.CoreApiSchemaRootNode["projectSchema"];
            coreProjectSchema?["openApiBaseDocuments"].Should().BeNull();
        }

        [Test]
        public void It_strips_openApiFragments_from_resourceSchemas()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var resourceSchemas = success
                .NormalizedNodes.CoreApiSchemaRootNode["projectSchema"]
                ?["resourceSchemas"]?.AsObject();

            resourceSchemas.Should().NotBeNull();
            foreach (var (_, resourceSchema) in resourceSchemas!)
            {
                resourceSchema?["openApiFragments"].Should().BeNull();
            }
        }

        [Test]
        public void It_strips_openApiFragment_from_abstractResources()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var abstractResources = success
                .NormalizedNodes.CoreApiSchemaRootNode["projectSchema"]
                ?["abstractResources"]?.AsObject();

            abstractResources.Should().NotBeNull();
            foreach (var (_, abstractResource) in abstractResources!)
            {
                abstractResource?["openApiFragment"].Should().BeNull();
            }
        }

        [Test]
        public void It_strips_openApi_from_extensions()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var extension = success.NormalizedNodes.ExtensionApiSchemaRootNodes[0];
            var extProjectSchema = extension["projectSchema"];

            extProjectSchema?["openApiBaseDocuments"].Should().BeNull();
        }

        [Test]
        public void It_does_not_mutate_original_core_schema()
        {
            // Original should still have the OpenAPI payloads
            _originalCoreSchema["projectSchema"]?["openApiBaseDocuments"].Should().NotBeNull();
            _originalCoreSchema["projectSchema"]
                ?["resourceSchemas"]?["students"]?["openApiFragments"].Should()
                .NotBeNull();
            _originalCoreSchema["projectSchema"]
                ?["abstractResources"]?["educationOrganization"]?["openApiFragment"].Should()
                .NotBeNull();
        }

        [Test]
        public void It_preserves_non_openApi_properties()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;
            var coreProjectSchema = success.NormalizedNodes.CoreApiSchemaRootNode["projectSchema"];

            // These properties should still exist
            coreProjectSchema?["projectName"]?.GetValue<string>().Should().Be("ed-fi");
            coreProjectSchema?["projectVersion"]?.GetValue<string>().Should().Be("5.0.0");
            coreProjectSchema?["projectEndpointName"]?.GetValue<string>().Should().Be("ed-fi");
            coreProjectSchema?["isExtensionProject"]?.GetValue<bool>().Should().BeFalse();

            // Resource schema should still exist with other properties
            var studentSchema = coreProjectSchema?["resourceSchemas"]?["students"];
            studentSchema?["resourceName"]?.GetValue<string>().Should().Be("Student");
        }
    }

    [TestFixture]
    public class Given_Extension_With_Missing_ProjectSchema : ApiSchemaInputNormalizerTests
    {
        private ApiSchemaInputNormalizer _normalizer = null!;
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            var malformedExtension = new JsonObject { ["apiSchemaVersion"] = "1.0.0" };

            _inputNodes = new ApiSchemaDocumentNodes(CreateValidCoreSchema(), [malformedExtension]);
            _result = _normalizer.Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_missing_or_malformed_result()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult>();
        }

        [Test]
        public void It_identifies_the_extension()
        {
            var failure = (ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult)_result;
            failure.SchemaSource.Should().Be("extension[0]");
        }
    }
}
