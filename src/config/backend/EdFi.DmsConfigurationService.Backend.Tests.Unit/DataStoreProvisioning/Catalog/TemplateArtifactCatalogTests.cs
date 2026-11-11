// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LogEntry = EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs.LogEntry;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;

/// <summary>
/// Spec 6.2 through the catalog over builder packages: file names derive from the allowlist alone; a missing
/// package or attestation is <c>ArtifactMissing</c> before anything is staged or verified; every untrusted,
/// malformed, and mismatching package is one failure code with the fixed operation that names the failed
/// check; each configuration comparison of step 6, including every <c>Compatibility:*</c> expectation and the
/// Minimal-under-Sample case, is refused in the delivered order; the readiness probe reports a bad policy and
/// a catalog that agrees with itself but not with the deployment; and the pin changes with the artifact bytes
/// under an otherwise identical manifest, which the orchestrator refuses before any engine call. Every
/// resolution releases its private directory.
/// </summary>
public class TemplateArtifactCatalogTests
{
    private static readonly string _otherHash = string.Concat(Enumerable.Repeat("ef", 32));

    private static DmsProvisioningFailure FailureOf(DmsArtifactResolution resolution) =>
        resolution.Should().BeOfType<DmsArtifactResolution.Failure>().Subject.Detail;

    private static DmsTrustedArtifactPin PinOf(DmsArtifactResolution resolution) =>
        resolution.Should().BeOfType<DmsArtifactResolution.Resolved>().Subject.Pin;

    /// <summary>One resolution in its own workspace, with what the operations root held afterwards.</summary>
    private sealed record Outcome(
        DmsArtifactResolution Resolution,
        string[] OperationEntries,
        LogEntry[] Logs
    );

    private static async Task<Outcome> ResolveInOwnWorkspace(
        Action<CatalogWorkspace> arrange,
        Func<CatalogWorkspace, DmsDataStoreProvisioningOptions>? options = null,
        DmsDatabaseTemplate? template = null
    )
    {
        using CatalogWorkspace workspace = new();
        arrange(workspace);
        TemplateArtifactCatalog catalog = workspace.Catalog(options?.Invoke(workspace));
        DmsArtifactResolution resolution = await catalog.ResolveAsync(
            template ?? DmsDatabaseTemplate.Minimal,
            CancellationToken.None
        );
        return new Outcome(resolution, workspace.OperationEntries, [.. workspace.Logger.Entries]);
    }

    /// <summary>An operations root that does not exist: staging there fails as <c>StagingFailed</c>, so a verdict reached before staging proves staging never ran.</summary>
    private static DmsDataStoreProvisioningOptions WithoutOperationsRoot(CatalogWorkspace workspace)
    {
        DmsDataStoreProvisioningOptions options = workspace.Options();
        options.OperationDirectory = Path.Combine(workspace.OperationsRoot, "missing");
        return options;
    }

    /// <summary>Startup validation in its own workspace: the readiness reasons it reports.</summary>
    private static async Task<IReadOnlyList<DmsReadinessReason>> ValidateInOwnWorkspace(
        Action<CatalogWorkspace> arrange
    )
    {
        using CatalogWorkspace workspace = new();
        arrange(workspace);
        return (await workspace.Catalog().ValidateAsync(CancellationToken.None)).Reasons;
    }

    /// <summary>F-06: the <c>HResult</c> values pinned on .NET 10 for a policy another handle holds.</summary>
    private static readonly int[] _pinnedContention =
    [
        unchecked((int)0x80070020), // Windows ERROR_SHARING_VIOLATION
        unchecked((int)0x80070021), // Windows ERROR_LOCK_VIOLATION
        11, // Linux EWOULDBLOCK from the FileShare emulation
    ];

    private static readonly (
        DmsProvisioningFailureCode,
        DmsProvisioningFailureCategory,
        string
    ) _unavailableForInspection = (
        DmsProvisioningFailureCode.TrustPolicyUnavailable,
        DmsProvisioningFailureCategory.OperatorInspectionRequired,
        TemplateArtifactCatalog.TrustPolicyStep
    );

