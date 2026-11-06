// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

public class TemplatePackageStagerTests
{
    private static readonly byte[] _otherArtifact = Encoding.UTF8.GetBytes("-- a different dump\n");

    private static TemplatePackageException Refusal(Exception? exception) =>
        exception.Should().BeOfType<TemplatePackageException>().Subject;

    [TestFixture]
    public class Given_a_well_formed_postgresql_package
    {
        private StagerWorkspace _workspace = null!;
        private TestTemplatePackage _package = null!;
        private StagedTemplatePackage _staged = null!;
        private StagedArtifact _artifact = null!;
        private Exception? _secondExtractionFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _package = _workspace.Build();
            _staged = await _workspace.StageAsync(_package);
            _artifact = await _staged.ExtractArtifactAsync(CancellationToken.None);
            _secondExtractionFailure = await Record.ExceptionAsync(() =>
                _staged.ExtractArtifactAsync(CancellationToken.None)
            );
        }

        [TearDown]
        public void TearDown()
        {
            _staged.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_hashes_the_private_copy() => _staged.PackageSha256.Should().Be(_package.PackageSha256);

        [Test]
        public void It_names_the_verifying_producer() => _staged.Producer.Should().Be(_package.ProducerName);

        [Test]
        public void It_reads_the_manifest()
        {
            _staged.Manifest.PackageId.Should().Be(_package.PackageId);
            _staged.Manifest.ArtifactFileName.Should().Be(_package.ArtifactFileName);
            _staged.Manifest.ArtifactSha256.Should().Be(_package.ArtifactSha256);
        }

        [Test]
        public void It_places_the_private_copy_under_a_generated_name_in_the_operation_directory()
        {
            _staged
                .PackageCopyPath.Should()
                .Be(
                    Path.Combine(_workspace.OperationDirectoryPath, StagedTemplatePackage.PackageCopyFileName)
                );
            _staged.Directory.Path.Should().Be(_workspace.OperationDirectoryPath);
        }

        [Test]
        public void It_keeps_the_private_copy_until_disposal() =>
            File.Exists(_staged.PackageCopyPath).Should().BeTrue();

        [Test]
        [Platform(Include = "Win")]
        public void It_holds_the_private_copy_exclusively_where_the_file_system_enforces_sharing()
        {
            Func<FileStream> open = () =>
                new FileStream(_staged.PackageCopyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            open.Should().Throw<IOException>();
        }

        [Test]
        public void It_extracts_the_artifact_to_a_generated_sql_name()
        {
            _artifact.FilePath.Should().Be(Path.Combine(_workspace.OperationDirectoryPath, "artifact.sql"));
            _artifact.Kind.Should().Be(DmsArtifactKind.PostgresqlSqlDump);
        }

        [Test]
        public void It_extracts_the_artifact_bytes_exactly()
        {
            File.ReadAllBytes(_artifact.FilePath).Should().Equal(_package.ArtifactBytes);
            _artifact.Sha256.Should().Be(_package.ArtifactSha256);
        }

        [Test]
        public void It_extracts_only_once() =>
            _secondExtractionFailure.Should().BeOfType<InvalidOperationException>();
    }

    [TestFixture]
    public class Given_a_well_formed_mssql_package
    {
        private StagerWorkspace _workspace = null!;
        private StagedTemplatePackage _staged = null!;
        private StagedArtifact _artifact = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            TestTemplatePackage package = _workspace.Build(new TestTemplatePackageBuilder(mssql: true));
            _staged = await _workspace.StageAsync(package);
            _artifact = await _staged.ExtractArtifactAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _staged.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_extracts_the_artifact_to_a_generated_bak_name()
        {
            _artifact.FilePath.Should().Be(Path.Combine(_workspace.OperationDirectoryPath, "artifact.bak"));
            _artifact.Kind.Should().Be(DmsArtifactKind.MssqlBackup);
        }

        [Test]
        public void It_reads_the_compatibility_level() =>
            _staged.Manifest.DatabaseCompatibilityLevel.Should().Be(170);
    }

    [TestFixture]
    public class Given_a_staged_package_is_disposed
    {
        private StagerWorkspace _workspace = null!;
        private string _artifactPath = null!;
        private bool _directoryExistedBeforeDisposal;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            StagedTemplatePackage staged = await _workspace.StageAsync(_workspace.Build());
            _artifactPath = (await staged.ExtractArtifactAsync(CancellationToken.None)).FilePath;
            _directoryExistedBeforeDisposal = _workspace.OperationDirectoryExists;
            staged.Dispose();
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_held_the_directory_until_disposal() =>
            _directoryExistedBeforeDisposal.Should().BeTrue();

        [Test]
        public void It_removes_the_operation_directory_with_the_copy_and_the_artifact()
        {
            _workspace.OperationDirectoryExists.Should().BeFalse();
            File.Exists(_artifactPath).Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_the_catalog_file_is_replaced_after_staging
    {
        private StagerWorkspace _workspace = null!;
        private TestTemplatePackage _original = null!;
        private StagedTemplatePackage _staged = null!;
        private StagedArtifact _artifact = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _original = _workspace.Build();
            _staged = await _workspace.StageAsync(_original);

            // A second, differently-signed package overwrites the catalog file and its attestation in place.
            StagerWorkspace other = new();
            try
            {
                TestTemplatePackage replacement = other.Build(
                    new TestTemplatePackageBuilder().WithArtifact(_otherArtifact)
                );
                File.Copy(replacement.PackagePath, _original.PackagePath, overwrite: true);
                File.Copy(replacement.AttestationPath, _original.AttestationPath, overwrite: true);
            }
            finally
            {
                other.Dispose();
            }

            _artifact = await _staged.ExtractArtifactAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _staged.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_extracts_from_the_private_copy_not_the_catalog_file() =>
            File.ReadAllBytes(_artifact.FilePath).Should().Equal(_original.ArtifactBytes);

        [Test]
        public void It_keeps_the_hash_of_the_bytes_it_authenticated() =>
            _staged.PackageSha256.Should().Be(_original.PackageSha256);

        [Test]
        public void It_really_replaced_the_catalog_file() =>
            TestTemplatePackageBuilder
                .Sha256Hex(File.ReadAllBytes(_original.PackagePath))
                .Should()
                .NotBe(_original.PackageSha256);
    }

    [TestFixture]
    public class Given_the_artifact_bytes_differ_from_the_manifest_hash
    {
        private StagerWorkspace _workspace = null!;
        private StagedTemplatePackage _staged = null!;
        private Exception? _extractionFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            TestTemplatePackage package = _workspace.Build(
                new TestTemplatePackageBuilder().WithManifest(manifest =>
                    manifest["artifactSha256"] = TestTemplatePackageBuilder.Sha256Hex(_otherArtifact)
                )
            );
            _staged = await _workspace.StageAsync(package);
            _extractionFailure = await Record.ExceptionAsync(() =>
                _staged.ExtractArtifactAsync(CancellationToken.None)
            );
        }

        [TearDown]
        public void TearDown()
        {
            _staged.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_stages_without_reading_the_artifact() => _staged.Should().NotBeNull();

        [Test]
        public void It_refuses_the_extraction_as_tampered() =>
            Refusal(_extractionFailure).Defect.Should().Be(TemplatePackageDefect.ArtifactTampered);

        [Test]
        public void It_deletes_the_partial_artifact() =>
            File.Exists(Path.Combine(_workspace.OperationDirectoryPath, "artifact.sql")).Should().BeFalse();

        [Test]
        public void It_keeps_the_operation_directory_for_the_owner_to_dispose() =>
            File.Exists(_staged.PackageCopyPath).Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_undeclared_database_artifact_entry
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _sqlFailure;
        private Exception? _upperCaseBakFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _sqlFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithEntry("extra.sql", "-- extra"))
            );
            _upperCaseBakFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithEntry("tools/EXTRA.BAK", "x"))
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_an_extra_sql_entry()
        {
            TemplatePackageException refusal = Refusal(_sqlFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal
                .Message.Should()
                .Be("The package contains 1 database artifact entries beyond the manifest-declared one.");
        }

        [Test]
        public void It_matches_artifact_extensions_ignoring_case_and_directory() =>
            Refusal(_upperCaseBakFailure).Defect.Should().Be(TemplatePackageDefect.PackageInvalid);

        [Test]
        public void It_cleans_up_the_operation_directory() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_duplicate_entry
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _exactDuplicateFailure;
        private Exception? _caseDuplicateFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            TestTemplatePackageBuilder exact = new();
            _exactDuplicateFailure = await _workspace.StagingFailureOf(
                _workspace.Build(exact.WithEntry(exact.ArtifactFileName, "-- second copy"))
            );
            _caseDuplicateFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithEntry("RESTORE-MANIFEST.JSON", "{}"))
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_an_exact_duplicate()
        {
            TemplatePackageException refusal = Refusal(_exactDuplicateFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.Message.Should().Be("The package holds more than one archive entry with the same name.");
        }

        [Test]
        public void It_treats_names_differing_only_in_case_as_duplicates() =>
            Refusal(_caseDuplicateFailure)
                .Message.Should()
                .Be("The package holds more than one archive entry with the same name.");

        [Test]
        public void It_cleans_up_the_operation_directory() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_nuspec_that_does_not_match_the_resolved_identity
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _versionFailure;
        private Exception? _idFailure;
        private Exception? _missingFailure;
        private Exception? _twoFailure;
        private Exception? _malformedFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            TestTemplatePackageBuilder version = new();
            _versionFailure = await _workspace.StagingFailureOf(
                _workspace.Build(version.WithNuspecIdentity(version.PackageId, "9.9.9"))
            );
            TestTemplatePackageBuilder id = new();
            _idFailure = await _workspace.StagingFailureOf(
                _workspace.Build(id.WithNuspecIdentity("Other.Package", id.PackageVersion))
            );
            _missingFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithoutNuspec())
            );
            _twoFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithEntry("second.nuspec", "<package />"))
            );
            _malformedFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithoutNuspec().WithEntry("broken.nuspec", "<package>")
                )
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_a_version_mismatch()
        {
            TemplatePackageException refusal = Refusal(_versionFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.IdentityMismatch);
            refusal
                .Message.Should()
                .Be("The .nuspec declares a package identity different from the resolved one.");
        }

        [Test]
        public void It_refuses_an_id_mismatch() =>
            Refusal(_idFailure).Defect.Should().Be(TemplatePackageDefect.IdentityMismatch);

        [Test]
        public void It_requires_exactly_one_nuspec()
        {
            Refusal(_missingFailure)
                .Message.Should()
                .Be("The package must contain exactly one .nuspec; found 0.");
            Refusal(_twoFailure)
                .Message.Should()
                .Be("The package must contain exactly one .nuspec; found 2.");
        }

        [Test]
        public void It_refuses_a_nuspec_that_is_not_xml()
        {
            TemplatePackageException refusal = Refusal(_malformedFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.InnerException.Should().BeOfType<System.Xml.XmlException>();
        }

        [Test]
        public void It_cleans_up_the_operation_directory() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_manifest_identity_that_differs_from_the_resolved_one
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _versionFailure;
        private Exception? _versionCaseFailure;
        private StagedTemplatePackage? _mixedCaseIdStaged;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _versionFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithManifest(manifest =>
                        manifest["packageVersion"] = "1.0.124"
                    )
                ),
                expectedPackageVersion: "1.0.123"
            );

            // Published, attested and allowlisted as the same version up to case: the attestation and the nuspec
            // compare ignoring case, the manifest comparison is exact (6.2 step 6).
            TestTemplatePackage prerelease = _workspace.Build(
                new TestTemplatePackageBuilder().WithPackageVersion("1.0.123-beta")
            );
            _versionCaseFailure = await _workspace.StagingFailureOf(
                prerelease,
                expectedPackageVersion: "1.0.123-BETA"
            );

            TestTemplatePackage package = _workspace.Build();
            _mixedCaseIdStaged = await _workspace.StageAsync(
                package,
                expectedPackageId: package.PackageId.ToUpperInvariant()
            );
        }

        [TearDown]
        public void TearDown()
        {
            _mixedCaseIdStaged?.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_refuses_a_different_version()
        {
            TemplatePackageException refusal = Refusal(_versionFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.IdentityMismatch);
            refusal
                .Message.Should()
                .Be("The restore manifest declares a package identity different from the resolved one.");
        }

        [Test]
        public void It_compares_the_version_exactly() =>
            Refusal(_versionCaseFailure).Defect.Should().Be(TemplatePackageDefect.IdentityMismatch);

        [Test]
        public void It_compares_the_id_ignoring_case() => _mixedCaseIdStaged.Should().NotBeNull();
    }

    [TestFixture]
    public class Given_the_manifest_entry_is_missing_duplicated_or_invalid
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _missingFailure;
        private Exception? _twoFailure;
        private Exception? _invalidFailure;
        private Exception? _artifactMissingFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _missingFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithoutManifest())
            );
            _twoFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithEntry("nested/restore-manifest.json", "{}")
                )
            );
            _invalidFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithManifestText("{ \"version\": 1 "))
            );
            _artifactMissingFailure = await _workspace.StagingFailureOf(
                _workspace.Build(new TestTemplatePackageBuilder().WithoutArtifact())
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_requires_exactly_one_manifest()
        {
            Refusal(_missingFailure)
                .Message.Should()
                .Be("The package must contain exactly one restore-manifest.json; found 0.");
            Refusal(_twoFailure)
                .Message.Should()
                .Be("The package must contain exactly one restore-manifest.json; found 2.");
        }

        [Test]
        public void It_reports_a_manifest_defect_with_the_reader_exception_inside()
        {
            TemplatePackageException refusal = Refusal(_invalidFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.ManifestInvalid);
            refusal.InnerException.Should().BeOfType<RestoreManifestException>();
            refusal.Message.Should().Be(refusal.InnerException!.Message);
        }

        [Test]
        public void It_requires_the_declared_artifact_entry()
        {
            TemplatePackageException refusal = Refusal(_artifactMissingFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal
                .Message.Should()
                .Be(
                    "The package must contain exactly one entry named by the manifest's artifactFileName; found 0."
                );
        }

        [Test]
        public void It_cleans_up_the_operation_directory() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    [TestFixture]
    public class Given_the_attestation_does_not_verify
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _unknownKeyFailure;
        private Exception? _hashMismatchFailure;
        private Exception? _absentFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();

            // Not even a zip: a refusal that names the key proves the archive was never opened.
            _unknownKeyFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder()
                        .WithRawPackageBytes(Encoding.UTF8.GetBytes("not a zip"))
                        .WithPolicyListingAnotherKey()
                )
            );
            _hashMismatchFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithAttestedSha256(
                        TestTemplatePackageBuilder.Sha256Hex(_otherArtifact)
                    )
                )
            );
            _absentFailure = await _workspace.StagingFailureOf(_workspace.Build(), attestationJson: "");
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_before_opening_the_archive()
        {
            TemplatePackageException refusal = Refusal(_unknownKeyFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.Untrusted);
            refusal.TrustRefusal.Should().Be(TrustRefusalReason.UnknownKey);
        }

        [Test]
        public void It_verifies_against_the_private_copy_hash() =>
            Refusal(_hashMismatchFailure).TrustRefusal.Should().Be(TrustRefusalReason.PackageHashMismatch);

        [Test]
        public void It_treats_an_absent_document_as_malformed() =>
            Refusal(_absentFailure).TrustRefusal.Should().Be(TrustRefusalReason.Malformed);

        [Test]
        public void It_cleans_up_the_operation_directory() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_trusted_package_that_is_not_a_zip_archive
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _failure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _failure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithRawPackageBytes(Encoding.UTF8.GetBytes("not a zip"))
                )
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_as_an_invalid_package()
        {
            TemplatePackageException refusal = Refusal(_failure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.Message.Should().Be("The package is not a readable zip archive.");
            refusal.InnerException.Should().BeOfType<InvalidDataException>();
        }

        [Test]
        public void It_cleans_up_the_operation_directory() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    /// <summary>
    /// Each package here authenticates (the attestation is signed over the corrupted bytes) and opens as a zip,
    /// so the reader reports the defect only later: when the entries are enumerated, when an entry is opened,
    /// or when the artifact is decompressed.
    /// </summary>
    [TestFixture]
    public class Given_an_authenticated_package_whose_container_is_malformed_past_the_archive_open
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _centralDirectoryFailure;
        private Exception? _manifestEntryFailure;
        private Exception? _nuspecEntryFailure;
        private bool _directoryRemainedAfterStagingFailures;
        private StagedTemplatePackage _staged = null!;
        private Exception? _extractionFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            _centralDirectoryFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithZipMutation(ZipLayout.CorruptCentralDirectory)
                )
            );
            _manifestEntryFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    new TestTemplatePackageBuilder().WithZipMutation(zip =>
                        ZipLayout.CorruptLocalHeader(zip, RestoreManifest.FileName)
                    )
                )
            );
            TestTemplatePackageBuilder nuspecBuilder = new();
            _nuspecEntryFailure = await _workspace.StagingFailureOf(
                _workspace.Build(
                    nuspecBuilder.WithZipMutation(zip =>
                        ZipLayout.SetUnsupportedCompressionMethod(zip, $"{nuspecBuilder.PackageId}.nuspec")
                    )
                )
            );
            _directoryRemainedAfterStagingFailures = _workspace.OperationDirectoryExists;

            TestTemplatePackageBuilder artifactBuilder = new();
            _staged = await _workspace.StageAsync(
                _workspace.Build(
                    artifactBuilder.WithZipMutation(zip =>
                        ZipLayout.CorruptDeflateStream(zip, artifactBuilder.ArtifactFileName)
                    )
                )
            );
            _extractionFailure = await Record.ExceptionAsync(() =>
                _staged.ExtractArtifactAsync(CancellationToken.None)
            );
        }

        [TearDown]
        public void TearDown()
        {
            _staged.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_refuses_a_corrupt_central_directory_as_an_invalid_package()
        {
            TemplatePackageException refusal = Refusal(_centralDirectoryFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.Message.Should().Be("The package's central directory is not readable.");
            refusal.InnerException.Should().BeOfType<InvalidDataException>();
        }

        [Test]
        public void It_refuses_an_unreadable_manifest_entry_as_an_invalid_package()
        {
            TemplatePackageException refusal = Refusal(_manifestEntryFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.Message.Should().Be("A package entry could not be read from the archive.");
            refusal.InnerException.Should().BeOfType<InvalidDataException>();
        }

        [Test]
        public void It_refuses_an_unreadable_nuspec_entry_as_an_invalid_package()
        {
            TemplatePackageException refusal = Refusal(_nuspecEntryFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.Message.Should().Be("A package entry could not be read from the archive.");
            refusal.InnerException.Should().BeOfType<InvalidDataException>();
        }

        [Test]
        public void It_cleans_up_the_operation_directory_after_each_staging_refusal() =>
            _directoryRemainedAfterStagingFailures.Should().BeFalse();

        [Test]
        public void It_stages_a_package_whose_artifact_stream_is_corrupt_without_reading_it() =>
            _staged.Should().NotBeNull();

        [Test]
        public void It_refuses_the_extraction_of_a_corrupt_artifact_stream_as_an_invalid_package()
        {
            TemplatePackageException refusal = Refusal(_extractionFailure);
            refusal.Defect.Should().Be(TemplatePackageDefect.PackageInvalid);
            refusal.Message.Should().Be("The artifact entry could not be read from the archive.");
            refusal.InnerException.Should().BeOfType<InvalidDataException>();
        }

        [Test]
        public void It_deletes_the_partial_artifact() =>
            File.Exists(Path.Combine(_workspace.OperationDirectoryPath, "artifact.sql")).Should().BeFalse();

        [Test]
        public void It_keeps_the_operation_directory_for_the_owner_to_dispose() =>
            File.Exists(_staged.PackageCopyPath).Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_catalog_file_is_missing_or_the_operation_is_cancelled
    {
        private StagerWorkspace _workspace = null!;
        private Exception? _missingFailure;
        private Exception? _cancelledFailure;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            TestTemplatePackage package = _workspace.Build();
            File.Delete(package.PackagePath);
            _missingFailure = await _workspace.StagingFailureOf(package);

            TestTemplatePackage present = _workspace.Build();
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            _cancelledFailure = await Record.ExceptionAsync(() =>
                _workspace.StageAsync(present, cancellationToken: cancelled.Token)
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_lets_a_missing_file_surface_as_the_file_system_exception() =>
            _missingFailure.Should().BeOfType<FileNotFoundException>();

        [Test]
        public void It_propagates_cancellation() =>
            _cancelledFailure.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_cleans_up_the_operation_directory_either_way() =>
            _workspace.OperationDirectoryExists.Should().BeFalse();
    }

    [TestFixture]
    [Platform(Exclude = "Win")]
    public class Given_a_package_staged_on_a_unix_file_system
    {
        private StagerWorkspace _workspace = null!;
        private UnixFileMode _directoryMode;
        private UnixFileMode _copyMode;
        private UnixFileMode _artifactMode;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new StagerWorkspace();
            using StagedTemplatePackage staged = await _workspace.StageAsync(_workspace.Build());
            StagedArtifact artifact = await staged.ExtractArtifactAsync(CancellationToken.None);
            if (!OperatingSystem.IsWindows())
            {
                _directoryMode = File.GetUnixFileMode(staged.Directory.Path);
                _copyMode = File.GetUnixFileMode(staged.PackageCopyPath);
                _artifactMode = File.GetUnixFileMode(artifact.FilePath);
            }
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_keeps_the_operation_directory_at_0700() =>
            _directoryMode.Should().Be(PrivateOperationDirectory.PrivateDirectoryMode);

        [Test]
        public void It_keeps_the_package_copy_at_0600() =>
            _copyMode.Should().Be(PrivateOperationDirectory.PrivateFileMode);

        [Test]
        public void It_keeps_the_artifact_at_0600() =>
            _artifactMode.Should().Be(PrivateOperationDirectory.PrivateFileMode);
    }
}
