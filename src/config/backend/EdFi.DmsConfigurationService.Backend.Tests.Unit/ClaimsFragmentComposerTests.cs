// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using FakeItEasy;
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
}
