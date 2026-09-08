// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcProviderSetupOrchestration(Ddl.CdcProvider provider)
{
    private string _root = null!;
    private CdcDeploymentRequest _request = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ServiceProvider _services = null!;
    private ICdcProjectionRuntime _runtime = null!;
    private Ddl.ICdcProviderSetupService _provider = null!;
    private CdcProviderSetupOrchestration _controller = null!;
    private List<Ddl.CdcProviderSetupRequest> _calls = null!;
    private DocumentCacheLifecycleState _lifecycle;
    private bool _rows;
    private bool _latch;
    private bool _exists;
    private string _identity = null!;
    private Func<Ddl.CdcProviderSetupResult, Ddl.CdcProviderSetupResult> _change = null!;
    private CdcTargetIdentity Target => _request.TargetIdentity;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-provider-" + Guid.NewGuid().ToString("N"));
        _store = new(_root);
        _request = CdcDeploymentRequestTestData.Request(provider);
        _calls = [];
        _rows = _latch = _exists = false;
        _identity = new('a', 64);
        _change = r => r;
        _lifecycle = DocumentCacheLifecycleState.Disabled;
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(Target, provisioner);
        var services = new ServiceCollection().AddCdcConnectorTemplates().AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(o => o.RootPath = _root);
        _services = services.BuildServiceProvider();
        _runtime = A.Fake<ICdcProjectionRuntime>();
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
                new CdcInitialDatabaseObservation(
                    DocumentCacheTargetKey.Create(
                        Target.TenantKey,
                        long.Parse(Target.DataStoreId, CultureInfo.InvariantCulture)
                    ),
                    provider == Ddl.CdcProvider.Postgresql
                        ? RelationalProviderToken.Postgresql
                        : RelationalProviderToken.SqlServer,
                    _request.Binding.PhysicalSourceFingerprint,
                    DateTimeOffset.UtcNow,
                    new(_lifecycle, _latch),
                    new(!_rows, true, true),
                    Guid.NewGuid().ToString("D")
                )
            );
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (DocumentCacheGuardedNewEmptyActivationRequest r, CancellationToken _) =>
                {
                    _lifecycle = DocumentCacheLifecycleState.Tracking;
                    return new DocumentCacheAdministrativeCommandResult(
                        DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                        r.TargetKey,
                        DocumentCacheAdministrativeCommandStatus.Completed,
                        DocumentCacheAdministrativeCommandClassification.Succeeded,
                        true
                    );
                }
            );
        (await new CdcInitialEnablement(_root).ActivateAsync(_request, _runtime))
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed);
        _provider = A.Fake<Ddl.ICdcProviderSetupService>();
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ReturnsLazily((Ddl.CdcProviderSetupRequest r, CancellationToken _) => ProviderResult(r));
        _controller = new(_root, _provider, _services.GetRequiredService<ICdcConnectorTemplateService>());
    }

    [TearDown]
    public async Task Teardown()
    {
        await _runtime.DisposeAsync();
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private Ddl.CdcProviderSetupResult ProviderResult(Ddl.CdcProviderSetupRequest r)
    {
        _calls.Add(r);
        ReadJournal().Operations.Should().Contain(o => o.Effect == CdcWorkflowEffect.CreateProvider);
        _lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        var result = CdcConnectorTemplateTestData.BuildProviderSetupResult(provider, mode: r.Mode);
        bool created = !_exists && r.Mode == Ddl.CdcProviderSetupMode.InitialCreateOrExactMatch;
        if (created)
        {
            _exists = true;
        }
        if (
            !_exists
            || provider == Ddl.CdcProvider.Postgresql
                && !created
                && r.Mode == Ddl.CdcProviderSetupMode.InitialCreateOrExactMatch
                && r.PostgresqlInitialReplicationSlotProof is null
        )
        {
            return result with { Outcome = Ddl.CdcProviderSetupOutcome.Failed };
        }
        var proof =
            provider == Ddl.CdcProvider.Postgresql
                ? new Ddl.CdcPostgresqlInitialReplicationSlotProof(
                    r.ArtifactNames.Postgresql!.ReplicationSlotName,
                    r.BoundPhysicalSourceFingerprint,
                    new("postgresql_database_identity_sha256:" + _identity),
                    "0/10",
                    "0/10"
                )
                : null;
        return _change(
            result with
            {
                Outcome = created
                    ? Ddl.CdcProviderSetupOutcome.CreatedOrMatched
                    : Ddl.CdcProviderSetupOutcome.ExactMatch,
                InitialReplicationSlotProof = created ? proof : null,
                ArtifactInventory = result
                    .ArtifactInventory.Select(a =>
                        a with
                        {
                            SafeObservedValues = new Dictionary<string, string>(a.SafeObservedValues)
                            {
                                ["database_identity_token"] =
                                    "postgresql_database_identity_sha256:" + _identity,
                                ["capture_identity_hash"] = _identity,
                            },
                        }
                    )
                    .ToArray(),
            }
        );
    }

    private CdcWorkflowJournal ReadJournal() =>
        JsonSerializer.Deserialize<CdcWorkflowJournal>(
            File.ReadAllText(
                Directory
                    .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                    .Single()
            ),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters =
                {
                    new System.Text.Json.Serialization.JsonStringEnumConverter(),
                    new SafeNameConverter(),
                },
            }
        )!;

    private sealed class SafeNameConverter : System.Text.Json.Serialization.JsonConverter<Ddl.CdcSafeName>
    {
        public override Ddl.CdcSafeName Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => new(reader.GetString()!);

        public override void Write(
            Utf8JsonWriter writer,
            Ddl.CdcSafeName value,
            JsonSerializerOptions options
        ) => writer.WriteStringValue(value.Value);
    }

    private Task<CdcTransportResult<CdcProviderSetupHandoff>> RunAsync() =>
        _controller.SetupAsync(_request, _runtime);

    private async Task IntentAsync(CdcWorkflowEffect effect)
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        var journal = await session.ReadAsync(Target, CancellationToken.None);
        await session.RecordIntentAsync(
            Target,
            journal.WorkflowId,
            Guid.NewGuid(),
            effect,
            [],
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_persists_live_provider_identity_and_proof_before_template_handoff()
    {
        var handoff = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcProviderSetupHandoff>.Observed>()
            .Subject.Value;
        _calls
            .Select(r => r.Mode)
            .Should()
            .Equal(Ddl.CdcProviderSetupMode.InitialCreateOrExactMatch, Ddl.CdcProviderSetupMode.ValidateOnly);
        _calls.Should().OnlyContain(r => !r.ArtifactOutput.IncludeManifestPayload);
        _calls
            .Should()
            .OnlyContain(r =>
                ReferenceEquals(r.ExpectedSourceInventory, _request.ProviderSetup.ExpectedSourceInventory)
                || r.ExpectedSourceInventory.SequenceEqual(_request.ProviderSetup.ExpectedSourceInventory)
            );
        handoff.TemplateRequest.Binding.Should().BeSameAs(_request.Binding);
        handoff.Template.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        var completion = ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider)
            .Completions.Single()
            .Evidence.Should()
            .BeOfType<CdcWorkflowCompletion.Provider>()
            .Subject;
        completion.Artifacts.Should().HaveCount(provider == Ddl.CdcProvider.Postgresql ? 1 : 3);
        completion.InitialSlotProofs.Should().HaveCount(provider == Ddl.CdcProvider.Postgresql ? 1 : 0);
        JsonSerializer
            .Serialize(handoff)
            .Should()
            .NotContain("private-source-host")
            .And.NotContain("database.password");
        ReadJournal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.RegisterConnector);
    }

    [Test]
    public async Task It_inspects_completed_setup_without_rewriting_original_evidence()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        var before = JsonSerializer.Serialize(ReadJournal());
        _calls.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _calls.Should().ContainSingle().Which.Mode.Should().Be(Ddl.CdcProviderSetupMode.ValidateOnly);
        _calls.Single().RequireUnconsumedInitialSlot.Should().BeTrue();
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
    }

    [Test]
    public async Task It_switches_to_consumption_safe_inspection_at_registration_intent_even_without_response()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        await IntentAsync(CdcWorkflowEffect.RegisterConnector);
        _rows = true;
        _calls.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _calls.Should().ContainSingle().Which.Mode.Should().Be(Ddl.CdcProviderSetupMode.ValidateOnly);
        _calls.Single().RequireUnconsumedInitialSlot.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_registration_intent_without_durable_provider_completion()
    {
        await IntentAsync(CdcWorkflowEffect.RegisterConnector);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [Test]
    public async Task It_never_recreates_missing_completed_capture()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _exists = false;
        _calls.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().ContainSingle().Which.Mode.Should().Be(Ddl.CdcProviderSetupMode.ValidateOnly);
        _exists.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_same_name_capture_with_changed_identity()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        await IntentAsync(CdcWorkflowEffect.RegisterConnector);
        _identity = new('b', 64);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase(DocumentCacheLifecycleState.Disabled, false, false)]
    [TestCase(DocumentCacheLifecycleState.Resetting, false, false)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding, false, false)]
    [TestCase(DocumentCacheLifecycleState.Tracking, true, false)]
    [TestCase(DocumentCacheLifecycleState.Tracking, false, true)]
    public async Task It_rejects_current_ineligible_projection_before_capture(
        DocumentCacheLifecycleState state,
        bool rows,
        bool latch
    )
    {
        _lifecycle = state;
        _rows = rows;
        _latch = latch;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    [TestCase("bindings")]
    public async Task It_rejects_lost_provenance_without_provider_calls(string folder)
    {
        Directory.Delete(Path.Combine(_root, folder), true);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _calls.Should().BeEmpty();
    }

    [TestCase("source")]
    [TestCase("failed")]
    [TestCase("mode")]
    [TestCase("inventory")]
    [TestCase("identity")]
    [TestCase("template")]
    public async Task It_rejects_invalid_or_incomplete_live_result(string fault)
    {
        _change = r =>
            fault switch
            {
                "source" => r with
                {
                    ObservedSourceFingerprint = CdcConnectorTemplateTestData.OtherPostgresqlSourceFingerprint,
                },
                "failed" => r with { Outcome = Ddl.CdcProviderSetupOutcome.Failed },
                "mode" => r with { Mode = (Ddl.CdcProviderSetupMode)999 },
                "inventory" => r with { SourceTableInventory = [] },
                "identity" => r with
                {
                    ArtifactInventory = r
                        .ArtifactInventory.Select(a =>
                            a with
                            {
                                SafeObservedValues = new Dictionary<string, string>(),
                            }
                        )
                        .ToArray(),
                },
                _ => r with { HeartbeatActionQuery = null },
            };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider)
            .Completions.Should()
            .HaveCount(fault == "template" ? 1 : 0);
    }

    [TestCase(CdcWorkflowWriteBoundary.BeforeTemporaryWrite)]
    [TestCase(CdcWorkflowWriteBoundary.AfterTemporaryFlush)]
    [TestCase(CdcWorkflowWriteBoundary.AfterAtomicReplacement)]
    public async Task It_reconciles_or_rejects_proof_persistence_crashes_without_recreating_history(
        CdcWorkflowWriteBoundary boundary
    )
    {
        int writes = 0;
        var store = new LocalCdcWorkflowJournalStore(
            _root,
            TimeProvider.System,
            b =>
            {
                if (b == boundary && ++writes == 2)
                {
                    throw new IOException("private-source-secret");
                }
            }
        );
        _controller = new(
            store,
            _services.GetRequiredService<ICdcBindingLifecycleService>(),
            _provider,
            _services.GetRequiredService<ICdcConnectorTemplateService>(),
            TimeProvider.System
        );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _controller = new(_root, _provider, _services.GetRequiredService<ICdcConnectorTemplateService>());
        var retry = await RunAsync();
        retry
            .State.Should()
            .Be(
                boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement
                || provider == Ddl.CdcProvider.SqlServer
                    ? CdcTransportEvidenceState.Observed
                    : CdcTransportEvidenceState.Unavailable
            );
        _exists.Should().BeTrue();
    }

    [Test]
    public async Task It_preserves_provider_proof_across_template_failure_and_retries_inspection_only()
    {
        _change = r => r with { HeartbeatActionQuery = null };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider)
            .Completions.Should()
            .ContainSingle();
        _calls.Clear();
        _change = r => r;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _calls.Should().ContainSingle().Which.Mode.Should().Be(Ddl.CdcProviderSetupMode.ValidateOnly);
    }

    [Test]
    public async Task It_reports_unavailable_history_without_claiming_artifact_absence()
    {
        _change = r =>
            r with
            {
                Outcome = Ddl.CdcProviderSetupOutcome.Failed,
                Diagnostics =
                [
                    new(
                        "provider-unavailable",
                        Ddl.CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                        Ddl.CdcProviderDiagnosticSeverity.Error,
                        Ddl.CdcPrincipalKind.None,
                        Ddl.CdcProviderArtifactKind.ProviderHistory,
                        new("history"),
                        null,
                        null,
                        null,
                        Ddl.CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                    ),
                ],
            };
        var result = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcProviderSetupHandoff>.Unavailable>()
            .Subject;
        result.Diagnostic.Failure.Should().Be(CdcDeploymentFailure.Unavailable);
        result.Diagnostic.Component.Should().Be(CdcDeploymentComponent.ProviderSetup);
    }

    [Test]
    public async Task It_bounds_provider_calls_and_releases_the_lock_on_timeout()
    {
        _request = new(
            _request.Binding,
            _request.DmsSettings,
            _request.ProviderSetup,
            _request.ConnectEndpoint,
            _request.WorkerMetricsEndpoint,
            _request.ConnectorPolicy,
            _request.WorkerPolicy,
            _request.ProviderConnectionProperties,
            _request.KafkaClientSecurityProperties,
            new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10))
        );
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (Ddl.CdcProviderSetupRequest r, CancellationToken token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ProviderResult(r);
                }
            );
        var result = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcProviderSetupHandoff>.Unavailable>()
            .Subject;
        result.Diagnostic.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        result.Diagnostic.Component.Should().Be(CdcDeploymentComponent.ProviderSetup);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_sanitizes_provider_exceptions()
    {
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("private-source-secret"));
        var result = await RunAsync();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Component.Should()
            .Be(CdcDeploymentComponent.ProviderSetup);
        JsonSerializer.Serialize(result).Should().NotContain("private-source-secret");
    }

    [Test]
    public async Task It_preserves_cancellation_and_releases_the_lock()
    {
        using var cancellation = new CancellationTokenSource();
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (Ddl.CdcProviderSetupRequest r, CancellationToken token) =>
                {
                    await cancellation.CancelAsync();
                    token.ThrowIfCancellationRequested();
                    return ProviderResult(r);
                }
            );
        Func<Task> run = () => _controller.SetupAsync(_request, _runtime, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_retains_the_controller_lock_through_provider_effects()
    {
        A.CallTo(() => _provider.SetupAsync(A<Ddl.CdcProviderSetupRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (Ddl.CdcProviderSetupRequest r, CancellationToken _) =>
                {
                    Func<Task> compete = async () =>
                    {
                        await using var session = await _store.AcquireAsync(
                            TimeSpan.FromMilliseconds(25),
                            TimeSpan.FromMilliseconds(5),
                            CancellationToken.None
                        );
                    };
                    await compete
                        .Should()
                        .ThrowAsync<CdcWorkflowStateException>()
                        .Where(e => e.Failure == CdcWorkflowStateFailure.LockTimeout);
                    return ProviderResult(r);
                }
            );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
    }
}
