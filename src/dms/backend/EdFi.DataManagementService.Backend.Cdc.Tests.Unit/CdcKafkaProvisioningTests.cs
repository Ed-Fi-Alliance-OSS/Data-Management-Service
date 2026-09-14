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
internal class Given_CdcKafkaProvisioning(Ddl.CdcProvider provider)
{
    private string _root = null!;
    private CdcDeploymentRequest _request = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ICdcBindingLifecycleService _bindings = null!;
    private ServiceProvider _services = null!;
    private ICdcKafkaAdminAdapter _kafka = null!;
    private ICdcProjectionRuntime _runtime = null!;
    private ICdcKafkaProducerInspection _producer = null!;
    private CdcKafkaProvisioning _controller = null!;
    private CdcDeploymentKafkaPolicyPlan _plan = null!;
    private Dictionary<string, CdcTransportResult<CdcKafkaTopicEvidence>> _topics = null!;
    private List<CdcKafkaAclGrant> _grants = null!;
    private List<string> _effects = null!;
    private bool _lostResponse;
    private bool _unknownAcls;
    private bool _unknownTopics;
    private bool _unknownProducer;
    private bool _lowBrokerCapacity;
    private bool _nonempty;
    private bool _latch;
    private DocumentCacheLifecycleState _lifecycle;
    private Action<CdcWorkflowWriteBoundary> _onWrite = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-kafka-" + Guid.NewGuid().ToString("N"));
        _onWrite = _ => { };
        _store = new(_root, TimeProvider.System, b => _onWrite(b));
        _request = CdcDeploymentRequestTestData.Request(
            provider,
            worker: CdcDeploymentRequestTestData.Worker(
                authorization: CdcKafkaAuthorizationProfile.AuthorizationEnabled
            )
        );
        _plan = CdcDeploymentKafkaPolicy.Build(_request);
        _topics = new(StringComparer.Ordinal);
        _grants = [];
        _effects = [];
        _lostResponse =
            _unknownAcls =
            _unknownTopics =
            _unknownProducer =
            _lowBrokerCapacity =
            _nonempty =
            _latch =
                false;
        _lifecycle = DocumentCacheLifecycleState.Tracking;
        var services = new ServiceCollection().AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(o => o.RootPath = _root);
        _services = services.BuildServiceProvider();
        _bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            _request.TargetIdentity,
            provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        await CompleteAsync(CdcWorkflowEffect.ReserveBinding);
        (await _bindings.CreateBindingIfAbsentAsync(_request.Binding, CancellationToken.None))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        await CompleteAsync(CdcWorkflowEffect.ActivateProjection);
        _runtime = A.Fake<ICdcProjectionRuntime>();
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
                new CdcInitialDatabaseObservation(
                    DocumentCacheTargetKey.Create(
                        _request.Binding.TenantKey,
                        long.Parse(_request.Binding.DataStoreId, CultureInfo.InvariantCulture)
                    ),
                    provider == Ddl.CdcProvider.Postgresql
                        ? RelationalProviderToken.Postgresql
                        : RelationalProviderToken.SqlServer,
                    _request.Binding.PhysicalSourceFingerprint,
                    DateTimeOffset.UtcNow,
                    new(_lifecycle, _latch),
                    new(!_nonempty, true, true),
                    Guid.NewGuid().ToString("D")
                )
            );
        _producer = A.Fake<ICdcKafkaProducerInspection>();
        A.CallTo(() => _producer.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                _unknownProducer
                    ? Unknown<CdcKafkaProducerCapacityEvidence>()
                    : Observed(
                        new CdcKafkaProducerCapacityEvidence(
                            _plan.MaxRecordBytes,
                            _plan.ProducerBufferBytes,
                            _request.WorkerPolicy.HeapBytes
                        )
                    )
            );
        _kafka = A.Fake<ICdcKafkaAdminAdapter>();
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .ReturnsLazily((CdcDeploymentRequest _, string topic, CancellationToken _) => ReadTopic(topic));
        A.CallTo(() => _kafka.InspectBrokersAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
                Observed(
                    new CdcKafkaBrokerEvidence(
                        true,
                        [new(0, _lowBrokerCapacity ? 1 : int.MaxValue, int.MaxValue, int.MaxValue)]
                    )
                )
            );
        A.CallTo(() => _kafka.InspectAclsAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(ReadAcls);
        A.CallTo(() =>
                _kafka.CreateMissingTopicAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaTopicIntent>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, CdcKafkaTopicIntent topic, CancellationToken _) =>
                {
                    AssertDurableIntent(topic.Role == CdcKafkaTopicRole.SharedOffsets);
                    _effects.Add("create:" + topic.Name);
                    _topics[topic.Name] = Observed(Topic(topic));
                    return _lostResponse ? Unknown<CdcKafkaTopicEvidence>() : _topics[topic.Name];
                }
            );
        A.CallTo(() =>
                _kafka.ReconcileMissingGrantsAsync(
                    A<CdcDeploymentRequest>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, bool shared, CancellationToken _) =>
                {
                    AssertDurableIntent(shared);
                    _effects.Add(shared ? "grant:shared" : "grant:binding");
                    var required = shared ? _plan.OffsetStoreGrants : _plan.BindingGrants;
                    _grants.AddRange(required.Except(_grants));
                    return _lostResponse ? Unknown<CdcKafkaAclEvidence>() : ReadAcls();
                }
            );
        _controller = new(_store, _bindings, _kafka, _runtime, _producer, TimeProvider.System);
    }

    [TearDown]
    public async Task Teardown()
    {
        await _runtime.DisposeAsync();
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private CdcTransportResult<CdcKafkaTopicEvidence> ReadTopic(string topic) =>
        _unknownTopics
            ? Unknown<CdcKafkaTopicEvidence>()
            : _topics.GetValueOrDefault(topic, new CdcTransportResult<CdcKafkaTopicEvidence>.Absent());

    private CdcTransportResult<CdcKafkaAclEvidence> ReadAcls() =>
        _unknownAcls
            ? Unknown<CdcKafkaAclEvidence>()
            : Observed(new CdcKafkaAclEvidence(true, true, false, [], _grants.ToArray()));

    private static CdcKafkaTopicEvidence Topic(CdcKafkaTopicIntent intent) =>
        new(
            intent.Name,
            Enumerable.Range(0, intent.PartitionCount).ToDictionary(i => i, _ => (IReadOnlyList<int>)[0]),
            intent.Configuration.ToDictionary(p => p.Key, p => new CdcKafkaConfigurationValue(p.Value, true))
        );

    private void AssertDurableIntent(bool shared)
    {
        Directory.GetFiles(Path.Combine(_root, "kafka-preparation"), "*.json").Should().NotBeEmpty();
        if (!shared)
        {
            File.ReadAllText(
                    Directory
                        .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                        .Single()
                )
                .Should()
                .Contain("PrepareKafka");
        }
    }

    private async Task<CdcWorkflowJournal> JournalAsync()
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        return await session.ReadAsync(_request.TargetIdentity, CancellationToken.None);
    }

    private async Task CompleteAsync(CdcWorkflowEffect effect, bool complete = true)
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        var journal = await session.ReadAsync(_request.TargetIdentity, CancellationToken.None);
        var id = Guid.NewGuid();
        await session.RecordIntentAsync(
            journal.Target,
            journal.WorkflowId,
            id,
            effect,
            [],
            CancellationToken.None
        );
        if (complete)
        {
            await session.ReconcileCompletionAsync(
                journal.Target,
                journal.WorkflowId,
                id,
                (_, _) =>
                    Task.FromResult(Observed<CdcWorkflowCompletion>(new CdcWorkflowCompletion.Reconciled())),
                CancellationToken.None
            );
        }
    }

    private async Task PrepareOffsetsAsync() =>
        Value(await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None))
            .PolicyState.Should()
            .Be(CdcConnectOffsetStorePolicyState.Satisfied);

    private async Task PrepareAllAsync()
    {
        await PrepareOffsetsAsync();
        Value(await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .PolicyState.Should()
            .Be(CdcKafkaPolicyState.Satisfied);
    }

    private static CdcTransportResult<T> Observed<T>(T value)
        where T : notnull => new CdcTransportResult<T>.Observed(value);

    private static CdcTransportResult<T> Unknown<T>()
        where T : notnull =>
        new CdcTransportResult<T>.Unavailable(
            new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
        );

    private static T Value<T>(CdcTransportResult<T> result)
        where T : notnull => result.Should().BeOfType<CdcTransportResult<T>.Observed>().Subject.Value;

    [Test]
    public async Task It_prepares_only_cluster_offsets_before_any_worker_or_binding_runtime_call()
    {
        Directory.Delete(Path.Combine(_root, "workflows"), true);
        await PrepareOffsetsAsync();
        _effects.Should().BeEquivalentTo("create:" + _plan.OffsetStore.Name, "grant:shared");
        _grants.Should().BeEquivalentTo(_plan.OffsetStoreGrants);
        _grants.Should().NotContain(g => g.Principal == _request.WorkerPolicy.Consumers[0].Principal.Value);
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _producer.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_journals_and_reconciles_exact_binding_topics_and_literal_grants()
    {
        await PrepareAllAsync();
        _topics
            .Keys.Should()
            .BeEquivalentTo(_plan.BindingTopics.Select(t => t.Name).Append(_plan.OffsetStore.Name));
        _grants.Should().BeEquivalentTo(_plan.BindingGrants.Concat(_plan.OffsetStoreGrants));
        (await JournalAsync())
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.PrepareKafka)
            .Completions.Should()
            .ContainSingle();
        _effects.Clear();
        await PrepareAllAsync();
        _effects.Should().BeEmpty();
    }

    [Test]
    public async Task It_accepts_lost_create_and_grant_responses_only_after_independent_live_reads()
    {
        _lostResponse = true;
        await PrepareAllAsync();
        _effects
            .Count(e => e.StartsWith("create:", StringComparison.Ordinal))
            .Should()
            .Be(_plan.BindingTopics.Count + 1);
    }

    [Test]
    public async Task It_does_not_treat_a_success_acknowledgement_as_live_topic_completion()
    {
        _request = WithTiming(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(100));
        A.CallTo(() =>
                _kafka.CreateMissingTopicAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaTopicIntent>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Observed(Topic(_plan.OffsetStore)));
        var result = await _controller
            .ProvisionOffsetStoreAsync(_request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        result
            .Diagnostics.Should()
            .ContainSingle(d =>
                d.Component == CdcDeploymentComponent.Kafka && d.Failure == CdcDeploymentFailure.Timeout
            );
        KafkaJournals().Single().ReconciledTopics.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_waits_for_new_topic_metadata_without_repeating_creation(bool lostResponse)
    {
        _lostResponse = lostResponse;
        _request = WithTiming(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        int readsAfterCreation = 0;
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, string topic, CancellationToken _) =>
                    _topics.ContainsKey(topic) && ++readsAfterCreation <= 2
                        ? new CdcTransportResult<CdcKafkaTopicEvidence>.Absent()
                        : ReadTopic(topic)
            );

        Value(await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None))
            .PolicyState.Should()
            .Be(CdcConnectOffsetStorePolicyState.Satisfied);
        readsAfterCreation.Should().BeGreaterThanOrEqualTo(3);
        _effects.Count(e => e.StartsWith("create:", StringComparison.Ordinal)).Should().Be(1);
        KafkaJournals().Single().ReconciledTopics.Should().ContainSingle();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_contradictory_or_unknown_new_topic_evidence_without_polling(bool unknown)
    {
        int readsAfterCreation = 0;
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, string topic, CancellationToken _) =>
                {
                    if (!_topics.ContainsKey(topic) || ++readsAfterCreation > 1)
                    {
                        return ReadTopic(topic);
                    }
                    return unknown
                        ? Unknown<CdcKafkaTopicEvidence>()
                        : Observed(
                            Topic(_plan.OffsetStore) with
                            {
                                Configuration = new Dictionary<string, CdcKafkaConfigurationValue>
                                {
                                    ["cleanup.policy"] = new("delete", true),
                                    ["min.insync.replicas"] = new("2", true),
                                },
                            }
                        );
                }
            );

        (await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        readsAfterCreation.Should().Be(1);
        KafkaJournals().Single().ReconciledTopics.Should().BeEmpty();
    }

    [Test]
    public async Task It_releases_the_lock_after_cancellation_during_new_topic_metadata_wait()
    {
        using var cancellation = new CancellationTokenSource();
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (CdcDeploymentRequest _, string topic, CancellationToken _) =>
                {
                    if (_topics.ContainsKey(topic))
                    {
                        cancellation.Cancel();
                    }
                    return new CdcTransportResult<CdcKafkaTopicEvidence>.Absent();
                }
            );

        Func<Task> setup = async () =>
            await _controller.ProvisionOffsetStoreAsync(_request, cancellation.Token);
        await setup.Should().ThrowAsync<OperationCanceledException>();
        KafkaJournals().Single().ReconciledTopics.Should().BeEmpty();
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_never_repairs_missing_previously_reconciled_topics(bool shared)
    {
        await PrepareAllAsync();
        _topics.Remove(shared ? _plan.OffsetStore.Name : _plan.BindingTopics[^1].Name);
        _effects.Clear();
        if (shared)
        {
            (await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        else
        {
            (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        _effects.Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_repairs_only_missing_required_acl_grants_in_eligible_setup(bool shared)
    {
        await PrepareAllAsync();
        _grants.Remove((shared ? _plan.OffsetStoreGrants : _plan.BindingGrants)[0]);
        _effects.Clear();
        if (shared)
        {
            await PrepareOffsetsAsync();
        }
        else
        {
            Value(await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
                .PolicyState.Should()
                .Be(CdcKafkaPolicyState.Satisfied);
        }
        _effects.Should().Equal(shared ? "grant:shared" : "grant:binding");
    }

    [TestCase("public")]
    [TestCase("progress")]
    [TestCase("shared")]
    public async Task It_rejects_drift_before_creating_other_missing_topics(string role)
    {
        await PrepareOffsetsAsync();
        var intent = role switch
        {
            "shared" => _plan.OffsetStore,
            "public" => _plan.BindingTopics[0],
            _ => _plan.BindingTopics[1],
        };
        var topic = Topic(intent);
        _topics[intent.Name] = Observed(
            topic with
            {
                Configuration = new Dictionary<string, CdcKafkaConfigurationValue>(topic.Configuration)
                {
                    ["cleanup.policy"] = new("delete", true),
                },
            }
        );
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [TestCase("wildcard")]
    [TestCase("peer")]
    [TestCase("internal")]
    [TestCase("group")]
    [TestCase("deny")]
    public async Task It_rejects_unsafe_effective_grants_without_removing_or_repairing_them(string fault)
    {
        await PrepareOffsetsAsync();
        var consumer = _request.WorkerPolicy.Consumers[0];
        _grants.Add(
            fault switch
            {
                "wildcard" => new(
                    consumer.Principal.Value,
                    CdcKafkaAclResourceType.Topic,
                    "*",
                    CdcKafkaAclOperation.Read
                ),
                "peer" => new(
                    consumer.Principal.Value,
                    CdcKafkaAclResourceType.Topic,
                    "another-instance",
                    CdcKafkaAclOperation.Read
                ),
                "internal" => new(
                    consumer.Principal.Value,
                    CdcKafkaAclResourceType.Topic,
                    _plan.BindingTopics[1].Name,
                    CdcKafkaAclOperation.Read
                ),
                "group" => new(
                    consumer.Principal.Value,
                    CdcKafkaAclResourceType.Group,
                    "*",
                    CdcKafkaAclOperation.Read
                ),
                _ => _plan.BindingGrants[0] with { Permission = CdcKafkaAclPermission.Deny },
            }
        );
        var before = _grants.ToArray();
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
        _grants.Should().Equal(before);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_observes_drift_without_any_journal_or_external_mutation(bool shared)
    {
        await PrepareAllAsync();
        var before = Files();
        _grants.Remove((shared ? _plan.OffsetStoreGrants : _plan.BindingGrants)[0]);
        _effects.Clear();
        if (shared)
        {
            Value(await _controller.ObserveOffsetStoreAsync(_request, CancellationToken.None))
                .PolicyState.Should()
                .Be(CdcConnectOffsetStorePolicyState.Invalid);
        }
        else
        {
            Value(await _controller.ObserveBindingAsync(_request, CancellationToken.None))
                .PolicyState.Should()
                .Be(CdcKafkaPolicyState.Invalid);
        }
        _effects.Should().BeEmpty();
        Files().Should().BeEquivalentTo(before);
    }

    [TestCase("workflows")]
    [TestCase("source-history")]
    [TestCase("bindings")]
    public async Task It_rejects_missing_binding_provenance_before_kafka_effects(string folder)
    {
        await PrepareOffsetsAsync();
        Directory.Delete(Path.Combine(_root, folder), true);
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [TestCase("rows")]
    [TestCase("latch")]
    [TestCase("disabled")]
    [TestCase("rebuilding")]
    public async Task It_requires_fresh_eligible_projection_before_binding_effects(string fault)
    {
        await PrepareOffsetsAsync();
        _nonempty = fault == "rows";
        _latch = fault == "latch";
        _lifecycle = fault switch
        {
            "disabled" => DocumentCacheLifecycleState.Disabled,
            "rebuilding" => DocumentCacheLifecycleState.Rebuilding,
            _ => DocumentCacheLifecycleState.Tracking,
        };
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [TestCase(CdcWorkflowEffect.RegisterConnector)]
    [TestCase(CdcWorkflowEffect.Retire)]
    [TestCase(CdcWorkflowEffect.StopConnector)]
    public async Task It_rejects_advanced_workflows_in_setup(CdcWorkflowEffect effect)
    {
        await PrepareOffsetsAsync();
        await CompleteAsync(effect, false);
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_low_broker_capacity_before_topic_creation()
    {
        await PrepareOffsetsAsync();
        _lowBrokerCapacity = true;
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [Test]
    public async Task It_prepares_infrastructure_without_fabricating_producer_readiness()
    {
        await PrepareOffsetsAsync();
        _unknownProducer = true;
        var observation = Value(await _controller.ProvisionBindingAsync(_request, CancellationToken.None));
        observation.PolicyState.Should().Be(CdcKafkaPolicyState.Unknown);
        (await JournalAsync())
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.PrepareKafka)
            .Completions.Should()
            .ContainSingle();
        _unknownProducer = false;
        Value(await _controller.ObserveBindingAsync(_request, CancellationToken.None))
            .PolicyState.Should()
            .Be(CdcKafkaPolicyState.Satisfied);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_keeps_unavailable_shared_policy_nonterminal_and_never_reports_binding_offset_loss(
        bool topic
    )
    {
        await PrepareAllAsync();
        _unknownTopics = topic;
        _unknownAcls = !topic;
        var result = Value(await _controller.ObserveOffsetStoreAsync(_request, CancellationToken.None));
        result.PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
        JsonSerializer.Serialize(result).Should().NotContain("sourceHistoryLost");
    }

    private static IEnumerable<object[]> CrashCases()
    {
        foreach (bool shared in new[] { true, false })
        {
            foreach (int write in Enumerable.Range(1, shared ? 4 : 8))
            {
                foreach (var boundary in Enum.GetValues<CdcWorkflowWriteBoundary>())
                {
                    yield return [shared, write, boundary];
                }
            }
        }
    }

    [TestCaseSource(nameof(CrashCases))]
    public async Task It_reconciles_interruption_at_each_atomic_intent_and_completion_boundary(
        bool shared,
        int write,
        CdcWorkflowWriteBoundary boundary
    )
    {
        if (!shared)
        {
            await PrepareOffsetsAsync();
        }
        int expectedWrites = shared ? 4 : _plan.BindingTopics.Count + 5;
        int crashWrite = Math.Min(write, expectedWrites);
        int reached = 0;
        _onWrite = b =>
        {
            if (b == boundary && ++reached == crashWrite)
            {
                throw new IOException("secret crash details");
            }
        };
        if (shared)
        {
            (await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        else
        {
            (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        reached.Should().Be(crashWrite);
        _onWrite = _ => { };
        await PrepareAllAsync();
        _effects.Where(e => e.StartsWith("create:", StringComparison.Ordinal)).Should().OnlyHaveUniqueItems();
        Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_lost_binding_kafka_receipts_even_when_live_artifacts_are_healthy()
    {
        await PrepareAllAsync();
        Directory.Delete(Path.Combine(_root, "kafka-preparation"), true);
        _effects.Clear();
        (await _controller.ProvisionBindingAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [TestCase("corrupt")]
    [TestCase("version")]
    [TestCase("scope")]
    [TestCase("duplicate")]
    [TestCase("permission")]
    public async Task It_fails_closed_on_invalid_cluster_receipts(string fault)
    {
        await PrepareOffsetsAsync();
        string path = Directory.GetFiles(Path.Combine(_root, "kafka-preparation"), "*.json").Single();
        string json = await File.ReadAllTextAsync(path);
        if (fault == "permission" && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
            );
        }
        else
        {
            await File.WriteAllTextAsync(
                path,
                fault switch
                {
                    "corrupt" => "{",
                    "version" => json.Replace("\"version\":1", "\"version\":999", StringComparison.Ordinal),
                    "scope" => json.Replace(
                        KafkaJournals().Single().ScopeHash,
                        "sha256:" + new string('b', 64),
                        StringComparison.Ordinal
                    ),
                    _ => "{\"version\":1," + json[1..],
                }
            );
        }
        _effects.Clear();
        (await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_offset_topic_changes_for_the_same_worker_without_creating_a_replacement()
    {
        await PrepareOffsetsAsync();
        var changed = CdcDeploymentRequestTestData.Request(
            provider,
            worker: CdcDeploymentRequestTestData.Worker(
                authorization: CdcKafkaAuthorizationProfile.AuthorizationEnabled,
                offsetTopic: "other-offsets"
            )
        );
        _effects.Clear();
        (await _controller.ProvisionOffsetStoreAsync(changed, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_shared_failure_for_each_binding_without_mutating_incident_state()
    {
        await PrepareAllAsync();
        var other = CdcDeploymentRequestTestData.Request(
            provider == Ddl.CdcProvider.Postgresql ? Ddl.CdcProvider.SqlServer : Ddl.CdcProvider.Postgresql,
            worker: _request.WorkerPolicy
        );
        var before = Files();
        _unknownAcls = true;
        Value(await _controller.ObserveOffsetStoreAsync(_request, CancellationToken.None))
            .PolicyState.Should()
            .Be(CdcConnectOffsetStorePolicyState.Unknown);
        Value(await _controller.ObserveOffsetStoreAsync(other, CancellationToken.None))
            .PolicyState.Should()
            .Be(CdcConnectOffsetStorePolicyState.Unknown);
        Files().Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_retains_one_controller_lock_across_external_effects_and_releases_after_cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() =>
                _kafka.CreateMissingTopicAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaTopicIntent>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (CdcDeploymentRequest _, CdcKafkaTopicIntent _, CancellationToken ct) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return Unknown<CdcKafkaTopicEvidence>();
                }
            );
        using var cancellation = new CancellationTokenSource();
        var setup = _controller.ProvisionOffsetStoreAsync(_request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Func<Task> contender = async () =>
        {
            await using var held = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None
            );
        };
        (await contender.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.LockTimeout);
        await cancellation.CancelAsync();
        Func<Task> cancelled = async () => await setup;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_bounds_a_noncooperative_transport_and_keeps_unknown_evidence_distinct_from_absence()
    {
        _grants.AddRange(_plan.OffsetStoreGrants);
        _request = WithTiming(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        var pending = new TaskCompletionSource<CdcTransportResult<CdcKafkaTopicEvidence>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        A.CallTo(() =>
                _kafka.InspectTopicAsync(A<CdcDeploymentRequest>._, A<string>._, A<CancellationToken>._)
            )
            .Returns(pending.Task);
        try
        {
            var result = await _controller
                .ObserveOffsetStoreAsync(_request, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Value(result).PolicyState.Should().Be(CdcConnectOffsetStorePolicyState.Unknown);
            _effects.Should().BeEmpty();
        }
        finally
        {
            pending.SetResult(Unknown<CdcKafkaTopicEvidence>());
        }
    }

    [Test]
    public async Task It_bounds_projection_inspection_before_kafka_effects()
    {
        await PrepareOffsetsAsync();
        _request = WithTiming(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1));
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily((CancellationToken ct) => NeverObserveAsync(ct));
        _effects.Clear();
        var result = await _controller.ProvisionBindingAsync(_request, CancellationToken.None);
        result
            .Diagnostics.Should()
            .ContainSingle(d =>
                d.Component == CdcDeploymentComponent.Projection && d.Failure == CdcDeploymentFailure.Timeout
            );
        _effects.Should().BeEmpty();
    }

    [Test]
    public async Task It_sanitizes_exceptions_and_persists_only_hashed_topic_and_acl_intent()
    {
        A.CallTo(() =>
                _kafka.CreateMissingTopicAsync(
                    A<CdcDeploymentRequest>._,
                    A<CdcKafkaTopicIntent>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new UnauthorizedAccessException("private-source-host super-secret"));
        var result = await _controller.ProvisionOffsetStoreAsync(_request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        string output =
            JsonSerializer.Serialize(result)
            + string.Join(
                "",
                Directory
                    .GetFiles(Path.Combine(_root, "kafka-preparation"), "*.json")
                    .Select(File.ReadAllText)
            );
        foreach (
            string secret in new[]
            {
                "private-source-host",
                "super-secret",
                _request.WorkerPolicy.WorkerPrincipal.Value,
                _plan.OffsetStore.Name,
            }
        )
        {
            output.Should().NotContain(secret);
        }
    }

    private static async Task<CdcInitialDatabaseObservation> NeverObserveAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    }

    private CdcDeploymentRequest WithTiming(TimeSpan call, TimeSpan wait) =>
        new(
            _request.Binding,
            _request.DmsSettings,
            _request.ProviderSetup,
            _request.ConnectEndpoint,
            _request.WorkerMetricsEndpoint,
            _request.ConnectorPolicy,
            _request.WorkerPolicy,
            _request.ProviderConnectionProperties,
            _request.KafkaClientSecurityProperties,
            new(call, wait, TimeSpan.FromMilliseconds(10))
        );

    private Dictionary<string, string> Files() =>
        Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllText);

    private CdcKafkaPreparationJournal[] KafkaJournals() =>
        Directory
            .GetFiles(Path.Combine(_root, "kafka-preparation"), "*.json")
            .Select(p =>
                JsonSerializer.Deserialize<CdcKafkaPreparationJournal>(
                    File.ReadAllText(p),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!
            )
            .ToArray();
}

[TestFixture]
internal class Given_CdcKafkaProvisioningProducerInspection
{
    private CdcDeploymentRequest _request = null!;
    private ICdcConnectTransport _connect = null!;
    private ICdcWorkerInspectionTransport _worker = null!;
    private CdcKafkaProducerInspection _inspection = null!;
    private Dictionary<string, string> _configuration = null!;
    private CdcWorkerInspection _before = null!;
    private CdcWorkerInspection _after = null!;

    [SetUp]
    public void Setup()
    {
        _request = CdcDeploymentRequestTestData.Request();
        _configuration = new()
        {
            ["producer.override.max.request.size"] = "42",
            ["producer.override.buffer.memory"] = "12345",
        };
        _before = Worker("one");
        _after = Worker("one");
        _worker = A.Fake<ICdcWorkerInspectionTransport>();
        int calls = 0;
        A.CallTo(() => _worker.InspectAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
                new CdcTransportResult<CdcWorkerInspection>.Observed(++calls == 1 ? _before : _after)
            );
        _connect = A.Fake<ICdcConnectTransport>();
        A.CallTo(() => _connect.ReadConfigurationAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
                new CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed(_configuration)
            );
        _inspection = new(_connect, _worker);
    }

    private CdcWorkerInspection Worker(
        string identity,
        long heap = 56789,
        string offset = "connect-offsets"
    ) =>
        new(
            identity,
            _request.WorkerMetricsEndpoint,
            new Dictionary<string, string> { ["offset.storage.topic"] = offset },
            _request.WorkerPolicy.QualifiedImageDigest,
            heap
        );

    [Test]
    public async Task It_supplies_actual_capacity_even_when_it_differs_from_requested_policy()
    {
        var result = await _inspection.InspectAsync(_request, CancellationToken.None);
        result
            .Should()
            .BeOfType<CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed>()
            .Subject.Value.Should()
            .Be(new CdcKafkaProducerCapacityEvidence(42, 12345, 56789));
    }

    [TestCase("missing")]
    [TestCase("malformed")]
    [TestCase("overflow")]
    public async Task It_never_fills_missing_or_malformed_overrides_from_the_request(string fault)
    {
        if (fault == "missing")
        {
            _configuration.Remove("producer.override.buffer.memory");
        }
        else
        {
            _configuration["producer.override.buffer.memory"] =
                fault == "overflow" ? "9223372036854775808" : "secret";
        }
        (await _inspection.InspectAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("process")]
    [TestCase("heap")]
    [TestCase("offset")]
    [TestCase("missingIdentity")]
    public async Task It_requires_stable_worker_identity_and_heap_for_the_selected_offset_store(string fault)
    {
        _after = fault switch
        {
            "process" => Worker("two"),
            "heap" => Worker("one", 67890),
            "offset" => Worker("one", offset: "other-offsets"),
            _ => Worker(""),
        };
        (await _inspection.InspectAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_treats_an_unregistered_connector_as_unavailable_capacity()
    {
        A.CallTo(() => _connect.ReadConfigurationAsync(_request, A<CancellationToken>._))
            .Returns(new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent());
        (await _inspection.InspectAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }
}
