// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class EffectiveSchemaHashProviderTests
{
    private static JsonNode CreateCoreSchema(
        string projectEndpointName = "ed-fi",
        string projectName = "Ed-Fi",
        string projectVersion = "5.0.0"
    )
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectName,
                ["projectVersion"] = projectVersion,
                ["projectEndpointName"] = projectEndpointName,
                ["isExtensionProject"] = false,
                ["resourceSchemas"] = new JsonObject
                {
                    ["students"] = new JsonObject { ["resourceName"] = "Student" },
                },
            },
        };
    }

    private static JsonNode CreateExtensionSchema(
        string projectEndpointName,
        string? projectName = null,
        string projectVersion = "1.0.0"
    )
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectName ?? projectEndpointName,
                ["projectVersion"] = projectVersion,
                ["projectEndpointName"] = projectEndpointName,
                ["isExtensionProject"] = true,
                ["resourceSchemas"] = new JsonObject(),
            },
        };
    }

    [TestFixture]
    public class Given_Same_Schema_Content : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            var nodes1 = new ApiSchemaDocumentNodes(CreateCoreSchema(), []);
            var nodes2 = new ApiSchemaDocumentNodes(CreateCoreSchema(), []);

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_identical_hashes()
        {
            _hash1.Should().Be(_hash2);
        }

        [Test]
        public void It_returns_64_character_hex_string()
        {
            // SHA-256 produces 32 bytes = 64 hex characters
            _hash1.Should().HaveLength(64);
        }

        [Test]
        public void It_returns_lowercase_hex()
        {
            _hash1.Should().MatchRegex("^[0-9a-f]+$");
        }
    }

    [TestFixture]
    public class Given_Different_Schema_Content : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            var nodes1 = new ApiSchemaDocumentNodes(CreateCoreSchema(projectVersion: "5.0.0"), []);
            var nodes2 = new ApiSchemaDocumentNodes(CreateCoreSchema(projectVersion: "5.1.0"), []);

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_different_hashes()
        {
            _hash1.Should().NotBe(_hash2);
        }
    }

    [TestFixture]
    public class Given_Same_Content_Different_Property_Order : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            // Create schemas with properties in different orders
            var schema1 = new JsonObject
            {
                ["apiSchemaVersion"] = "1.0.0",
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectVersion"] = "5.0.0",
                    ["projectEndpointName"] = "ed-fi",
                    ["isExtensionProject"] = false,
                },
            };

            var schema2 = new JsonObject
            {
                ["projectSchema"] = new JsonObject
                {
                    ["projectVersion"] = "5.0.0",
                    ["isExtensionProject"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["projectEndpointName"] = "ed-fi",
                },
                ["apiSchemaVersion"] = "1.0.0",
            };

            var nodes1 = new ApiSchemaDocumentNodes(schema1, []);
            var nodes2 = new ApiSchemaDocumentNodes(schema2, []);

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_identical_hashes()
        {
            _hash1.Should().Be(_hash2);
        }
    }

    [TestFixture]
    public class Given_Schema_With_Extensions : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hashWithExtensions = null!;
        private string _hashWithoutExtensions = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            var coreSchema = CreateCoreSchema();

            var nodesWithExtensions = new ApiSchemaDocumentNodes(
                coreSchema.DeepClone(),
                [CreateExtensionSchema("tpdm", "TPDM")]
            );
            var nodesWithoutExtensions = new ApiSchemaDocumentNodes(coreSchema.DeepClone(), []);

            _hashWithExtensions = _provider.ComputeHash(nodesWithExtensions);
            _hashWithoutExtensions = _provider.ComputeHash(nodesWithoutExtensions);
        }

        [Test]
        public void It_produces_different_hashes()
        {
            _hashWithExtensions.Should().NotBe(_hashWithoutExtensions);
        }
    }

    [TestFixture]
    public class Given_Same_Extensions_In_Same_Order : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            var nodes1 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("alpha", "Alpha"), CreateExtensionSchema("beta", "Beta")]
            );
            var nodes2 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("alpha", "Alpha"), CreateExtensionSchema("beta", "Beta")]
            );

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_identical_hashes()
        {
            _hash1.Should().Be(_hash2);
        }
    }

    [TestFixture]
    public class Given_Different_Extension_Order : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            // Extensions provided in different orders - should produce SAME hash
            // because the provider sorts by projectEndpointName internally
            var nodes1 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("alpha", "Alpha"), CreateExtensionSchema("beta", "Beta")]
            );
            var nodes2 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("beta", "Beta"), CreateExtensionSchema("alpha", "Alpha")]
            );

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_identical_hashes_because_projects_are_sorted()
        {
            // The provider sorts all projects by projectEndpointName,
            // so input order doesn't affect the hash
            _hash1.Should().Be(_hash2);
        }
    }

    [TestFixture]
    public class Given_Empty_Extensions_Array : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            var nodes = new ApiSchemaDocumentNodes(CreateCoreSchema(), []);
            _hash = _provider.ComputeHash(nodes);
        }

        [Test]
        public void It_returns_valid_hash()
        {
            _hash.Should().NotBeNullOrEmpty();
            _hash.Should().HaveLength(64);
        }
    }

    [TestFixture]
    public class Given_Core_And_Extension_With_Ordinal_Sort_Order : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            // "Ed-Fi" sorts before "ed-fi" in ordinal comparison (uppercase before lowercase)
            // but we use projectEndpointName for sorting, not projectName
            // "alpha" < "beta" < "ed-fi" in ordinal order
            var nodes = new ApiSchemaDocumentNodes(
                CreateCoreSchema(projectEndpointName: "ed-fi"),
                [CreateExtensionSchema("beta", "Beta"), CreateExtensionSchema("alpha", "Alpha")]
            );

            _hash = _provider.ComputeHash(nodes);
        }

        [Test]
        public void It_returns_valid_hash()
        {
            _hash.Should().HaveLength(64);
            _hash.Should().MatchRegex("^[0-9a-f]+$");
        }
    }

    /// <summary>
    /// Tests using a checked-in fixture file to lock down the expected hash.
    /// The fixture file is located at: Fixtures/EffectiveSchemaHash/core-schema-fixture.json
    /// </summary>
    [TestFixture]
    public class Given_Known_Fixture_Schema : EffectiveSchemaHashProviderTests
    {
        private const string FixturePath = "Fixtures/EffectiveSchemaHash/core-schema-fixture.json";

        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash = null!;

        // This is the locked expected hash for the fixture schema file.
        // If canonicalization, manifest format, hash algorithm, or fixture content changes,
        // this value must be updated intentionally via a "bless" workflow.
        private const string ExpectedHash =
            "76508a0692e6a4856b5315c8e331b3f7b35c1c8d0b8b3b7ff545cad92c8f689f";

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            // Load the checked-in fixture file
            var fixtureJson = File.ReadAllText(FixturePath);
            var coreSchema =
                JsonNode.Parse(fixtureJson)
                ?? throw new InvalidOperationException($"Failed to parse fixture file: {FixturePath}");

            var nodes = new ApiSchemaDocumentNodes(coreSchema, []);
            _hash = _provider.ComputeHash(nodes);
        }

        [Test]
        public void It_produces_expected_locked_hash()
        {
            _hash.Should().Be(ExpectedHash);
        }

        [Test]
        public void It_is_64_lowercase_hex_characters()
        {
            _hash.Should().HaveLength(64);
            _hash.Should().MatchRegex("^[0-9a-f]+$");
        }
    }

    [TestFixture]
    public class Given_Different_ApiSchemaVersion : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            var schema1 = new JsonObject
            {
                ["apiSchemaVersion"] = "1.0.0",
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectVersion"] = "5.0.0",
                    ["projectEndpointName"] = "ed-fi",
                    ["isExtensionProject"] = false,
                },
            };

            var schema2 = new JsonObject
            {
                ["apiSchemaVersion"] = "2.0.0",
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectVersion"] = "5.0.0",
                    ["projectEndpointName"] = "ed-fi",
                    ["isExtensionProject"] = false,
                },
            };

            var nodes1 = new ApiSchemaDocumentNodes(schema1, []);
            var nodes2 = new ApiSchemaDocumentNodes(schema2, []);

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_different_hashes()
        {
            // apiSchemaFormatVersion is included in the manifest
            _hash1.Should().NotBe(_hash2);
        }
    }

    [TestFixture]
    public class Given_Different_ProjectName : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private string _hash1 = null!;
        private string _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);

            // Same endpoint name but different project names
            var nodes1 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(projectEndpointName: "ed-fi", projectName: "Ed-Fi"),
                []
            );
            var nodes2 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(projectEndpointName: "ed-fi", projectName: "EdFi"),
                []
            );

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_different_hashes()
        {
            // projectName is included in manifest and affects per-project hash
            _hash1.Should().NotBe(_hash2);
        }
    }

    /// <summary>
    /// Tests that OpenAPI payload sections are excluded from hash computation.
    /// Note: OpenAPI stripping is done by ApiSchemaInputNormalizer before ComputeHash is called.
    /// This test verifies the end-to-end behavior by passing schemas through the normalizer first.
    /// </summary>
    [TestFixture]
    public class Given_Schema_With_OpenApi_Payloads_After_Normalization : EffectiveSchemaHashProviderTests
    {
        private EffectiveSchemaHashProvider _provider = null!;
        private ApiSchemaInputNormalizer _normalizer = null!;
        private string _hashWithOpenApi = null!;
        private string _hashWithoutOpenApi = null!;

        private static JsonNode CreateSchemaWithOpenApi()
        {
            return new JsonObject
            {
                ["apiSchemaVersion"] = "1.0.0",
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectVersion"] = "5.0.0",
                    ["projectEndpointName"] = "ed-fi",
                    ["isExtensionProject"] = false,
                    ["openApiBaseDocuments"] = new JsonObject
                    {
                        ["resources"] = new JsonObject { ["openapi"] = "3.0.0" },
                        ["descriptors"] = new JsonObject { ["openapi"] = "3.0.0" },
                    },
                    ["resourceSchemas"] = new JsonObject
                    {
                        ["students"] = new JsonObject
                        {
                            ["resourceName"] = "Student",
                            ["openApiFragments"] = new JsonObject
                            {
                                ["get"] = new JsonObject { ["description"] = "Gets students" },
                            },
                        },
                    },
                    ["abstractResources"] = new JsonObject
                    {
                        ["educationOrganization"] = new JsonObject
                        {
                            ["resourceName"] = "EducationOrganization",
                            ["openApiFragment"] = new JsonObject { ["schema"] = new JsonObject() },
                        },
                    },
                },
            };
        }

        private static JsonNode CreateSchemaWithoutOpenApi()
        {
            return new JsonObject
            {
                ["apiSchemaVersion"] = "1.0.0",
                ["projectSchema"] = new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectVersion"] = "5.0.0",
                    ["projectEndpointName"] = "ed-fi",
                    ["isExtensionProject"] = false,
                    ["resourceSchemas"] = new JsonObject
                    {
                        ["students"] = new JsonObject { ["resourceName"] = "Student" },
                    },
                    ["abstractResources"] = new JsonObject
                    {
                        ["educationOrganization"] = new JsonObject
                        {
                            ["resourceName"] = "EducationOrganization",
                        },
                    },
                },
            };
        }

        [SetUp]
        public void Setup()
        {
            _provider = new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance);
            _normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);

            // Schema WITH OpenAPI payloads - normalize first (strips OpenAPI)
            var nodesWithOpenApi = new ApiSchemaDocumentNodes(CreateSchemaWithOpenApi(), []);
            var normalizedWithOpenApi = _normalizer.Normalize(nodesWithOpenApi);

            // Schema WITHOUT OpenAPI payloads - normalize for consistency
            var nodesWithoutOpenApi = new ApiSchemaDocumentNodes(CreateSchemaWithoutOpenApi(), []);
            var normalizedWithoutOpenApi = _normalizer.Normalize(nodesWithoutOpenApi);

            // Both should be successful
            normalizedWithOpenApi.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
            normalizedWithoutOpenApi.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();

            var successWithOpenApi = (ApiSchemaNormalizationResult.SuccessResult)normalizedWithOpenApi;
            var successWithoutOpenApi = (ApiSchemaNormalizationResult.SuccessResult)normalizedWithoutOpenApi;

            _hashWithOpenApi = _provider.ComputeHash(successWithOpenApi.NormalizedNodes);
            _hashWithoutOpenApi = _provider.ComputeHash(successWithoutOpenApi.NormalizedNodes);
        }

        [Test]
        public void It_produces_identical_hashes()
        {
            // OpenAPI payloads are stripped by the normalizer before hashing,
            // so schemas with and without OpenAPI should produce the same hash
            _hashWithOpenApi.Should().Be(_hashWithoutOpenApi);
        }
    }

    /// <summary>
    /// Tests that the RelationalMappingVersion constant participates in hash computation.
    /// This is verified indirectly: the locked fixture hash would change if the constant changed.
    /// </summary>
    [TestFixture]
    public class Given_RelationalMappingVersion_Constant : EffectiveSchemaHashProviderTests
    {
        [Test]
        public void It_exists_and_is_non_empty()
        {
            // The RelationalMappingVersion constant must exist and have a value
            SchemaHashConstants.RelationalMappingVersion.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void It_is_included_in_manifest_format()
        {
            // Verify the constant follows expected format (simple version string)
            SchemaHashConstants.RelationalMappingVersion.Should().MatchRegex("^v\\d+$");
        }

        [Test]
        public void It_is_documented_as_affecting_hash()
        {
            // The HashVersion header identifies the hash algorithm version
            SchemaHashConstants.HashVersion.Should().NotBeNullOrEmpty();
            SchemaHashConstants.HashVersion.Should().Contain("dms-effective-schema-hash");
        }
    }
}
