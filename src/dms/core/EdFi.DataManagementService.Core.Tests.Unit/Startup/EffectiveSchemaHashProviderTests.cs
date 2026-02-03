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
public class EffectiveSchemaHashProviderTests
{
    private static JsonNode CreateCoreSchema(
        string projectEndpointName = "ed-fi",
        string projectVersion = "5.0.0"
    )
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = "ed-fi",
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

    private static JsonNode CreateExtensionSchema(string projectEndpointName)
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectEndpointName,
                ["projectVersion"] = "1.0.0",
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
                    ["projectName"] = "ed-fi",
                    ["projectVersion"] = "5.0.0",
                },
            };

            var schema2 = new JsonObject
            {
                ["projectSchema"] = new JsonObject
                {
                    ["projectVersion"] = "5.0.0",
                    ["projectName"] = "ed-fi",
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
                [CreateExtensionSchema("tpdm")]
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

            // Extensions should already be sorted by ApiSchemaInputNormalizer
            // but the hash provider should still produce consistent results
            var nodes1 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("alpha"), CreateExtensionSchema("beta")]
            );
            var nodes2 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("alpha"), CreateExtensionSchema("beta")]
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

            // Note: In practice, extensions are pre-sorted by ApiSchemaInputNormalizer
            // This test verifies that array order IS significant (extensions must be pre-sorted)
            var nodes1 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("alpha"), CreateExtensionSchema("beta")]
            );
            var nodes2 = new ApiSchemaDocumentNodes(
                CreateCoreSchema(),
                [CreateExtensionSchema("beta"), CreateExtensionSchema("alpha")]
            );

            _hash1 = _provider.ComputeHash(nodes1);
            _hash2 = _provider.ComputeHash(nodes2);
        }

        [Test]
        public void It_returns_different_hashes_because_array_order_matters()
        {
            // This confirms that pre-sorting by ApiSchemaInputNormalizer is required
            _hash1.Should().NotBe(_hash2);
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
}
