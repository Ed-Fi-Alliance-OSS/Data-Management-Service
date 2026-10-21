// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;
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

    private List<string> WarningMessages() =>
        Fake.GetCalls(_logger)
            .Where(call =>
                call.Method.Name == nameof(ILogger.Log) && call.GetArgument<LogLevel>(0) == LogLevel.Warning
            )
            .Select(call => call.Arguments[2]!.ToString()!)
            .ToList();

    [TestFixture]
    public class Given_DiscoverFragmentFiles : ClaimsFragmentComposerTests
    {
        [Test]
        public void It_should_find_fragment_files_matching_pattern()
        {
            // Arrange
            var fragmentFiles = new[]
            {
                "sample-claims.json",
                "homograph-claims.json",
                "custom-extension-claims.json",
            };
            var nonFragmentFiles = new[]
            {
                "Claims.json", // Should be excluded (base file)
                "config.json", // Should be excluded (wrong pattern)
                "test-claims.txt", // Should be excluded (wrong extension)
                "legacy-claimset.json", // Should be excluded (retired suffix)
                "customclaims.json", // Should be excluded (no dash before the suffix)
            };

            foreach (var file in fragmentFiles.Concat(nonFragmentFiles))
            {
                File.WriteAllText(Path.Combine(_testFragmentsPath, file), "{}");
            }

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result, Does.Contain(Path.Combine(_testFragmentsPath, "sample-claims.json")));
            Assert.That(result, Does.Contain(Path.Combine(_testFragmentsPath, "homograph-claims.json")));
            Assert.That(
                result,
                Does.Contain(Path.Combine(_testFragmentsPath, "custom-extension-claims.json"))
            );
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "Claims.json")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "config.json")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "test-claims.txt")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "legacy-claimset.json")));
            Assert.That(result, Does.Not.Contain(Path.Combine(_testFragmentsPath, "customclaims.json")));
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

            File.WriteAllText(Path.Combine(_testFragmentsPath, "root-claims.json"), "{}");
            File.WriteAllText(Path.Combine(subDir, "sub-claims.json"), "{}");

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result, Does.Contain(Path.Combine(_testFragmentsPath, "root-claims.json")));
            Assert.That(result, Does.Contain(Path.Combine(subDir, "sub-claims.json")));
        }

        [Test]
        public void It_should_warn_about_each_file_with_the_retired_suffix()
        {
            // Arrange
            var subDir = Path.Combine(_testFragmentsPath, "subdirectory");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(_testFragmentsPath, "001-current-claims.json"), "{}");
            File.WriteAllText(Path.Combine(_testFragmentsPath, "002-old-claimset.json"), "{}");
            File.WriteAllText(Path.Combine(subDir, "003-nested-claimset.json"), "{}");

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            result.Should().Equal(Path.Combine(_testFragmentsPath, "001-current-claims.json"));
            List<string> warnings = WarningMessages();
            warnings.Should().HaveCount(2);
            warnings
                .Should()
                .ContainSingle(message =>
                    message.Contains(Path.Combine(_testFragmentsPath, "002-old-claimset.json"))
                );
            warnings
                .Should()
                .ContainSingle(message => message.Contains(Path.Combine(subDir, "003-nested-claimset.json")));
            warnings.Should().OnlyContain(message => message.Contains("must end in -claims.json"));
        }

        [Test]
        public void It_should_warn_and_return_no_fragments_when_only_retired_files_exist()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testFragmentsPath, "001-old-claimset.json"), "{}");

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            result.Should().BeEmpty();
            WarningMessages()
                .Should()
                .ContainSingle(message =>
                    message.Contains(Path.Combine(_testFragmentsPath, "001-old-claimset.json"))
                );
        }

        [Test]
        public void It_should_not_warn_when_every_file_uses_the_current_suffix()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testFragmentsPath, "001-current-claims.json"), "{}");
            File.WriteAllText(Path.Combine(_testFragmentsPath, "Claims.json"), "{}");

            // Act
            _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            WarningMessages().Should().BeEmpty();
        }

        [Test]
        public void It_should_return_files_in_sorted_order()
        {
            // Arrange
            var fragmentFiles = new[] { "z-claims.json", "a-claims.json", "m-claims.json" };
            foreach (var file in fragmentFiles)
            {
                File.WriteAllText(Path.Combine(_testFragmentsPath, file), "{}");
            }

            // Act
            var result = _composer.DiscoverFragmentFiles(_testFragmentsPath);

            // Assert
            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result[0], Does.EndWith("a-claims.json"));
            Assert.That(result[1], Does.EndWith("m-claims.json"));
            Assert.That(result[2], Does.EndWith("z-claims.json"));
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
            File.WriteAllText(Path.Combine(_testFragmentsPath, "test-claims.json"), fragmentContent);

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

            File.WriteAllText(Path.Combine(_testFragmentsPath, "extension1-claims.json"), fragment1);
            File.WriteAllText(Path.Combine(_testFragmentsPath, "extension2-claims.json"), fragment2);

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
                Path.Combine(_testFragmentsPath, "bad-claims.json"),
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
            File.WriteAllText(Path.Combine(_testFragmentsPath, "empty-claims.json"), fragmentContent);

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

    private const string ClaimUriPrefix = "http://ed-fi.org/identity/claims/";

    // The shipped claims for a standard, loaded from the embedded Claims.json resource
    private static ClaimsDocument EmbeddedBaseDocument(string standardFolder)
    {
        Assembly assembly = typeof(ClaimsProvider).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Claims.Standards.{standardFolder}.Claims.json";

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not load embedded resource '{resourceName}'.");
        using StreamReader reader = new(stream);

        JsonObject claims = (
            JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Embedded Claims.json parsed to null.")
        ).AsObject();

        return new ClaimsDocument(claims["claimSets"]!, claims["claimsHierarchy"]!);
    }

    // A fragment with one non-parent entry per name, each granting Read on that resource claim
    private static string GrantFragment(string claimSetName, params string[] resourceClaimNames) =>
        $$"""
            {
              "name": "{{claimSetName}}",
              "resourceClaims": [
                {{string.Join(
                ",",
                resourceClaimNames.Select(name =>
                    $$"""
                    {
                      "name": "{{name}}",
                      "authorizationStrategyOverridesForCRUD": [
                        {
                          "actionName": "Read",
                          "authorizationStrategies": [{ "name": "NoFurtherAuthorizationRequired" }]
                        }
                      ]
                    }
                    """
                )
            )}}
              ]
            }
            """;

    // The claimSets entry for the claim set on the hierarchy node with the full claim URI, at any depth
    private static JsonObject? GrantOn(ClaimsLoadResult result, string claimUri, string claimSetName)
    {
        static JsonObject? Find(JsonNode? nodes, string claimUri)
        {
            foreach (JsonObject node in nodes?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (node["name"]?.GetValue<string>() == claimUri)
                {
                    return node;
                }

                if (Find(node["claims"], claimUri) is { } descendant)
                {
                    return descendant;
                }
            }

            return null;
        }

        return Find(result.Nodes!.ClaimsHierarchyNode, claimUri)
            ?["claimSets"]?.AsArray()
            .OfType<JsonObject>()
            .SingleOrDefault(claimSet => claimSet["name"]?.GetValue<string>() == claimSetName);
    }

    private static string[] ReadActionNames(JsonObject grant) =>
        [.. grant["actions"]!.AsArray().Select(action => action!["name"]!.GetValue<string>())];

    [TestFixture]
    public class Given_a_non_parent_fragment_with_an_undeclared_name : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("defined-claims.json", NonParentFragment("Fragment-Defined"))
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
            JsonObject grant = GrantOnAcademicWeek(_result, "Fragment-Defined")
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            grant["actions"]!
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
                ("declared-claims.json", NonParentFragment("Already-Declared"))
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
            _result = ComposeWithFragments(BaseDocument(), ("extension-claims.json", ParentOnlyFragment));
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
            _result = ComposeWithFragments(BaseDocument(), ("mixed-claims.json", MixedFragment));
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
            _result = ComposeWithFragments(BaseDocument(), ("unnamed-claims.json", NonParentFragment(null)));
        }

        [Test]
        public void It_registers_the_file_name_the_grant_is_attached_under()
        {
            ClaimSetsNamed(_result, "unnamed-claims").Should().ContainSingle();
            GrantOnAcademicWeek(_result, "unnamed-claims").Should().NotBeNull();
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
                ("a-claims.json", NonParentFragment("Shared-Defined")),
                ("b-claims.json", NonParentFragment("shared-defined"))
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
                ("b-claims.json", NonParentFragment("Second-Defined")),
                ("a-claims.json", NonParentFragment("First-Defined"))
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
                ("unmatched-claims.json", NonParentFragment("Unmatched-Defined", "ed-fi/notARealResources"))
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
                ("empty-claims.json", """{ "name": "Empty-Defined" }""")
            );
        }

        [Test]
        public void It_does_not_register_the_fragment_name()
        {
            ClaimSetNames(_result).Should().Equal("Base");
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_whose_file_name_contains_white_space
        : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                BaseDocument(),
                ("my vendor-claims.json", NonParentFragment(null)),
                ("valid-claims.json", NonParentFragment("Valid-Defined"))
            );
        }

        [Test]
        public void It_returns_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }

        [Test]
        public void It_does_not_register_the_invalid_name()
        {
            ClaimSetsNamed(_result, "my vendor-claims").Should().BeEmpty();
        }

        [Test]
        public void It_still_composes_the_other_fragments()
        {
            ClaimSetsNamed(_result, "Valid-Defined").Should().ContainSingle();
            GrantOnAcademicWeek(_result, "Valid-Defined").Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_whose_name_is_too_long : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;
        private readonly string _name = new('a', 257);

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(BaseDocument(), ("long-claims.json", NonParentFragment(_name)));
        }

        [Test]
        public void It_composes_without_registering_the_name()
        {
            _result.Failures.Should().BeEmpty();
            ClaimSetNames(_result).Should().Equal("Base");
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_whose_name_is_at_the_length_limit : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;
        private readonly string _name = new('a', 256);

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(BaseDocument(), ("limit-claims.json", NonParentFragment(_name)));
        }

        [Test]
        public void It_registers_the_claim_set()
        {
            _result.Failures.Should().BeEmpty();
            ClaimSetsNamed(_result, _name).Should().ContainSingle();
        }
    }

    [TestFixture]
    public class Given_a_non_parent_fragment_with_an_empty_name : ClaimsFragmentComposerTests
    {
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(BaseDocument(), ("empty-name-claims.json", NonParentFragment("")));
        }

        [Test]
        public void It_composes_without_registering_the_name()
        {
            _result.Failures.Should().BeEmpty();
            ClaimSetNames(_result).Should().Equal("Base");
        }

        [Test]
        public void It_leaves_the_empty_named_grant_for_claims_validation_to_reject()
        {
            GrantOnAcademicWeek(_result, "").Should().NotBeNull();
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
                ("defined-claims.json", NonParentFragment("Fragment-Defined"))
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

    [TestFixture]
    public class Given_grant_entries_named_by_DS61_claims_that_end_in_s : ClaimsFragmentComposerTests
    {
        private const string ClaimSetName = "Ds61-Claims-Ending-In-S";
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                EmbeddedBaseDocument("ds61"),
                (
                    "grants-claims.json",
                    GrantFragment(
                        ClaimSetName,
                        "ed-fi/studentPathMilestoneStatus",
                        "ed-fi/candidateDemographics"
                    )
                )
            );
        }

        [Test]
        public void It_grants_the_claim_set_on_studentPathMilestoneStatus()
        {
            JsonObject grant = GrantOn(
                    _result,
                    ClaimUriPrefix + "ed-fi/studentPathMilestoneStatus",
                    ClaimSetName
                )
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_grants_the_claim_set_on_candidateDemographics()
        {
            JsonObject grant = GrantOn(_result, ClaimUriPrefix + "ed-fi/candidateDemographics", ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_logs_no_unmatched_grant_warning()
        {
            _logger.VerifyNoLog(LogLevel.Warning, "matches no resource claim");
        }
    }

    [TestFixture("ds52")]
    [TestFixture("ds61")]
    public class Given_a_grant_entry_naming_a_domain_that_ends_in_s(string standardFolder)
        : ClaimsFragmentComposerTests
    {
        private const string ClaimSetName = "Domain-Ending-In-S";
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                EmbeddedBaseDocument(standardFolder),
                ("grants-claims.json", GrantFragment(ClaimSetName, "domains/systemDescriptors"))
            );
        }

        [Test]
        public void It_grants_the_claim_set_on_the_systemDescriptors_domain()
        {
            JsonObject grant = GrantOn(_result, ClaimUriPrefix + "domains/systemDescriptors", ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_logs_no_unmatched_grant_warning()
        {
            _logger.VerifyNoLog(LogLevel.Warning, "matches no resource claim");
        }
    }

    [TestFixture("ds52")]
    [TestFixture("ds61")]
    public class Given_grant_entries_named_by_plural_API_paths(string standardFolder)
        : ClaimsFragmentComposerTests
    {
        private const string ClaimSetName = "Plural-Api-Paths";
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                EmbeddedBaseDocument(standardFolder),
                (
                    "grants-claims.json",
                    GrantFragment(
                        ClaimSetName,
                        "ed-fi/schools",
                        "ed-fi/academicWeeks",
                        "ed-fi/localEducationAgencies"
                    )
                )
            );
        }

        [Test]
        public void It_grants_the_claim_set_on_school()
        {
            JsonObject grant = GrantOn(_result, ClaimUriPrefix + "ed-fi/school", ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_grants_the_claim_set_on_academicWeek()
        {
            JsonObject grant = GrantOn(_result, ClaimUriPrefix + "ed-fi/academicWeek", ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_grants_the_claim_set_on_localEducationAgency()
        {
            JsonObject grant = GrantOn(_result, ClaimUriPrefix + "ed-fi/localEducationAgency", ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_logs_no_unmatched_grant_warning()
        {
            _logger.VerifyNoLog(LogLevel.Warning, "matches no resource claim");
        }
    }

    [TestFixture]
    public class Given_a_grant_entry_whose_name_and_singular_form_are_both_claims
        : ClaimsFragmentComposerTests
    {
        private const string ClaimSetName = "Exact-Before-Singular";
        private const string StatisticsClaim = ClaimUriPrefix + "ed-fi/statistics";
        private const string StatisticClaim = ClaimUriPrefix + "ed-fi/statistic";
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            ClaimsDocument baseDocument = new(
                JsonNode.Parse("""[{ "claimSetName": "Base", "isSystemReserved": true }]""")!,
                JsonNode.Parse(
                    $$"""
                    [
                      {
                        "name": "http://ed-fi.org/identity/claims/domains/relationshipBasedData",
                        "claims": [{ "name": "{{StatisticClaim}}" }, { "name": "{{StatisticsClaim}}" }]
                      }
                    ]
                    """
                )!
            );

            _result = ComposeWithFragments(
                baseDocument,
                ("grants-claims.json", GrantFragment(ClaimSetName, "ed-fi/statistics"))
            );
        }

        [Test]
        public void It_grants_the_claim_set_on_the_exact_name()
        {
            JsonObject grant = GrantOn(_result, StatisticsClaim, ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_does_not_grant_the_claim_set_on_the_singular_form()
        {
            GrantOn(_result, StatisticClaim, ClaimSetName).Should().BeNull();
        }

        [Test]
        public void It_logs_no_unmatched_grant_warning()
        {
            _logger.VerifyNoLog(LogLevel.Warning, "matches no resource claim");
        }
    }

    [TestFixture("ds52")]
    [TestFixture("ds61")]
    public class Given_a_fragment_with_a_grant_entry_that_matches_no_claim(string standardFolder)
        : ClaimsFragmentComposerTests
    {
        private const string FragmentFile = "partly-unmatched-claims.json";
        private const string ClaimSetName = "Partly-Unmatched";
        private const string UnmatchedEntry = "ed-fi/notARealResources";
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _result = ComposeWithFragments(
                EmbeddedBaseDocument(standardFolder),
                (FragmentFile, GrantFragment(ClaimSetName, "ed-fi/academicWeeks", UnmatchedEntry))
            );
        }

        [Test]
        public void It_returns_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }

        [Test]
        public void It_still_applies_the_matching_entry()
        {
            JsonObject grant = GrantOn(_result, ClaimUriPrefix + "ed-fi/academicWeek", ClaimSetName)
                .Should()
                .BeOfType<JsonObject>()
                .Which;

            ReadActionNames(grant).Should().Equal("Read");
        }

        [Test]
        public void It_still_registers_the_claim_set()
        {
            ClaimSetsNamed(_result, ClaimSetName).Should().ContainSingle();
        }

        [Test]
        public void It_logs_one_warning_naming_the_fragment_claim_set_and_entry()
        {
            List<string> warnings =
            [
                .. _logger
                    .LoggedEntries()
                    .Where(entry =>
                        entry.Level == LogLevel.Warning && entry.Message.Contains("matches no resource claim")
                    )
                    .Select(entry => entry.Message),
            ];

            warnings.Should().ContainSingle();
            warnings[0].Should().Contain(FragmentFile);
            warnings[0].Should().Contain(ClaimSetName);
            warnings[0].Should().Contain(UnmatchedEntry);
        }
    }

    [TestFixture]
    public class Given_actions_with_and_without_authorization_strategies : ClaimsFragmentComposerTests
    {
        private const string ClaimSetName = "Strategy-Listing";
        private const string NewDomainClaim = ClaimUriPrefix + "domains/strategyListing";
        private ClaimsLoadResult _result = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            const string Actions = """
                [
                  { "actionName": "Read" },
                  { "actionName": "Update", "authorizationStrategies": [] },
                  {
                    "actionName": "Create",
                    "authorizationStrategies": [{ "name": "NoFurtherAuthorizationRequired" }]
                  }
                ]
                """;
            string fragment = $$"""
                {
                  "name": "{{ClaimSetName}}",
                  "resourceClaims": [
                    {
                      "isParent": true,
                      "name": "{{NewDomainClaim}}",
                      "_defaultAuthorizationStrategiesForCrud": {{Actions}}
                    },
                    {
                      "name": "ed-fi/academicWeeks",
                      "authorizationStrategyOverridesForCRUD": {{Actions}}
                    }
                  ]
                }
                """;
            _result = ComposeWithFragments(BaseDocument(), ("strategy-listing-claims.json", fragment));
        }

        private static JsonObject ActionNamed(JsonNode? actions, string actionName) =>
            actions
                .Should()
                .BeOfType<JsonArray>()
                .Which.OfType<JsonObject>()
                .Single(action => action["name"]?.GetValue<string>() == actionName);

        private static string[] StrategyNames(JsonObject action, string propertyName) =>
            [.. action[propertyName]!.AsArray().Select(strategy => strategy!["name"]!.GetValue<string>())];

        private JsonObject GrantAction(string actionName) =>
            ActionNamed(
                GrantOn(_result, AcademicWeekClaim, ClaimSetName).Should().BeOfType<JsonObject>().Which[
                    "actions"
                ],
                actionName
            );

        private JsonObject DefaultAction(string actionName) =>
            ActionNamed(
                _result
                    .Nodes!.ClaimsHierarchyNode.AsArray()
                    .OfType<JsonObject>()
                    .Single(claim => claim["name"]?.GetValue<string>() == NewDomainClaim)[
                        "defaultAuthorization"
                    ]
                    ?["actions"],
                actionName
            );

        [Test]
        public void It_returns_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }

        [Test]
        public void It_omits_the_overrides_of_a_grant_action_that_lists_no_strategies()
        {
            GrantAction("Read").ContainsKey("authorizationStrategyOverrides").Should().BeFalse();
        }

        [Test]
        public void It_omits_the_overrides_of_a_grant_action_that_lists_an_empty_strategy_array()
        {
            GrantAction("Update").ContainsKey("authorizationStrategyOverrides").Should().BeFalse();
        }

        [Test]
        public void It_keeps_the_overrides_of_a_grant_action_that_lists_strategies()
        {
            StrategyNames(GrantAction("Create"), "authorizationStrategyOverrides")
                .Should()
                .Equal("NoFurtherAuthorizationRequired");
        }

        [Test]
        public void It_omits_the_strategies_of_a_default_action_that_lists_none()
        {
            DefaultAction("Read").ContainsKey("authorizationStrategies").Should().BeFalse();
        }

        [Test]
        public void It_omits_the_strategies_of_a_default_action_that_lists_an_empty_array()
        {
            DefaultAction("Update").ContainsKey("authorizationStrategies").Should().BeFalse();
        }

        [Test]
        public void It_keeps_the_strategies_of_a_default_action_that_lists_some()
        {
            StrategyNames(DefaultAction("Create"), "authorizationStrategies")
                .Should()
                .Equal("NoFurtherAuthorizationRequired");
        }
    }
}
