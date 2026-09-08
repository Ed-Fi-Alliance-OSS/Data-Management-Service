// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// Real CLI process, packaged history provider, local durable store, and isolated provider database.
/// Bindings and retirement proofs are synthetic fixture inputs; no broker, capture, or purge is exercised.
/// </summary>
[TestFixtureSource(nameof(Scenarios))]
[NonParallelizable]
[Category("Runbook")]
public sealed class Given_DocumentCacheAdminPackagedHistoryRejections(
    string provider,
    string command,
    string evidence
)
{
    private const string DeploymentKey = "runbook-rejections";
    private const string OtherFingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private DocumentCacheAdminCliTarget _target = null!;
    private DocumentCacheAdminCliProcessHarness _harness = null!;
    private DocumentCacheAdminCliProcessResult _result = null!;
    private StateSnapshot _before = null!;
    private StateSnapshot _after = null!;
    private Dictionary<string, string> _recordsBefore = [];
    private Dictionary<string, string> _recordsAfter = [];
    private string _fingerprint = string.Empty;
    private string _artifactPath = string.Empty;
    private string _observedHistory = string.Empty;

    public static IEnumerable<TestFixtureData> Scenarios()
    {
        foreach (string provider in new[] { "postgresql", "mssql" })
        {
            foreach (
                string command in new[] { "activate-offline", "deactivate-offline", "recover-cache-ahead" }
            )
            {
                foreach (
                    string evidence in new[]
                    {
                        "active",
                        "historical",
                        "unknown",
                        "missing",
                        "mismatchedTarget",
                        "mismatchedSource",
                    }
                )
                {
                    TestFixtureData fixture = new(provider, command, evidence);
                    fixture.Properties.Add(
                        "Category",
                        provider == "postgresql" ? "PostgresqlIntegration" : "MssqlIntegration"
                    );
                    yield return fixture;
                }
            }
        }
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        _target =
            provider == "postgresql"
                ? await DocumentCacheAdminCliTarget.CreatePostgresqlAsync()
                : await Given_DocumentCacheAdminMssqlRebuildOnline.CreateReadyMssqlTargetAsync();
        _harness = await DocumentCacheAdminCliProcessHarness.CreateAsync(_target);
        await _harness.ConfigureCdcBindingStateAsync(DeploymentKey);

        if (provider == "mssql")
        {
            await Given_DocumentCacheAdminMssqlStatus.WithNestedTriggersAsync(
                _target,
                enabled: true,
                ArrangeAndActAsync
            );
        }
        else
        {
            await ArrangeAndActAsync();
        }
    }

    private async Task ArrangeAndActAsync()
    {
        DocumentCacheAdminCliSeededDocument document =
            provider == "postgresql"
                ? await _target.State.InsertPostgresqlCanonicalDocumentAsync()
                : await _target.State.InsertMssqlCanonicalDocumentAsync();
        bool recovery = command == "recover-cache-ahead";
        if (provider == "postgresql")
        {
            await _target.State.InsertPostgresqlDocumentCacheAsync(
                document,
                cacheContentVersion: document.ContentVersion + (recovery ? 1 : 0)
            );
            await _target.State.InsertPostgresqlProjectionWorkAsync(document);
        }
        else
        {
            await _target.State.InsertMssqlDocumentCacheAsync(
                document,
                cacheContentVersion: document.ContentVersion + (recovery ? 1 : 0)
            );
            await _target.State.InsertMssqlProjectionWorkAsync(document);
        }
        await _target.State.SetLifecycleAsync(
            command == "activate-offline" ? "Disabled" : "Tracking",
            recovery
        );
        _fingerprint = await _target.State.ReadPhysicalSourceFingerprintAsync();
        await SeedHistoryAsync();
        _before = await SnapshotAsync();
        _recordsBefore = ReadRecords();

        _result = await _harness.RunAsync(
            command,
            "--data-store-id",
            _target.DataStoreId.ToString(CultureInfo.InvariantCulture),
            "--confirm",
            command switch
            {
                "activate-offline" => "offlineActivation",
                "deactivate-offline" => "offlineDeactivation",
                _ => "internalCacheAheadRecovery",
            },
            "--offline-writer-admission",
            "closedAndDrained",
            "--expected-physical-source-fingerprint",
            _fingerprint,
            "--json",
            "--command-timeout-seconds",
            "30"
        );
        _after = await SnapshotAsync();
        _recordsAfter = ReadRecords();

        _artifactPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"{provider}-{command}-{evidence}.json"
        );
        await File.WriteAllTextAsync(
            _artifactPath,
            JsonSerializer.Serialize(
                new
                {
                    provider,
                    command,
                    evidence,
                    observedHistory = _observedHistory,
                    boundary = "packaged CLI, synthetic durable records, real provider database; no broker/capture",
                    _result.ExitCode,
                    _result.StandardOutput,
                    _result.StandardError,
                    before = _before,
                    after = _after,
                    recordsBefore = _recordsBefore,
                    recordsAfter = _recordsAfter,
                },
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
    }

    [Test]
    public void It_reports_the_history_rejection_for_the_exact_target_and_source()
    {
        JsonObject result = DocumentCacheAdminCliCommandResultAssertions.AssertCommandResult(
            _result,
            _target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        RunbookWorkflowAssertions.AssertCommandContract(
            result,
            command switch
            {
                "activate-offline" => "offlineActivation",
                "deactivate-offline" => "offlineDeactivation",
                _ => "internalOnlyCacheAheadRecovery",
            },
            "rejectedNoMutation",
            "downstreamHistoryPresentOrUnknown",
            false,
            command == "activate-offline" ? "disabled" : "tracking",
            command == "recover-cache-ahead"
        );
        result["physicalSourceFingerprint"]!.GetValue<string>().Should().Be(_fingerprint);
        result["phaseDiagnostics"]!.AsArray().Should().ContainSingle();
        result["phaseDiagnostics"]![0]!["diagnosticCategory"]!
            .GetValue<string>()
            .Should()
            .Be("downstreamPublicationHistoryPresentOrUnknown");
        result["phaseDiagnostics"]![0]!["message"]!
            .GetValue<string>()
            .Should()
            .Be("Downstream publication history is not internal-only.");
        TestContext.AddTestAttachment(_artifactPath);
    }

    [Test]
    public void It_preserves_lifecycle_latch_and_database_evidence() =>
        _after.Should().BeEquivalentTo(_before);

    [Test]
    public void It_preserves_the_durable_binding_and_retirement_evidence() =>
        _recordsAfter.Should().BeEquivalentTo(_recordsBefore);

    [Test]
    public void It_keeps_json_on_stdout_and_secrets_out_of_both_streams()
    {
        _result.StandardError.Should().BeEmpty();
        _result.StandardOutput.TrimEnd().Should().NotContain("\n");
        _result
            .StandardOutput.Should()
            .NotContain(_target.ConnectionString)
            .And.NotContain(_harness.SecretFromEnvironment);
        _harness.ConfigurationService.DataStoresRequestCount.Should().BeGreaterThan(0);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
        if (_target is not null)
        {
            await _target.DisposeAsync();
        }
    }

    private async Task SeedHistoryAsync()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddDmsCdcControlPlane()
            .Configure<CdcBindingStateStoreOptions>(options =>
                options.RootPath = _harness.CdcBindingStatePath
            )
            .BuildServiceProvider();
        ICdcBindingLifecycleService lifecycle = services.GetRequiredService<ICdcBindingLifecycleService>();
        CdcArtifactInventory names = CdcArtifactNameGenerator
            .Render(
                new(
                    DeploymentKey,
                    "edfi.documents",
                    "instance",
                    1,
                    provider == "postgresql" ? CdcProvider.Postgresql : CdcProvider.SqlServer
                )
            )
            .Inventory!;
        CdcBinding binding = new(
            CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            evidence == "mismatchedTarget" ? "another-tenant" : "default",
            "1",
            "instance",
            1,
            provider == "postgresql" ? CdcProvider.Postgresql : CdcProvider.SqlServer,
            evidence == "mismatchedSource" ? OtherFingerprint : _fingerprint,
            names.ConnectorName,
            names.TopicName,
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );

        if (evidence != "missing")
        {
            CdcBindingLifecycleResult created = await lifecycle.CreateBindingIfAbsentAsync(binding);
            created
                .Status.Should()
                .Be(
                    CdcControlPlaneOperationStatus.Succeeded,
                    string.Join("; ", created.Diagnostics.Select(diagnostic => diagnostic.Message))
                );
        }
        if (evidence == "historical")
        {
            // Fixture proof only: seed the same retained record the lifecycle service writes on cleanup.
            CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;
            CdcCleanupProof proof = new(
                CdcJsonContract.CurrentContractVersion,
                "fixture-retirement",
                DateTimeOffset.UnixEpoch,
                binding.ToCompleteBindingIdentity(),
                CdcCleanupMode.RetireBindingGeneration,
                [
                    .. inventory.GovernedArtifacts.Select(artifact => new CdcGovernedArtifact(
                        artifact.Kind,
                        artifact.Name,
                        CdcCleanupState.Deleted,
                        "fixture cleanup proof"
                    )),
                ]
            );
            (await lifecycle.DeleteStateAfterVerifiedCleanupAsync(proof))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
        }
        if (evidence == "unknown")
        {
            string bindingFile = Directory
                .GetFiles(_harness.CdcBindingStatePath, "*.json", SearchOption.AllDirectories)
                .Single();
            await File.WriteAllTextAsync(bindingFile, "{unreadable fixture record");
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [CdcDownstreamPublicationHistoryProvider.DeploymentKeyConfigurationPath] = DeploymentKey,
                }
            )
            .Build();
        CdcDownstreamPublicationHistoryProvider history = new(lifecycle, configuration, TimeProvider.System);
        DocumentCacheDownstreamPublicationHistoryObservation observation = await history.ObserveAsync(
            DocumentCacheTargetKey.Create(_target.TenantKey, _target.DataStoreId),
            new DocumentCachePhysicalSourceFingerprint(_fingerprint)
        );
        _observedHistory = observation.Status.ToString();
        observation
            .Status.Should()
            .Be(
                evidence switch
                {
                    "active" => DocumentCacheDownstreamPublicationStatus.Active,
                    "historical" or "mismatchedSource" => DocumentCacheDownstreamPublicationStatus.Historical,
                    _ => DocumentCacheDownstreamPublicationStatus.Unknown,
                }
            );
    }

    private async Task<StateSnapshot> SnapshotAsync() =>
        new(
            await _target.State.ReadLifecycleAsync(),
            await _target.State.ReadMutableCountsAsync(),
            await _target.State.ReadCachedVersionsByDocumentIdAsync(),
            (await _target.State.ReadOldestWorkFirstEnqueuedAtAsync())
                ?? throw new InvalidOperationException("Fixture projection work is missing."),
            await _target.State.ReadCanonicalDocumentCountAsync(),
            await _target.State.ReadPhysicalSourceFingerprintAsync(),
            provider == "postgresql"
                ? await _target.State.ReadPostgresqlCachedJsonByDocumentIdAsync()
                : await _target.State.ReadMssqlCachedJsonByDocumentIdAsync(),
            provider == "postgresql"
                ? await _target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync()
                : await _target.State.ReadMssqlWorkVersionsByDocumentIdAsync()
        );

    private Dictionary<string, string> ReadRecords() =>
        Directory.Exists(_harness.CdcBindingStatePath)
            ? Directory
                .GetFiles(_harness.CdcBindingStatePath, "*.json", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(_harness.CdcBindingStatePath, path),
                    File.ReadAllText
                )
            : [];

    private sealed record StateSnapshot(
        DocumentCacheAdminCliLifecycleState Lifecycle,
        DocumentCacheAdminCliMutableCounts Counts,
        IReadOnlyDictionary<long, long> CachedVersions,
        DateTime OldestWorkFirstEnqueuedAt,
        long CanonicalDocuments,
        string PhysicalSourceFingerprint,
        IReadOnlyDictionary<long, string> Cache,
        IReadOnlyDictionary<long, long> Work
    );
}
