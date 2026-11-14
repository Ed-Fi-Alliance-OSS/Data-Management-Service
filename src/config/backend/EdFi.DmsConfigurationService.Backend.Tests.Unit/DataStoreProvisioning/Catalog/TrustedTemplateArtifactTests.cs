// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using LogEntry = EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs.LogEntry;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;

/// <summary>
/// The create path's handle (6.2 step 8): a staged artifact carries the same pin resolution does, holds its
/// private directory until released, extracts the artifact once while verifying its hash, refuses a tampered
/// artifact as <c>ArtifactTampered</c> with the partial file gone, and reports what a release could not
/// delete as residue with generated resource ids, both from the handle and from a resolution whose release
/// fails, retrying the deletion on a later release.
/// </summary>
public class TrustedTemplateArtifactTests
{
    private static readonly string _otherHash = string.Concat(Enumerable.Repeat("ef", 32));

    private static TrustedTemplateArtifact StagedOf(TemplateArtifactStaging staging) =>
        staging.Should().BeOfType<TemplateArtifactStaging.Staged>().Subject.Artifact;

    [TestFixture]
    public class Given_a_staged_artifact_for_the_create_path
    {
        private CatalogWorkspace _workspace = null!;
        private TestTemplatePackage _package = null!;
        private DmsOperationId _operationId;
        private TrustedTemplateArtifact _artifact = null!;
        private DmsTrustedArtifactPin _resolvedPin = null!;
        private bool _directoryExistedWhileHeld;
        private StagedArtifact _extracted = null!;
        private IReadOnlyList<DmsCleanupResidue> _firstRelease = [];
        private IReadOnlyList<DmsCleanupResidue> _secondRelease = [];
        private Exception? _extractionAfterRelease;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            (_package, _) = _workspace.BuildBoth();
            TemplateArtifactCatalog catalog = _workspace.Catalog();
            DmsArtifactResolution resolution = await catalog.ResolveAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _resolvedPin = resolution.Should().BeOfType<DmsArtifactResolution.Resolved>().Subject.Pin;

            _operationId = DmsOperationId.New();
            _artifact = StagedOf(
                await catalog.StageAsync(DmsDatabaseTemplate.Minimal, _operationId, CancellationToken.None)
            );
            _directoryExistedWhileHeld = Directory.Exists(_workspace.OperationDirectoryPathOf(_operationId));
            _extracted = await _artifact.ExtractArtifactAsync(CancellationToken.None);
            _firstRelease = _artifact.Release();
            _secondRelease = _artifact.Release();
            _extractionAfterRelease = await Record.ExceptionAsync(() =>
                _artifact.ExtractArtifactAsync(CancellationToken.None)
            );
        }

        [TearDown]
        public void TearDown()
        {
            _artifact.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_carries_the_pin_resolution_returns() => _artifact.Pin.Should().Be(_resolvedPin);

        [Test]
        public void It_names_the_operation_and_the_producer()
        {
            _artifact.OperationId.Should().Be(_operationId);
            _artifact.Producer.Should().Be(_package.ProducerName);
        }

        [Test]
        public void It_holds_the_operation_directory_until_released()
        {
            _directoryExistedWhileHeld.Should().BeTrue();
            _artifact.OperationDirectoryPath.Should().Be(_workspace.OperationDirectoryPathOf(_operationId));
        }

        [Test]
        public void It_extracts_the_artifact_into_the_operation_directory()
        {
            using AssertionScope scope = new();
            _extracted
                .FilePath.Should()
                .Be(Path.Combine(_workspace.OperationDirectoryPathOf(_operationId), "artifact.sql"));
            _extracted.Sha256.Should().Be(_package.ArtifactSha256);
            _extracted.Kind.Should().Be(DmsArtifactKind.PostgresqlSqlDump);
        }

        [Test]
        public void It_releases_everything_and_reports_no_residue()
        {
            using AssertionScope scope = new();
            _firstRelease.Should().BeEmpty();
            _secondRelease.Should().BeEmpty();
            _artifact.IsReleased.Should().BeTrue();
            _workspace.OperationEntries.Should().BeEmpty();
        }

        [Test]
        public void It_refuses_extraction_after_release() =>
            _extractionAfterRelease.Should().BeOfType<ObjectDisposedException>();
    }

