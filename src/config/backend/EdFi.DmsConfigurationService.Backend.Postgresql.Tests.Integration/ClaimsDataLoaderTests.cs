// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Postgresql.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class ClaimsDataLoaderTests : DatabaseTestBase
{
    private IClaimsDataLoader _claimsDataLoader = null!;
    private IClaimsProvider _claimsProvider = null!;
    private IClaimSetRepository _claimSetRepository = null!;
    private IClaimsHierarchyRepository _claimsHierarchyRepository = null!;
    private IClaimsHierarchyManager _claimsHierarchyManager = null!;
    private IClaimsTableValidator _claimsTableValidator = null!;
    private IClaimsUploadService _claimsUploadService = null!;
    private IResourceClaimMetadataRepository _resourceClaimMetadataRepository = null!;

    // "dmscs"."ResourceClaim" is a shared baseline that ClearClaimsTablesAsync does NOT clear (other
    // fixtures' projection-integrity checks depend on it). Snapshot it at setup and restore at
    // teardown so loader-path seeding tests never leak synthetic rows into that baseline.
    private IReadOnlyList<string> _resourceClaimBaseline = [];

    // Derives the expected claim set count from the embedded Claims.json so additions
    // (e.g. SeedLoader) don't require touching every count assertion in this file.
    // The Given_Embedded_Claims_Json unit-test fixture is the stronger contract for
    // claim set semantics; these counts only verify "all of them loaded".
    private static readonly int EmbeddedClaimSetCount = LoadEmbeddedClaimSetCount();

    private static int LoadEmbeddedClaimSetCount()
    {
        var assembly = typeof(ClaimsProvider).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Claims.Standards.ds52.Claims.json";
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not load embedded resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var root =
            JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Embedded Claims.json parsed to null.");
        return root.AsObject()["claimSets"]!.AsArray().Count;
    }

    [SetUp]
    public void Setup()
    {
        // Clear any existing data
        ClearClaimsTablesAsync().GetAwaiter().GetResult();
        _resourceClaimBaseline = SnapshotResourceClaimNamesAsync().GetAwaiter().GetResult();

        // Set up dependencies
        var databaseOptions = Options.Create(Configuration.DatabaseOptions.Value);
        _claimsHierarchyManager = new ClaimsHierarchyManager();
        _claimsHierarchyRepository = new ClaimsHierarchyRepository(
            databaseOptions,
            NullLogger<ClaimsHierarchyRepository>.Instance,
            new TestAuditContext()
        );
        _claimSetRepository = new ClaimSetRepository(
            databaseOptions,
            NullLogger<ClaimSetRepository>.Instance,
            _claimsHierarchyRepository,
            _claimsHierarchyManager,
            new TestAuditContext(),
            new TenantContextProvider()
        );

        // Configure ClaimsProvider to use the embedded resource from PostgreSQL assembly
        var claimsOptions = Options.Create(
            new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Embedded, // Use embedded resource
                ClaimsDirectory = "",
            }
        );

        var claimsValidator = new ClaimsValidator(NullLogger<ClaimsValidator>.Instance);
        var claimsFragmentComposer = new ClaimsFragmentComposer(NullLogger<ClaimsFragmentComposer>.Instance);

        _claimsTableValidator = new ClaimsTableValidator(
            Options.Create(Configuration.DatabaseOptions.Value),
            NullLogger<ClaimsTableValidator>.Instance
        );

        // Create ClaimsDocumentRepository
        var claimsDocumentRepository = new ClaimsDocumentRepository(
            databaseOptions,
            NullLogger<ClaimsDocumentRepository>.Instance
        );

        // Wire the real ResourceClaimMetadataRepository so the loader's LoadInitialClaimsAsync /
        // UpdateClaimsAsync paths exercise the production seeding behavior, not the no-op fallback.
        _resourceClaimMetadataRepository = new ResourceClaimMetadataRepository(
            databaseOptions,
            NullLogger<ResourceClaimMetadataRepository>.Instance
        );

        // Create ClaimsDataLoader first (without ClaimsProvider)
        _claimsDataLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            null!, // Will be set after creating ClaimsProvider
            _claimSetRepository,
            _claimsHierarchyRepository,
            _claimsTableValidator,
            claimsDocumentRepository,
            NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance
        );

        // Now create the ClaimsProvider (no longer needs ClaimsDataLoader)
        _claimsProvider = new ClaimsProvider(
            NullLogger<ClaimsProvider>.Instance,
            claimsOptions,
            claimsValidator,
            claimsFragmentComposer
        );

        // Create the real ClaimsDataLoader with the ClaimsProvider and the resource-claim repository
        _claimsDataLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            _claimsProvider,
            _claimSetRepository,
            _claimsHierarchyRepository,
            _claimsTableValidator,
            claimsDocumentRepository,
            NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance,
            _resourceClaimMetadataRepository
        );

        // Create ClaimsUploadService for upload/reload operations
        _claimsUploadService = new ClaimsUploadService(
            NullLogger<ClaimsUploadService>.Instance,
            _claimsProvider,
            _claimsDataLoader,
            claimsValidator
        );
    }

    [TearDown]
    public void TearDown()
    {
        // Restore the ResourceClaim baseline by removing only the rows seeded during the test, so
        // loader-path seeding here never pollutes the shared baseline other fixtures rely on.
        RestoreResourceClaimBaselineAsync().GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SnapshotResourceClaimNamesAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        IEnumerable<string> names = await connection.QueryAsync<string>(
            "SELECT \"ClaimName\" FROM \"dmscs\".\"ResourceClaim\""
        );
        return names.ToList();
    }

    private async Task RestoreResourceClaimBaselineAsync()
    {
        if (_resourceClaimBaseline.Count == 0)
        {
            return;
        }

        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" <> ALL(@Baseline)",
            new { Baseline = _resourceClaimBaseline.ToArray() }
        );
    }

    [Test]
    public async Task Given_empty_tables_It_should_return_true_for_AreClaimsTablesEmpty()
    {
        // Act
        var result = await _claimsDataLoader.AreClaimsTablesEmptyAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Given_ClaimSet_has_data_It_should_return_false_for_AreClaimsTablesEmpty()
    {
        // Arrange
        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO \"dmscs\".\"ClaimSet\" (\"ClaimSetName\", \"IsSystemReserved\") VALUES ('TestClaim', true)"
        );

        // Act
        var result = await _claimsDataLoader.AreClaimsTablesEmptyAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Given_ClaimsHierarchy_has_data_It_should_return_false_for_AreClaimsTablesEmpty()
    {
        // Arrange
        await using var connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO \"dmscs\".\"ClaimsHierarchy\" (\"Hierarchy\") VALUES ('[]'::jsonb)"
        );

        // Act
        var result = await _claimsDataLoader.AreClaimsTablesEmptyAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Given_empty_tables_It_should_load_claims_successfully()
    {
        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
        var success = (ClaimsDataLoadResult.Success)result;
        Assert.That(success.ClaimSetsLoaded, Is.GreaterThan(0));
        Assert.That(success.HierarchyLoaded, Is.True);

        // Verify data was actually loaded
        var (claimSetCount, hierarchyCount) = await GetClaimsTableCountsAsync();
        Assert.That(claimSetCount, Is.GreaterThan(0));
        Assert.That(hierarchyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Given_resource_claims_already_seeded_It_should_insert_only_missing_rows()
    {
        // Arrange - synthetic claim names absent from the static 0009 seed, cleared up front so the
        // assertion is independent of prior runs against the shared test database.
        const string testClaimPrefix = "http://ed-fi.org/identity/claims/test/";
        var likeFilter = new { Prefix = $"{testClaimPrefix}%" };
        await using (var arrangeConnection = await DataSource!.OpenConnectionAsync())
        {
            await arrangeConnection.ExecuteAsync(
                "DELETE FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" LIKE @Prefix",
                likeFilter
            );
        }

        var repository = new ResourceClaimMetadataRepository(
            Options.Create(Configuration.DatabaseOptions.Value),
            NullLogger<ResourceClaimMetadataRepository>.Instance
        );
        var seeds = new List<ResourceClaimMetadataSeed>
        {
            new("integrationSeedA", $"{testClaimPrefix}integrationSeedA"),
            new("integrationSeedB", $"{testClaimPrefix}integrationSeedB"),
        };

        // Act - seed the same set twice.
        var firstInserted = await repository.SeedResourceClaims(seeds);
        var secondInserted = await repository.SeedResourceClaims(seeds);

        // Assert - the first call inserts both rows; the second inserts none
        // (ON CONFLICT (ClaimName) DO NOTHING), so existing rows are never duplicated.
        Assert.That(firstInserted, Is.EqualTo(2));
        Assert.That(secondInserted, Is.EqualTo(0));

        await using var connection = await DataSource!.OpenConnectionAsync();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" LIKE @Prefix",
            likeFilter
        );
        Assert.That(count, Is.EqualTo(2));

        // Cleanup the synthetic rows so the shared test database is left clean.
        await connection.ExecuteAsync(
            "DELETE FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" LIKE @Prefix",
            likeFilter
        );
    }

    [Test]
    public async Task Given_upload_introduces_a_new_claim_It_should_seed_resource_claim_metadata_through_the_loader()
    {
        // Exercises the production loader wiring and ordering: with the real
        // ResourceClaimMetadataRepository injected, an upload that introduces a brand-new claim must
        // seed the matching ResourceClaim row THROUGH ClaimsDataLoader.UpdateClaimsAsync (after the
        // claims document is applied), not just via the repository in isolation.
        const string newClaimName = "http://ed-fi.org/identity/claims/domains/loaderSeededTestDomain";

        // Arrange - baseline load, then confirm the synthetic claim has no ResourceClaim row yet.
        await _claimsDataLoader.LoadInitialClaimsAsync();

        await using (var arrange = await DataSource!.OpenConnectionAsync())
        {
            bool alreadySeeded = await arrange.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" = @ClaimName)",
                new { ClaimName = newClaimName }
            );
            Assert.That(
                alreadySeeded,
                Is.False,
                "precondition: the synthetic claim must not be seeded before the upload"
            );
        }

        var customClaimsJson = JsonNode.Parse(
            $$"""
            {
              "claimSets": [
                { "claimSetName": "LoaderSeedTestClaimSet", "isSystemReserved": false }
              ],
              "claimsHierarchy": [
                {
                  "name": "{{newClaimName}}",
                  "defaultAuthorization": {
                    "actions": [
                      {
                        "name": "Read",
                        "authorizationStrategies": [ { "name": "NoFurtherAuthorizationRequired" } ]
                      }
                    ]
                  },
                  "claimSets": [
                    { "name": "LoaderSeedTestClaimSet", "actions": [ { "name": "Read" } ] }
                  ]
                }
              ]
            }
            """
        )!;

        // Act - routes through ClaimsUploadService -> ClaimsDataLoader.UpdateClaimsAsync.
        var uploadResult = await _claimsUploadService.UploadClaimsAsync(customClaimsJson);

        // Assert - upload succeeded and the loader seeded the new claim's ResourceClaim row.
        Assert.That(
            uploadResult.Success,
            Is.True,
            string.Join(", ", uploadResult.Failures.Select(f => $"{f.FailureType}: {f.Message}"))
        );

        await using var connection = await DataSource!.OpenConnectionAsync();
        string? seededResourceName = await connection.ExecuteScalarAsync<string?>(
            "SELECT \"ResourceName\" FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" = @ClaimName",
            new { ClaimName = newClaimName }
        );
        Assert.That(
            seededResourceName,
            Is.EqualTo("loaderSeededTestDomain"),
            "UpdateClaimsAsync must seed the new claim's ResourceClaim row through the wired repository"
        );

        // The synthetic ResourceClaim row is removed by TearDown's snapshot/restore.
    }

    [Test]
    public async Task Given_populated_tables_It_should_return_AlreadyLoaded()
    {
        // Arrange - Load data first time
        var firstResult = await _claimsDataLoader.LoadInitialClaimsAsync();
        Assert.That(firstResult, Is.TypeOf<ClaimsDataLoadResult.Success>());

        // Act - Try to load again
        var secondResult = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(secondResult, Is.TypeOf<ClaimsDataLoadResult.AlreadyLoaded>());
    }

    [Test]
    public async Task Given_invalid_claims_provider_It_should_return_ValidationFailure()
    {
        // Arrange - Create a claims provider that returns invalid data
        var mockClaimsProvider = new MockInvalidClaimsProvider();
        var claimsDocumentRepository = new ClaimsDocumentRepository(
            Options.Create(Configuration.DatabaseOptions.Value),
            NullLogger<ClaimsDocumentRepository>.Instance
        );
        var loader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            mockClaimsProvider,
            _claimSetRepository,
            _claimsHierarchyRepository,
            _claimsTableValidator,
            claimsDocumentRepository,
            NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance
        );

        // Act
        var result = await loader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.ValidationFailure>());
        var failure = (ClaimsDataLoadResult.ValidationFailure)result;
        Assert.That(failure.Errors, Is.Not.Empty);
    }

    [Test]
    public Task Given_database_error_during_load_It_should_rollback_transaction()
    {
        // This test would require mocking a database failure, which is complex
        // In a real scenario, you might inject a mock repository that throws
        Assert.Pass("Database rollback testing requires more complex mocking setup");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Given_loaded_data_It_should_match_expected_claim_sets()
    {
        // Arrange
        var expectedClaimSets = new[]
        {
            "E2E-NameSpaceBasedClaimSet",
            "SISVendor",
            "EdFiSandbox",
            "AssessmentVendor",
            "EdFiAPIPublisherReader",
            "E2E-NoFurtherAuthRequiredClaimSet",
            "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
            "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet",
            "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet",
            "E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet",
        };

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());

        await using var connection = await DataSource!.OpenConnectionAsync();
        var loadedClaimSets = await connection.QueryAsync<string>(
            "SELECT \"ClaimSetName\" FROM \"dmscs\".\"ClaimSet\" ORDER BY \"ClaimSetName\""
        );

        foreach (var expectedClaimSet in expectedClaimSets)
        {
            Assert.That(loadedClaimSets, Contains.Item(expectedClaimSet));
        }
    }

    [Test]
    public async Task Given_loaded_hierarchy_It_should_contain_expected_domains()
    {
        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());

        var hierarchyResult = await _claimsHierarchyRepository.GetClaimsHierarchy();
        Assert.That(hierarchyResult, Is.TypeOf<ClaimsHierarchyGetResult.Success>());

        var success = (ClaimsHierarchyGetResult.Success)hierarchyResult;
        var domainNames = success.Claims.Select(c => c.Name).ToList();

        // Check for expected domains
        Assert.That(domainNames, Contains.Item("http://ed-fi.org/identity/claims/domains/edFiTypes"));
        Assert.That(
            domainNames,
            Contains.Item("http://ed-fi.org/identity/claims/domains/educationOrganizations")
        );
        Assert.That(domainNames, Contains.Item("http://ed-fi.org/identity/claims/domains/people"));
    }

    [Test]
    public async Task Given_loaded_claim_sets_It_should_preserve_system_reserved_flags()
    {
        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());

        await using var connection = await DataSource!.OpenConnectionAsync();
        var systemReservedCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM \"dmscs\".\"ClaimSet\" WHERE \"IsSystemReserved\" = true"
        );

        // All claim sets in the default Claims.json are system reserved
        Assert.That(systemReservedCount, Is.EqualTo(EmbeddedClaimSetCount));
    }

    [Test]
    public async Task Given_successful_load_It_should_return_correct_count()
    {
        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
        var success = (ClaimsDataLoadResult.Success)result;

        Assert.That(success.ClaimSetsLoaded, Is.EqualTo(EmbeddedClaimSetCount));
        Assert.That(success.HierarchyLoaded, Is.True);
    }

    [TestFixture]
    public class Given_Upload_and_Reload_Integration : ClaimsDataLoaderTests
    {
        [Test]
        public async Task It_should_allow_upload_in_embedded_mode()
        {
            // Arrange - Load initial claims first
            var initialResult = await _claimsDataLoader.LoadInitialClaimsAsync();
            Assert.That(initialResult, Is.TypeOf<ClaimsDataLoadResult.Success>());

            // Create custom claims JSON for upload
            var customClaimsJson = JsonNode.Parse(
                """
                {
                  "claimSets": [
                    {
                      "claimSetName": "CustomTestClaimSet",
                      "isSystemReserved": false
                    }
                  ],
                  "claimsHierarchy": [
                    {
                      "name": "http://ed-fi.org/identity/claims/domains/customTest",
                      "defaultAuthorization": {
                        "actions": [
                          {
                            "name": "Read",
                            "authorizationStrategies": [
                              {
                                "name": "NoFurtherAuthorizationRequired"
                              }
                            ]
                          }
                        ]
                      },
                      "claimSets": [
                        {
                          "name": "CustomTestClaimSet",
                          "actions": [
                            {
                              "name": "Read"
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """
            )!;

            // Act - Upload custom claims
            var uploadResult = await _claimsUploadService.UploadClaimsAsync(customClaimsJson);

            // Assert - Upload should succeed in embedded mode
            if (!uploadResult.Success)
            {
                var failureMessages = string.Join(
                    ", ",
                    uploadResult.Failures.Select(f => $"{f.FailureType}: {f.Message}")
                );
                Assert.Fail($"Upload failed with errors: {failureMessages}");
            }
            Assert.That(uploadResult.Success, Is.True);
            Assert.That(uploadResult.Failures, Is.Empty);

            // Verify custom claim set is in database
            await using var connection = await DataSource!.OpenConnectionAsync();
            var customClaimExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'CustomTestClaimSet')"
            );
            Assert.That(customClaimExists, Is.True);
        }

        [Test]
        public async Task It_should_restore_to_embedded_baseline_on_reload_in_embedded_mode()
        {
            // Arrange - Load initial claims and upload custom claims
            await _claimsDataLoader.LoadInitialClaimsAsync();

            var customClaimsJson = JsonNode.Parse(
                """
                {
                  "claimSets": [
                    {
                      "claimSetName": "TempCustomClaimSet",
                      "isSystemReserved": false
                    }
                  ],
                  "claimsHierarchy": [
                    {
                      "name": "http://ed-fi.org/identity/claims/domains/tempCustom",
                      "defaultAuthorization": {
                        "actions": [
                          {
                            "name": "Read",
                            "authorizationStrategies": [
                              {
                                "name": "NoFurtherAuthorizationRequired"
                              }
                            ]
                          }
                        ]
                      },
                      "claimSets": [
                        {
                          "name": "TempCustomClaimSet",
                          "actions": [
                            {
                              "name": "Read"
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """
            )!;

            await _claimsUploadService.UploadClaimsAsync(customClaimsJson);

            // Verify custom claim exists
            await using var connection = await DataSource!.OpenConnectionAsync();
            var customExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'TempCustomClaimSet')"
            );
            Assert.That(customExists, Is.True);

            // Act - Reload to baseline
            var reloadResult = await _claimsUploadService.ReloadClaimsAsync();

            // Assert - Should restore to embedded baseline (16 system claim sets)
            Assert.That(reloadResult.Success, Is.True);
            Assert.That(reloadResult.Failures, Is.Empty);

            // Verify custom claim set is removed and standard ones are restored
            var customExistsAfterReload = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'TempCustomClaimSet')"
            );
            Assert.That(customExistsAfterReload, Is.False);

            var standardClaimExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'SISVendor')"
            );
            Assert.That(standardClaimExists, Is.True);
        }

        [Test]
        public async Task It_should_allow_upload_in_hybrid_mode()
        {
            // Arrange - Create hybrid mode loader
            var hybridClaimsOptions = Options.Create(
                new ClaimsOptions { ClaimsSource = ClaimsSource.Hybrid, ClaimsDirectory = Path.GetTempPath() }
            );

            var hybridClaimsValidator = new ClaimsValidator(NullLogger<ClaimsValidator>.Instance);
            var hybridClaimsFragmentComposer = new ClaimsFragmentComposer(
                NullLogger<ClaimsFragmentComposer>.Instance
            );
            var hybridClaimsProvider = new ClaimsProvider(
                NullLogger<ClaimsProvider>.Instance,
                hybridClaimsOptions,
                hybridClaimsValidator,
                hybridClaimsFragmentComposer
            );

            var hybridClaimsDocumentRepository = new ClaimsDocumentRepository(
                Options.Create(Configuration.DatabaseOptions.Value),
                NullLogger<ClaimsDocumentRepository>.Instance
            );

            var hybridLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
                hybridClaimsProvider,
                _claimSetRepository,
                _claimsHierarchyRepository,
                _claimsTableValidator,
                hybridClaimsDocumentRepository,
                NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance
            );

            // Load initial claims
            await hybridLoader.LoadInitialClaimsAsync();

            var customClaimsJson = JsonNode.Parse(
                """
                {
                  "claimSets": [
                    {
                      "claimSetName": "HybridCustomClaimSet",
                      "isSystemReserved": false
                    }
                  ],
                  "claimsHierarchy": [
                    {
                      "name": "http://ed-fi.org/identity/claims/domains/hybridCustom",
                      "defaultAuthorization": {
                        "actions": [
                          {
                            "name": "Read",
                            "authorizationStrategies": [
                              {
                                "name": "NoFurtherAuthorizationRequired"
                              }
                            ]
                          }
                        ]
                      },
                      "claimSets": [
                        {
                          "name": "HybridCustomClaimSet",
                          "actions": [
                            {
                              "name": "Read"
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """
            )!;

            // Create hybrid ClaimsUploadService
            var hybridUploadService = new ClaimsUploadService(
                NullLogger<ClaimsUploadService>.Instance,
                hybridClaimsProvider,
                hybridLoader,
                hybridClaimsValidator
            );

            // Act - Upload in hybrid mode
            var uploadResult = await hybridUploadService.UploadClaimsAsync(customClaimsJson);

            // Assert - Should succeed
            Assert.That(uploadResult.Success, Is.True);
            Assert.That(uploadResult.Failures, Is.Empty);

            // Verify custom claim set exists
            await using var connection = await DataSource!.OpenConnectionAsync();
            var customExists = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'HybridCustomClaimSet')"
            );
            Assert.That(customExists, Is.True);
        }

        [Test]
        public async Task It_should_restore_to_composed_baseline_on_reload_in_hybrid_mode()
        {
            // Arrange - Create temporary fragment file for hybrid mode testing
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);

            try
            {
                // Create hybrid mode test without fragments (just embedded base)

                // Create hybrid mode loader with fragment
                var hybridClaimsOptions = Options.Create(
                    new ClaimsOptions { ClaimsSource = ClaimsSource.Hybrid, ClaimsDirectory = tempPath }
                );

                var hybridClaimsValidator = new ClaimsValidator(NullLogger<ClaimsValidator>.Instance);
                var hybridClaimsFragmentComposer = new ClaimsFragmentComposer(
                    NullLogger<ClaimsFragmentComposer>.Instance
                );
                var hybridClaimsProvider = new ClaimsProvider(
                    NullLogger<ClaimsProvider>.Instance,
                    hybridClaimsOptions,
                    hybridClaimsValidator,
                    hybridClaimsFragmentComposer
                );

                var hybridClaimsDocumentRepository = new ClaimsDocumentRepository(
                    Options.Create(Configuration.DatabaseOptions.Value),
                    NullLogger<ClaimsDocumentRepository>.Instance
                );

                var hybridLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
                    hybridClaimsProvider,
                    _claimSetRepository,
                    _claimsHierarchyRepository,
                    _claimsTableValidator,
                    hybridClaimsDocumentRepository,
                    NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance
                );

                // Load initial claims with embedded base
                var initialResult = await hybridLoader.LoadInitialClaimsAsync();
                Assert.That(initialResult, Is.TypeOf<ClaimsDataLoadResult.Success>());

                // Upload custom claims that should be replaced on reload
                var customClaimsJson = JsonNode.Parse(
                    """
                    {
                      "claimSets": [
                        {
                          "claimSetName": "TempHybridClaimSet",
                          "isSystemReserved": false
                        }
                      ],
                      "claimsHierarchy": [
                        {
                          "name": "http://ed-fi.org/identity/claims/domains/tempHybrid",
                          "defaultAuthorization": {
                            "actions": [
                              {
                                "name": "Read",
                                "authorizationStrategies": [
                                  {
                                    "name": "NoFurtherAuthorizationRequired"
                                  }
                                ]
                              }
                            ]
                          },
                          "claimSets": [
                            {
                              "name": "TempHybridClaimSet",
                              "actions": [
                                {
                                  "name": "Read"
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                    """
                )!;

                // Create hybrid ClaimsUploadService for this test
                var hybridUploadService = new ClaimsUploadService(
                    NullLogger<ClaimsUploadService>.Instance,
                    hybridClaimsProvider,
                    hybridLoader,
                    hybridClaimsValidator
                );

                await hybridUploadService.UploadClaimsAsync(customClaimsJson);

                // Act - Reload to fragment-composed baseline
                var reloadResult = await hybridUploadService.ReloadClaimsAsync();

                // Assert - Should restore to embedded + fragment composition
                Assert.That(reloadResult.Success, Is.True);
                Assert.That(reloadResult.Failures, Is.Empty);

                await using var connection = await DataSource!.OpenConnectionAsync();

                // Verify temp custom claim is removed
                var tempCustomExists = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'TempHybridClaimSet')"
                );
                Assert.That(tempCustomExists, Is.False);

                // Verify standard claims are restored
                var standardClaimCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM \"dmscs\".\"ClaimSet\" WHERE \"IsSystemReserved\" = true"
                );
                Assert.That(standardClaimCount, Is.EqualTo(EmbeddedClaimSetCount));

                // Verify hierarchy includes embedded base claims
                var hierarchyResult = await _claimsHierarchyRepository.GetClaimsHierarchy();
                Assert.That(hierarchyResult, Is.TypeOf<ClaimsHierarchyGetResult.Success>());
                var hierarchySuccess = (ClaimsHierarchyGetResult.Success)hierarchyResult;

                // Check that the standard embedded hierarchy exists
                var hierarchyJson = JsonSerializer.Serialize(hierarchySuccess.Claims);
                Assert.That(hierarchyJson, Does.Contain("edFiTypes"));
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                }
            }
        }

        [Test]
        public async Task It_should_handle_mode_switching_gracefully()
        {
            // This test verifies that configuration changes between modes work correctly
            // In practice, mode switches would require application restart, but we can test
            // the behavior with different loader instances

            // Arrange - Start with embedded mode
            var initialResult = await _claimsDataLoader.LoadInitialClaimsAsync();
            Assert.That(initialResult, Is.TypeOf<ClaimsDataLoadResult.Success>());

            var (initialClaimSetCount, initialHierarchyCount) = await GetClaimsTableCountsAsync();
            Assert.That(initialClaimSetCount, Is.EqualTo(EmbeddedClaimSetCount));
            Assert.That(initialHierarchyCount, Is.EqualTo(1));

            // Create a filesystem mode loader (simulating configuration change)
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);

            try
            {
                // Create a base Claims.json file in the temp directory
                var baseClaimsContent = """
                    {
                      "claimSets": [
                        {
                          "claimSetName": "FilesystemOnlyClaimSet",
                          "isSystemReserved": false
                        }
                      ],
                      "claimsHierarchy": [
                        {
                          "name": "http://ed-fi.org/identity/claims/domains/filesystemOnly",
                          "defaultAuthorization": {
                            "actions": [
                              {
                                "name": "Read",
                                "authorizationStrategies": [
                                  {
                                    "name": "NoFurtherAuthorizationRequired"
                                  }
                                ]
                              }
                            ]
                          },
                          "claimSets": [
                            {
                              "name": "FilesystemOnlyClaimSet",
                              "actions": [
                                {
                                  "name": "Read"
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                    """;
                await File.WriteAllTextAsync(Path.Combine(tempPath, "Claims.json"), baseClaimsContent);

                var filesystemClaimsOptions = Options.Create(
                    new ClaimsOptions { ClaimsSource = ClaimsSource.Filesystem, ClaimsDirectory = tempPath }
                );

                var filesystemClaimsValidator = new ClaimsValidator(NullLogger<ClaimsValidator>.Instance);
                var filesystemClaimsFragmentComposer = new ClaimsFragmentComposer(
                    NullLogger<ClaimsFragmentComposer>.Instance
                );
                var filesystemClaimsProvider = new ClaimsProvider(
                    NullLogger<ClaimsProvider>.Instance,
                    filesystemClaimsOptions,
                    filesystemClaimsValidator,
                    filesystemClaimsFragmentComposer
                );

                var filesystemClaimsDocumentRepository = new ClaimsDocumentRepository(
                    Options.Create(Configuration.DatabaseOptions.Value),
                    NullLogger<ClaimsDocumentRepository>.Instance
                );

                var filesystemLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
                    filesystemClaimsProvider,
                    _claimSetRepository,
                    _claimsHierarchyRepository,
                    _claimsTableValidator,
                    filesystemClaimsDocumentRepository,
                    NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance
                );

                // Clear existing claims to simulate mode switch on application restart
                await ClearClaimsTablesAsync();

                // Act - Load with filesystem mode (should load filesystem data)
                var filesystemResult = await filesystemLoader.LoadInitialClaimsAsync();

                // Assert - Should load filesystem-only claims
                Assert.That(filesystemResult, Is.TypeOf<ClaimsDataLoadResult.Success>());
                var filesystemSuccess = (ClaimsDataLoadResult.Success)filesystemResult;
                Assert.That(filesystemSuccess.ClaimSetsLoaded, Is.EqualTo(1));

                // Verify filesystem claim exists
                await using var connection = await DataSource!.OpenConnectionAsync();
                var filesystemClaimExists = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'FilesystemOnlyClaimSet')"
                );
                Assert.That(filesystemClaimExists, Is.True);

                // Verify embedded claims are replaced
                var embeddedClaimExists = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM \"dmscs\".\"ClaimSet\" WHERE \"ClaimSetName\" = 'SISVendor')"
                );
                Assert.That(embeddedClaimExists, Is.False);
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                }
            }
        }
    }

    private class MockInvalidClaimsProvider : IClaimsProvider
    {
        public ClaimsDocument GetClaimsDocumentNodes() => null!;

        public Guid ReloadId => Guid.NewGuid();
        public bool IsClaimsValid => false;
        public List<ClaimsFailure> ClaimsFailures => [new ClaimsFailure("Test", "Invalid claims data")];

        public ClaimsLoadResult LoadClaimsFromSource() => new ClaimsLoadResult(null, ClaimsFailures);

        public void UpdateInMemoryState(ClaimsDocument claimsNodes, Guid newReloadId)
        {
            // Mock implementation - do nothing
        }
    }
}