    private static readonly (
        DmsProvisioningFailureCode,
        DmsProvisioningFailureCategory,
        string
    ) _unavailableForNow = (
        DmsProvisioningFailureCode.TrustPolicyUnavailable,
        DmsProvisioningFailureCategory.Transient,
        TemplateArtifactCatalog.TrustPolicyStep
    );

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) VerdictOf(
        DmsProvisioningFailure failure
    ) => (failure.Code, failure.Category, failure.Diagnostic.Operation);

    /// <summary>A trust-policy reader that throws <paramref name="exception"/>.</summary>
    private static TemplateArtifactCatalog.BoundedTextReader Throwing(Exception exception) =>
        (_, _, _) => Task.FromException<(string?, TemplateArtifactCatalog.BoundedTextDefect)>(exception);

    /// <summary>A trust-policy reader that reports <paramref name="defect"/> and no text.</summary>
    private static TemplateArtifactCatalog.BoundedTextReader Returning(
        TemplateArtifactCatalog.BoundedTextDefect defect
    ) => (_, _, _) => Task.FromResult<(string?, TemplateArtifactCatalog.BoundedTextDefect)>((null, defect));

    [TestFixture]
    public class Given_a_valid_catalog
    {
        private CatalogWorkspace _workspace = null!;
        private TestTemplatePackage _minimal = null!;
        private TestTemplatePackage _sample = null!;
        private DmsArtifactResolution _minimalResolution = null!;
        private DmsArtifactResolution _sampleResolution = null!;
        private IReadOnlyList<DmsReadinessReason> _reasons = [];
        private string[] _operationEntries = [];

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            (_minimal, _sample) = _workspace.BuildBoth();
            TemplateArtifactCatalog catalog = _workspace.Catalog();
            _minimalResolution = await catalog.ResolveAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _sampleResolution = await catalog.ResolveAsync(
                DmsDatabaseTemplate.Sample,
                CancellationToken.None
            );
            _reasons = (await catalog.ValidateAsync(CancellationToken.None)).Reasons;
            _operationEntries = _workspace.OperationEntries;
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        private static DmsTrustedArtifactPin ExpectedPin(
            TestTemplatePackage package,
            string templateKind = RestoreManifest.MinimalTemplateKind
        ) =>
            new()
            {
                ContractVersion = DmsTrustedArtifactPin.CurrentContractVersion,
                Provider = DmsDataStoreProvisioningOptions.PostgresqlProvider,
                PackageId = package.PackageId,
                PackageVersion = package.PackageVersion,
                PackageSha256 = package.PackageSha256,
                ArtifactSha256 = package.ArtifactSha256,
                TemplateKind = templateKind,
                DataStandardVersion = "5.2.0",
                EffectiveSchemaHash = RestoreManifestSample.EffectiveSchemaHash,
                InventorySha256 = RestoreManifestSample.InventorySha256,
                ApiSchemaFormatVersion = "1.0.0",
                RelationalMappingVersion = "v2",
            };

        [Test]
        public void It_pins_the_minimal_package_from_the_allowlist_and_its_manifest() =>
            PinOf(_minimalResolution).Should().Be(ExpectedPin(_minimal));

        [Test]
        public void It_pins_the_sample_package_as_populated() =>
            PinOf(_sampleResolution).Should().Be(ExpectedPin(_sample, RestoreManifest.PopulatedTemplateKind));

        [Test]
        public void It_releases_every_operation_directory() => _operationEntries.Should().BeEmpty();

        [Test]
        public void It_reports_clean_cleanup()
        {
            _minimalResolution
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Which.Cleanup.HasResidue.Should()
                .BeFalse();
            _sampleResolution
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Which.Cleanup.HasResidue.Should()
                .BeFalse();
        }

        [Test]
        public void It_passes_the_readiness_probes() => _reasons.Should().BeEmpty();

        [Test]
        public void It_logs_nothing_at_error_or_above() =>
            _workspace.Logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [TestFixture]
    public class Given_the_allowlist_names_a_version_the_directory_does_not_hold
    {
        private Outcome _outcome = null!;

        [SetUp]
        public async Task Setup() =>
            _outcome = await ResolveInOwnWorkspace(
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace =>
                {
                    DmsDataStoreProvisioningOptions options = WithoutOperationsRoot(workspace);
                    options.Templates![DmsDatabaseTemplate.MinimalWireValue]!.PackageVersion = "1.0.124";
                    return options;
                }
            );

        [Test]
        public void It_derives_the_file_name_from_the_allowlist_alone()
        {
            DmsProvisioningFailure failure = FailureOf(_outcome.Resolution);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactMissing,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        TemplateArtifactCatalog.LocateArtifactStep
                    )
                );
        }

        [Test]
        public void It_reports_clean_cleanup() =>
            _outcome
                .Resolution.Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Which.Cleanup.HasResidue.Should()
                .BeFalse();

        [Test]
        public void It_logs_the_refusal_with_the_code_and_the_template() =>
            _outcome
                .Logs.Should()
                .ContainSingle(entry =>
                    entry.EventId == TemplateArtifactCatalog.ArtifactRefused
                    && entry.Level == LogLevel.Warning
                )
                .Which.Fields.Should()
                .Contain(
                    new KeyValuePair<string, object?>("Code", DmsProvisioningFailureCode.ArtifactMissing),
                    new KeyValuePair<string, object?>("Template", DmsDatabaseTemplate.MinimalWireValue)
                );
    }

    [TestFixture]
    public class Given_the_package_or_its_attestation_is_absent
    {
        private Outcome _attestationAbsent = null!;
        private Outcome _packageAbsent = null!;

        [SetUp]
        public async Task Setup()
        {
            // The policy does not know the signing key, so verification, had it run, would say UnknownKey.
            _attestationAbsent = await ResolveInOwnWorkspace(
                workspace =>
                    File.Delete(
                        workspace
                            .Build(workspace.MinimalBuilder().WithPolicyListingAnotherKey())
                            .AttestationPath
                    ),
                WithoutOperationsRoot
            );
            _packageAbsent = await ResolveInOwnWorkspace(
                workspace => File.Delete(workspace.Build(workspace.MinimalBuilder()).PackagePath),
                WithoutOperationsRoot
            );
        }

        [Test]
        public void It_reports_a_missing_attestation_before_verifying_anything()
        {
            DmsProvisioningFailure failure = FailureOf(_attestationAbsent.Resolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.ArtifactMissing, TemplateArtifactCatalog.LocateArtifactStep));
        }

        [Test]
        public void It_reports_a_missing_package()
        {
            DmsProvisioningFailure failure = FailureOf(_packageAbsent.Resolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.ArtifactMissing, TemplateArtifactCatalog.LocateArtifactStep));
        }
    }

    [TestFixture]
    public class Given_packages_the_trust_policy_does_not_vouch_for
    {
        private Outcome _unknownKey = null!;
        private Outcome _alteredAfterSigning = null!;
        private Outcome _oversizedAttestation = null!;

        [SetUp]
        public async Task Setup()
        {
            _unknownKey = await ResolveInOwnWorkspace(workspace =>
                workspace.Build(workspace.MinimalBuilder().WithPolicyListingAnotherKey())
            );
            _alteredAfterSigning = await ResolveInOwnWorkspace(workspace =>
            {
                TestTemplatePackage package = workspace.Build(workspace.MinimalBuilder());
                byte[] bytes = File.ReadAllBytes(package.PackagePath);
                bytes[^1] ^= 0x01;
                File.WriteAllBytes(package.PackagePath, bytes);
            });
            _oversizedAttestation = await ResolveInOwnWorkspace(
                workspace =>
                {
                    TestTemplatePackage package = workspace.Build(workspace.MinimalBuilder());
                    File.WriteAllBytes(
                        package.AttestationPath,
                        new byte[TemplateArtifactCatalog.MaxAttestationBytes + 1]
                    );
                },
                WithoutOperationsRoot
            );
        }

        [Test]
        public void It_refuses_an_unknown_key_as_untrusted_naming_the_reason()
        {
            DmsProvisioningFailure failure = FailureOf(_unknownKey.Resolution);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactUntrusted,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        $"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.UnknownKey}"
                    )
                );
        }

        [Test]
        public void It_refuses_a_package_altered_after_signing_as_a_hash_mismatch() =>
            FailureOf(_alteredAfterSigning.Resolution)
                .Diagnostic.Operation.Should()
                .Be(
                    $"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.PackageHashMismatch}"
                );

        [Test]
        public void It_refuses_an_oversized_attestation_as_malformed_without_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_oversizedAttestation.Resolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactUntrusted,
                        $"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.Malformed}"
                    )
                );
        }

        [Test]
        public void It_releases_the_operation_directory_on_every_refusal()
        {
            _unknownKey.OperationEntries.Should().BeEmpty();
            _alteredAfterSigning.OperationEntries.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_trust_policy_that_cannot_anchor_trust
    {
        private CatalogWorkspace _noProducers = null!;
        private CatalogWorkspace _malformed = null!;
        private CatalogWorkspace _missing = null!;
        private DmsArtifactResolution _noProducersResolution = null!;
        private IReadOnlyList<DmsReadinessReason> _noProducersReasons = [];
        private DmsArtifactResolution _malformedResolution = null!;
        private IReadOnlyList<DmsReadinessReason> _malformedReasons = [];
        private DmsArtifactResolution _missingResolution = null!;
        private IReadOnlyList<DmsReadinessReason> _missingReasons = [];

        [SetUp]
        public async Task Setup()
        {
            _noProducers = new CatalogWorkspace();
            _noProducers.BuildBoth();
            _noProducers.WriteTrustPolicy("""{ "version": 1, "producers": [] }""");
            TemplateArtifactCatalog noProducers = _noProducers.Catalog();
            _noProducersResolution = await noProducers.ResolveAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _noProducersReasons = (await noProducers.ValidateAsync(CancellationToken.None)).Reasons;

            _malformed = new CatalogWorkspace();
            _malformed.BuildBoth();
            _malformed.WriteTrustPolicy("{");
            TemplateArtifactCatalog malformed = _malformed.Catalog(WithoutOperationsRoot(_malformed));
            _malformedResolution = await malformed.ResolveAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _malformedReasons = (await malformed.ValidateAsync(CancellationToken.None)).Reasons;

            _missing = new CatalogWorkspace();
            _missing.BuildBoth();
            File.Delete(_missing.TrustPolicyPath);
            TemplateArtifactCatalog missing = _missing.Catalog(WithoutOperationsRoot(_missing));
            _missingResolution = await missing.ResolveAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _missingReasons = (await missing.ValidateAsync(CancellationToken.None)).Reasons;
        }

        [TearDown]
        public void TearDown()
        {
            _noProducers.Dispose();
            _malformed.Dispose();
            _missing.Dispose();
        }

        [Test]
        public void It_fails_closed_on_zero_producers_at_resolution()
        {
            DmsProvisioningFailure failure = FailureOf(_noProducersResolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactUntrusted,
                        $"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.NoProducers}"
                    )
                );
        }

        [Test]
        public void It_reports_zero_producers_as_the_policy_probe_at_startup() =>
            _noProducersReasons
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        TemplateArtifactCatalog.TrustPolicyProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            $"{TemplateArtifactCatalog.TrustPolicyStep}.{TemplateArtifactCatalog.NoProducersCheck}"
                        )
                    )
                );

        [Test]
        public void It_reports_a_malformed_policy_for_inspection_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_malformedResolution);
            using AssertionScope scope = new();
            VerdictOf(failure).Should().Be(_unavailableForInspection);
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(TemplateTrustPolicyException).FullName);
        }

        [Test]
        public void It_reports_a_missing_policy_for_inspection_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_missingResolution);
            using AssertionScope scope = new();
            VerdictOf(failure).Should().Be(_unavailableForInspection);
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(FileNotFoundException).FullName);
        }

        [Test]
        public void It_reports_the_policy_probe_alone_at_startup_when_the_policy_cannot_load()
        {
            using AssertionScope scope = new();
            _malformedReasons
                .Should()
                .ContainSingle()
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StartupValidationFailed
                    && reason.Probe == TemplateArtifactCatalog.TrustPolicyProbe
                );
            _missingReasons
                .Should()
                .ContainSingle()
                .Which.Probe.Should()
                .Be(TemplateArtifactCatalog.TrustPolicyProbe);
        }
    }

    [TestFixture]
    public class Given_packages_that_violate_the_package_contract
    {
        private Outcome _notAZip = null!;
        private Outcome _manifestNotVersionOne = null!;
        private Outcome _manifestNamesAnotherVersion = null!;

        [SetUp]
        public async Task Setup()
        {
            _notAZip = await ResolveInOwnWorkspace(workspace =>
                workspace.Build(
                    workspace.MinimalBuilder().WithRawPackageBytes(Encoding.UTF8.GetBytes("not a zip"))
                )
            );
            _manifestNotVersionOne = await ResolveInOwnWorkspace(workspace =>
                workspace.Build(workspace.MinimalBuilder().WithManifestText("{}"))
            );
            _manifestNamesAnotherVersion = await ResolveInOwnWorkspace(workspace =>
                workspace.Build(
                    workspace.MinimalBuilder().WithManifest(manifest => manifest["packageVersion"] = "9.9.9")
                )
            );
        }

        [Test]
        public void It_refuses_a_trusted_package_that_is_not_a_zip_as_manifest_invalid()
        {
            DmsProvisioningFailure failure = FailureOf(_notAZip.Resolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.ManifestInvalid, TemplateArtifactCatalog.ReadPackageStep));
        }

        [Test]
        public void It_refuses_a_manifest_outside_the_contract_with_the_reader_in_the_type_chain()
        {
            DmsProvisioningFailure failure = FailureOf(_manifestNotVersionOne.Resolution);
            using AssertionScope scope = new();
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be((DmsProvisioningFailureCode.ManifestInvalid, TemplateArtifactCatalog.ReadPackageStep));
            failure.Diagnostic.ExceptionTypeChain.Should().EndWith(typeof(RestoreManifestException).FullName);
        }

        [Test]
        public void It_refuses_a_manifest_naming_another_identity_as_manifest_mismatch()
        {
            DmsProvisioningFailure failure = FailureOf(_manifestNamesAnotherVersion.Resolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ManifestMismatch,
                        TemplateArtifactCatalog.MatchPackageIdentityStep
                    )
                );
        }

        [Test]
        public void It_releases_the_operation_directory_on_every_refusal()
        {
            _notAZip.OperationEntries.Should().BeEmpty();
            _manifestNotVersionOne.OperationEntries.Should().BeEmpty();
            _manifestNamesAnotherVersion.OperationEntries.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_each_configuration_comparison_of_step_six
    {
        private static readonly Dictionary<
            string,
            (
                Action<CatalogWorkspace> Arrange,
                Func<CatalogWorkspace, DmsDataStoreProvisioningOptions>? Options,
                DmsDatabaseTemplate Template
            )
        > _refused = new()
        {
            ["engine"] = (
                workspace => workspace.Build(workspace.MinimalBuilder(mssql: true)),
                workspace => workspace.Options(minimalPackageId: RestoreManifestSample.MssqlPackageId),
                DmsDatabaseTemplate.Minimal
            ),
            ["kind: the Minimal package allowlisted under Sample"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace => workspace.Options(samplePackageId: CatalogWorkspace.MinimalPackageId),
                DmsDatabaseTemplate.Sample
            ),
            ["data standard declared by the manifest"] = (
                workspace =>
                    workspace.Build(
                        Mutated(workspace, manifest => manifest["dataStandardVersion"] = "5.3.0")
                    ),
                null,
                DmsDatabaseTemplate.Minimal
            ),
            ["data standard expected by the deployment"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace =>
                    Expecting(workspace, compatibility => compatibility.DataStandardVersion = "6.1.0"),
                DmsDatabaseTemplate.Minimal
            ),
            ["effective schema hash"] = (
                workspace =>
                    workspace.Build(
                        Mutated(workspace, manifest => manifest["effectiveSchemaHash"] = _otherHash)
                    ),
                null,
                DmsDatabaseTemplate.Minimal
            ),
            ["projects: the deployment serves fewer"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace => Expecting(workspace, compatibility => compatibility.Projects = ["edfi"]),
                DmsDatabaseTemplate.Minimal
            ),
            ["projects: the manifest declares more"] = (
                workspace =>
                    workspace.Build(
                        Mutated(
                            workspace,
                            manifest => manifest["projects"] = new JsonArray("edfi", "tpdm", "sample")
                        )
                    ),
                null,
                DmsDatabaseTemplate.Minimal
            ),
            ["api schema format version expected"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace =>
                    Expecting(workspace, compatibility => compatibility.ApiSchemaFormatVersion = "2.0.0"),
                DmsDatabaseTemplate.Minimal
            ),
            ["relational mapping version expected"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace =>
                    Expecting(workspace, compatibility => compatibility.RelationalMappingVersion = "v3"),
                DmsDatabaseTemplate.Minimal
            ),
            ["document json column type"] = (
                workspace =>
                    workspace.Build(
                        Mutated(workspace, manifest => manifest["documentJsonColumnType"] = "json")
                    ),
                null,
                DmsDatabaseTemplate.Minimal
            ),
            ["order: data standard before the hash"] = (
                workspace =>
                    workspace.Build(
                        Mutated(
                            workspace,
                            manifest =>
                            {
                                manifest["dataStandardVersion"] = "5.3.0";
                                manifest["effectiveSchemaHash"] = _otherHash;
                            }
                        )
                    ),
                null,
                DmsDatabaseTemplate.Minimal
            ),
        };

        private static readonly Dictionary<string, string> _expectedCheck = new()
        {
            ["engine"] = "DatabaseEngine",
            ["kind: the Minimal package allowlisted under Sample"] = "TemplateKind",
            ["data standard declared by the manifest"] = "DataStandardVersion",
            ["data standard expected by the deployment"] = "DataStandardVersion",
            ["effective schema hash"] = "EffectiveSchemaHash",
            ["projects: the deployment serves fewer"] = "Projects",
            ["projects: the manifest declares more"] = "Projects",
            ["api schema format version expected"] = "ApiSchemaFormatVersion",
            ["relational mapping version expected"] = "RelationalMappingVersion",
            ["document json column type"] = "DocumentJsonColumnType",
            ["order: data standard before the hash"] = "DataStandardVersion",
        };

        private readonly Dictionary<string, Outcome> _outcomes = [];
        private Outcome _projectsInAnotherCaseAndOrder = null!;
        private Outcome _optionalExpectationsMet = null!;

        private static TestTemplatePackageBuilder Mutated(
            CatalogWorkspace workspace,
            Action<JsonObject> mutate
        ) => workspace.MinimalBuilder().WithManifest(mutate);

        private static DmsDataStoreProvisioningOptions Expecting(
            CatalogWorkspace workspace,
            Action<DmsDataStoreProvisioningOptions.CompatibilityExpectations> expect
        )
        {
            DmsDataStoreProvisioningOptions options = workspace.Options();
            expect(options.Compatibility!);
            return options;
        }

        [SetUp]
        public async Task Setup()
        {
            _outcomes.Clear();
            foreach ((string label, var scenario) in _refused)
            {
                _outcomes[label] = await ResolveInOwnWorkspace(
                    scenario.Arrange,
                    scenario.Options,
                    scenario.Template
                );
            }

            _projectsInAnotherCaseAndOrder = await ResolveInOwnWorkspace(
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace => Expecting(workspace, compatibility => compatibility.Projects = ["TPDM", "edfi"])
            );
            _optionalExpectationsMet = await ResolveInOwnWorkspace(
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace =>
                    Expecting(
                        workspace,
                        compatibility =>
                        {
                            compatibility.ApiSchemaFormatVersion = "1.0.0";
                            compatibility.RelationalMappingVersion = "v2";
                        }
                    )
            );
        }

        [Test]
        public void It_refuses_each_mismatch_naming_the_first_failing_check()
        {
            using AssertionScope scope = new();
            foreach ((string label, Outcome outcome) in _outcomes)
            {
                DmsProvisioningFailure failure = FailureOf(outcome.Resolution);
                (failure.Code, failure.Category, failure.Diagnostic.Operation)
                    .Should()
                    .Be(
                        (
                            DmsProvisioningFailureCode.ManifestMismatch,
                            DmsProvisioningFailureCategory.OperatorInspectionRequired,
                            $"{TemplateArtifactCatalog.MatchConfigurationStep}.{_expectedCheck[label]}"
                        ),
                        label
                    );
            }
        }

        [Test]
        public void It_covers_every_comparison() =>
            _outcomes.Keys.Should().BeEquivalentTo(_expectedCheck.Keys);

        [Test]
        public void It_releases_the_operation_directory_on_every_mismatch() =>
            _outcomes.Values.Should().OnlyContain(outcome => outcome.OperationEntries.Length == 0);

        [Test]
        public void It_reports_clean_cleanup_on_every_mismatch() =>
            _outcomes
                .Values.Select(outcome => outcome.Resolution)
                .Should()
                .AllBeOfType<DmsArtifactResolution.Failure>()
                .Which.Should()
                .OnlyContain(failure => !failure.Cleanup.HasResidue);

        [Test]
        public void It_compares_projects_as_a_set_ignoring_case() =>
            _projectsInAnotherCaseAndOrder.Resolution.Should().BeOfType<DmsArtifactResolution.Resolved>();

        [Test]
        public void It_accepts_optional_expectations_the_manifest_meets() =>
            _optionalExpectationsMet.Resolution.Should().BeOfType<DmsArtifactResolution.Resolved>();
    }

    [TestFixture]
    public class Given_both_manifests_agree_with_each_other_but_not_with_the_deployment
    {
        private CatalogWorkspace _workspace = null!;
        private IReadOnlyList<DmsReadinessReason> _hashReasons = [];
        private IReadOnlyList<DmsReadinessReason> _projectReasons = [];

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();

            DmsDataStoreProvisioningOptions otherHash = _workspace.Options();
            otherHash.Compatibility!.EffectiveSchemaHash = _otherHash;
            _hashReasons = (
                await _workspace.Catalog(otherHash).ValidateAsync(CancellationToken.None)
            ).Reasons;

            DmsDataStoreProvisioningOptions otherProjects = _workspace.Options();
            otherProjects.Compatibility!.Projects = ["edfi"];
            _projectReasons = (
                await _workspace.Catalog(otherProjects).ValidateAsync(CancellationToken.None)
            ).Reasons;
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_is_not_ready_on_the_effective_schema_hash_for_both_templates() =>
            _hashReasons
                .Should()
                .BeEquivalentTo([
                    Reason(DmsDatabaseTemplate.Minimal, "EffectiveSchemaHash"),
                    Reason(DmsDatabaseTemplate.Sample, "EffectiveSchemaHash"),
                ]);

        [Test]
        public void It_is_not_ready_on_the_project_set_for_both_templates() =>
            _projectReasons
                .Should()
                .BeEquivalentTo([
                    Reason(DmsDatabaseTemplate.Minimal, "Projects"),
                    Reason(DmsDatabaseTemplate.Sample, "Projects"),
                ]);

        [Test]
        public void It_releases_every_operation_directory() => _workspace.OperationEntries.Should().BeEmpty();

        private static DmsReadinessReason Reason(DmsDatabaseTemplate template, string check) =>
            new(
                DmsProvisioningFailureCode.ManifestMismatch,
                TemplateArtifactCatalog.CatalogProbeFor(template),
                DmsProvisioningDiagnostic.ForOperation(
                    $"{TemplateArtifactCatalog.MatchConfigurationStep}.{check}"
                )
            );
    }

    [TestFixture]
    public class Given_the_artifact_bytes_change_under_an_identical_manifest
    {
        private CatalogWorkspace _workspace = null!;
        private SpyDmsTargetEngine _engine = null!;
        private DmsTrustedArtifactPin _firstPin = null!;
        private DmsTrustedArtifactPin _secondPin = null!;
        private DmsArtifactResolution _orchestratorResolution = null!;
        private DmsDataStoreCreateResult _createWithFirstPin = null!;

        private sealed class ReadyReadiness : IDmsDataStoreProvisionerReadiness
        {
            public DmsProvisionerReadiness Current { get; } =
                new DmsProvisionerReadiness.Ready(
                    DmsDataStoreProvisioningOptions.PostgresqlProvider,
                    new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)
                );
        }

        private static DmsDataStoreProvisioningService Orchestrator(
            TemplateArtifactCatalog catalog,
            SpyDmsTargetEngine engine
        ) =>
            new(
                new ReadyReadiness(),
                engine,
                catalog,
                Options.Create(new DmsDataStoreProvisioningOptions()),
                ProtectedDatabaseNames.Create("edfi_configurationservice", []),
                new FakeAdminConnectionSource(),
                TimeProvider.System,
                NullLogger<DmsDataStoreProvisioningService>.Instance
            );

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            TemplateArtifactCatalog catalog = _workspace.Catalog();
            _firstPin = PinOf(
                await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None)
            );

            // The same package rebuilt from different artifact bytes: same schema, inventory, and identity.
            _workspace.Build(
                _workspace.MinimalBuilder().WithArtifact(Encoding.UTF8.GetBytes("-- another dump\n"))
            );
            _secondPin = PinOf(
                await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None)
            );

            _engine = new SpyDmsTargetEngine();
            DmsDataStoreProvisioningService orchestrator = Orchestrator(catalog, _engine);
            _orchestratorResolution = await orchestrator.ResolveArtifactAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            DmsTargetDatabaseName
                .TryCreate(
                    "EdFi_Ods_Test",
                    ProtectedDatabaseNames.Create("edfi_configurationservice", []),
                    out DmsTargetDatabaseName name
                )
                .Should()
                .Be(DmsTargetNameValidation.Valid);
            _createWithFirstPin = await orchestrator.CreateAsync(
                new DmsDataStoreCreateRequest(DmsDatabaseTemplate.Minimal, name, Guid.NewGuid(), _firstPin),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_pins_a_different_artifact() => _secondPin.Should().NotBe(_firstPin);

        [Test]
        public void It_differs_only_in_the_package_and_artifact_hashes() =>
            (
                _secondPin with
                {
                    PackageSha256 = _firstPin.PackageSha256,
                    ArtifactSha256 = _firstPin.ArtifactSha256,
                }
            )
                .Should()
                .Be(_firstPin);

        [Test]
        public void It_resolves_the_current_pin_through_the_orchestrator() =>
            PinOf(_orchestratorResolution).Should().Be(_secondPin);

        [Test]
        public void It_refuses_a_create_presenting_the_earlier_pin()
        {
            DmsProvisioningFailure failure = _createWithFirstPin
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            failure.Code.Should().Be(DmsProvisioningFailureCode.ArtifactChangedSinceCreate);
        }

        [Test]
        public void It_touches_no_engine_and_leaves_no_files()
        {
            _engine.Calls.Should().BeEmpty();
            _workspace.OperationEntries.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_claim_is_refused
    {
        private CatalogWorkspace _workspace = null!;
        private DmsOperationId _takenId;
        private DmsOperationId _racedId;
        private string _takenFilePath = null!;
        private string _racedFilePath = null!;
        private DmsArtifactResolution _rootMissing = null!;
        private TemplateArtifactStaging _nameTaken = null!;
        private TemplateArtifactStaging _nameRaced = null!;
        private LogEntry[] _residueLogs = [];

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            _rootMissing = await _workspace
                .Catalog(WithoutOperationsRoot(_workspace))
                .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);

            // An entry of the generated name held before the operation starts.
            _takenId = DmsOperationId.New();
            _takenFilePath = PlantForeignEntry(_takenId);
            _nameTaken = await _workspace
                .Catalog()
                .StageAsync(DmsDatabaseTemplate.Minimal, _takenId, CancellationToken.None);

            // A competitor that wins the name after every check and immediately before the claim.
            _racedId = DmsOperationId.New();
            TemplateArtifactCatalog raced = _workspace.Catalog();
            raced.BeforeClaim = () => _racedFilePath = PlantForeignEntry(_racedId);
            _nameRaced = await raced.StageAsync(
                DmsDatabaseTemplate.Minimal,
                _racedId,
                CancellationToken.None
            );

            _residueLogs =
            [
                .. _workspace.Logger.Entries.Where(entry =>
                    entry.EventId == TemplateArtifactCatalog.ResidueLeft
                ),
            ];
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        private string PlantForeignEntry(DmsOperationId operationId)
        {
            string taken = _workspace.OperationDirectoryPathOf(operationId);
            Directory.CreateDirectory(taken);
            string foreignFilePath = Path.Combine(taken, "foreign.txt");
            File.WriteAllText(foreignFilePath, "not ours");
            return foreignFilePath;
        }

        private static void ShouldBeTransientWithoutResidue(TemplateArtifactStaging staging)
        {
            TemplateArtifactStaging.Failure failure = staging
                .Should()
                .BeOfType<TemplateArtifactStaging.Failure>()
                .Subject;
            using AssertionScope scope = new();
            (failure.Detail.Code, failure.Detail.Category, failure.Detail.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingFailed,
                        DmsProvisioningFailureCategory.Transient,
                        TemplateArtifactCatalog.StagePackageStep
                    )
                );
            failure.Detail.Diagnostic.ExceptionTypeChain.Should().Be(typeof(IOException).FullName);
            failure.Cleanup.HasResidue.Should().BeFalse();
        }

        [Test]
        public void It_reports_a_missing_root_as_transient_staging_failure()
        {
            DmsProvisioningFailure failure = FailureOf(_rootMissing);
            using AssertionScope scope = new();
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingFailed,
                        DmsProvisioningFailureCategory.Transient,
                        TemplateArtifactCatalog.StagePackageStep
                    )
                );
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(DirectoryNotFoundException).FullName);
        }

        [Test]
        public void It_refuses_a_name_already_held_without_residue() =>
            ShouldBeTransientWithoutResidue(_nameTaken);

        [Test]
        public void It_refuses_a_name_a_competitor_won_at_the_claim_without_residue() =>
            ShouldBeTransientWithoutResidue(_nameRaced);

        [Test]
        public void It_leaves_the_foreign_entries_untouched()
        {
            File.ReadAllText(_takenFilePath).Should().Be("not ours");
            File.ReadAllText(_racedFilePath).Should().Be("not ours");
        }

        [Test]
        public void It_logs_no_residue_for_a_directory_it_did_not_claim() => _residueLogs.Should().BeEmpty();
    }

    /// <summary>
    /// H-03: an operations root another identity could replace entries in is a configuration defect. An operation is
    /// refused before anything is staged (so before any replay) as <c>StagingFailed</c> for inspection, naming the check
    /// and reason only; startup validation reports the same refusal for every template; with the sticky bit added the
    /// same root resolves.
    /// </summary>
    [TestFixture]
    [Platform(Exclude = "Win")]
    public class Given_an_operations_root_another_identity_could_replace
    {
        private CatalogWorkspace _workspace = null!;
        private DmsArtifactResolution _refused = null!;
        private TemplateCatalogValidation _startup = null!;
        private string[] _entriesAfterRefusal = [];
        private DmsArtifactResolution _sticky = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            UnixFileMode everyone =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(_workspace.OperationsRoot, everyone);
            _refused = await _workspace
                .Catalog()
                .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
            _startup = await _workspace.Catalog().ValidateAsync(CancellationToken.None);
            _entriesAfterRefusal = _workspace.OperationEntries;

            File.SetUnixFileMode(_workspace.OperationsRoot, everyone | UnixFileMode.StickyBit);
            _sticky = await _workspace
                .Catalog()
                .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        private static readonly string _operation =
            TemplateArtifactCatalog.StagePackageStep
            + "."
            + TemplateArtifactCatalog.OperationRootUnsafeCheck
            + "."
            + nameof(UnsafeOperationRootReason.ReplaceableEntries);

        [Test]
        public void It_refuses_the_operation_for_inspection_naming_the_check_and_reason()
        {
            DmsProvisioningFailure failure = FailureOf(_refused);
            (
                failure.Code,
                failure.Category,
                failure.Diagnostic.Operation,
                failure.Diagnostic.ExceptionTypeChain
            )
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        _operation,
                        typeof(UnsafeOperationRootException).FullName!
                    )
                );
        }

        [Test]
        public void It_stages_nothing_under_the_refused_root() => _entriesAfterRefusal.Should().BeEmpty();

        [Test]
        public void It_reports_the_refusal_at_startup_for_every_template() =>
            _startup
                .Reasons.Select(reason => (reason.Code, reason.Diagnostic!.Operation))
                .Should()
                .Equal(
                    DmsDatabaseTemplate.All.Select(_ =>
                        (DmsProvisioningFailureCode.StagingFailed, _operation)
                    )
                );

        [Test]
        public void It_resolves_once_the_root_is_sticky() =>
            _sticky.Should().BeOfType<DmsArtifactResolution.Resolved>();
    }

    [TestFixture]
    public class Given_a_trust_policy_larger_than_the_catalog_reads
    {
        private Outcome _outcome = null!;
        private IReadOnlyList<DmsReadinessReason> _startupReasons = [];

        private static void Oversized(CatalogWorkspace workspace)
        {
            workspace.BuildBoth();
            File.WriteAllBytes(
                workspace.TrustPolicyPath,
                new byte[TemplateArtifactCatalog.MaxTrustPolicyBytes + 1]
            );
        }

        [SetUp]
        public async Task Setup()
        {
            _outcome = await ResolveInOwnWorkspace(Oversized, WithoutOperationsRoot);
            _startupReasons = await ValidateInOwnWorkspace(Oversized);
        }

        [Test]
        public void It_reports_the_policy_for_inspection_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_outcome.Resolution);
            using AssertionScope scope = new();
            VerdictOf(failure).Should().Be(_unavailableForInspection);
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(TemplateTrustPolicyException).FullName);
        }

        [Test]
        public void It_still_fails_startup_validation_on_the_policy_probe() =>
            _startupReasons
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        TemplateArtifactCatalog.TrustPolicyProbe,
                        DmsProvisioningDiagnostic.From(
                            new TemplateTrustPolicyException("x"),
                            TemplateArtifactCatalog.TrustPolicyStep
                        )
                    )
                );
    }

    [TestFixture]
    public class Given_a_text_source_whose_reported_length_understates_its_content
    {
        private const int Limit = 64;
        private (string? Text, TemplateArtifactCatalog.BoundedTextDefect Defect) _overLimit;
        private (string? Text, TemplateArtifactCatalog.BoundedTextDefect Defect) _atLimit;
        private (string? Text, TemplateArtifactCatalog.BoundedTextDefect Defect) _withByteOrderMark;
        private (string? Text, TemplateArtifactCatalog.BoundedTextDefect Defect) _nonAscii;
        private (string? Text, TemplateArtifactCatalog.BoundedTextDefect Defect) _invalidUtf8;

        /// <summary>A source that reports one byte however many it yields, the way a file replaced after it was measured would.</summary>
        private sealed class UnderstatingStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override long Length => 1;
        }

        private static async Task<(string? Text, TemplateArtifactCatalog.BoundedTextDefect Defect)> ReadAsync(
            byte[] bytes
        )
        {
            await using UnderstatingStream source = new(bytes);
            return await TemplateArtifactCatalog.ReadBoundedTextAsync(source, Limit, CancellationToken.None);
        }

        [SetUp]
        public async Task Setup()
        {
            _overLimit = await ReadAsync(new byte[Limit + 1]);
            _atLimit = await ReadAsync(Encoding.ASCII.GetBytes(new string('x', Limit)));
            _withByteOrderMark = await ReadAsync([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);
            _nonAscii = await ReadAsync([0xC3, 0xA9, (byte)'{', (byte)'}']);
            _invalidUtf8 = await ReadAsync([(byte)'{', 0xFF, (byte)'}']);
        }

        [Test]
        public void It_refuses_content_beyond_the_limit_whatever_the_length_says() =>
            _overLimit.Should().Be((null, TemplateArtifactCatalog.BoundedTextDefect.TooLarge));

        [Test]
        public void It_accepts_content_at_the_limit() =>
            _atLimit.Should().Be((new string('x', Limit), TemplateArtifactCatalog.BoundedTextDefect.None));

        [Test]
        public void It_skips_a_utf8_byte_order_mark() =>
            _withByteOrderMark.Should().Be(("{}", TemplateArtifactCatalog.BoundedTextDefect.None));

        [Test]
        public void It_decodes_valid_non_ascii_text() =>
            _nonAscii.Should().Be(("\u00e9{}", TemplateArtifactCatalog.BoundedTextDefect.None));

        [Test]
        public void It_refuses_an_invalid_byte_sequence_instead_of_repairing_it() =>
            _invalidUtf8.Should().Be((null, TemplateArtifactCatalog.BoundedTextDefect.MalformedUtf8));
    }

    [TestFixture]
    public class Given_raw_bytes_outside_utf8_inside_an_ignored_member
    {
        private static readonly byte[] _invalidUtf8 = [0xFF];
        private static readonly byte[] _acuteE = [0xC3, 0xA9];

        private Outcome _attestationInvalid = null!;
        private Outcome _attestationNonAscii = null!;
        private Outcome _policyInvalid = null!;
        private Outcome _policyNonAscii = null!;
        private IReadOnlyList<DmsReadinessReason> _policyInvalidAtStartup = [];

        /// <summary>The package's attestation document with one extra string member the verifier ignores, holding the raw bytes.</summary>
        private static byte[] AttestationWith(TestTemplatePackage package, byte[] rawValueBytes)
        {
            string json = package.AttestationJson.TrimEnd();
            json = json[..^1];
            return
            [
                .. Encoding.UTF8.GetBytes(json + ",\"comment\":\""),
                .. rawValueBytes,
                .. Encoding.UTF8.GetBytes("\"}"),
            ];
        }

        /// <summary>The workspace's policy with the tracked policy's comment member holding the raw bytes.</summary>
        private static byte[] PolicyWith(CatalogWorkspace workspace, byte[] rawValueBytes)
        {
            string json = workspace.Signer.PolicyJson();
            int afterOpening = json.IndexOf('{') + 1;
            return
            [
                .. Encoding.UTF8.GetBytes(json[..afterOpening] + " \"_comment\": \""),
                .. rawValueBytes,
                .. Encoding.UTF8.GetBytes("\"," + json[afterOpening..]),
            ];
        }

        [SetUp]
        public async Task Setup()
        {
            _attestationInvalid = await ResolveInOwnWorkspace(
                workspace =>
                {
                    TestTemplatePackage package = workspace.Build(workspace.MinimalBuilder());
                    File.WriteAllBytes(package.AttestationPath, AttestationWith(package, _invalidUtf8));
                },
                WithoutOperationsRoot
            );
            _attestationNonAscii = await ResolveInOwnWorkspace(workspace =>
            {
                TestTemplatePackage package = workspace.Build(workspace.MinimalBuilder());
                File.WriteAllBytes(package.AttestationPath, AttestationWith(package, _acuteE));
            });
            _policyInvalid = await ResolveInOwnWorkspace(
                workspace =>
                {
                    workspace.Build(workspace.MinimalBuilder());
                    File.WriteAllBytes(workspace.TrustPolicyPath, PolicyWith(workspace, _invalidUtf8));
                },
                WithoutOperationsRoot
            );
            _policyNonAscii = await ResolveInOwnWorkspace(workspace =>
            {
                workspace.Build(workspace.MinimalBuilder());
                File.WriteAllBytes(workspace.TrustPolicyPath, PolicyWith(workspace, _acuteE));
            });
            _policyInvalidAtStartup = await ValidateInOwnWorkspace(workspace =>
            {
                workspace.BuildBoth();
                File.WriteAllBytes(workspace.TrustPolicyPath, PolicyWith(workspace, _invalidUtf8));
            });
        }

        [Test]
        public void It_refuses_an_attestation_with_an_invalid_byte_as_malformed_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_attestationInvalid.Resolution);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactUntrusted,
                        $"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.Malformed}"
                    )
                );
        }

        [Test]
        public void It_accepts_the_same_attestation_with_valid_non_ascii_text() =>
            _attestationNonAscii.Resolution.Should().BeOfType<DmsArtifactResolution.Resolved>();

        [Test]
        public void It_reports_a_policy_with_an_invalid_byte_for_inspection_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_policyInvalid.Resolution);
            using AssertionScope scope = new();
            VerdictOf(failure).Should().Be(_unavailableForInspection);
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(TemplateTrustPolicyException).FullName);
        }

        [Test]
        public void It_still_fails_startup_validation_for_a_policy_with_an_invalid_byte() =>
            _policyInvalidAtStartup
                .Should()
                .ContainSingle()
                .Which.Should()
                .Match<DmsReadinessReason>(reason =>
                    reason.Code == DmsProvisioningFailureCode.StartupValidationFailed
                    && reason.Probe == TemplateArtifactCatalog.TrustPolicyProbe
                );

        [Test]
        public void It_accepts_the_same_policy_with_valid_non_ascii_text() =>
            _policyNonAscii.Resolution.Should().BeOfType<DmsArtifactResolution.Resolved>();
    }

    [TestFixture]
    public class Given_the_operation_root_setting
    {
        private string _defaultRoot = null!;
        private string _configuredRoot = null!;

        [SetUp]
        public void Setup()
        {
            _defaultRoot = TemplateArtifactCatalog.OperationRootDirectoryFor(
                new DmsDataStoreProvisioningOptions()
            );
            _configuredRoot = TemplateArtifactCatalog.OperationRootDirectoryFor(
                new DmsDataStoreProvisioningOptions
                {
                    OperationDirectory = Path.GetTempPath() + "dms-operations" + Path.DirectorySeparatorChar,
                }
            );
        }

        [Test]
        public void It_defaults_to_the_process_temp_path() =>
            _defaultRoot.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));

        [Test]
        public void It_uses_the_configured_directory_without_a_trailing_separator() =>
            _configuredRoot.Should().Be(Path.Combine(Path.GetTempPath(), "dms-operations"));
    }

    /// <summary>
    /// Step 4.9 review: the startup validation reports the <c>engineVersion</c> of each manifest it authenticated,
    /// per template, and nothing for a template that did not resolve or when the policy cannot load.
    /// </summary>
    [TestFixture]
    public class Given_templates_whose_manifests_declare_engine_versions
    {
        private CatalogWorkspace _both = null!;
        private CatalogWorkspace _minimalOnly = null!;
        private CatalogWorkspace _noPolicy = null!;
        private TemplateCatalogValidation _bothValidation = null!;
        private TemplateCatalogValidation _minimalOnlyValidation = null!;
        private TemplateCatalogValidation _noPolicyValidation = null!;

        [SetUp]
        public async Task Setup()
        {
            _both = new CatalogWorkspace();
            _both.Build(_both.MinimalBuilder());
            _both.Build(_both.SampleBuilder().WithManifest(manifest => manifest["engineVersion"] = "17.1"));
            _bothValidation = await _both.Catalog().ValidateAsync(CancellationToken.None);

            _minimalOnly = new CatalogWorkspace();
            _minimalOnly.Build(_minimalOnly.MinimalBuilder());
            _minimalOnlyValidation = await _minimalOnly.Catalog().ValidateAsync(CancellationToken.None);

            _noPolicy = new CatalogWorkspace();
            _noPolicy.BuildBoth();
            File.Delete(_noPolicy.TrustPolicyPath);
            _noPolicyValidation = await _noPolicy
                .Catalog(WithoutOperationsRoot(_noPolicy))
                .ValidateAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _both.Dispose();
            _minimalOnly.Dispose();
            _noPolicy.Dispose();
        }

        [Test]
        public void It_passes_the_probes_for_both() => _bothValidation.Reasons.Should().BeEmpty();

        [Test]
        public void It_reports_each_templates_own_engine_version() =>
            _bothValidation
                .EngineRequirements.Should()
                .Equal(
                    new TemplateEngineRequirement(DmsDatabaseTemplate.Minimal, "16.8"),
                    new TemplateEngineRequirement(DmsDatabaseTemplate.Sample, "17.1")
                );

        [Test]
        public void It_reports_no_engine_version_for_a_template_that_did_not_resolve()
        {
            _minimalOnlyValidation
                .EngineRequirements.Should()
                .Equal(new TemplateEngineRequirement(DmsDatabaseTemplate.Minimal, "16.8"));
            _minimalOnlyValidation
                .Reasons.Should()
                .ContainSingle()
                .Which.Probe.Should()
                .Be(TemplateArtifactCatalog.CatalogProbeFor(DmsDatabaseTemplate.Sample));
        }

        [Test]
        public void It_reports_no_engine_version_when_the_policy_cannot_load() =>
            _noPolicyValidation.EngineRequirements.Should().BeEmpty();
    }

    /// <summary>
    /// F-06 (D-6b): temporary file contention is recognised only by the <c>HResult</c> values pinned on .NET 10 for a
    /// policy another handle holds; every other I/O failure, including a missing file, is not.
    /// </summary>
    [TestFixture]
    public class Given_io_failures_to_classify
    {
        [Test]
        public void It_recognizes_each_pinned_contention_value() =>
            _pinnedContention
                .Select(hresult =>
                    TemplateArtifactCatalog.IsTemporaryFileContention(new IOException("x", hresult))
                )
                .Should()
                .Equal(true, true, true);

        [Test]
        public void It_recognizes_no_other_value()
        {
            IOException[] others =
            [
                new("x", unchecked((int)0x80070005)), // access denied
                new("x", unchecked((int)0x8007001F)), // general failure
                new("x", unchecked((int)0x80070070)), // disk full
                new("x", 5), // Linux EIO
                new("x", 0),
                new(),
                new FileNotFoundException("x"),
                new DirectoryNotFoundException("x"),
            ];
            others
                .Select(TemplateArtifactCatalog.IsTemporaryFileContention)
                .Should()
                .Equal(Enumerable.Repeat(false, others.Length));
        }
    }

    /// <summary>
    /// F-06: a trust-policy defect met by an operation (the public resolve and stage) is
    /// <c>TrustPolicyUnavailable</c> at <c>TrustPolicy</c>, before staging: transient only for a pinned contention
    /// value, and for inspection for any other I/O failure, a missing or denied file, an oversized policy, or one
    /// that is not UTF-8. The diagnostic holds the type chain only, never the path the exception names.
    /// </summary>
    [TestFixture]
    public class Given_the_trust_policy_read_fails_during_an_operation
    {
        private CatalogWorkspace _workspace = null!;
        private string _path = string.Empty;
        private DmsProvisioningFailure[] _contention = [];
        private DmsProvisioningFailure _generalFailure = null!;
        private DmsProvisioningFailure _notFound = null!;
        private DmsProvisioningFailure _denied = null!;
        private DmsProvisioningFailure _tooLarge = null!;
        private DmsProvisioningFailure _malformed = null!;
        private DmsProvisioningFailure _stagedContention = null!;

        private async Task<DmsProvisioningFailure> ResolveThroughAsync(
            TemplateArtifactCatalog.BoundedTextReader reader
        ) =>
            FailureOf(
                await _workspace
                    .CatalogReadingPolicyThrough(reader, WithoutOperationsRoot(_workspace))
                    .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None)
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            _path = _workspace.TrustPolicyPath;
            List<DmsProvisioningFailure> contention = [];
            foreach (int hresult in _pinnedContention)
            {
                contention.Add(
                    await ResolveThroughAsync(Throwing(new IOException($"{_path} is in use.", hresult)))
                );
            }

            _contention = [.. contention];
            _generalFailure = await ResolveThroughAsync(
                Throwing(new IOException($"{_path} failed.", unchecked((int)0x8007001F)))
            );
            _notFound = await ResolveThroughAsync(Throwing(new FileNotFoundException("Not found.", _path)));
            _denied = await ResolveThroughAsync(
                Throwing(new UnauthorizedAccessException($"Access to {_path} is denied."))
            );
            _tooLarge = await ResolveThroughAsync(
                Returning(TemplateArtifactCatalog.BoundedTextDefect.TooLarge)
            );
            _malformed = await ResolveThroughAsync(
                Returning(TemplateArtifactCatalog.BoundedTextDefect.MalformedUtf8)
            );
            TemplateArtifactStaging staged = await _workspace
                .CatalogReadingPolicyThrough(
                    Throwing(new IOException($"{_path} is in use.", _pinnedContention[0])),
                    WithoutOperationsRoot(_workspace)
                )
                .StageAsync(DmsDatabaseTemplate.Minimal, DmsOperationId.New(), CancellationToken.None);
            _stagedContention = staged.Should().BeOfType<TemplateArtifactStaging.Failure>().Subject.Detail;
        }

        [OneTimeTearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_reports_each_pinned_contention_value_as_transient() =>
            _contention
                .Select(VerdictOf)
                .Should()
                .Equal(Enumerable.Repeat(_unavailableForNow, _pinnedContention.Length));

        [Test]
        public void It_does_not_assume_any_other_io_failure_is_transient() =>
            VerdictOf(_generalFailure).Should().Be(_unavailableForInspection);

        [Test]
        public void It_reports_a_missing_or_denied_policy_for_inspection() =>
            new[] { _notFound, _denied }
                .Select(VerdictOf)
                .Should()
                .Equal(_unavailableForInspection, _unavailableForInspection);

        [Test]
        public void It_reports_an_oversized_or_malformed_policy_for_inspection() =>
            new[] { _tooLarge, _malformed }
                .Select(VerdictOf)
                .Should()
                .Equal(_unavailableForInspection, _unavailableForInspection);

        [Test]
        public void It_treats_the_public_stage_as_an_operation() =>
            VerdictOf(_stagedContention).Should().Be(_unavailableForNow);

        [Test]
        public void It_keeps_only_the_type_chain_and_never_the_path()
        {
            using AssertionScope scope = new();
            _generalFailure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(IOException).FullName);
            _denied.Diagnostic.ExceptionTypeChain.Should().Be(typeof(UnauthorizedAccessException).FullName);
            foreach (
                DmsProvisioningFailure failure in new[] { _generalFailure, _notFound, _denied }.Concat(
                    _contention
                )
            )
            {
                failure.Diagnostic.ProviderErrorCode.Should().BeNull();
                new[] { failure.Diagnostic.ExceptionTypeChain, failure.Diagnostic.Operation, failure.Summary }
                    .Should()
                    .NotContain(text => text.Contains(_path, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// F-06 on a real file: a policy another handle holds with <c>FileShare.None</c> is transient for an operation
    /// (Windows sharing violation, Linux <c>EWOULDBLOCK</c>) and still a startup validation failure for readiness.
    /// </summary>
    [TestFixture]
    public class Given_the_trust_policy_file_is_held_exclusively
    {
        private DmsProvisioningFailure _operation = null!;
        private IReadOnlyList<DmsReadinessReason> _startup = [];

        [SetUp]
        public async Task Setup()
        {
            using CatalogWorkspace workspace = new();
            workspace.BuildBoth();
            using FileStream holder = new(
                workspace.TrustPolicyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            );
            _operation = FailureOf(
                await workspace
                    .Catalog(WithoutOperationsRoot(workspace))
                    .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None)
            );
            _startup = (await workspace.Catalog().ValidateAsync(CancellationToken.None)).Reasons;
        }

        [Test]
        public void It_reports_the_held_policy_as_transient_for_an_operation()
        {
            using AssertionScope scope = new();
            VerdictOf(_operation).Should().Be(_unavailableForNow);
            _operation.Diagnostic.ExceptionTypeChain.Should().Be(typeof(IOException).FullName);
        }

        [Test]
        public void It_still_fails_startup_validation_on_the_policy_probe() =>
            _startup
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        TemplateArtifactCatalog.TrustPolicyProbe,
                        DmsProvisioningDiagnostic.From(
                            new IOException(),
                            TemplateArtifactCatalog.TrustPolicyStep
                        )
                    )
                );
    }

    /// <summary>
    /// F-06, supplementary on Unix: a real policy file the service may not read (mode <c>000</c>) is for inspection.
    /// Windows covers the denial mapping through the seam in
    /// <see cref="Given_the_trust_policy_read_fails_during_an_operation"/>. Root ignores the mode, so this runs as
    /// any non-root user. .NET reports the denial with the <c>EACCES</c> as an inner <see cref="IOException"/>.
    /// </summary>
    [TestFixture]
    [Platform(Exclude = "Win")]
    public class Given_the_trust_policy_file_may_not_be_read
    {
        private DmsProvisioningFailure? _operation;

        [SetUp]
        public async Task Setup()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            using CatalogWorkspace workspace = new();
            workspace.BuildBoth();
            File.SetUnixFileMode(workspace.TrustPolicyPath, UnixFileMode.None);
            try
            {
                _operation = FailureOf(
                    await workspace
                        .Catalog(WithoutOperationsRoot(workspace))
                        .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None)
                );
            }
            finally
            {
                File.SetUnixFileMode(
                    workspace.TrustPolicyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                );
            }
        }

        [Test]
        public void It_reports_the_denied_policy_for_inspection()
        {
            using AssertionScope scope = new();
            VerdictOf(_operation!).Should().Be(_unavailableForInspection);
            _operation!
                .Diagnostic.ExceptionTypeChain.Should()
                .Be($"{typeof(UnauthorizedAccessException).FullName}/{typeof(IOException).FullName}");
        }
    }

    /// <summary>
    /// F-06, nested startup context: startup validation loads the policy itself (call 1, passed through to the real
    /// reader) and again inside each template's resolution (calls 2 and 3). A defect on call 2, the first template's
    /// load, is that template's catalog probe failing as <c>StartupValidationFailed</c>, never
    /// <c>TrustPolicyUnavailable</c>, for an unrecognised I/O failure, a pinned contention value, and malformed UTF-8
    /// alike. The other template still resolves.
    /// </summary>
    [TestFixture]
    public class Given_the_policy_fails_inside_a_templates_startup_resolution
    {
        private readonly Dictionary<
            string,
            (IReadOnlyList<DmsReadinessReason> Reasons, int Calls)
        > _variants = [];

        private static async Task<(IReadOnlyList<DmsReadinessReason>, int)> ValidateFailingCallTwoAsync(
            TemplateArtifactCatalog.BoundedTextReader onCallTwo
        )
        {
            using CatalogWorkspace workspace = new();
            workspace.BuildBoth();
            TemplateArtifactCatalog.BoundedTextReader real = workspace.Catalog().ReadTrustPolicyText;
            int calls = 0;
            TemplateArtifactCatalog catalog = workspace.CatalogReadingPolicyThrough(
                (path, maxBytes, cancellationToken) =>
                    ++calls == 2
                        ? onCallTwo(path, maxBytes, cancellationToken)
                        : real(path, maxBytes, cancellationToken)
            );
            IReadOnlyList<DmsReadinessReason> reasons = (
                await catalog.ValidateAsync(CancellationToken.None)
            ).Reasons;
            return (reasons, calls);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            _variants["unrecognised"] = await ValidateFailingCallTwoAsync(
                Throwing(new IOException("x", unchecked((int)0x8007001F)))
            );
            _variants["contention"] = await ValidateFailingCallTwoAsync(
                Throwing(new IOException("x", _pinnedContention[0]))
            );
            _variants["malformed"] = await ValidateFailingCallTwoAsync(
                Returning(TemplateArtifactCatalog.BoundedTextDefect.MalformedUtf8)
            );
        }

        [Test]
        public void It_loads_the_policy_once_for_startup_and_once_per_template() =>
            _variants.Values.Select(variant => variant.Calls).Should().Equal(3, 3, 3);

        [Test]
        public void It_fails_the_first_templates_catalog_probe_as_a_startup_failure() =>
            _variants
                .Values.Select(variant =>
                    variant
                        .Reasons.Select(reason => (reason.Code, reason.Probe, reason.Diagnostic?.Operation))
                        .ToArray()
                )
                .Should()
                .AllSatisfy(reasons =>
                    reasons
                        .Should()
                        .Equal(
                            (
                                DmsProvisioningFailureCode.StartupValidationFailed,
                                TemplateArtifactCatalog.CatalogProbeFor(DmsDatabaseTemplate.All[0]),
                                TemplateArtifactCatalog.TrustPolicyStep
                            )
                        )
                );
    }
}
