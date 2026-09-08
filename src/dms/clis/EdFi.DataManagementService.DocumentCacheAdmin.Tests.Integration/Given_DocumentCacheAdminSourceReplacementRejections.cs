// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// Packaged CLI refusals over a real provider database and a synthetic outgoing binding.
/// No connector or broker is provisioned; successful replacement requires the upstream provisioning handoff.
/// </summary>
[TestFixtureSource(nameof(Scenarios))]
[NonParallelizable]
[Category("CdcSourceReplacement")]
public sealed class Given_DocumentCacheAdminSourceReplacementRejections(string provider, string evidence)
{
    private const string DeploymentKey = "replacement-rejections";
    private const string InstanceKey = "instance";
    private DocumentCacheAdminCliTarget _target = null!;
    private DocumentCacheAdminCliProcessHarness _harness = null!;
    private DocumentCacheAdminCliProcessResult _result = null!;
    private DocumentCacheAdminCliLifecycleState _before = null!;
    private DocumentCacheAdminCliLifecycleState _after = null!;
    private Dictionary<string, string> _recordsBefore = [];
    private Dictionary<string, string> _recordsAfter = [];
    private string _fingerprintBefore = string.Empty;
    private string _fingerprintAfter = string.Empty;
    private string _artifactPath = string.Empty;

