// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class ClaimsFragmentComposerTests
{
    private ILogger<ClaimsFragmentComposer> _logger = null!;
    private ClaimsFragmentComposer _composer = null!;
    private string _testFragmentsPath = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<ClaimsFragmentComposer>>();
        _composer = new ClaimsFragmentComposer(_logger);
        _testFragmentsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testFragmentsPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testFragmentsPath))
        {
            Directory.Delete(_testFragmentsPath, true);
        }
    }

    [TestFixture]
    public class Given_DiscoverFragmentFiles : ClaimsFragmentComposerTests
    {
        [Test]
        public void It_should_find_fragment_files_matching_pattern()
        {
            // Arrange
            var fragmentFiles = new[]
            {
                "sample-claimset.json",
                "homograph-claimset.json",
                "custom-extension-claimset.json",
            };
            var nonFragmentFiles = new[]
            {
                "Claims.json", // Should be excluded (base file)
                "config.json", // Should be excluded (wrong pattern)
                "test-claims.txt", // Should be excluded (wrong extension)
            };

            foreach (var file in fragmentFiles.Concat(nonFragmentFiles))
            {
                File.WriteAllText(Path.Combine(_testFragmentsPath, file), "{}");
            }

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result, Does.Contain(Path.Combine(_testFragmentsPath, "sample-claimset.json")));
            Assert.That(result, Does.Contain(Path.Combine(_testFragmentsPath, "homograph-claimset.json")));
            Assert.That(
                result,
                Does.Contain(Path.Combine(_testFragmentsPath, "custom-extension-claimset.json"))
            );
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "Claims.json")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "config.json")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "test-claims.txt")));
        }

        [Test]
        public void It_should_return_empty_list_when_no_fragments_found()
        {
            // Arrange - Empty directory

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void It_should_return_empty_list_when_directory_does_not_exist()
        {
            // Arrange
            var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            // Act
            var result = _composer.DiscoverFragmentFiles(nonExistentPath);

            // Assert
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void It_should_find_fragments_in_subdirectories()
        {
            // Arrange
            var subDir = Path.Combine(_testFragmentsPath, "subdirectory");
            Directory.CreateDirectory(subDir);

            File.WriteAllText(Path.Combine(_testFragmentsPath, "root-claimset.json"), "{}");
            File.WriteAllText(Path.Combine(subDir, "sub-claimset.json"), "{}");

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result, Does.Contain(Path.Combine(_testFragmentsPath, "root-claimset.json")));
            Assert.That(result, Does.Contain(Path.Combine(subDir, "sub-claimset.json")));
        }

        [Test]
        public void It_should_return_files_in_sorted_order()
        {
            // Arrange
            var fragmentFiles = new[] { "z-claimset.json", "a-claimset.json", "m-claimset.json" };
            foreach (var file in fragmentFiles)
            {
                File.WriteAllText(Path.Combine(_testFragmentsPath, file), "{}");
            }

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result[0], Does.EndWith("a-claimset.json"));
            Assert.That(result[1], Does.EndWith("m-claimset.json"));
            Assert.That(result[2], Does.EndWith("z-claimset.json"));
        }
    }

    [TestFixture]
    public class Given_ComposeClaimsFromFragments : ClaimsFragmentComposerTests
    {
        [Test]
        public void It_should_return_base_claims_when_no_fragments_found()
        {
            // Arrange
            var baseClaimSets = JsonNode.Parse("[{\"claimSetName\": \"Base\", \"isSystemReserved\": true}]")!;
            var baseHierarchy = JsonNode.Parse("[{\"name\": \"base-domain\"}]")!;
            var baseClaimsNodes = new ClaimsDocument(baseClaimSets, baseHierarchy);

            // Act
            var result = _composer.ComposeClaimsFromFragments(baseClaimsNodes, _testFragmentsPath);

            // Assert
            Assert.That(result.Nodes, Is.EqualTo(baseClaimsNodes));
            Assert.That(result.Failures, Is.Empty);
        }

        [Test]
        public void It_should_compose_claims_with_single_fragment()
        {
            // Arrange
            var baseClaimSets = JsonNode.Parse("[{\"claimSetName\": \"Base\", \"isSystemReserved\": true}]")!;
            var baseHierarchy = JsonNode.Parse(
                """
                [
                  {
                    "name": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                    "claims": [
                      {
                        "name": "http://ed-fi.org/identity/claims/ed-fi/academicSubjectDescriptor"
                      }
                    ]
                  }
                ]
                """
            )!;
            var baseClaimsNodes = new ClaimsDocument(baseClaimSets, baseHierarchy);

            // Create a fragment file
            var fragmentContent = """
                {
                  "name": "TestExtension",
                  "resourceClaims": [
                    {
                      "isParent": true,
                      "name": "domains/systemDescriptors",
                      "children": [
                        {
                          "name": "http://ed-fi.org/identity/claims/test/testDescriptor"
                        }
                      ]
                    }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(_testFragmentsPath, "test-claimset.json"), fragmentContent);

            // Act
            var result = _composer.ComposeClaimsFromFragments(baseClaimsNodes, _testFragmentsPath);

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);

            // Verify the composition worked - should have additional claims added
            var hierarchyArray = result.Nodes!.ClaimsHierarchyNode.AsArray();
            var systemDescriptorsDomain = hierarchyArray!.FirstOrDefault(n =>
                n?["name"]?.ToString().Contains("systemDescriptors") == true
            );
            Assert.That(systemDescriptorsDomain, Is.Not.Null);

            var claims = systemDescriptorsDomain!["claims"]?.AsArray();
            Assert.That(claims, Is.Not.Null);
            Assert.That(claims.Count, Is.GreaterThan(0)); // Should have the new claim
        }

        [Test]
        public void It_should_handle_multiple_fragments()
        {
            // Arrange
            var baseClaimSets = JsonNode.Parse("[{\"claimSetName\": \"Base\", \"isSystemReserved\": true}]")!;
            var baseHierarchy = JsonNode.Parse(
                """
                [
                  {
                    "name": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                    "claims": []
                  }
                ]
                """
            )!;
            var baseClaimsNodes = new ClaimsDocument(baseClaimSets, baseHierarchy);

            // Create multiple fragment files
            var fragment1 = """
                {
                  "name": "Extension1",
                  "resourceClaims": [
                    {
                      "isParent": true,
                      "name": "domains/systemDescriptors",
                      "children": [
                        {
                          "name": "http://ed-fi.org/identity/claims/ext1/descriptor1"
                        }
                      ]
                    }
                  ]
                }
                """;
            var fragment2 = """
                {
                  "name": "Extension2",
                  "resourceClaims": [
                    {
                      "isParent": true,
                      "name": "domains/systemDescriptors",
                      "children": [
                        {
                          "name": "http://ed-fi.org/identity/claims/ext2/descriptor2"
                        }
                      ]
                    }
                  ]
                }
                """;

            File.WriteAllText(Path.Combine(_testFragmentsPath, "extension1-claimset.json"), fragment1);
            File.WriteAllText(Path.Combine(_testFragmentsPath, "extension2-claimset.json"), fragment2);

            // Act
            var result = _composer.ComposeClaimsFromFragments(baseClaimsNodes, _testFragmentsPath);

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);

            // Verify both extensions were applied
            var hierarchyArray = result.Nodes!.ClaimsHierarchyNode.AsArray();
            var systemDescriptorsDomain = hierarchyArray!.FirstOrDefault(n =>
                n?["name"]?.ToString().Contains("systemDescriptors") == true
            );
            Assert.That(systemDescriptorsDomain, Is.Not.Null);

            var claims = systemDescriptorsDomain!["claims"]?.AsArray();
            Assert.That(claims, Is.Not.Null);
            Assert.That(claims.Count, Is.GreaterThan(0)); // Should have claims from both extensions
        }

        [Test]
        public void It_should_handle_malformed_fragment_gracefully()
        {
            // Arrange
            var baseClaimSets = JsonNode.Parse("[{\"claimSetName\": \"Base\"}]")!;
            var baseHierarchy = JsonNode.Parse("[{\"name\": \"base\"}]")!;
            var baseClaimsNodes = new ClaimsDocument(baseClaimSets, baseHierarchy);

            // Create a malformed fragment file
            File.WriteAllText(
                Path.Combine(_testFragmentsPath, "bad-claimset.json"),
                "{ \"name\": invalid json without quotes }"
            );

            // Act
            var result = _composer.ComposeClaimsFromFragments(baseClaimsNodes, _testFragmentsPath);

            // Assert
            Assert.That(result.Nodes, Is.Null);
            Assert.That(result.Failures, Is.Not.Empty);
            Assert.That(result.Failures[0].FailureType, Is.EqualTo("JsonError"));
        }

        [Test]
        public void It_should_handle_missing_resource_claims_in_fragment()
        {
            // Arrange
            var baseClaimSets = JsonNode.Parse("[{\"claimSetName\": \"Base\"}]")!;
            var baseHierarchy = JsonNode.Parse("[{\"name\": \"base\"}]")!;
            var baseClaimsNodes = new ClaimsDocument(baseClaimSets, baseHierarchy);

            // Create fragment without resourceClaims
            var fragmentContent = """
                {
                  "name": "EmptyExtension"
                }
                """;
            File.WriteAllText(Path.Combine(_testFragmentsPath, "empty-claimset.json"), fragmentContent);

            // Act
            var result = _composer.ComposeClaimsFromFragments(baseClaimsNodes, _testFragmentsPath);

            // Assert - Should succeed but not change anything
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
            // Since fragment had no resourceClaims, hierarchy should remain unchanged in structure
            // (though JSON serialization might differ due to transformation)
            var resultHierarchy = result.Nodes.ClaimsHierarchyNode.AsArray();
            Assert.That(resultHierarchy, Is.Not.Null);
            Assert.That(resultHierarchy.Count, Is.EqualTo(1));
            Assert.That(resultHierarchy[0]?["name"]?.ToString(), Is.EqualTo("base"));
        }
    }

    private const string AcademicWeekClaim = "http://ed-fi.org/identity/claims/ed-fi/academicWeek";

    private static ClaimsDocument BaseDocument(
        string claimSetsJson =
            """
                [{ "claimSetName": "Base", "isSystemReserved": true }]
                """
    ) =>
        new(
            JsonNode.Parse(claimSetsJson)!,
            JsonNode.Parse(
                $$"""
                [
                  {
                    "name": "http://ed-fi.org/identity/claims/domains/relationshipBasedData",
                    "claims": [{ "name": "{{AcademicWeekClaim}}" }]
                  }
                ]
                """
            )!
        );

    // A fragment whose non-parent entry grants Read on academicWeeks under the fragment's name
    private static string NonParentFragment(string? name, string resourceClaimName = "ed-fi/academicWeeks")
    {
        string nameProperty = name is null ? "" : $"\"name\": \"{name}\",";
        return $$"""
            {
              {{nameProperty}}
              "resourceClaims": [
                {
                  "name": "{{resourceClaimName}}",
                  "authorizationStrategyOverridesForCRUD": [
                    {
                      "actionName": "Read",
                      "authorizationStrategies": [{ "name": "NoFurtherAuthorizationRequired" }]
                    }
                  ]
                }
              ]
            }
            """;
    }

    private const string ParentOnlyFragment = """
        {
          "name": "ParentOnlyExtensionClaims",
          "resourceClaims": [
            {
              "isParent": true,
              "name": "domains/relationshipBasedData",
              "children": [{ "name": "http://ed-fi.org/identity/claims/sample/bus" }]
            }
          ]
        }
        """;

    private ClaimsLoadResult ComposeWithFragments(
        ClaimsDocument baseDocument,
        params (string FileName, string Content)[] fragments
    )
    {
        foreach ((string fileName, string content) in fragments)
        {
            File.WriteAllText(Path.Combine(_testFragmentsPath, fileName), content);
        }

        return _composer.ComposeClaimsFromFragments(baseDocument, _testFragmentsPath);
    }

    private static List<JsonObject> ClaimSetsNamed(ClaimsLoadResult result, string claimSetName) =>
        result
            .Nodes!.ClaimSetsNode.AsArray()
            .OfType<JsonObject>()
            .Where(claimSet =>
                string.Equals(
                    claimSet["claimSetName"]?.GetValue<string>(),
                    claimSetName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

    private static List<string> ClaimSetNames(ClaimsLoadResult result) =>
        [
            .. result
                .Nodes!.ClaimSetsNode.AsArray()
                .OfType<JsonObject>()
                .Select(claimSet => claimSet["claimSetName"]!.GetValue<string>()),
        ];

    private static JsonObject? GrantOnAcademicWeek(ClaimsLoadResult result, string claimSetName)
    {
        JsonObject academicWeek = result
            .Nodes!.ClaimsHierarchyNode.AsArray()
            .OfType<JsonObject>()
            .SelectMany(domain => domain["claims"]?.AsArray().OfType<JsonObject>() ?? [])
            .Single(claim => claim["name"]?.GetValue<string>() == AcademicWeekClaim);

        return academicWeek["claimSets"]
            ?.AsArray()
            .OfType<JsonObject>()
            .SingleOrDefault(claimSet => claimSet["name"]?.GetValue<string>() == claimSetName);
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_with_an_undeclared_name : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("defined-claimset.json", NonParentFragment("Fragment-Defined"))
            );
        }

        [Test]
        public void It_returns_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }

        [Test]
        public void It_registers_the_fragment_name_as_a_system_reserved_claim_set()
        {
            JsonObject claimSet = ClaimSetsNamed(_result, "Fragment-Defined").Should().ContainSingle().Which;

            claimSet["claimSetName"]!.GetValue<string>().Should().Be("Fragment-Defined");
            claimSet["isSystemReserved"]!.GetValue<bool>().Should().BeTrue();
        }

        [Test]
        public void It_keeps_the_base_claim_sets()
        {
            ClaimSetNames(_result).Should().Equal("Base", "Fragment-Defined");
        }

        [Test]
        public void It_attaches_the_grant_under_the_registered_name()
        {
            JsonObject? grant = GrantOnAcademicWeek(_result, "Fragment-Defined");

            grant.Should().NotBeNull();
            grant!["actions"]!
                .AsArray()
                .Select(action => action!["name"]!.GetValue<string>())
                .Should()
                .Equal("Read");
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_whose_name_is_already_declared : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(
                    """
                    [
                      { "claimSetName": "Base", "isSystemReserved": true },
                      { "claimSetName": "already-declared", "isSystemReserved": false }
                    ]
                    """
                ),
                ("declared-claimset.json", NonParentFragment("Already-Declared"))
            );
        }

        [Test]
        public void It_does_not_add_a_second_entry_for_a_case_variant_name()
        {
            ClaimSetsNamed(_result, "Already-Declared").Should().ContainSingle();
            ClaimSetNames(_result).Should().Equal("Base", "already-declared");
        }

        [Test]
        public void It_preserves_the_base_declaration()
        {
            JsonObject claimSet = ClaimSetsNamed(_result, "Already-Declared").Single();

            claimSet["claimSetName"]!.GetValue<string>().Should().Be("already-declared");
            claimSet["isSystemReserved"]!.GetValue<bool>().Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_a_parent_only_fragment : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(BaseDocument(), ("extension-claimset.json", ParentOnlyFragment));
        }

        [Test]
        public void It_does_not_register_the_fragment_label_as_a_claim_set()
        {
            _result.Failures.Should().BeEmpty();
            ClaimSetNames(_result).Should().Equal("Base");
        }
    }

    [TestFixture]
    public class Given_a_fragment_mixing_parent_and_non_parent_entries : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            const string MixedFragment = """
                {
                  "name": "Mixed-Defined",
                  "resourceClaims": [
                    {
                      "isParent": true,
                      "name": "domains/relationshipBasedData",
                      "children": [{ "name": "http://ed-fi.org/identity/claims/sample/bus" }]
                    },
                    {
                      "name": "ed-fi/academicWeeks",
                      "authorizationStrategyOverridesForCRUD": [{ "actionName": "Read" }]
                    },
                    {
                      "name": "ed-fi/academicWeeks",
                      "authorizationStrategyOverridesForCRUD": [{ "actionName": "Update" }]
                    }
                  ]
                }
                """;
            _result = ComposeWithFragments(BaseDocument(), ("mixed-claimset.json", MixedFragment));
        }

        [Test]
        public void It_registers_the_fragment_name_once()
        {
            ClaimSetsNamed(_result, "Mixed-Defined").Should().ContainSingle();
            ClaimSetNames(_result).Should().Equal("Base", "Mixed-Defined");
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_without_a_name : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("unnamed-claimset.json", NonParentFragment(null))
            );
        }

        [Test]
        public void It_registers_the_file_name_the_grant_is_attached_under()
        {
            ClaimSetsNamed(_result, "unnamed-claimset").Should().ContainSingle();
            GrantOnAcademicWeek(_result, "unnamed-claimset").Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_two_fragments_defining_the_same_name : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("a-claimset.json", NonParentFragment("Shared-Defined")),
                ("b-claimset.json", NonParentFragment("shared-defined"))
            );
        }

        [Test]
        public void It_registers_the_name_once_with_the_first_spelling()
        {
            JsonObject claimSet = ClaimSetsNamed(_result, "Shared-Defined").Should().ContainSingle().Which;

            claimSet["claimSetName"]!.GetValue<string>().Should().Be("Shared-Defined");
        }
    }

    [TestFixture]
    public class Given_fragments_defining_distinct_names : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("b-claimset.json", NonParentFragment("Second-Defined")),
                ("a-claimset.json", NonParentFragment("First-Defined"))
            );
        }

        [Test]
        public void It_appends_them_after_the_base_in_fragment_application_order()
        {
            ClaimSetNames(_result).Should().Equal("Base", "First-Defined", "Second-Defined");
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_that_matches_no_resource_claim : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("unmatched-claimset.json", NonParentFragment("Unmatched-Defined", "ed-fi/notARealResources"))
            );
        }

        [Test]
        public void It_still_registers_the_claim_set()
        {
            ClaimSetsNamed(_result, "Unmatched-Defined").Should().ContainSingle();
        }

        [Test]
        public void It_attaches_no_grant()
        {
            GrantOnAcademicWeek(_result, "Unmatched-Defined").Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_a_fragment_without_resource_claims : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("empty-claimset.json", """{ "name": "Empty-Defined" }""")
            );
        }

        [Test]
        public void It_does_not_register_the_fragment_name()
        {
            ClaimSetNames(_result).Should().Equal("Base");
        }
    }

    [TestFixture]
    public class Given_a_fragment_defines_a_claim_set : ClaimsFragmentComposerTests
    {
        private ClaimsDocument _baseDocument = null!;
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _baseDocument = BaseDocument();
            _result = ComposeWithFragments(
                _baseDocument,
                ("defined-claimset.json", NonParentFragment("Fragment-Defined"))
            );
        }

        [Test]
        public void It_does_not_modify_the_base_claim_sets()
        {
            _baseDocument
                .ClaimSetsNode.AsArray()
                .Select(claimSet => claimSet!["claimSetName"]!.GetValue<string>())
                .Should()
                .Equal("Base");
        }

        [Test]
        public void It_returns_a_claim_sets_node_distinct_from_the_base()
        {
            _result.Nodes!.ClaimSetsNode.Should().NotBeSameAs(_baseDocument.ClaimSetsNode);
        }
    }
}
