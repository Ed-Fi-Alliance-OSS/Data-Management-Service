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
internal class Given_CdcConnectorRegistration(Ddl.CdcProvider provider)
{
    private string _root = null!;
    private CdcDeploymentRequest _request = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ServiceProvider _services = null!;
    private ICdcProjectionRuntime _runtime = null!;
    private Ddl.ICdcProviderSetupService _provider = null!;
    private CdcConnectorRegistration _controller = null!;
    private ICdcConnectTransport _connect = null!;
    private ICdcKafkaAdminAdapter _kafka = null!;
    private ICdcWorkerInspectionTransport _worker = null!;
    private ICdcConnectorTemplateService _templates = null!;
    private CdcProviderSetupHandoff _handoff = null!;
    private CdcWorkerInspection _workerEvidence = null!;
    private Dictionary<string, string> _live = null!;
    private List<string> _trace = null!;
    private Action<CdcWorkflowWriteBoundary> _onWrite = null!;
    private Action<string> _onCall = null!;
    private bool _connectorExists;
    private CdcConnectOffsetState _offsetState;
    private CdcConnectorRuntimeState _runtimeState;
    private int _posts;
    private int _offsetReads;
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
        _onWrite = _ => { };
        _onCall = _ => { };
        _store = new(_root, TimeProvider.System, b => _onWrite(b));
        _request = CdcDeploymentRequestTestData.Request(
            provider,
            worker: CdcDeploymentRequestTestData.Worker(digest: CdcQualifiedWorkerImage.Digests.Single())
        );
        _trace = [];
        _connectorExists = false;
        _posts = _offsetReads = 0;
        _offsetState = CdcConnectOffsetState.Streaming;
        _runtimeState = CdcConnectorRuntimeState.Running;
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
        _templates = _services.GetRequiredService<ICdcConnectorTemplateService>();
        _handoff = (
            await new CdcProviderSetupOrchestration(_root, _provider, _templates).SetupAsync(
                _request,
                _runtime
            )
        )
            .Should()
            .BeOfType<CdcTransportResult<CdcProviderSetupHandoff>.Observed>()
            .Subject.Value;
        await CompleteAsync(CdcWorkflowEffect.PrepareKafka);
        _calls.Clear();
        _live = new(_handoff.Template.Config);
        _workerEvidence = new(
            "jvm-1",
            _request.WorkerMetricsEndpoint,
            new Dictionary<string, string>
            {
                ["group.id"] = _request.WorkerPolicy.WorkerKey.Value,
                ["offset.storage.topic"] = _request.WorkerPolicy.OffsetStorageTopic.Value,
                ["connector.client.config.override.policy"] = "All",
            },
            _request.WorkerPolicy.QualifiedImageDigest,
            _request.WorkerPolicy.HeapBytes,
            "worker:8083"
        );
        _worker = A.Fake<ICdcWorkerInspectionTransport>();
        A.CallTo(() => _worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("worker");
                return Observed(_workerEvidence);
            });
        _connect = A.Fake<ICdcConnectTransport>();
        A.CallTo(() =>
                _connect.ValidateConfigurationAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, CdcKafkaConnectRegistrationPayload payload, CancellationToken _) =>
                {
                    Trace("preflight");
                    return Observed<IReadOnlyDictionary<string, string>>(payload.Config);
                }
            );
        A.CallTo(() => _connect.ReadConfigurationAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("config");
                return _connectorExists
                    ? Observed<IReadOnlyDictionary<string, string>>(_live)
                    : new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent();
            });
        A.CallTo(() =>
                _connect.CreateAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, CdcKafkaConnectRegistrationPayload payload, CancellationToken _) =>
                {
                    ReadJournal()
                        .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
                        .ConnectorRegistration.Single()
                        .ConfigSha256.Should()
                        .Be(_handoff.Template.ConfigSha256);
                    _posts++;
                    Trace("post-before");
                    _connectorExists = true;
                    _live = new(payload.Config);
                    Trace("post-after");
                    return Observed(new CdcTransportAcknowledgement());
                }
            );
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                return Observed(Status());
            });
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _offsetReads++;
                Trace("offset");
                return Observed(Offsets());
            });
        _kafka = A.Fake<ICdcKafkaAdminAdapter>();
        var plan = CdcDeploymentKafkaPolicy.Build(_request);
        var topics = plan.BindingTopics.Append(plan.OffsetStore).ToDictionary(t => t.Name);
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, string name, CancellationToken _) =>
                {
                    Trace("topic");
                    var intent = topics[name];
                    return Observed(
                        new CdcKafkaTopicEvidence(
                            name,
                            Enumerable
                                .Range(0, intent.PartitionCount)
                                .ToDictionary(i => i, _ => (IReadOnlyList<int>)[0]),
                            intent.Configuration.ToDictionary(
                                p => p.Key,
                                p => new CdcKafkaConfigurationValue(p.Value, true)
                            )
                        )
                    );
                }
            );
        A.CallTo(() => _kafka.InspectBrokersAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("brokers");
                return Observed(
                    new CdcKafkaBrokerEvidence(true, [new(0, int.MaxValue, int.MaxValue, int.MaxValue)])
                );
            });
        A.CallTo(() => _kafka.InspectAclsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("acls");
                return Observed(new CdcKafkaAclEvidence(false, true, false, [], []));
            });
        ResetController();
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
        Trace("provider");
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

    private void ResetController() =>
        _controller = new(
            _store,
            _services.GetRequiredService<ICdcBindingLifecycleService>(),
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            TimeProvider.System
        );

    private void Trace(string name)
    {
        _trace.Add(name);
        _onCall(name);
    }

    private static CdcTransportResult<T> Observed<T>(T value)
        where T : notnull => new CdcTransportResult<T>.Observed(value);

    private Task<CdcTransportResult<CdcConnectorRegistrationReceipt>> RunAsync(
        CancellationToken token = default
    ) => _controller.RegisterAsync(_request, _runtime, token);

    private CdcConnectStatus Status() =>
        new(
            new(
                CdcJsonContract.CurrentContractVersion,
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow,
                Target,
                _request.Binding.Provider,
                _request.Binding.PhysicalSourceFingerprint,
                _request.Binding.ConnectorName,
                _runtimeState,
                1,
                _runtimeState == CdcConnectorRuntimeState.Running ? 1 : 0,
                _runtimeState,
                CdcConnectorSnapshotState.NotApplicable,
                null,
                null,
                []
            )
            {
                SoleTaskState = _runtimeState,
            },
            "worker:8083",
            [new(0, _runtimeState, "worker:8083")]
        );

    private CdcConnectOffsetEvidence Offsets()
    {
        if (_offsetState != CdcConnectOffsetState.Streaming)
        {
            return new(
                _offsetState,
                "",
                new(CdcConnectorOffsetMatchResult.Missing, false, false, null),
                new(CdcConnectorOffsetMatchResult.Missing, false, false, null, null, null)
            );
        }
        Dictionary<string, string> partition = new() { ["server"] = _request.Binding.ConnectorName };
        if (provider == Ddl.CdcProvider.SqlServer)
        {
            partition["database"] = _request.ProviderConnectionProperties.Properties["database.names"];
        }
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(
                new
                {
                    offsets = new[]
                    {
                        new
                        {
                            partition,
                            offset = provider == Ddl.CdcProvider.Postgresql
                                ? (object)new { lsn_proc = 16L, snapshot = false }
                                : new
                                {
                                    commit_lsn = "00000001:00000002:0003",
                                    change_lsn = "00000001:00000002:0003",
                                    event_serial_no = 0L,
                                    snapshot = false,
                                },
                        },
                    },
                }
            )
        );
        return CdcConnectOffsetEvidence.Parse(_request, document.RootElement);
    }

    private async Task<CdcWorkflowJournal> CompleteAsync(CdcWorkflowEffect effect)
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
        var journal = await session.ReadAsync(Target, CancellationToken.None);
        var id = Guid.NewGuid();
        await session.RecordIntentAsync(Target, journal.WorkflowId, id, effect, [], CancellationToken.None);
        return await session.ReconcileCompletionAsync(
            Target,
            journal.WorkflowId,
            id,
            (_, _) =>
                Task.FromResult(Observed<CdcWorkflowCompletion>(new CdcWorkflowCompletion.Reconciled())),
            CancellationToken.None
        );
    }

    private async Task RegisterIntentAsync()
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
        var journal = await session.ReadAsync(Target, CancellationToken.None);
        await session.RecordConnectorRegistrationIntentAsync(
            Target,
            journal.WorkflowId,
            Guid.NewGuid(),
            new(_handoff.Template.ConfigSha256!),
            CancellationToken.None
        );
    }

    private void ShortTiming(int milliseconds = 250) =>
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
            new(
                TimeSpan.FromMilliseconds(milliseconds),
                TimeSpan.FromMilliseconds(milliseconds * 5),
                TimeSpan.FromMilliseconds(5)
            )
        );

    [Test]
    public async Task It_establishes_only_after_policy_live_configuration_running_and_streaming_offsets()
    {
        var bindingPath = Directory
            .GetFiles(Path.Combine(_root, "bindings"), "*.json", SearchOption.AllDirectories)
            .Single();
        var before = await File.ReadAllBytesAsync(bindingPath);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _trace.IndexOf("preflight").Should().BeLessThan(_trace.IndexOf("post-before"));
        _trace.IndexOf("acls").Should().BeLessThan(_trace.IndexOf("post-before"));
        _trace.IndexOf("status").Should().BeLessThan(_trace.IndexOf("offset"));
        _posts.Should().Be(1);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Single()
            .Evidence.Should()
            .Be(new CdcWorkflowCompletion.Connector(Offsets().SourcePartitionHash));
        _calls.Should().OnlyContain(c => c.Mode == Ddl.CdcProviderSetupMode.ValidateOnly);
        _calls[0].RequireUnconsumedInitialSlot.Should().BeTrue();
        _calls[^1].RequireUnconsumedInitialSlot.Should().BeFalse();
        (await File.ReadAllBytesAsync(bindingPath)).Should().Equal(before);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task It_reconciles_an_existing_exact_connector_without_creation_or_rewriting_establishment()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        var before = JsonSerializer.Serialize(ReadJournal());
        _calls.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(1);
        _calls
            .Should()
            .OnlyContain(c =>
                c.Mode == Ddl.CdcProviderSetupMode.ValidateOnly && !c.RequireUnconsumedInitialSlot
            );
        JsonSerializer.Serialize(ReadJournal()).Should().Be(before);
    }

    [TestCase("post-before")]
    [TestCase("post-after")]
    public async Task It_reconciles_an_uncertain_post_by_independent_readback(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new HttpRequestException("private-secret");
            }
        };
        var result = await RunAsync();
        result
            .State.Should()
            .Be(
                stage == "post-after"
                    ? CdcTransportEvidenceState.Observed
                    : CdcTransportEvidenceState.Unavailable
            );
        _onCall = _ => { };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(stage == "post-after" ? 1 : 2);
    }

    [TestCase("post-before")]
    [TestCase("post-after")]
    [TestCase("status")]
    [TestCase("offset")]
    public async Task It_preserves_cancellation_and_reconciles_on_a_new_invocation(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == stage)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        Func<Task> run = () => RunAsync(cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        _onCall = _ => { };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(stage == "post-before" ? 2 : 1);
    }

    [Test]
    public async Task It_retains_initial_awaiting_offset_state_and_resumes_without_recreation()
    {
        ShortTiming();
        _offsetState = CdcConnectOffsetState.Missing;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
            .Completions.Should()
            .ContainSingle();
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
        _offsetState = CdcConnectOffsetState.Streaming;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_established_offset_loss_without_wait_or_repair()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _offsetState = CdcConnectOffsetState.Missing;
        _offsetReads = 0;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(1);
        _posts.Should().Be(1);
    }

    [TestCase(CdcConnectOffsetState.Snapshot)]
    [TestCase(CdcConnectOffsetState.Null)]
    [TestCase(CdcConnectOffsetState.Multiple)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    [TestCase(CdcConnectOffsetState.SourcePartitionMismatch)]
    public async Task It_rejects_non_streaming_offset_evidence(CdcConnectOffsetState state)
    {
        ShortTiming();
        _offsetState = state;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
    }

    [TestCase("tasks.max", "2")]
    [TestCase("table.include.list", "dms.Document")]
    [TestCase("snapshot.mode", "always")]
    [TestCase("heartbeat.interval.ms", "0")]
    [TestCase("transforms", "foreign")]
    [TestCase("producer.override.max.request.size", "1")]
    [TestCase("database.hostname", "other-source")]
    [TestCase("unrecognized.reserved.key", "secret")]
    public async Task It_rejects_live_drift_before_waiting_or_mutating(string key, string value)
    {
        await RegisterIntentAsync();
        _connectorExists = true;
        _live[key] = value;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
        _offsetReads.Should().Be(0);
    }

    [Test]
    public async Task It_accepts_masked_live_credentials_using_existing_template_rules()
    {
        await RegisterIntentAsync();
        _connectorExists = true;
        _live["database.password"] = "********";
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _posts.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_refuses_changed_payload_on_retry_even_if_live_configuration_matches(
        bool connectorPresent
    )
    {
        await RegisterIntentAsync();
        _connectorExists = connectorPresent;
        _request = CdcDeploymentRequestTestData.Request(
            provider,
            worker: _request.WorkerPolicy,
            password: "${env:OTHER_PASSWORD}"
        );
        _live["database.password"] = "${env:OTHER_PASSWORD}";
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_adopt_an_unjournaled_connector()
    {
        _connectorExists = true;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.RegisterConnector);
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    [TestCase("bindings")]
    public async Task It_rejects_missing_provenance_before_connect_calls(string folder)
    {
        Directory.Delete(Path.Combine(_root, folder), true);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().NotContain("preflight");
        _posts.Should().Be(0);
    }

    [TestCase(CdcConnectorRuntimeState.Failed)]
    [TestCase(CdcConnectorRuntimeState.Stopped)]
    [TestCase(CdcConnectorRuntimeState.Paused)]
    public async Task It_does_not_read_offsets_or_resume_failed_or_stopped_tasks(
        CdcConnectorRuntimeState state
    )
    {
        _runtimeState = state;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(0);
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase("image")]
    [TestCase("heap")]
    [TestCase("group.id")]
    [TestCase("offset.storage.topic")]
    [TestCase("connector.client.config.override.policy")]
    public async Task It_rejects_worker_drift_before_creation(string field)
    {
        var config = new Dictionary<string, string>(_workerEvidence.EffectiveConfiguration);
        if (field is not ("image" or "heap"))
        {
            config[field] = "wrong";
        }
        _workerEvidence = new(
            _workerEvidence.ProcessIdentity,
            _workerEvidence.MetricsEndpoint,
            config,
            field == "image" ? "sha256:" + new string('b', 64) : _workerEvidence.ImageDigest,
            field == "heap" ? 1 : _workerEvidence.HeapBytes,
            _workerEvidence.ConnectWorkerId
        );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
    }

    [TestCase("offset")]
    [TestCase("post-before")]
    public async Task It_rejects_worker_replacement_during_registration(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                _workerEvidence = new(
                    "jvm-replaced",
                    _workerEvidence.MetricsEndpoint,
                    _workerEvidence.EffectiveConfiguration,
                    _workerEvidence.ImageDigest,
                    _workerEvidence.HeapBytes,
                    _workerEvidence.ConnectWorkerId
                );
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
    }

    [TestCase("topic")]
    [TestCase("brokers")]
    [TestCase("acls")]
    [TestCase("preflight")]
    [TestCase("worker")]
    public async Task It_sanitizes_unavailable_prerequisites_and_prevents_creation(string stage)
    {
        _onCall = name =>
        {
            if (name == stage)
            {
                throw new IOException("private-secret-source");
            }
        };
        var result = await RunAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("private-secret-source");
        _posts.Should().Be(0);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task It_reconciles_each_atomic_journal_boundary_after_a_crash(int write)
    {
        foreach (var boundary in Enum.GetValues<CdcWorkflowWriteBoundary>())
        {
            // Each iteration needs its own original managed workflow.
            if (boundary != Enum.GetValues<CdcWorkflowWriteBoundary>()[0])
            {
                await Teardown();
                await Setup();
            }
            int count = 0;
            _onWrite = current =>
            {
                if (current == boundary && ++count == write)
                {
                    throw new IOException("crash-secret");
                }
            };
            (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
            _onWrite = _ => { };
            ResetController();
            (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
            _posts.Should().Be(1);
            ReadJournal()
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
                .Completions.Should()
                .ContainSingle();
        }
    }

    [Test]
    public async Task It_does_not_treat_active_history_before_establishment_replacement_as_awaiting_first_offset()
    {
        int writes = 0;
        _onWrite = b =>
        {
            if (b == CdcWorkflowWriteBoundary.AfterAtomicReplacement && ++writes == 4)
            {
                throw new IOException();
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _onWrite = _ => { };
        _offsetState = CdcConnectOffsetState.Missing;
        _offsetReads = 0;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _offsetReads.Should().Be(1);
    }

    [Test]
    public async Task It_bounds_unresponsive_calls_and_releases_the_controller_lock()
    {
        ShortTiming();
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() => new TaskCompletionSource<CdcTransportResult<CdcConnectStatus>>().Task);
        var result = await RunAsync();
        result.Diagnostics.Should().ContainSingle().Which.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_holds_the_controller_lock_across_external_effects()
    {
        A.CallTo(() =>
                _connect.ValidateConfigurationAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    CdcDeploymentRequest _,
                    CdcKafkaConnectRegistrationPayload payload,
                    CancellationToken _
                ) =>
                {
                    Func<Task> compete = async () =>
                    {
                        await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                            TimeSpan.FromMilliseconds(25),
                            TimeSpan.FromMilliseconds(5),
                            CancellationToken.None
                        );
                    };
                    await compete
                        .Should()
                        .ThrowAsync<CdcWorkflowStateException>()
                        .Where(e => e.Failure == CdcWorkflowStateFailure.LockTimeout);
                    return Observed<IReadOnlyDictionary<string, string>>(payload.Config);
                }
            );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.Snapshot)]
    public async Task It_waits_for_initial_streaming_offsets_without_resetting(CdcConnectOffsetState initial)
    {
        ShortTiming(500);
        _offsetState = initial;
        _onCall = stage =>
        {
            if (stage == "offset" && _offsetReads == 2)
            {
                _offsetState = CdcConnectOffsetState.Streaming;
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _offsetReads.Should().Be(2);
        A.CallTo(() => _connect.DeleteOffsetsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [TestCase(CdcWorkflowEffect.StopConnector)]
    [TestCase(CdcWorkflowEffect.ResumeConnector)]
    [TestCase(CdcWorkflowEffect.Retire)]
    [TestCase(CdcWorkflowEffect.AuthorizeWriterPublication)]
    public async Task It_does_not_reenter_initial_registration_after_lifecycle_or_publication(
        CdcWorkflowEffect effect
    )
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        await CompleteAsync(effect);
        _trace.Clear();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_a_missing_previously_registered_connector_without_recreating()
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        _connectorExists = false;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(1);
    }

    [Test]
    public void It_keeps_full_live_template_validation_requiring_actual_partition_evidence()
    {
        var config = new CdcConnectorTemplateEffectiveConfigValidationRequest(
            _handoff.TemplateRequest,
            _live,
            _handoff.TemplateRequest.ProviderSetupEvidence
        );
        _templates
            .ValidateLiveConfigurationReadBack(config)
            .Outcome.Should()
            .Be(CdcConnectorTemplateOutcome.Rendered);
        _templates
            .ValidateLiveReadBack(config)
            .Outcome.Should()
            .Be(CdcConnectorTemplateOutcome.ValidationFailed);
    }

    [Test]
    public async Task It_rejects_legacy_registration_intent_without_payload_identity()
    {
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            )
        )
        {
            var journal = await session.ReadAsync(Target, CancellationToken.None);
            await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.RegisterConnector,
                [],
                CancellationToken.None
            );
        }
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
    }

    [TestCase("topic")]
    [TestCase("brokers")]
    [TestCase("acls")]
    [TestCase("preflight")]
    public async Task It_requires_affirmative_live_policy_before_post(string component)
    {
        if (component == "topic")
        {
            A.CallTo(() =>
                    _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(
                    Observed(
                        new CdcKafkaTopicEvidence(
                            "wrong",
                            new Dictionary<int, IReadOnlyList<int>>(),
                            new Dictionary<string, CdcKafkaConfigurationValue>()
                        )
                    )
                );
        }
        if (component == "brokers")
        {
            A.CallTo(() => _kafka.InspectBrokersAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Returns(Observed(new CdcKafkaBrokerEvidence(true, [new(0, 1, 1, 1)])));
        }
        if (component == "acls")
        {
            A.CallTo(() => _kafka.InspectAclsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .Returns(Observed(new CdcKafkaAclEvidence(true, true, false, [], [])));
        }
        if (component == "preflight")
        {
            A.CallTo(() =>
                    _connect.ValidateConfigurationAsync(
                        A<CdcDeploymentRequest>._,
                        A<CdcKafkaConnectRegistrationPayload>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Observed<IReadOnlyDictionary<string, string>>(
                        new Dictionary<string, string>(_live) { ["producer.override.max.request.size"] = "1" }
                    )
                );
        }
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _posts.Should().Be(0);
        ReadJournal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.RegisterConnector);
    }

    [Test]
    public async Task It_reconciles_create_conflicts_but_rejects_changed_readback()
    {
        A.CallTo(() =>
                _connect.CreateAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                _connectorExists = true;
                _live["tasks.max"] = "2";
                return new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(
                    new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Conflict)
                );
            });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
            .Completions.Should()
            .BeEmpty();
        _offsetReads.Should().Be(0);
    }

    [Test]
    public async Task It_requires_actual_readback_after_a_create_acknowledgement()
    {
        A.CallTo(() =>
                _connect.CreateAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaConnectRegistrationPayload>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Observed(new CdcTransportAcknowledgement()));
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.RegisterConnector)
            .Completions.Should()
            .BeEmpty();
        _offsetReads.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_changed_provider_identity_after_the_running_wait()
    {
        _onCall = name =>
        {
            if (name == "offset")
            {
                _identity = new('b', 64);
            }
        };
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.EstablishConnector)
            .Completions.Should()
            .BeEmpty();
    }

    [Test]
    public async Task It_does_not_persist_payload_source_identifiers_offsets_or_readiness()
    {
        var result = await RunAsync();
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
        string journal = JsonSerializer.Serialize(ReadJournal());
        journal
            .Should()
            .NotContain("private-source-host")
            .And.NotContain("DATABASE_PASSWORD")
            .And.NotContain("database.password")
            .And.NotContain("lsn_proc")
            .And.NotContain("commit_lsn")
            .And.NotContain("Ready");
        JsonSerializer
            .Serialize(result)
            .Should()
            .NotContain("private-source-host")
            .And.NotContain("database.password");
    }
}