    public static IEnumerable<TestFixtureData> Scenarios()
    {
        foreach (string provider in new[] { "postgresql", "mssql" })
        {
            foreach (
                string evidence in new[]
                {
                    "unrotatedIdentity",
                    "nonAdvancingGeneration",
                    "missingWriteAdmission",
                }
            )
            {
                TestFixtureData fixture = new(provider, evidence);
                fixture.Properties.Add(
                    "Category",
                    provider == "postgresql" ? "PostgresqlIntegration" : "MssqlIntegration"
                );
                yield return fixture;
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
        await _target.State.SetLifecycleAsync("Disabled", false);
        _fingerprintBefore = await _target.State.ReadPhysicalSourceFingerprintAsync();

        CdcProvider engine = provider == "postgresql" ? CdcProvider.Postgresql : CdcProvider.SqlServer;
        CdcArtifactInventory inventory = CdcArtifactNameGenerator
            .Render(
                new(DeploymentKey, DocumentCacheAdminCliProcessHarness.CdcTopicPrefix, InstanceKey, 1, engine)
            )
            .Inventory!;
        CdcBinding binding = new(
            CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            "default",
            "1",
            InstanceKey,
            1,
            engine,
            _fingerprintBefore,
            inventory.ConnectorName,
            inventory.TopicName,
            1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CdcJsonContract.CurrentContractVersion
        );
        await using ServiceProvider services = new ServiceCollection()
            .AddDmsCdcControlPlane()
            .Configure<CdcBindingStateStoreOptions>(options =>
                options.RootPath = _harness.CdcBindingStatePath
            )
            .BuildServiceProvider();
        CdcBindingLifecycleResult created = await services
            .GetRequiredService<ICdcBindingLifecycleService>()
            .CreateBindingIfAbsentAsync(binding);
        created.Status.Should().Be(CdcControlPlaneOperationStatus.Succeeded);
        _before = await _target.State.ReadLifecycleAsync();
        _recordsBefore = ReadRecords();

        List<string> arguments =
        [
            "cdc",
            "replace-source",
            "--data-store-id",
            _target.DataStoreId.ToString(CultureInfo.InvariantCulture),
            "--deployment-key",
            DeploymentKey,
            "--instance-key",
            InstanceKey,
            "--generation",
            evidence == "nonAdvancingGeneration" ? "1" : "2",
            "--previous-generation",
            "1",
            "--confirm",
            "cdcSourceReplacement",
            "--database-creation-mode",
            "created-for-initial-cdc-provisioning",
            "--kafka-bootstrap-servers",
            "localhost:9092",
            "--connect-base-url",
            "http://localhost:8083",
            "--max-record-bytes",
            "1048576",
            "--durability-profile",
            "local",
            "--cdc-binding-state-path",
            _harness.CdcBindingStatePath,
            "--json",
        ];
        if (evidence != "missingWriteAdmission")
        {
            arguments.AddRange(["--write-admission", "closed-never-opened"]);
        }
        _result = await _harness.RunAsync(arguments.ToArray());
        _after = await _target.State.ReadLifecycleAsync();
        _fingerprintAfter = await _target.State.ReadPhysicalSourceFingerprintAsync();
        _recordsAfter = ReadRecords();

        _artifactPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"replacement-{provider}-{evidence}.json"
        );
        await File.WriteAllTextAsync(
            _artifactPath,
            JsonSerializer.Serialize(
                new
                {
                    provider,
                    evidence,
                    boundary = evidence == "unrotatedIdentity"
                        ? "packaged CLI and real provider eligibility probe; synthetic outgoing binding; no broker"
                        : "packaged CLI preflight; synthetic outgoing binding; no provider probe or broker",
                    arguments = arguments.Select(value =>
                        value == _harness.CdcBindingStatePath ? "<disposable-state-root>" : value
                    ),
                    _result.ExitCode,
                    _result.StandardOutput,
                    _result.StandardError,
                    before = _before,
                    after = _after,
                    fingerprintBefore = _fingerprintBefore,
                    fingerprintAfter = _fingerprintAfter,
                    recordsBefore = _recordsBefore,
                    recordsAfter = _recordsAfter,
                }
            )
        );
    }

    [Test]
    public void It_reports_the_refusal_and_exit_code_without_a_success_proof()
    {
        if (evidence == "missingWriteAdmission")
        {
            _result.ExitCode.Should().Be(64);
            _result.StandardOutput.Should().BeEmpty();
            _result.StandardError.Should().Contain("--write-admission").And.Contain("closed-never-opened");
            return;
        }
        _result.ExitCode.Should().Be(12);
        CdcContractReadResult<CdcAdmission> read = CdcJsonContract.Deserialize<CdcAdmission>(
            _result.StandardOutput
        );
        read.Succeeded.Should().BeTrue();
        CdcAdmission admission = read.Contract!;
        admission.AdmissionState.Should().Be(CdcAdmissionState.Unknown);
        admission.TargetIdentity.DeploymentKey.Should().Be(DeploymentKey);
        admission.TargetIdentity.TenantKey.Should().Be("default");
        admission.TargetIdentity.DataStoreId.Should().Be("1");
        admission.TargetIdentity.Generation.Should().Be(evidence == "nonAdvancingGeneration" ? 1 : 2);
        admission
            .TargetIdentity.Provider.Should()
            .Be(provider == "postgresql" ? CdcProvider.Postgresql : CdcProvider.SqlServer);
        CdcDiagnostic refusal = admission
            .Diagnostics.Should()
            .ContainSingle(d => d.Code == "replaceSourceRefused")
            .Subject;
        refusal
            .Category.Should()
            .Be(
                evidence == "unrotatedIdentity"
                    ? CdcDiagnosticCategory.SourceMismatch
                    : CdcDiagnosticCategory.BindingMismatch
            );
        refusal.Retryable.Should().BeFalse();
        refusal.Observed.Should().Be(evidence == "unrotatedIdentity" ? "retained" : "1");
        // The packaged Kafka client can log connection diagnostics during construction even when
        // the controller refuses before any broker observation. The contract belongs only on stdout.
        _result.StandardOutput.TrimEnd().Should().NotContain("\n");
        _result
            .StandardError.Should()
            .NotContain("\"contractVersion\"")
            .And.NotContain("replaceSourceRefused");
    }

    [Test]
    public void It_preserves_the_original_generation_identity_and_lifecycle()
    {
        _recordsBefore.Should().NotBeEmpty();
        _recordsAfter.Should().BeEquivalentTo(_recordsBefore);
        _fingerprintAfter.Should().Be(_fingerprintBefore);
        _after.Should().Be(_before);
    }

    [Test]
    public void It_keeps_credentials_out_of_operator_output()
    {
        string output = _result.StandardOutput + _result.StandardError;
        output.Should().NotContain(_target.ConnectionString).And.NotContain(_harness.SecretFromEnvironment);
        TestContext.AddTestAttachment(_artifactPath);
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

    private Dictionary<string, string> ReadRecords() =>
        Directory
            .GetFiles(_harness.CdcBindingStatePath, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_harness.CdcBindingStatePath, path), File.ReadAllText);
}
