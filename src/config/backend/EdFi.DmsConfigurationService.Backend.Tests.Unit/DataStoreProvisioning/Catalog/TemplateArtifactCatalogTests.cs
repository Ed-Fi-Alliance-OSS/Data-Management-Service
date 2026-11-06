// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
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
    private sealed record Outcome(DmsArtifactResolution Resolution, string[] OperationEntries, LogEntry[] Logs);

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
            _minimalResolution = await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
            _sampleResolution = await catalog.ResolveAsync(DmsDatabaseTemplate.Sample, CancellationToken.None);
            _reasons = await catalog.ValidateAsync(CancellationToken.None);
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
                    entry.EventId == TemplateArtifactCatalog.ArtifactRefused && entry.Level == LogLevel.Warning
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
                        workspace.Build(workspace.MinimalBuilder().WithPolicyListingAnotherKey()).AttestationPath
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
                .Be($"{TemplateArtifactCatalog.VerifyAttestationStep}.{TrustRefusalReason.PackageHashMismatch}");

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
            _noProducersReasons = await noProducers.ValidateAsync(CancellationToken.None);

            _malformed = new CatalogWorkspace();
            _malformed.BuildBoth();
            _malformed.WriteTrustPolicy("{");
            TemplateArtifactCatalog malformed = _malformed.Catalog(WithoutOperationsRoot(_malformed));
            _malformedResolution = await malformed.ResolveAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _malformedReasons = await malformed.ValidateAsync(CancellationToken.None);

            _missing = new CatalogWorkspace();
            _missing.BuildBoth();
            File.Delete(_missing.TrustPolicyPath);
            TemplateArtifactCatalog missing = _missing.Catalog(WithoutOperationsRoot(_missing));
            _missingResolution = await missing.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
            _missingReasons = await missing.ValidateAsync(CancellationToken.None);
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
        public void It_reports_a_malformed_policy_as_not_ready_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_malformedResolution);
            using AssertionScope scope = new();
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        DmsProvisioningFailureCategory.NotReady,
                        TemplateArtifactCatalog.TrustPolicyStep
                    )
                );
            failure.Diagnostic.ExceptionTypeChain.Should().Be(typeof(TemplateTrustPolicyException).FullName);
        }

        [Test]
        public void It_reports_a_missing_policy_as_not_ready_before_staging()
        {
            DmsProvisioningFailure failure = FailureOf(_missingResolution);
            using AssertionScope scope = new();
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        DmsProvisioningFailureCategory.NotReady,
                        TemplateArtifactCatalog.TrustPolicyStep
                    )
                );
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
                workspace.Build(workspace.MinimalBuilder().WithRawPackageBytes(Encoding.UTF8.GetBytes("not a zip")))
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
                .Be((DmsProvisioningFailureCode.ManifestMismatch, TemplateArtifactCatalog.MatchPackageIdentityStep));
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
            (Action<CatalogWorkspace> Arrange, Func<CatalogWorkspace, DmsDataStoreProvisioningOptions>? Options, DmsDatabaseTemplate Template)
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
                workspace => workspace.Build(Mutated(workspace, manifest => manifest["dataStandardVersion"] = "5.3.0")),
                null,
                DmsDatabaseTemplate.Minimal
            ),
            ["data standard expected by the deployment"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace => Expecting(workspace, compatibility => compatibility.DataStandardVersion = "6.1.0"),
                DmsDatabaseTemplate.Minimal
            ),
            ["effective schema hash"] = (
                workspace => workspace.Build(Mutated(workspace, manifest => manifest["effectiveSchemaHash"] = _otherHash)),
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
                        Mutated(workspace, manifest => manifest["projects"] = new JsonArray("edfi", "tpdm", "sample"))
                    ),
                null,
                DmsDatabaseTemplate.Minimal
            ),
            ["api schema format version expected"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace => Expecting(workspace, compatibility => compatibility.ApiSchemaFormatVersion = "2.0.0"),
                DmsDatabaseTemplate.Minimal
            ),
            ["relational mapping version expected"] = (
                workspace => workspace.Build(workspace.MinimalBuilder()),
                workspace => Expecting(workspace, compatibility => compatibility.RelationalMappingVersion = "v3"),
                DmsDatabaseTemplate.Minimal
            ),
            ["document json column type"] = (
                workspace => workspace.Build(Mutated(workspace, manifest => manifest["documentJsonColumnType"] = "json")),
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

        private static TestTemplatePackageBuilder Mutated(CatalogWorkspace workspace, Action<JsonObject> mutate) =>
            workspace.MinimalBuilder().WithManifest(mutate);

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
        public void It_covers_every_comparison() => _outcomes.Keys.Should().BeEquivalentTo(_expectedCheck.Keys);

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
            _hashReasons = await _workspace.Catalog(otherHash).ValidateAsync(CancellationToken.None);

            DmsDataStoreProvisioningOptions otherProjects = _workspace.Options();
            otherProjects.Compatibility!.Projects = ["edfi"];
            _projectReasons = await _workspace.Catalog(otherProjects).ValidateAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_is_not_ready_on_the_effective_schema_hash_for_both_templates() =>
            _hashReasons
                .Should()
                .BeEquivalentTo(
                    [
                        Reason(DmsDatabaseTemplate.Minimal, "EffectiveSchemaHash"),
                        Reason(DmsDatabaseTemplate.Sample, "EffectiveSchemaHash"),
                    ]
                );

        [Test]
        public void It_is_not_ready_on_the_project_set_for_both_templates() =>
            _projectReasons
                .Should()
                .BeEquivalentTo(
                    [Reason(DmsDatabaseTemplate.Minimal, "Projects"), Reason(DmsDatabaseTemplate.Sample, "Projects")]
                );

        [Test]
        public void It_releases_every_operation_directory() => _workspace.OperationEntries.Should().BeEmpty();

        private static DmsReadinessReason Reason(DmsDatabaseTemplate template, string check) =>
            new(
                DmsProvisioningFailureCode.ManifestMismatch,
                TemplateArtifactCatalog.CatalogProbeFor(template),
                DmsProvisioningDiagnostic.ForOperation($"{TemplateArtifactCatalog.MatchConfigurationStep}.{check}")
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
                TimeProvider.System,
                NullLogger<DmsDataStoreProvisioningService>.Instance
            );

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            TemplateArtifactCatalog catalog = _workspace.Catalog();
            _firstPin = PinOf(await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None));

            // The same package rebuilt from different artifact bytes: same schema, inventory, and identity.
            _workspace.Build(_workspace.MinimalBuilder().WithArtifact(Encoding.UTF8.GetBytes("-- another dump\n")));
            _secondPin = PinOf(await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None));

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
            (_secondPin with { PackageSha256 = _firstPin.PackageSha256, ArtifactSha256 = _firstPin.ArtifactSha256 })
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
    public class Given_the_operation_root_cannot_be_used
    {
        private CatalogWorkspace _workspace = null!;
        private DmsOperationId _operationId;
        private string _foreignFilePath = null!;
        private DmsArtifactResolution _rootMissing = null!;
        private TemplateArtifactStaging _nameTaken = null!;

        [SetUp]
        public async Task Setup()
        {
            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            _rootMissing = await _workspace
                .Catalog(WithoutOperationsRoot(_workspace))
                .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);

            // An entry of the generated name that this operation did not create.
            _operationId = DmsOperationId.New();
            string taken = _workspace.OperationDirectoryPathOf(_operationId);
            Directory.CreateDirectory(taken);
            _foreignFilePath = Path.Combine(taken, "foreign.txt");
            await File.WriteAllTextAsync(_foreignFilePath, "not ours");
            _nameTaken = await _workspace
                .Catalog()
                .StageAsync(DmsDatabaseTemplate.Minimal, _operationId, CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

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
        public void It_refuses_a_taken_name_as_transient_without_claiming_it()
        {
            TemplateArtifactStaging.Failure failure = _nameTaken
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
            failure.Cleanup.HasResidue.Should().BeFalse();
        }

        [Test]
        public void It_leaves_the_foreign_entry_untouched() =>
            File.ReadAllText(_foreignFilePath).Should().Be("not ours");
    }

    [TestFixture]
    public class Given_the_operation_root_setting
    {
        private string _defaultRoot = null!;
        private string _configuredRoot = null!;

        [SetUp]
        public void Setup()
        {
            _defaultRoot = TemplateArtifactCatalog.OperationRootDirectoryFor(new DmsDataStoreProvisioningOptions());
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
}
