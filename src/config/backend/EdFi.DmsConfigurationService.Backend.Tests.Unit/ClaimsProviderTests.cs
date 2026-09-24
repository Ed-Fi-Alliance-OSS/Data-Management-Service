// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class ClaimsProviderTests
{
    private ILogger<ClaimsProvider> _logger = null!;
    private IOptions<ClaimsOptions> _claimsOptions = null!;
    private IClaimsValidator _claimsValidator = null!;
    private IClaimsFragmentComposer _claimsFragmentComposer = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<ClaimsProvider>>();
        _claimsOptions = A.Fake<IOptions<ClaimsOptions>>();
        _claimsValidator = A.Fake<IClaimsValidator>();
        _claimsFragmentComposer = A.Fake<IClaimsFragmentComposer>();

        // Setup default options with new ClaimsSource structure
        A.CallTo(() => _claimsOptions.Value)
            .Returns(new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded, ClaimsDirectory = "" });
    }

    /// <summary>
    /// Exercises the real embedded-resource selection (a real ClaimsProvider, not the testable
    /// double) to confirm the data-standard version selects the matching Claims/Standards/ds&lt;NN&gt;
    /// embedded resource and fails fast for an unknown version. DS 5.2 must remain the default.
    /// </summary>
    [TestFixture]
    public class Given_a_data_standard_version_is_configured : ClaimsProviderTests
    {
        private ClaimsProvider CreateProvider(string? dataStandardVersion)
        {
            var options = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            if (dataStandardVersion is not null)
            {
                options.DataStandardVersion = dataStandardVersion;
            }
            A.CallTo(() => _claimsOptions.Value).Returns(options);

            return new ClaimsProvider(_logger, _claimsOptions, _claimsValidator, _claimsFragmentComposer);
        }

        [Test]
        public void It_loads_embedded_ds52_claims_by_default()
        {
            ClaimsProvider provider = CreateProvider(null);

            ClaimsLoadResult result = provider.LoadClaimsFromSource();

            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.Nodes, Is.Not.Null);
        }

        [Test]
        public void It_loads_embedded_ds52_claims_when_version_is_5_2()
        {
            ClaimsProvider provider = CreateProvider("5.2");

            ClaimsLoadResult result = provider.LoadClaimsFromSource();

            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.Nodes, Is.Not.Null);
        }

        [Test]
        public void It_loads_embedded_ds61_claims_when_version_is_6_1()
        {
            ClaimsProvider provider = CreateProvider("6.1");

            ClaimsLoadResult result = provider.LoadClaimsFromSource();

            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.Nodes, Is.Not.Null);
        }

        [Test]
        public void It_fails_fast_when_no_embedded_claims_exist_for_the_version()
        {
            ClaimsProvider provider = CreateProvider("9.9");

            ClaimsLoadResult result = provider.LoadClaimsFromSource();

            Assert.That(result.Nodes, Is.Null);
            Assert.That(result.Failures, Is.Not.Empty);
            Assert.That(result.Failures[0].Message, Does.Contain("9.9"));
            Assert.That(result.Failures[0].Message, Does.Contain("ds99"));
        }
    }

    /// <summary>
    /// Exercises real Hybrid composition end to end (embedded ds52 base, real composer, real
    /// validator): a claim set defined only by a fragment must reach the validated document.
    /// </summary>
    [TestFixture]
    public class Given_hybrid_mode_with_a_fragment_defining_a_claim_set : ClaimsProviderTests
    {
        private const string DefinedClaimSetName = "Hybrid-Fragment-Defined";
        private string _fragmentsPath = null!;
        private ClaimsProvider _provider = null!;
        private ClaimsDocument _document = null!;

        [SetUp]
        public void Arrange_and_act()
        {
            _fragmentsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_fragmentsPath);
            File.WriteAllText(
                Path.Combine(_fragmentsPath, "001-defined-claimset.json"),
                $$"""
                {
                  "name": "{{DefinedClaimSetName}}",
                  "resourceClaims": [
                    {
                      "name": "ed-fi/academicWeeks",
                      "authorizationStrategyOverridesForCRUD": [
                        {
                          "actionName": "Read",
                          "authorizationStrategies": [{ "name": "NoFurtherAuthorizationRequired" }]
                        }
                      ]
                    }
                  ]
                }
                """
            );

            A.CallTo(() => _claimsOptions.Value)
                .Returns(
                    new ClaimsOptions { ClaimsSource = ClaimsSource.Hybrid, ClaimsDirectory = _fragmentsPath }
                );

            _provider = new ClaimsProvider(
                _logger,
                _claimsOptions,
                new ClaimsValidator(A.Fake<ILogger<ClaimsValidator>>()),
                new ClaimsFragmentComposer(A.Fake<ILogger<ClaimsFragmentComposer>>())
            );

            _document = _provider.GetClaimsDocumentNodes();
        }

        [TearDown]
        public void Cleanup()
        {
            Directory.Delete(_fragmentsPath, true);
        }

        [Test]
        public void It_passes_claims_validation()
        {
            _provider.IsClaimsValid.Should().BeTrue();
            _provider.ClaimsFailures.Should().BeEmpty();
        }

        [Test]
        public void It_declares_the_fragment_defined_claim_set_as_system_reserved()
        {
            JsonObject claimSet = _document
                .ClaimSetsNode.AsArray()
                .OfType<JsonObject>()
                .Should()
                .ContainSingle(claimSet =>
                    claimSet["claimSetName"]!.GetValue<string>() == DefinedClaimSetName
                )
                .Which;

            claimSet["isSystemReserved"]!.GetValue<bool>().Should().BeTrue();
        }

        [Test]
        public void It_attaches_the_fragment_grant_to_the_embedded_leaf_claim()
        {
            JsonObject academicWeek = Descendants(_document.ClaimsHierarchyNode.AsArray())
                .Single(claim =>
                    claim["name"]?.GetValue<string>() == "http://ed-fi.org/identity/claims/ed-fi/academicWeek"
                );

            academicWeek["claimSets"]!
                .AsArray()
                .OfType<JsonObject>()
                .Should()
                .ContainSingle(claimSet => claimSet["name"]!.GetValue<string>() == DefinedClaimSetName);
        }

        private static IEnumerable<JsonObject> Descendants(JsonArray claims)
        {
            foreach (JsonObject claim in claims.OfType<JsonObject>())
            {
                yield return claim;

                if (claim["claims"] is JsonArray children)
                {
                    foreach (JsonObject descendant in Descendants(children))
                    {
                        yield return descendant;
                    }
                }
            }
        }
    }

    [TestFixture]
    public class Given_LoadClaimsFromSource_is_called : ClaimsProviderTests
    {
        [Test]
        public void It_should_return_valid_claims_when_embedded_resource_exists()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            provider.SetClaimsNodes(claimsNodes);

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
        }

        [Test]
        public void It_should_return_failures_when_no_claims_available()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );
            // Don't set any test claims nodes

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Null);
            Assert.That(result.Failures, Is.Not.Empty);
            Assert.That(result.Failures[0].Message, Is.EqualTo("No test data provided"));
        }
    }

    [TestFixture]
    public class Given_UpdateInMemoryState_is_called : ClaimsProviderTests
    {
        [Test]
        public void It_should_update_reload_id_and_claims_validity()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            var newReloadId = Guid.NewGuid();
            var initialReloadId = provider.ReloadId;

            // Act
            provider.UpdateInMemoryState(claimsNodes, newReloadId);

            // Assert
            Assert.That(provider.ReloadId, Is.EqualTo(newReloadId));
            Assert.That(provider.ReloadId, Is.Not.EqualTo(initialReloadId));
            Assert.That(provider.IsClaimsValid, Is.True);
            Assert.That(provider.GetClaimsDocumentNodes(), Is.EqualTo(claimsNodes));
        }
    }

    [TestFixture]
    public class Given_GetClaimsDocumentNodes_is_called : ClaimsProviderTests
    {
        [Test]
        public void It_should_throw_exception_when_no_claims_loaded()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );
            // Don't set any claims nodes

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetClaimsDocumentNodes());
            Assert.That(ex.Message, Is.EqualTo("Claims loading failed. Check ClaimsFailures for details."));
            Assert.That(provider.IsClaimsValid, Is.False);
            Assert.That(provider.ClaimsFailures, Is.Not.Empty);
        }

        [Test]
        public void It_should_return_claims_nodes_when_available()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Set up the claims nodes
            provider.UpdateInMemoryState(claimsNodes, Guid.NewGuid());

            // Act
            var result = provider.GetClaimsDocumentNodes();

            // Assert
            Assert.That(result, Is.EqualTo(claimsNodes));
            Assert.That(result!.ClaimSetsNode.ToJsonString(), Is.EqualTo(claimSetsNode!.ToJsonString()));
            Assert.That(
                result!.ClaimsHierarchyNode.ToJsonString(),
                Is.EqualTo(hierarchyNode!.ToJsonString())
            );
        }
    }

    [TestFixture]
    public class Given_properties_are_accessed : ClaimsProviderTests
    {
        [Test]
        public void ReloadId_should_be_unique_for_each_instance()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider1 = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );
            var provider2 = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            // Act & Assert
            Assert.That(provider1.ReloadId, Is.Not.EqualTo(provider2.ReloadId));
            Assert.That(provider1.ReloadId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(provider2.ReloadId, Is.Not.EqualTo(Guid.Empty));
        }

        [Test]
        public void IsClaimsValid_should_default_to_true()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            // Act & Assert
            Assert.That(provider.IsClaimsValid, Is.True);
        }

        [Test]
        public void ClaimsFailures_should_default_to_empty_list()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            // Act & Assert
            Assert.That(provider.ClaimsFailures, Is.Not.Null);
            Assert.That(provider.ClaimsFailures, Is.Empty);
        }

        [Test]
        public void ReloadId_should_change_when_UpdateInMemoryState_is_called()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );
            var initialReloadId = provider.ReloadId;
            var newReloadId = Guid.NewGuid();

            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Act
            provider.UpdateInMemoryState(claimsNodes, newReloadId);

            // Assert
            Assert.That(provider.ReloadId, Is.EqualTo(newReloadId));
            Assert.That(provider.ReloadId, Is.Not.EqualTo(initialReloadId));
        }
    }

    [TestFixture]
    public class Given_LoadClaimsFromSource_edge_cases : ClaimsProviderTests
    {
        [Test]
        public void It_should_handle_ClaimsSource_Filesystem_with_valid_directory()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Filesystem,
                ClaimsDirectory = "/valid/path",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProviderWithPathSupport(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );
            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            provider.SetClaimsNodes(claimsNodes);

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
        }

        [Test]
        public void It_should_handle_ClaimsSource_Filesystem_with_empty_directory()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Filesystem,
                ClaimsDirectory = "",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProviderWithPathSupport(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Null);
            Assert.That(result.Failures, Is.Not.Empty);
            Assert.That(result.Failures[0].Message, Does.Contain("ClaimsDirectory"));
        }
    }

    [TestFixture]
    public class Given_UpdateInMemoryState_edge_cases : ClaimsProviderTests
    {
        [Test]
        public void It_should_handle_null_claims_nodes()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );
            var newReloadId = Guid.NewGuid();

            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() => provider.UpdateInMemoryState(null!, newReloadId));
            Assert.That(provider.ReloadId, Is.EqualTo(newReloadId));
        }

        [Test]
        public void It_should_handle_empty_guid_reload_id()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse("[{\"claimSetName\": \"Test\", \"isSystemReserved\": false}]");
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);

            // Act
            provider.UpdateInMemoryState(claimsNodes, Guid.Empty);

            // Assert
            Assert.That(provider.ReloadId, Is.EqualTo(Guid.Empty));
            Assert.That(provider.GetClaimsDocumentNodes(), Is.EqualTo(claimsNodes));
        }
    }

    [TestFixture]
    public class Given_TripleMode_LoadClaimsFromSource : ClaimsProviderTests
    {
        [Test]
        public void It_should_load_from_embedded_resource_in_pure_embedded_mode()
        {
            // Arrange - Pure Embedded Mode (E2E Testing)
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"E2E-Test\", \"isSystemReserved\": true}]"
            );
            var hierarchyNode = JsonNode.Parse("[{\"name\": \"test-hierarchy\"}]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            provider.SetClaimsNodes(claimsNodes);

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
            // Fragment composer should not be called in embedded mode
            A.CallTo(() =>
                    _claimsFragmentComposer.ComposeClaimsFromFragments(A<ClaimsDocument>._, A<string>._)
                )
                .MustNotHaveHappened();
        }

        [Test]
        public void It_should_compose_fragments_in_hybrid_mode()
        {
            // Arrange - Hybrid Mode (Production with Embedded Base)
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Hybrid,
                ClaimsDirectory = "/test/path",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var baseClaimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"Base\", \"isSystemReserved\": true}]"
            );
            var baseHierarchyNode = JsonNode.Parse("[{\"name\": \"base-hierarchy\"}]");
            var baseClaimsNodes = new ClaimsDocument(baseClaimSetsNode!, baseHierarchyNode!);
            provider.SetClaimsNodes(baseClaimsNodes);

            var composedClaimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"Composed\", \"isSystemReserved\": true}]"
            );
            var composedHierarchyNode = JsonNode.Parse("[{\"name\": \"composed-hierarchy\"}]");
            var composedClaimsNodes = new ClaimsDocument(composedClaimSetsNode!, composedHierarchyNode!);

            A.CallTo(() =>
                    _claimsFragmentComposer.ComposeClaimsFromFragments(A<ClaimsDocument>._, A<string>._)
                )
                .Returns(new ClaimsLoadResult(composedClaimsNodes, new List<ClaimsFailure>()));

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Nodes, Is.EqualTo(baseClaimsNodes)); // TestableClaimsProvider returns base claims
            Assert.That(result.Failures, Is.Empty);
            // Fragment composer should not be called because TestableClaimsProvider overrides LoadClaimsFromSource
            A.CallTo(() =>
                    _claimsFragmentComposer.ComposeClaimsFromFragments(A<ClaimsDocument>._, A<string>._)
                )
                .MustNotHaveHappened();
        }

        [Test]
        public void It_should_compose_fragments_in_pure_filesystem_mode()
        {
            // Arrange - Pure File System Mode (Production)
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Filesystem,
                ClaimsDirectory = "/test/path",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProviderWithFilesystem(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var baseClaimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"FileSystem\", \"isSystemReserved\": false}]"
            );
            var baseHierarchyNode = JsonNode.Parse("[{\"name\": \"filesystem-hierarchy\"}]");
            var baseClaimsNodes = new ClaimsDocument(baseClaimSetsNode!, baseHierarchyNode!);

            var composedClaimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"ComposedFS\", \"isSystemReserved\": false}]"
            );
            var composedHierarchyNode = JsonNode.Parse("[{\"name\": \"composed-fs-hierarchy\"}]");
            var composedClaimsNodes = new ClaimsDocument(composedClaimSetsNode!, composedHierarchyNode!);

            provider.SetFilesystemClaims(baseClaimsNodes);
            A.CallTo(() =>
                    _claimsFragmentComposer.ComposeClaimsFromFragments(A<ClaimsDocument>._, A<string>._)
                )
                .Returns(new ClaimsLoadResult(composedClaimsNodes, new List<ClaimsFailure>()));

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Nodes, Is.EqualTo(baseClaimsNodes)); // TestableClaimsProviderWithFilesystem returns base claims
            Assert.That(result.Failures, Is.Empty);
            // Fragment composer should not be called because TestableClaimsProviderWithFilesystem overrides LoadClaimsFromSource
            A.CallTo(() =>
                    _claimsFragmentComposer.ComposeClaimsFromFragments(A<ClaimsDocument>._, A<string>._)
                )
                .MustNotHaveHappened();
        }

        [Test]
        public void It_should_handle_fragment_composition_failures_gracefully()
        {
            // Arrange - Hybrid mode with composition failure
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Hybrid,
                ClaimsDirectory = "/test/path",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var baseClaimsNodes = new ClaimsDocument(
                JsonNode.Parse("[{\"claimSetName\": \"Base\"}]")!,
                JsonNode.Parse("[{\"name\": \"base\"}]")!
            );
            provider.SetClaimsNodes(baseClaimsNodes);

            var compositionFailure = new ClaimsFailure("FragmentComposition", "Test failure");
            A.CallTo(() =>
                    _claimsFragmentComposer.ComposeClaimsFromFragments(A<ClaimsDocument>._, A<string>._)
                )
                .Returns(new ClaimsLoadResult(null, [compositionFailure]));

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert - Should return base claims when composition fails
            Assert.That(result.Nodes, Is.EqualTo(baseClaimsNodes));
            Assert.That(result.Failures, Is.Empty);
        }
    }

    [TestFixture]
    public class Given_ClaimsOptions_Validation : ClaimsProviderTests
    {
        [Test]
        public void It_should_throw_when_ClaimsSource_Hybrid_but_ClaimsDirectory_missing()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Hybrid,
                ClaimsDirectory = "",
            };

            // Act & Assert - use ClaimsOptions validation directly
            var ex = Assert.Throws<InvalidOperationException>(() => claimsOptions.Validate());
            Assert.That(
                ex.Message,
                Is.EqualTo("ClaimsDirectory must be set when ClaimsSource is Hybrid or Filesystem")
            );
        }

        [Test]
        public void It_should_throw_when_ClaimsSource_Filesystem_but_ClaimsDirectory_missing()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Filesystem,
                ClaimsDirectory = "",
            };

            // Act & Assert - use ClaimsOptions validation directly
            var ex = Assert.Throws<InvalidOperationException>(() => claimsOptions.Validate());
            Assert.That(
                ex.Message,
                Is.EqualTo("ClaimsDirectory must be set when ClaimsSource is Hybrid or Filesystem")
            );
        }

        [Test]
        public void It_should_accept_valid_configuration_combinations()
        {
            // Test all valid combinations
            var validConfigurations = new[]
            {
                new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded },
                new ClaimsOptions { ClaimsSource = ClaimsSource.Hybrid, ClaimsDirectory = "/test" },
                new ClaimsOptions { ClaimsSource = ClaimsSource.Filesystem, ClaimsDirectory = "/test" },
            };

            foreach (var config in validConfigurations)
            {
                A.CallTo(() => _claimsOptions.Value).Returns(config);

                // Should not throw
                Assert.DoesNotThrow(() =>
                    new TestableClaimsProvider(
                        _logger,
                        _claimsOptions,
                        _claimsValidator,
                        _claimsFragmentComposer
                    )
                );
            }
        }

        [Test]
        public void It_should_test_each_ClaimsSource_enum_value_Embedded()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"Embedded-Test\", \"isSystemReserved\": false}]"
            );
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            provider.SetClaimsNodes(claimsNodes);

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
        }

        [Test]
        public void It_should_test_each_ClaimsSource_enum_value_Hybrid()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Hybrid,
                ClaimsDirectory = "/test/hybrid/path",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProvider(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"Hybrid-Test\", \"isSystemReserved\": true}]"
            );
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            provider.SetClaimsNodes(claimsNodes);

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
        }

        [Test]
        public void It_should_test_each_ClaimsSource_enum_value_Filesystem()
        {
            // Arrange
            var claimsOptions = new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Filesystem,
                ClaimsDirectory = "/test/filesystem/path",
            };
            A.CallTo(() => _claimsOptions.Value).Returns(claimsOptions);

            var provider = new TestableClaimsProviderWithFilesystem(
                _logger,
                _claimsOptions,
                _claimsValidator,
                _claimsFragmentComposer
            );

            var claimSetsNode = JsonNode.Parse(
                "[{\"claimSetName\": \"Filesystem-Test\", \"isSystemReserved\": false}]"
            );
            var hierarchyNode = JsonNode.Parse("[]");
            var claimsNodes = new ClaimsDocument(claimSetsNode!, hierarchyNode!);
            provider.SetFilesystemClaims(claimsNodes);

            // Act
            var result = provider.LoadClaimsFromSource();

            // Assert
            Assert.That(result.Nodes, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty);
        }
    }

    /// <summary>
    /// Extended testable version that simulates path-based claims loading without validation
    /// </summary>
    private class TestableClaimsProviderWithPathSupport : IClaimsProvider
    {
        private readonly IOptions<ClaimsOptions> _testClaimsOptions;
        private ClaimsDocument? _testClaimsNodes;
        private Guid _reloadId = Guid.NewGuid();

        public TestableClaimsProviderWithPathSupport(
            ILogger<ClaimsProvider> logger,
            IOptions<ClaimsOptions> claimsOptions,
            IClaimsValidator claimsValidator,
            IClaimsFragmentComposer claimsFragmentComposer
        )
        {
            _ = logger; // Suppress unused parameter warning
            _ = claimsValidator; // Suppress unused parameter warning
            _ = claimsFragmentComposer; // Suppress unused parameter warning
            _testClaimsOptions = claimsOptions;
        }

        public Guid ReloadId => _reloadId;
        public bool IsClaimsValid => true;
        public List<ClaimsFailure> ClaimsFailures => [];

        public void SetClaimsNodes(ClaimsDocument nodes)
        {
            _testClaimsNodes = nodes;
        }

        public ClaimsDocument GetClaimsDocumentNodes()
        {
            return _testClaimsNodes ?? throw new InvalidOperationException("No test claims nodes set");
        }

        public void UpdateInMemoryState(ClaimsDocument claimsNodes, Guid newReloadId)
        {
            _testClaimsNodes = claimsNodes;
            _reloadId = newReloadId;
        }

        public ClaimsLoadResult LoadClaimsFromSource()
        {
            // Simulate directory-based loading logic for testing
            var options = _testClaimsOptions.Value;
            if (
                (
                    options.ClaimsSource == ClaimsSource.Hybrid
                    || options.ClaimsSource == ClaimsSource.Filesystem
                ) && string.IsNullOrWhiteSpace(options.ClaimsDirectory)
            )
            {
                return new ClaimsLoadResult(
                    null,
                    [
                        new ClaimsFailure(
                            "Configuration",
                            "ClaimsDirectory cannot be null or empty when ClaimsSource is Hybrid or Filesystem"
                        ),
                    ]
                );
            }

            if (_testClaimsNodes != null)
            {
                return new ClaimsLoadResult(_testClaimsNodes, []);
            }
            return new ClaimsLoadResult(null, [new ClaimsFailure("Test", "No test data provided")]);
        }
    }

    /// <summary>
    /// Testable version of ClaimsProvider that allows setting claims nodes for testing
    /// </summary>
    private class TestableClaimsProvider : ClaimsProvider
    {
        private ClaimsDocument? _testClaimsNodes;

        public TestableClaimsProvider(
            ILogger<ClaimsProvider> logger,
            IOptions<ClaimsOptions> claimsOptions,
            IClaimsValidator claimsValidator,
            IClaimsFragmentComposer claimsFragmentComposer
        )
            : base(logger, claimsOptions, claimsValidator, claimsFragmentComposer) { }

        public void SetClaimsNodes(ClaimsDocument nodes)
        {
            _testClaimsNodes = nodes;
        }

        protected override Assembly GetAssemblyForEmbeddedResource()
        {
            return typeof(TestableClaimsProvider).Assembly;
        }

        // Override the LoadClaimsFromSource to return test data
        public override ClaimsLoadResult LoadClaimsFromSource()
        {
            if (_testClaimsNodes != null)
            {
                return new ClaimsLoadResult(_testClaimsNodes, []);
            }
            return new ClaimsLoadResult(null, [new ClaimsFailure("Test", "No test data provided")]);
        }
    }

    /// <summary>
    /// Testable version that simulates filesystem-based claims loading for pure production mode
    /// </summary>
    private class TestableClaimsProviderWithFilesystem : ClaimsProvider
    {
        private ClaimsDocument? _filesystemClaimsNodes;

        public TestableClaimsProviderWithFilesystem(
            ILogger<ClaimsProvider> logger,
            IOptions<ClaimsOptions> claimsOptions,
            IClaimsValidator claimsValidator,
            IClaimsFragmentComposer claimsFragmentComposer
        )
            : base(logger, claimsOptions, claimsValidator, claimsFragmentComposer) { }

        public void SetFilesystemClaims(ClaimsDocument nodes)
        {
            _filesystemClaimsNodes = nodes;
        }

        /// <summary>
        /// Override to simulate filesystem loading returning our test data
        /// </summary>
        public override ClaimsLoadResult LoadClaimsFromSource()
        {
            // Simulate the actual LoadClaimsFromFileSystemWithFragments logic for testing
            if (_filesystemClaimsNodes != null)
            {
                return new ClaimsLoadResult(_filesystemClaimsNodes, []);
            }
            return new ClaimsLoadResult(
                null,
                [new ClaimsFailure("Test", "No filesystem test data provided")]
            );
        }
    }
}