    [TestFixture]
    public class Given_a_staged_artifact_whose_manifest_declares_another_artifact_hash
    {
        private CatalogWorkspace _workspace = null!;
        private DmsOperationId _operationId;
        private TrustedTemplateArtifact _artifact = null!;
        private Exception? _extractionFailure;
        private bool _partialArtifactLeft;
        private IReadOnlyList<DmsCleanupResidue> _release = [];

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.Build(
                _workspace.MinimalBuilder().WithManifest(manifest => manifest["artifactSha256"] = _otherHash)
            );
            _workspace.Build(_workspace.SampleBuilder());
            _operationId = DmsOperationId.New();
            _artifact = StagedOf(
                await _workspace
                    .Catalog()
                    .StageAsync(DmsDatabaseTemplate.Minimal, _operationId, CancellationToken.None)
            );
            _extractionFailure = await Record.ExceptionAsync(() =>
                _artifact.ExtractArtifactAsync(CancellationToken.None)
            );
            _partialArtifactLeft = File.Exists(
                Path.Combine(_workspace.OperationDirectoryPathOf(_operationId), "artifact.sql")
            );
            _release = _artifact.Release();
        }

        [TearDown]
        public void TearDown()
        {
            _artifact.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_stages_without_reading_the_artifact() =>
            _artifact.Pin.ArtifactSha256.Should().Be(_otherHash);

        [Test]
        public void It_refuses_extraction_as_artifact_tampered_for_the_operation_boundary()
        {
            DmsProvisioningFailure failure = _extractionFailure
                .Should()
                .BeOfType<DmsProvisioningFailedException>()
                .Subject.Failure;
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactTampered,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        TemplateArtifactCatalog.ExtractArtifactStep
                    )
                );
        }

        [Test]
        public void It_deletes_the_partial_artifact_before_the_refusal_leaves() =>
            _partialArtifactLeft.Should().BeFalse();

        [Test]
        public void It_still_releases_cleanly() =>
            (_release.Count, _workspace.OperationEntries.Length).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_a_release_whose_deletion_the_file_system_refuses
    {
        private CatalogWorkspace _workspace = null!;
        private DmsOperationId _operationId;
        private TrustedTemplateArtifact _artifact = null!;
        private DeletionTrap _trap = null!;
        private IReadOnlyList<DmsCleanupResidue> _trappedRelease = [];
        private IReadOnlyList<DmsCleanupResidue> _repeatedWhileTrapped = [];
        private Dictionary<string, bool> _residueEntriesExisted = [];
        private LogEntry[] _residueLogs = [];
        private IReadOnlyList<DmsCleanupResidue> _releaseAfterTrapLifted = [];

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            _operationId = DmsOperationId.New();
            _artifact = StagedOf(
                await _workspace
                    .Catalog()
                    .StageAsync(DmsDatabaseTemplate.Minimal, _operationId, CancellationToken.None)
            );
            await _artifact.ExtractArtifactAsync(CancellationToken.None);

            string directoryPath = _workspace.OperationDirectoryPathOf(_operationId);
            _trap = new DeletionTrap(_artifact.OperationDirectoryPath);
            _trappedRelease = _artifact.Release();
            _repeatedWhileTrapped = _artifact.Release();
            _residueEntriesExisted = _trappedRelease.ToDictionary(
                residue => residue.ResourceId,
                residue =>
                    Path.Exists(Path.Combine(Path.GetDirectoryName(directoryPath)!, residue.ResourceId))
            );
            _residueLogs =
            [
                .. _workspace.Logger.Entries.Where(entry =>
                    entry.EventId == TemplateArtifactCatalog.ResidueLeft
                ),
            ];

            _trap.Dispose();
            _releaseAfterTrapLifted = _artifact.Release();
        }

        [TearDown]
        public void TearDown()
        {
            _trap.Dispose();
            _artifact.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_reports_the_operation_directory_as_residue_under_its_generated_name() =>
            _trappedRelease
                .Should()
                .Contain(
                    new DmsCleanupResidue(
                        DmsResidueKind.OperationDirectory,
                        PrivateOperationDirectory.NamePrefix + _operationId.Value
                    )
                );

        [Test]
        public void It_reports_only_entries_that_remained() =>
            _residueEntriesExisted.Should().NotBeEmpty().And.OnlyContain(pair => pair.Value);

        [Test]
        public void It_names_every_residue_from_the_generated_directory_name() =>
            _trappedRelease
                .Should()
                .OnlyContain(residue =>
                    residue.ResourceId.StartsWith(PrivateOperationDirectory.NamePrefix + _operationId.Value)
                );

        [Test]
        public void It_logs_each_residue_at_error_with_the_fixed_code()
        {
            using AssertionScope scope = new();
            _residueLogs.Should().HaveCount(_trappedRelease.Count + _repeatedWhileTrapped.Count);
            _residueLogs.Should().OnlyContain(entry => entry.Level == LogLevel.Error);
            _residueLogs
                .Select(entry => entry.Field("ResourceId"))
                .Should()
                .Contain(PrivateOperationDirectory.NamePrefix + _operationId.Value);
        }

        [Test]
        public void It_retries_the_deletion_on_a_later_release()
        {
            using AssertionScope scope = new();
            _repeatedWhileTrapped.Should().NotBeEmpty();
            _releaseAfterTrapLifted.Should().BeEmpty();
            _workspace.OperationEntries.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_an_owned_staging_failure_whose_deletion_fails
    {
        private CatalogWorkspace _workspace = null!;
        private DeletionTrap? _trap;
        private DmsArtifactResolution _resolution = null!;
        private Dictionary<string, bool> _residueEntriesExisted = [];
        private LogEntry[] _residueLogs = [];

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.Build(_workspace.MinimalBuilder().WithPolicyListingAnotherKey());
            TemplateArtifactCatalog catalog = _workspace.Catalog();
            catalog.BeforeRelease = directory => _trap = new DeletionTrap(directory.Path);
            _resolution = await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);

            IReadOnlyList<DmsCleanupResidue> residue = _resolution is DmsArtifactResolution.Failure failure
                ? failure.Cleanup.Residue
                : [];
            _residueEntriesExisted = residue.ToDictionary(
                item => item.ResourceId,
                item => Path.Exists(Path.Combine(_workspace.OperationsRoot, item.ResourceId))
            );
            _residueLogs =
            [
                .. _workspace.Logger.Entries.Where(entry =>
                    entry.EventId == TemplateArtifactCatalog.ResidueLeft
                ),
            ];
        }

        [TearDown]
        public void TearDown()
        {
            _trap?.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_still_refuses_the_package_as_untrusted()
        {
            DmsProvisioningFailure failure = _resolution
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactUntrusted,
                        $"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.UnknownKey}"
                    )
                );
        }

        [Test]
        public void It_reports_the_directory_it_claimed_as_residue() =>
            _resolution
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Which.Cleanup.Residue.Should()
                .Contain(residue =>
                    residue.Kind == DmsResidueKind.OperationDirectory
                    && residue.ResourceId.StartsWith(PrivateOperationDirectory.NamePrefix)
                );

        [Test]
        public void It_reports_only_entries_that_remained() =>
            _residueEntriesExisted.Should().NotBeEmpty().And.OnlyContain(pair => pair.Value);

        [Test]
        public void It_logs_each_residue_at_error()
        {
            _residueLogs.Should().HaveCount(_residueEntriesExisted.Count);
            _residueLogs.Should().OnlyContain(entry => entry.Level == LogLevel.Error);
        }
    }

    [TestFixture]
    public class Given_a_resolution_whose_release_fails
    {
        private CatalogWorkspace _workspace = null!;
        private readonly List<DeletionTrap> _traps = [];
        private DmsArtifactResolution _resolved = null!;
        private DmsArtifactResolution _mismatched = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();

            TemplateArtifactCatalog catalog = _workspace.Catalog();
            catalog.BeforeRelease = directory => _traps.Add(new DeletionTrap(directory.Path));
            _resolved = await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);

            DmsDataStoreProvisioningOptions otherProjects = _workspace.Options();
            otherProjects.Compatibility!.Projects = ["edfi"];
            TemplateArtifactCatalog mismatching = _workspace.Catalog(otherProjects);
            mismatching.BeforeRelease = directory => _traps.Add(new DeletionTrap(directory.Path));
            _mismatched = await mismatching.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _traps.ForEach(trap => trap.Dispose());
            _workspace.Dispose();
        }

        [Test]
        public void It_still_resolves_the_pin_and_reports_the_residue()
        {
            DmsArtifactResolution.Resolved resolved = _resolved
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Subject;
            using AssertionScope scope = new();
            resolved.Pin.PackageId.Should().Be(CatalogWorkspace.MinimalPackageId);
            resolved
                .Cleanup.Residue.Should()
                .Contain(residue => residue.Kind == DmsResidueKind.OperationDirectory);
            resolved
                .Cleanup.Residue.Should()
                .OnlyContain(residue => residue.ResourceId.StartsWith(PrivateOperationDirectory.NamePrefix));
        }

        [Test]
        public void It_still_refuses_the_mismatch_and_reports_the_residue()
        {
            DmsArtifactResolution.Failure failure = _mismatched
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Subject;
            using AssertionScope scope = new();
            failure.Detail.Code.Should().Be(DmsProvisioningFailureCode.ManifestMismatch);
            failure
                .Cleanup.Residue.Should()
                .Contain(residue => residue.Kind == DmsResidueKind.OperationDirectory);
        }

        [Test]
        public void It_left_both_operation_directories_for_the_operator() =>
            _workspace.OperationEntries.Should().HaveCount(2);
    }
}
