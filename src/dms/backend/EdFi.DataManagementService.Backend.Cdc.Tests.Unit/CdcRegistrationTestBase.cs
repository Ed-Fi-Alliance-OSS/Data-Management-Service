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

internal abstract class CdcRegistrationTestBase(Ddl.CdcProvider provider)
{
    protected Ddl.CdcProvider Provider { get; } = provider;
    protected string _root = null!;
    protected CdcDeploymentRequest _request = null!;
    protected LocalCdcWorkflowJournalStore _store = null!;
    protected ServiceProvider _services = null!;
    protected ICdcProjectionRuntime _runtime = null!;
    protected Ddl.ICdcProviderSetupService _provider = null!;
    protected CdcConnectorRegistration _controller = null!;
    protected ICdcConnectTransport _connect = null!;
    protected ICdcKafkaAdminAdapter _kafka = null!;
    protected ICdcWorkerInspectionTransport _worker = null!;
    protected ICdcConnectorTemplateService _templates = null!;
    protected CdcProviderSetupHandoff _handoff = null!;
    protected CdcWorkerInspection _workerEvidence = null!;
    protected Dictionary<string, string> _live = null!;
    protected List<string> _trace = null!;
    protected Action<CdcWorkflowWriteBoundary> _onWrite = null!;
    protected Action<string> _onCall = null!;
    protected bool _connectorExists;
    protected CdcConnectOffsetState _offsetState;
    protected CdcConnectorRuntimeState _runtimeState;
    protected int _posts;
    protected int _offsetReads;
    protected List<Ddl.CdcProviderSetupRequest> _calls = null!;
    protected DocumentCacheLifecycleState _lifecycle;
    protected bool _rows;
    protected bool _latch;
    protected bool _exists;
    protected string _identity = null!;
    protected Func<Ddl.CdcProviderSetupResult, Ddl.CdcProviderSetupResult> _change = null!;
    protected CdcTargetIdentity Target => _request.TargetIdentity;

    protected virtual CdcDeploymentRequest CreateRequest() =>
        CdcDeploymentRequestTestData.Request(
            Provider,
            worker: CdcDeploymentRequestTestData.Worker(digest: CdcQualifiedWorkerImage.Digests.Single())
        );

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-provider-" + Guid.NewGuid().ToString("N"));
        _onWrite = _ => { };
        _onCall = _ => { };
        _store = new(_root, TimeProvider.System, b => _onWrite(b));
        _request = CreateRequest();
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
        await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        var services = new ServiceCollection().AddCdcConnectorTemplates().AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(o => o.RootPath = _root);
        _services = services.BuildServiceProvider();
        _runtime = A.Fake<ICdcProjectionRuntime>();
        A.CallTo(() => _runtime.ObserveEstablishedDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily((CancellationToken ct) => _runtime.ObserveInitialDatabaseAsync(ct));
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
                new CdcInitialDatabaseObservation(
                    DocumentCacheTargetKey.Create(
                        Target.TenantKey,
                        long.Parse(Target.DataStoreId, CultureInfo.InvariantCulture)
                    ),
                    Provider == Ddl.CdcProvider.Postgresql
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

    protected Ddl.CdcProviderSetupResult ProviderResult(Ddl.CdcProviderSetupRequest r)
    {
        _calls.Add(r);
        Trace("provider");
        ReadJournal().Operations.Should().Contain(o => o.Effect == CdcWorkflowEffect.CreateProvider);
        _lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        var result = CdcConnectorTemplateTestData.BuildProviderSetupResult(
            Provider,
            mode: r.Mode,
            binding: _request.Binding
        );
        bool created = !_exists && r.Mode == Ddl.CdcProviderSetupMode.InitialCreateOrExactMatch;
        if (created)
        {
            _exists = true;
        }
        if (
            !_exists
            || Provider == Ddl.CdcProvider.Postgresql
                && !created
                && r.Mode == Ddl.CdcProviderSetupMode.InitialCreateOrExactMatch
                && r.PostgresqlInitialReplicationSlotProof is null
        )
        {
            return result with { Outcome = Ddl.CdcProviderSetupOutcome.Failed };
        }
        var proof =
            Provider == Ddl.CdcProvider.Postgresql
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

    protected CdcWorkflowJournal ReadJournal() =>
        JsonSerializer.Deserialize<CdcWorkflowJournal>(
            File.ReadAllText(
                Directory
                    .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                    .Single(p =>
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(p));
                        return document
                                .RootElement.GetProperty("target")
                                .GetProperty("instanceKey")
                                .GetString() == Target.InstanceKey;
                    })
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

    protected sealed class SafeNameConverter : System.Text.Json.Serialization.JsonConverter<Ddl.CdcSafeName>
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

    protected void ResetController() =>
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

    protected void Trace(string name)
    {
        _trace.Add(name);
        _onCall(name);
    }

    protected static CdcTransportResult<T> Observed<T>(T value)
        where T : notnull => new CdcTransportResult<T>.Observed(value);

    protected Task<CdcTransportResult<CdcConnectorRegistrationReceipt>> RunAsync(
        CancellationToken token = default
    ) => _controller.RegisterAsync(_request, _runtime, token);

    protected CdcConnectStatus Status() =>
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

    protected CdcConnectOffsetEvidence Offsets()
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
        if (Provider == Ddl.CdcProvider.SqlServer)
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
                            offset = Provider == Ddl.CdcProvider.Postgresql
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

    protected async Task<CdcWorkflowJournal> CompleteAsync(CdcWorkflowEffect effect)
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

    protected async Task RegisterIntentAsync()
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

    protected void ShortTiming(int milliseconds = 250) =>
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
}
