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
public class ResourceKeySeedProviderTests
{
    private static JsonNode CreateMinimalSchema(
        string projectName = "Ed-Fi",
        string projectVersion = "5.0.0",
        string projectEndpointName = "ed-fi",
        bool isExtension = false,
        JsonObject? resourceSchemas = null,
        JsonObject? abstractResources = null
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
                ["isExtensionProject"] = isExtension,
                ["description"] = "Test schema",
                ["resourceNameMapping"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
                ["educationOrganizationHierarchy"] = new JsonObject(),
                ["educationOrganizationTypes"] = new JsonArray(),
                ["resourceSchemas"] = resourceSchemas ?? new JsonObject(),
                ["abstractResources"] = abstractResources ?? new JsonObject(),
            },
        };
    }

    private static JsonObject CreateResourceSchema(
        string resourceName,
        bool isDescriptor = false,
        bool isResourceExtension = false
    )
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = isDescriptor,
            ["isSchoolYearEnumeration"] = false,
            ["isResourceExtension"] = isResourceExtension,
            ["allowIdentityUpdates"] = false,
            ["isSubclass"] = false,
            ["identityJsonPaths"] = new JsonArray("$.id"),
            ["booleanJsonPaths"] = new JsonArray(),
            ["numericJsonPaths"] = new JsonArray(),
            ["dateJsonPaths"] = new JsonArray(),
            ["dateTimeJsonPaths"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["queryFieldMapping"] = new JsonObject(),
            ["securableElements"] = new JsonObject
            {
                ["Namespace"] = new JsonArray(),
                ["EducationOrganization"] = new JsonArray(),
                ["Student"] = new JsonArray(),
                ["Contact"] = new JsonArray(),
                ["Staff"] = new JsonArray(),
            },
            ["authorizationPathways"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("id"),
            },
        };
    }

    [TestFixture]
    public class Given_Single_Project_With_Concrete_And_Abstract_Resources : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var resourceSchemas = new JsonObject
            {
                ["students"] = CreateResourceSchema("Student"),
                ["schools"] = CreateResourceSchema("School"),
            };

            var abstractResources = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject { ["identityJsonPaths"] = new JsonArray("$.id") },
            };

            var schema = CreateMinimalSchema(
                resourceSchemas: resourceSchemas,
                abstractResources: abstractResources
            );

            var nodes = new ApiSchemaDocumentNodes(schema, []);
            _seeds = _provider.GetSeeds(nodes);
        }

        [Test]
        public void It_returns_seeds_for_both_concrete_and_abstract()
        {
            _seeds.Should().HaveCount(3);
            _seeds.Select(s => s.ResourceName).Should().Contain("Student");
            _seeds.Select(s => s.ResourceName).Should().Contain("School");
            _seeds.Select(s => s.ResourceName).Should().Contain("EducationOrganization");
        }

        [Test]
        public void It_assigns_contiguous_ids_starting_from_1()
        {
            _seeds.Select(s => s.ResourceKeyId).Should().BeEquivalentTo([1, 2, 3]);
        }

        [Test]
        public void It_sorts_by_resource_name_using_ordinal()
        {
            // Ed-Fi project only, so sorted by resource name: EducationOrganization, School, Student
            var resourceNames = _seeds.Select(s => s.ResourceName).ToList();
            resourceNames.Should().BeInAscendingOrder(StringComparer.Ordinal);
        }

        [Test]
        public void It_correctly_identifies_abstract_resources()
        {
            var edOrgSeed = _seeds.Single(s => s.ResourceName == "EducationOrganization");
            edOrgSeed.IsAbstract.Should().BeTrue();

            var studentSeed = _seeds.Single(s => s.ResourceName == "Student");
            studentSeed.IsAbstract.Should().BeFalse();
        }

        [Test]
        public void It_includes_correct_resource_version()
        {
            _seeds.Should().AllSatisfy(s => s.ResourceVersion.Should().Be("5.0.0"));
        }

        [Test]
        public void It_includes_correct_project_name()
        {
            _seeds.Should().AllSatisfy(s => s.ProjectName.Should().Be("Ed-Fi"));
        }
    }

    [TestFixture]
    public class Given_Multiple_Projects : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var coreSchema = CreateMinimalSchema(
                projectName: "Ed-Fi",
                projectVersion: "5.0.0",
                projectEndpointName: "ed-fi",
                resourceSchemas: new JsonObject { ["students"] = CreateResourceSchema("Student") },
                abstractResources: new JsonObject()
            );

            var extensionSchema = CreateMinimalSchema(
                projectName: "TPDM",
                projectVersion: "1.0.0",
                projectEndpointName: "tpdm",
                isExtension: true,
                resourceSchemas: new JsonObject { ["candidates"] = CreateResourceSchema("Candidate") },
                abstractResources: new JsonObject()
            );

            var nodes = new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]);
            _seeds = _provider.GetSeeds(nodes);
        }

        [Test]
        public void It_sorts_by_project_then_resource_using_ordinal()
        {
            // Sorted by (ProjectName, ResourceName): Ed-Fi.Student, TPDM.Candidate
            _seeds.Should().HaveCount(2);
            _seeds[0].ProjectName.Should().Be("Ed-Fi");
            _seeds[0].ResourceName.Should().Be("Student");
            _seeds[1].ProjectName.Should().Be("TPDM");
            _seeds[1].ResourceName.Should().Be("Candidate");
        }

        [Test]
        public void It_includes_resources_from_all_projects()
        {
            _seeds.Select(s => s.ProjectName).Distinct().Should().HaveCount(2);
            _seeds.Select(s => s.ProjectName).Should().Contain("Ed-Fi");
            _seeds.Select(s => s.ProjectName).Should().Contain("TPDM");
        }

        [Test]
        public void It_preserves_project_specific_versions()
        {
            var edFiSeed = _seeds.Single(s => s.ProjectName == "Ed-Fi");
            edFiSeed.ResourceVersion.Should().Be("5.0.0");

            var tpdmSeed = _seeds.Single(s => s.ProjectName == "TPDM");
            tpdmSeed.ResourceVersion.Should().Be("1.0.0");
        }
    }

    [TestFixture]
    public class Given_Project_With_Resource_Extensions : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var coreSchema = CreateMinimalSchema(
                projectName: "Ed-Fi",
                resourceSchemas: new JsonObject { ["students"] = CreateResourceSchema("Student") },
                abstractResources: new JsonObject()
            );

            // Extension project with a resource extension (extends Student)
            var extensionResourceSchemas = new JsonObject
            {
                ["candidates"] = CreateResourceSchema("Candidate"),
                ["students"] = CreateResourceSchema("Student", isResourceExtension: true),
            };

            var extensionSchema = CreateMinimalSchema(
                projectName: "TPDM",
                projectVersion: "1.0.0",
                projectEndpointName: "tpdm",
                isExtension: true,
                resourceSchemas: extensionResourceSchemas,
                abstractResources: new JsonObject()
            );

            var nodes = new ApiSchemaDocumentNodes(coreSchema, [extensionSchema]);
            _seeds = _provider.GetSeeds(nodes);
        }

        [Test]
        public void It_excludes_resource_extensions_from_seed_list()
        {
            // Should only have Ed-Fi.Student and TPDM.Candidate, not the TPDM Student extension
            _seeds.Should().HaveCount(2);
            _seeds.Should().NotContain(s => s.ProjectName == "TPDM" && s.ResourceName == "Student");
        }

        [Test]
        public void It_includes_new_resources_from_extension()
        {
            _seeds.Should().Contain(s => s.ProjectName == "TPDM" && s.ResourceName == "Candidate");
        }
    }

    [TestFixture]
    public class Given_Same_Schema_Computed_Twice : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private byte[] _hash1 = null!;
        private byte[] _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var schema = CreateMinimalSchema(
                resourceSchemas: new JsonObject { ["students"] = CreateResourceSchema("Student") },
                abstractResources: new JsonObject()
            );

            var nodes1 = new ApiSchemaDocumentNodes(schema.DeepClone(), []);
            var nodes2 = new ApiSchemaDocumentNodes(schema.DeepClone(), []);

            var seeds1 = _provider.GetSeeds(nodes1);
            var seeds2 = _provider.GetSeeds(nodes2);

            _hash1 = _provider.ComputeSeedHash(seeds1);
            _hash2 = _provider.ComputeSeedHash(seeds2);
        }

        [Test]
        public void It_produces_identical_hashes()
        {
            _hash1.Should().BeEquivalentTo(_hash2);
        }

        [Test]
        public void It_returns_32_byte_sha256_hash()
        {
            _hash1.Should().HaveCount(32);
        }
    }

    [TestFixture]
    public class Given_Different_Schema_Content : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private byte[] _hash1 = null!;
        private byte[] _hash2 = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var schema1 = CreateMinimalSchema(
                resourceSchemas: new JsonObject { ["students"] = CreateResourceSchema("Student") },
                abstractResources: new JsonObject()
            );

            var schema2 = CreateMinimalSchema(
                resourceSchemas: new JsonObject
                {
                    ["students"] = CreateResourceSchema("Student"),
                    ["schools"] = CreateResourceSchema("School"),
                },
                abstractResources: new JsonObject()
            );

            var seeds1 = _provider.GetSeeds(new ApiSchemaDocumentNodes(schema1, []));
            var seeds2 = _provider.GetSeeds(new ApiSchemaDocumentNodes(schema2, []));

            _hash1 = _provider.ComputeSeedHash(seeds1);
            _hash2 = _provider.ComputeSeedHash(seeds2);
        }

        [Test]
        public void It_produces_different_hashes()
        {
            _hash1.Should().NotBeEquivalentTo(_hash2);
        }
    }

    [TestFixture]
    public class Given_Known_Fixture_Schema : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;
        private byte[] _hash = null!;

        // Locked expected hash for the inline fixture schema.
        // If canonicalization, manifest format, hash algorithm, or schema content changes,
        // this value must be updated intentionally.
        // NOTE: This hash uses LF line endings for cross-platform determinism.
        // Manifest format: {id}|{projectName}|{resourceName}|{resourceVersion}
        private const string ExpectedHashHex =
            "74805d4a2fed04970ca3b3e69569ad02bde531e4898a1c1a3f4c998a02749466";

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            // Inline fixture: 3 concrete resources + 1 abstract
            var resourceSchemas = new JsonObject
            {
                ["students"] = CreateResourceSchema("Student"),
                ["schools"] = CreateResourceSchema("School"),
                ["academicWeeks"] = CreateResourceSchema("AcademicWeek"),
            };

            var abstractResources = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject { ["identityJsonPaths"] = new JsonArray("$.id") },
            };

            var schema = CreateMinimalSchema(
                resourceSchemas: resourceSchemas,
                abstractResources: abstractResources
            );

            var nodes = new ApiSchemaDocumentNodes(schema, []);
            _seeds = _provider.GetSeeds(nodes);
            _hash = _provider.ComputeSeedHash(_seeds);
        }

        [Test]
        public void It_produces_expected_locked_hash()
        {
            var actualHashHex = Convert.ToHexStringLower(_hash);
            actualHashHex.Should().Be(ExpectedHashHex);
        }

        [Test]
        public void It_is_32_bytes()
        {
            _hash.Should().HaveCount(32);
        }

        [Test]
        public void It_produces_expected_seed_count()
        {
            // 3 concrete (AcademicWeek, School, Student) + 1 abstract (EducationOrganization) = 4
            _seeds.Should().HaveCount(4);
        }

        [Test]
        public void It_produces_expected_ordering()
        {
            // Sorted by (ProjectName, ResourceName) with ordinal comparison
            // Ed-Fi: AcademicWeek, EducationOrganization, School, Student
            _seeds[0].ResourceName.Should().Be("AcademicWeek");
            _seeds[1].ResourceName.Should().Be("EducationOrganization");
            _seeds[2].ResourceName.Should().Be("School");
            _seeds[3].ResourceName.Should().Be("Student");
        }
    }

    [TestFixture]
    public class Given_Empty_Schema : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;
        private byte[] _hash = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var schema = CreateMinimalSchema(
                resourceSchemas: new JsonObject(),
                abstractResources: new JsonObject()
            );

            var nodes = new ApiSchemaDocumentNodes(schema, []);
            _seeds = _provider.GetSeeds(nodes);
            _hash = _provider.ComputeSeedHash(_seeds);
        }

        [Test]
        public void It_returns_empty_seed_list()
        {
            _seeds.Should().BeEmpty();
        }

        [Test]
        public void It_returns_valid_hash_for_empty_manifest()
        {
            // Even with no seeds, should produce a valid 32-byte hash
            _hash.Should().HaveCount(32);
        }
    }

    [TestFixture]
    public class Given_Schema_With_Descriptors : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            var resourceSchemas = new JsonObject
            {
                ["students"] = CreateResourceSchema("Student"),
                ["gradeLevelDescriptors"] = CreateResourceSchema("GradeLevelDescriptor", isDescriptor: true),
            };

            var schema = CreateMinimalSchema(
                resourceSchemas: resourceSchemas,
                abstractResources: new JsonObject()
            );

            var nodes = new ApiSchemaDocumentNodes(schema, []);
            _seeds = _provider.GetSeeds(nodes);
        }

        [Test]
        public void It_includes_descriptors_in_seed_list()
        {
            _seeds.Should().HaveCount(2);
            _seeds.Select(s => s.ResourceName).Should().Contain("GradeLevelDescriptor");
        }
    }

    [TestFixture]
    public class Given_ResourceKeySeedHashVersion_Constant : ResourceKeySeedProviderTests
    {
        [Test]
        public void It_exists_and_is_non_empty()
        {
            SchemaHashConstants.ResourceKeySeedHashVersion.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void It_follows_expected_format()
        {
            SchemaHashConstants
                .ResourceKeySeedHashVersion.Should()
                .MatchRegex("^resource-key-seed-hash:v\\d+$");
        }

        [Test]
        public void It_is_v1()
        {
            SchemaHashConstants.ResourceKeySeedHashVersion.Should().Be("resource-key-seed-hash:v1");
        }
    }

    [TestFixture]
    public class Given_Schema_Exceeding_SmallInt_Limit : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private ApiSchemaDocumentNodes _nodes = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            // Generate 32768 resources to exceed the smallint max of 32767
            var resourceSchemas = new JsonObject();
            for (var i = 0; i < 32768; i++)
            {
                resourceSchemas[$"resource{i}"] = CreateResourceSchema($"Resource{i}");
            }

            var schema = CreateMinimalSchema(
                resourceSchemas: resourceSchemas,
                abstractResources: new JsonObject()
            );

            _nodes = new ApiSchemaDocumentNodes(schema, []);
        }

        [Test]
        public void It_throws_on_overflow()
        {
            var act = () => _provider.GetSeeds(_nodes);
            act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds maximum*");
        }
    }

    [TestFixture]
    public class Given_Schema_With_Duplicate_Resource_Names : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private ApiSchemaDocumentNodes _nodes = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            // Create a schema where a concrete and abstract resource share the same name
            var resourceSchemas = new JsonObject { ["students"] = CreateResourceSchema("Student") };

            var abstractResources = new JsonObject
            {
                ["Student"] = new JsonObject { ["identityJsonPaths"] = new JsonArray("$.id") },
            };

            var schema = CreateMinimalSchema(
                resourceSchemas: resourceSchemas,
                abstractResources: abstractResources
            );

            _nodes = new ApiSchemaDocumentNodes(schema, []);
        }

        [Test]
        public void It_throws_on_duplicate_project_and_resource_name()
        {
            var act = () => _provider.GetSeeds(_nodes);
            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Duplicate resource key seed*Ed-Fi*Student*");
        }
    }

    [TestFixture]
    public class Given_Case_Sensitive_Ordinal_Sorting : ResourceKeySeedProviderTests
    {
        private ResourceKeySeedProvider _provider = null!;
        private IReadOnlyList<ResourceKeySeed> _seeds = null!;

        [SetUp]
        public void Setup()
        {
            _provider = new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance);

            // Create resources with names that differ in case
            // Ordinal comparison: uppercase letters come before lowercase
            var resourceSchemas = new JsonObject
            {
                ["zebras"] = CreateResourceSchema("Zebra"),
                ["animals"] = CreateResourceSchema("Animal"),
                ["bears"] = CreateResourceSchema("bear"), // lowercase 'b'
            };

            var schema = CreateMinimalSchema(
                resourceSchemas: resourceSchemas,
                abstractResources: new JsonObject()
            );

            var nodes = new ApiSchemaDocumentNodes(schema, []);
            _seeds = _provider.GetSeeds(nodes);
        }

        [Test]
        public void It_sorts_using_ordinal_comparison()
        {
            // Ordinal: 'A' (65) < 'Z' (90) < 'b' (98)
            // So: Animal, Zebra, bear
            _seeds[0].ResourceName.Should().Be("Animal");
            _seeds[1].ResourceName.Should().Be("Zebra");
            _seeds[2].ResourceName.Should().Be("bear");
        }
    }
}
