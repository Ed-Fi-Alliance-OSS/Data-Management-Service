// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
internal class Given_CdcBindingRetirement(Ddl.CdcProvider provider)
{
    private string _root = null!;
    private ServiceProvider _services = null!;
    private CdcDeploymentRequest _request = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ICdcBindingLifecycleService _bindings = null!;
    private ICdcBindingLifecycleService _realBindings = null!;
    private ICdcConnectTransport _connect = null!;
    private ICdcKafkaArtifactCleanupAdapter _kafka = null!;
    private ICdcProviderArtifactCleanupAdapter _provider = null!;
    private CdcBindingRetirement _controller = null!;
    private Guid _workflow;
    private bool _exists;
    private bool _stopped;
    private bool _offsets;
    private bool _jobs;
    private HashSet<CdcGovernedArtifactKind> _artifacts = null!;
    private List<string> _trace = null!;
    private Action<string> _onCall = null!;
    private Action<CdcWorkflowWriteBoundary> _onWrite = null!;
    private CdcArtifactCleanupScope _scope = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-retirement-" + Guid.NewGuid().ToString("N"));
        _request = CdcDeploymentRequestTestData.Request(provider);
        _onWrite = _ => { };
        _onCall = _ => { };
        _trace = [];
        _store = new(_root, TimeProvider.System, boundary => _onWrite(boundary));
        await ProvisionAsync();
        var services = new ServiceCollection().AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = _root);
        _services = services.BuildServiceProvider();
        _realBindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _bindings = A.Fake<ICdcBindingLifecycleService>(options => options.Strict());
        A.CallTo(() => _bindings.ListBindingsAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                (string deployment, CancellationToken ct) => _realBindings.ListBindingsAsync(deployment, ct)
            );
        A.CallTo(() => _bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken ct) =>
                    _realBindings.ExactMatchBindingAsync(binding, ct)
            );
        A.CallTo(() =>
                _bindings.DeleteStateAfterVerifiedCleanupAsync(A<CdcCleanupProof>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                async (CdcCleanupProof proof, CancellationToken ct) =>
                {
                    Trace("state-delete");
                    _exists.Should().BeFalse();
                    _offsets.Should().BeFalse();
                    _artifacts.Should().BeEmpty();
                    _jobs.Should().BeFalse();
                    Step(CdcRetirementStepKind.DeleteState).Should().NotBeNull();
                    proof.GovernedArtifacts.Should().HaveCount(_scope.Inventory.Count);
                    var result = await _realBindings.DeleteStateAfterVerifiedCleanupAsync(proof, ct);
                    Trace("state-deleted");
                    return result;
                }
            );
        await using (var session = await Acquire())
        {
            await session.RecordIntentAsync(
                _request.TargetIdentity,
                _workflow,
                Guid.NewGuid(),
                CdcWorkflowEffect.ReserveBinding,
                [],
                CancellationToken.None
            );
            (await _realBindings.CreateBindingIfAbsentAsync(_request.Binding))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
        }
        _scope = CdcArtifactCleanupTestData.Scope(_request);
        _artifacts = _scope
            .Inventory.Select(a => a.Kind)
            .Where(k =>
                k
                    is not (
                        CdcGovernedArtifactKind.KafkaConnectConnector
                        or CdcGovernedArtifactKind.ConnectSourceOffsets
                    )
            )
            .ToHashSet();
        _exists = _offsets = true;
        _stopped = false;
        _jobs = provider == Ddl.CdcProvider.SqlServer;
        _connect = A.Fake<ICdcConnectTransport>(options => options.Strict());
        _kafka = A.Fake<ICdcKafkaArtifactCleanupAdapter>(options => options.Strict());
        _provider = A.Fake<ICdcProviderArtifactCleanupAdapter>(options => options.Strict());
        A.CallTo(() => _connect.ReadConfigurationAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("config");
                return _exists
                    ? Observed<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())
                    : new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent();
            });
        A.CallTo(() => _connect.StopAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("stop");
                Step(CdcRetirementStepKind.ConnectSourceOffsets).Should().NotBeNull();
                _stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                return Observed(Status());
            });
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("offsets");
                using var json = JsonDocument.Parse(_offsets ? "{\"offsets\":[{}]}" : "{\"offsets\":[]}");
                return Observed(CdcConnectOffsetEvidence.Parse(_request, json.RootElement));
            });
        A.CallTo(() => _connect.DeleteOffsetsAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("offset-delete");
                _stopped.Should().BeTrue();
                _exists.Should().BeTrue();
                _offsets = false;
                Trace("offset-deleted");
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.DeleteAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("connector-delete");
                Step(CdcRetirementStepKind.ConnectSourceOffsets).VerifiedAt.Should().ContainSingle();
                Step(CdcRetirementStepKind.KafkaConnectConnector).Should().NotBeNull();
                _offsets.Should().BeFalse();
                _stopped.Should().BeTrue();
                _exists = false;
                Trace("connector-deleted");
                return Observed(new CdcTransportAcknowledgement());
            });
        foreach (var adapter in new ICdcArtifactCleanupAdapter[] { _kafka, _provider })
        {
            A.CallTo(() =>
                    adapter.DeleteAsync(
                        A<CdcArtifactCleanupScope>._,
                        A<CdcGovernedArtifactKind>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(
                    (CdcArtifactCleanupScope scope, CdcGovernedArtifactKind kind, CancellationToken _) =>
                    {
                        Trace(kind.ToString());
                        _exists.Should().BeFalse();
                        Step(Enum.Parse<CdcRetirementStepKind>(kind.ToString())).Should().NotBeNull();
                        scope.Request.Should().BeSameAs(_request);
                        scope.Inventory.Should().BeEquivalentTo(_scope.Inventory);
                        if (scope.RequireAbsence && _artifacts.Contains(kind))
                        {
                            return new CdcTransportResult<CdcGovernedArtifact>.Unavailable(
                                new(
                                    CdcDeploymentComponent.ProviderSetup,
                                    CdcDeploymentFailure.ValidationFailed
                                )
                            );
                        }
                        bool deleted = _artifacts.Remove(kind);
                        Trace(kind + "-deleted");
                        return Observed(
                            new CdcGovernedArtifact(
                                kind,
                                scope.Inventory.Single(a => a.Kind == kind).Name,
                                deleted ? CdcCleanupState.Deleted : CdcCleanupState.NotFound,
                                "verified"
                            )
                        );
                    }
                );
        }
        A.CallTo(() =>
                _provider.DeleteOwnedSqlServerJobsAsync(
                    A<CdcArtifactCleanupScope>._,
                    A<LocalCdcWorkflowJournalStore.Session>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                Trace("jobs");
                Step(CdcRetirementStepKind.SqlServerJobs).Should().NotBeNull();
                _artifacts
                    .Should()
                    .NotContain(k =>
                        k == CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                        || k == CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                        || k == CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
                    );
                _jobs = false;
                Trace("jobs-deleted");
                return Observed(new CdcTransportAcknowledgement());
            });
        ResetController();
    }

    [TearDown]
    public void Teardown()
    {
        _services.Dispose();
        Directory.Delete(_root, true);
    }

    private async Task ProvisionAsync()
    {
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        _workflow = (
            await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
                _request.TargetIdentity,
                provisioner,
                purpose: CdcWorkflowPurpose.InitialCdcProvisioning
            )
        ).WorkflowId;
    }

    private void ResetController() =>
        _controller = new(_store, _bindings, _connect, _kafka, _provider, TimeProvider.System);

    private void Trace(string call)
    {
        _trace.Add(call);
        _onCall(call);
    }

    private static CdcTransportResult<T> Observed<T>(T value)
        where T : notnull => new CdcTransportResult<T>.Observed(value);

    private Task<LocalCdcWorkflowJournalStore.Session> Acquire() =>
        _store.AcquireAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), CancellationToken.None);

    private Task<CdcBindingRetirementResult> Run(CancellationToken token = default) =>
        _controller.RetireAsync(_request, _request.Binding.Generation, true, token);

    private string JournalPath =>
        Directory.GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories).Single();
    private string BindingPath =>
        Path.Combine(
            _root,
            "bindings",
            _request.Binding.DeploymentKey,
            _request.Binding.InstanceKey,
            _request.Binding.Generation + ".json"
        );

    private string IncidentPath =>
        Path.Combine(
            _root,
            "incidents",
            _request.Binding.DeploymentKey,
            _request.Binding.InstanceKey,
            _request.Binding.Generation + ".json"
        );

    private CdcWorkflowJournal Journal() =>
        JsonSerializer.Deserialize<CdcWorkflowJournal>(
            File.ReadAllText(JournalPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() },
            }
        )!;

    private CdcRetirementStep Step(CdcRetirementStepKind kind) =>
        Journal()
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.Retire)
            .Retirement.Single()
            .Steps.Single(s => s.Kind == kind);

    private CdcConnectStatus Status() =>
        new(
            new(
                1,
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow,
                _request.TargetIdentity,
                _request.Binding.Provider,
                _request.Binding.PhysicalSourceFingerprint,
                _request.Binding.ConnectorName,
                _stopped ? CdcConnectorRuntimeState.Stopped : CdcConnectorRuntimeState.Running,
                _stopped ? 0 : 1,
                _stopped ? 0 : 1,
                _stopped ? CdcConnectorRuntimeState.Stopped : CdcConnectorRuntimeState.Running,
                CdcConnectorSnapshotState.NotApplicable,
                null,
                null,
                []
            ),
            "private-worker",
            _stopped ? [] : [new(0, CdcConnectorRuntimeState.Running, "private-worker")]
        );

    private async Task PrepareUnreservedAsync(bool possible = true)
    {
        // Start another owned fixture database, then interrupt the real reservation write sequence.
        Directory.Delete(_root, true);
        await ProvisionAsync();
        _exists = _offsets = _jobs = false;
        _artifacts.Clear();
        if (possible)
        {
            await using var session = await Acquire();
            _onWrite = boundary =>
            {
                if (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement)
                {
                    throw new IOException("interrupted after durable source exposure");
                }
            };
            try
            {
                Func<Task> reserve = () =>
                    session.RecordIntentAsync(
                        _request.TargetIdentity,
                        _workflow,
                        Guid.NewGuid(),
                        CdcWorkflowEffect.ReserveBinding,
                        [],
                        CancellationToken.None
                    );
                (await reserve.Should().ThrowAsync<CdcWorkflowStateException>())
                    .Which.Failure.Should()
                    .Be(CdcWorkflowStateFailure.Unavailable);
            }
            finally
            {
                _onWrite = _ => { };
            }
        }
        Journal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.ReserveBinding);
        File.Exists(BindingPath).Should().BeFalse();
        await using var verification = await Acquire();
        var history = await verification.ReadSourcePublicationHistoryAsync(
            _request.TargetIdentity,
            _request.Binding.PhysicalSourceFingerprint,
            CancellationToken.None
        );
        history
            .Transitions[^1]
            .Status.Should()
            .Be(
                possible
                    ? DocumentCacheDownstreamPublicationStatus.Possible
                    : DocumentCacheDownstreamPublicationStatus.InternalOnly
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_retires_unreserved_source_without_reversing_exposure(bool possible)
    {
        await PrepareUnreservedAsync(possible);
        string historyPath = Directory
            .GetFiles(Path.Combine(_root, "source-history"), "*.json", SearchOption.AllDirectories)
            .Single();
        string originalHistory = await File.ReadAllTextAsync(historyPath);
        var result = await Run();
        result.Succeeded.Should().BeTrue();
        Journal().Operations.Last().Completions.Should().ContainSingle();
        Journal()
            .Operations.Last()
            .Retirement.Single()
            .Steps.Should()
            .OnlyContain(s => s.VerifiedAt.Length == 1);
        (await File.ReadAllTextAsync(historyPath)).Should().Be(originalHistory);
        File.Exists(BindingPath).Should().BeFalse();
        Journal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.ReserveBinding);
        _trace
            .Should()
            .Contain("offsets")
            .And.Contain("PublicTopic")
            .And.NotContain("state-delete")
            .And.NotContain("stop")
            .And.NotContain("offset-delete")
            .And.NotContain("connector-delete");
        A.CallTo(() =>
                _bindings.DeleteStateAfterVerifiedCleanupAsync(A<CdcCleanupProof>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
        var runtime = A.Fake<ICdcProjectionRuntime>(o => o.Strict());
        (
            await new CdcInitialEnablement(_store, _bindings, TimeProvider.System).ActivateAsync(
                _request,
                runtime
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        Fake.GetCalls(runtime).Should().BeEmpty();
        // Completion is resumable, but still requires fresh live absence evidence.
        _trace.Clear();
        (await Run()).Succeeded.Should().BeTrue();
        _trace.Should().Contain("offsets").And.Contain("PublicTopic");
        (await File.ReadAllTextAsync(historyPath)).Should().Be(originalHistory);
        if (possible)
        {
            var history = new CdcDownstreamPublicationHistoryProvider(
                _store,
                _request.Binding.DeploymentKey,
                _request.Binding.Provider,
                TimeProvider.System,
                TimeSpan.FromSeconds(1)
            );
            var key = DocumentCacheTargetKey.Create(string.Empty, 1);
            var fingerprint = new DocumentCachePhysicalSourceFingerprint(
                _request.Binding.PhysicalSourceFingerprint
            );
            var command = new DocumentCacheAdministrativeCommandRunnerRequest(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                DocumentCacheAdministrativeTargetKey.FromTargetKey(key)
            );
            var administration = await history.ExecuteAsync(
                command,
                async () =>
                {
                    var observation = await history.ObserveAsync(key, fingerprint);
                    observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Possible);
                    var proof = DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                        key,
                        fingerprint,
                        observation
                    );
                    return new(command.Command, command.TargetKey, proof.Classification);
                },
                CancellationToken.None
            );
            administration
                .Classification.Should()
                .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        }
    }

    [Test]
    public async Task It_holds_unreserved_possible_cleanup_session_through_absence_checks()
    {
        await PrepareUnreservedAsync();
        A.CallTo(() => _connect.ReadConfigurationAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                Journal().Operations.Last().Effect.Should().Be(CdcWorkflowEffect.Retire);
                Func<Task> competing = async () =>
                {
                    await using var session = await _store.AcquireAsync(
                        TimeSpan.FromMilliseconds(40),
                        TimeSpan.FromMilliseconds(5),
                        CancellationToken.None
                    );
                };
                (await competing.Should().ThrowAsync<CdcWorkflowStateException>())
                    .Which.Failure.Should()
                    .Be(CdcWorkflowStateFailure.LockTimeout);
                return (CdcTransportResult<IReadOnlyDictionary<string, string>>)
                    new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent();
            });
        (await Run()).Succeeded.Should().BeTrue();
        await using var released = await Acquire();
    }

    [TestCase("connector")]
    [TestCase("offsets")]
    [TestCase("topic")]
    [TestCase("provider")]
    [TestCase("unavailable")]
    public async Task It_rejects_unreserved_possible_cleanup_without_authoritative_absence(string failure)
    {
        await PrepareUnreservedAsync();
        switch (failure)
        {
            case "connector":
                _exists = true;
                break;
            case "offsets":
                _offsets = true;
                break;
            case "topic":
                _artifacts.Add(CdcGovernedArtifactKind.PublicTopic);
                break;
            case "provider":
                _artifacts.Add(
                    provider == Ddl.CdcProvider.Postgresql
                        ? CdcGovernedArtifactKind.PostgresqlLogicalSlot
                        : CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                );
                break;
            default:
                A.CallTo(() => _connect.ReadConfigurationAsync(_request, A<CancellationToken>._))
                    .Returns(
                        new CdcTransportResult<IReadOnlyDictionary<string, string>>.Unavailable(
                            new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                        )
                    );
                break;
        }
        var artifacts = _artifacts.ToArray();
        (await Run()).Succeeded.Should().BeFalse();
        Journal().Operations.Last().Effect.Should().Be(CdcWorkflowEffect.Retire);
        Journal().Operations.Last().Completions.Should().BeEmpty();
        _artifacts.Should().BeEquivalentTo(artifacts);
        _trace
            .Should()
            .NotContain("state-delete")
            .And.NotContain("stop")
            .And.NotContain("offset-delete")
            .And.NotContain("connector-delete");
        await using var session = await Acquire();
        (
            await session.ReadSourcePublicationHistoryAsync(
                _request.TargetIdentity,
                _request.Binding.PhysicalSourceFingerprint,
                CancellationToken.None
            )
        )
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
    }

    [TestCase("binding")]
    [TestCase("alias")]
    [TestCase("generation")]
    [TestCase("incident")]
    [TestCase("receipt")]
    [TestCase("source")]
    [TestCase("missing-history")]
    [TestCase("corrupt-history")]
    [TestCase("active")]
    [TestCase("historical")]
    [TestCase("unavailable-inventory")]
    public async Task It_rejects_unreserved_possible_cleanup_with_contradictory_provenance(string failure)
    {
        await PrepareUnreservedAsync();
        if (failure is "binding" or "alias" or "generation")
        {
            var binding = failure switch
            {
                "alias" => CdcConnectorTemplateTestData.BuildBinding(
                    provider,
                    tenantKey: "alias",
                    dataStoreId: "2",
                    instanceKey: "alias"
                ),
                "generation" => CdcConnectorTemplateTestData.BuildBinding(
                    provider,
                    bindingGeneration: _request.Binding.Generation + 1
                ),
                _ => _request.Binding,
            };
            (await _realBindings.CreateBindingIfAbsentAsync(binding))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
        }
        else if (failure == "unavailable-inventory")
        {
            A.CallTo(() => _bindings.ListBindingsAsync(A<string>._, A<CancellationToken>._))
                .ThrowsAsync(new IOException("unavailable private inventory"));
        }
        else if (failure == "incident")
        {
            (await _realBindings.CreateBindingIfAbsentAsync(_request.Binding))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
            var incident = new CdcIncident(
                1,
                CdcIncidentType.SourceHistoryContinuityLost,
                DateTimeOffset.UtcNow,
                _request.Binding.ToCompleteBindingIdentity(),
                CdcIncidentFailureCategory.ConnectOffsetMissing,
                new(
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [CdcIncidentUnavailableFact.ConnectOffset]
                )
            );
            (await _realBindings.LatchSourceHistoryLossAsync(incident))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
            File.Delete(BindingPath);
        }
        else
        {
            string path = Directory
                .GetFiles(Path.Combine(_root, "source-history"), "*.json", SearchOption.AllDirectories)
                .Single();
            if (failure == "missing-history")
            {
                File.Delete(path);
            }
            else if (failure == "corrupt-history")
            {
                await File.WriteAllTextAsync(path, "{");
            }
            else
            {
                var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                if (failure == "receipt")
                {
                    json["creationReceipt"]!["receiptId"] = Guid.NewGuid();
                }
                else if (failure is "active" or "historical")
                {
                    json["transitions"]!.AsArray()[^1]!["status"] = failure;
                }
                else
                {
                    json["physicalSourceFingerprint"] = new string('f', 64);
                }
                await File.WriteAllTextAsync(path, json.ToJsonString());
            }
        }
        (await Run()).Succeeded.Should().BeFalse();
        _trace.Should().BeEmpty();
        Journal().Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.Retire);
        await using var session = await Acquire();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_resumes_unreserved_possible_cleanup_after_interrupted_verification(bool cancellation)
    {
        await PrepareUnreservedAsync();
        using var caller = new CancellationTokenSource();
        _onWrite = boundary =>
        {
            if (
                boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement
                && Journal().Operations.Last().Retirement is [{ Steps.Length: > 0 } retirement]
                && retirement.Steps.Last()
                    is { Kind: CdcRetirementStepKind.PublicTopic, VerifiedAt.Length: 1 }
            )
            {
                if (cancellation)
                {
                    caller.Cancel();
                    caller.Token.ThrowIfCancellationRequested();
                }
                throw new IOException("interrupted verified cleanup");
            }
        };
        if (cancellation)
        {
            Func<Task> run = () => Run(caller.Token);
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            (await Run()).Succeeded.Should().BeFalse();
        }
        Guid operation = Journal().Operations.Last().OperationId;
        Journal().Operations.Last().Completions.Should().BeEmpty();
        _onWrite = _ => { };
        _trace.Clear();
        var result = await Run();
        result.Succeeded.Should().BeTrue();
        result.OperationId.Should().Be(operation);
        _trace.Should().Contain("offsets").And.Contain("PublicTopic");
        await using var session = await Acquire();
        (
            await session.ReadSourcePublicationHistoryAsync(
                _request.TargetIdentity,
                _request.Binding.PhysicalSourceFingerprint,
                CancellationToken.None
            )
        )
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
    }

    [Test]
    public async Task It_retires_in_order_and_retains_exposure_history_and_journal()
    {
        var result = await Run();
        result.Succeeded.Should().BeTrue();
        result.OperationId.Should().NotBeEmpty();
        File.Exists(BindingPath).Should().BeFalse();
        var steps = Journal().Operations.Last().Retirement.Single().Steps;
        steps
            .Select(s => s.Kind)
            .Should()
            .Equal(CdcWorkflowJournalValidation.RetirementSteps(_request.Binding.Provider));
        steps.Should().OnlyContain(s => s.VerifiedAt.Length == 1);
        Journal().Operations.Last().Completions.Should().ContainSingle();
        await using var session = await Acquire();
        var history = await session.ReadSourcePublicationHistoryAsync(
            _request.TargetIdentity,
            _request.Binding.PhysicalSourceFingerprint,
            CancellationToken.None
        );
        history.Transitions[^1].Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Historical);
        File.Exists(Path.Combine(_root, "controller.lock")).Should().BeTrue();
    }

    [Test]
    public async Task It_can_reconcile_a_completed_retirement_again_without_recreating_state()
    {
        var first = await Run();
        first.Succeeded.Should().BeTrue();
        _trace.Clear();
        ResetController();
        var second = await Run();
        second.Succeeded.Should().BeTrue();
        second.OperationId.Should().Be(first.OperationId);
        _trace.Should().NotContain("stop").And.NotContain("offset-delete").And.NotContain("connector-delete");
        _trace.Should().Contain("PublicTopic").And.Contain("state-delete");
    }

    [TestCase(false, 1)]
    [TestCase(true, 0)]
    [TestCase(true, -1)]
    [TestCase(true, 999)]
    public async Task It_requires_explicit_matching_generation_and_destructive_intent(
        bool destructive,
        long generation
    )
    {
        (await _controller.RetireAsync(_request, generation, destructive)).Succeeded.Should().BeFalse();
        _trace.Should().BeEmpty();
        Journal().RetirementIntended.Should().BeFalse();
        File.Exists(BindingPath).Should().BeTrue();
    }

    [TestCase("binding")]
    [TestCase("journal")]
    [TestCase("history")]
    public async Task It_rejects_missing_cleanup_authority_before_effects(string missing)
    {
        File.Delete(
            missing switch
            {
                "binding" => BindingPath,
                "journal" => JournalPath,
                _ => Directory
                    .GetFiles(Path.Combine(_root, "source-history"), "*.json", SearchOption.AllDirectories)
                    .Single(),
            }
        );
        (await Run()).Succeeded.Should().BeFalse();
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_generic_legacy_retirement_as_cleanup_authority()
    {
        await using (var session = await Acquire())
        {
            await session.RecordIntentAsync(
                _request.TargetIdentity,
                _workflow,
                Guid.NewGuid(),
                CdcWorkflowEffect.Retire,
                [],
                CancellationToken.None
            );
        }
        (await Run()).Succeeded.Should().BeFalse();
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_missing_binding_after_partial_artifact_cleanup()
    {
        _onCall = call =>
        {
            if (call == "PublicTopic")
            {
                throw new IOException("secret");
            }
        };
        (await Run()).Succeeded.Should().BeFalse();
        File.Delete(BindingPath);
        _onCall = _ => { };
        _trace.Clear();
        (await Run()).Succeeded.Should().BeFalse();
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_an_absent_connector_without_offset_evidence()
    {
        _exists = false;
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_request, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcConnectOffsetEvidence>.Absent());
        (await Run()).Succeeded.Should().BeFalse();
        File.Exists(BindingPath).Should().BeTrue();
        _trace.Should().NotContain("PublicTopic");
    }

    [TestCase(CdcRetirementOffsetState.Absent, true)]
    [TestCase(CdcRetirementOffsetState.Present, false)]
    public async Task It_uses_kafka_absence_only_when_absent_connector_rest_cannot_inspect_offsets(
        CdcRetirementOffsetState state,
        bool succeeds
    )
    {
        _exists = _offsets = _jobs = false;
        _artifacts.Clear();
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_request, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                )
            );
        A.CallTo(() =>
                _kafka.InspectRetirementOffsetsAsync(A<CdcArtifactCleanupScope>._, A<CancellationToken>._)
            )
            .Returns(Observed(state));
        (await Run()).Succeeded.Should().Be(succeeds);
        File.Exists(BindingPath).Should().Be(!succeeds);
        _trace.Should().NotContain("offset-delete").And.NotContain("connector-delete");
        A.CallTo(() =>
                _kafka.InspectRetirementOffsetsAsync(A<CdcArtifactCleanupScope>._, A<CancellationToken>._)
            )
            .MustHaveHappened();
    }

    [Test]
    public async Task It_retains_state_when_both_absent_connector_offset_sources_are_unavailable()
    {
        _exists = false;
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_request, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                )
            );
        A.CallTo(() =>
                _kafka.InspectRetirementOffsetsAsync(A<CdcArtifactCleanupScope>._, A<CancellationToken>._)
            )
            .Returns(
                new CdcTransportResult<CdcRetirementOffsetState>.Unavailable(
                    new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.AuthenticationFailed)
                )
            );
        var result = await Run();
        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new CdcDeploymentDiagnostic(
                    CdcDeploymentComponent.Kafka,
                    CdcDeploymentFailure.AuthenticationFailed
                )
            );
        File.Exists(BindingPath).Should().BeTrue();
        _trace.Should().NotContain("PublicTopic").And.NotContain("connector-delete");
    }

    [Test]
    public async Task It_never_overrides_observed_retained_rest_offsets_with_kafka_absence()
    {
        _exists = false;
        A.CallTo(() =>
                _kafka.InspectRetirementOffsetsAsync(A<CdcArtifactCleanupScope>._, A<CancellationToken>._)
            )
            .Returns(Observed(CdcRetirementOffsetState.Absent));
        (await Run()).Succeeded.Should().BeFalse();
        File.Exists(BindingPath).Should().BeTrue();
        A.CallTo(() =>
                _kafka.InspectRetirementOffsetsAsync(A<CdcArtifactCleanupScope>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_retires_an_unused_binding_only_with_independent_absence_evidence()
    {
        _exists = _offsets = _jobs = false;
        _artifacts.Clear();
        (await Run()).Succeeded.Should().BeTrue();
        _trace.Should().Contain("offsets").And.NotContain("offset-delete").And.NotContain("connector-delete");
    }

    [TestCase("stop")]
    [TestCase("offset-delete")]
    [TestCase("offset-deleted")]
    [TestCase("connector-delete")]
    [TestCase("connector-deleted")]
    [TestCase("PublicTopicAcls")]
    [TestCase("PublicTopicAcls-deleted")]
    [TestCase("ProgressTopicAcls")]
    [TestCase("ProgressTopicAcls-deleted")]
    [TestCase("PublicTopic")]
    [TestCase("PublicTopic-deleted")]
    [TestCase("ProgressTopic")]
    [TestCase("ProgressTopic-deleted")]
    [TestCase("state-delete")]
    [TestCase("state-deleted")]
    public async Task It_reconciles_crashes_at_external_boundaries(string boundary)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = call =>
        {
            if (call == boundary)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        Func<Task> run = () => Run(cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        _onCall = _ => { };
        if (boundary != "state-deleted")
        {
            File.Exists(BindingPath).Should().BeTrue();
        }
        ResetController();
        (await Run()).Succeeded.Should().BeTrue();
        File.Exists(BindingPath).Should().BeFalse();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public async Task It_reconciles_provider_capture_and_job_interruptions(int artifactIndex)
    {
        var candidates = CdcWorkflowJournalValidation
            .RetirementSteps(_request.Binding.Provider)
            .Where(k =>
                k.ToString()
                    .StartsWith(
                        provider == Ddl.CdcProvider.Postgresql ? "Postgresql" : "SqlServer",
                        StringComparison.Ordinal
                    )
            )
            .ToArray();
        // Exercise all provider artifacts without skipping either provider's fixture.
        string boundary =
            candidates[artifactIndex % candidates.Length] == CdcRetirementStepKind.SqlServerJobs
                ? "jobs"
                : candidates[artifactIndex % candidates.Length].ToString();
        _onCall = call =>
        {
            if (call == boundary)
            {
                throw new IOException("private-source-secret");
            }
        };
        var failed = await Run();
        failed.Succeeded.Should().BeFalse();
        JsonSerializer.Serialize(failed).Should().NotContain("private-source-secret");
        File.Exists(BindingPath).Should().BeTrue();
        _onCall = _ => { };
        (await Run()).Succeeded.Should().BeTrue();
    }

    private static IEnumerable<TestCaseData> AtomicBoundaries()
    {
        foreach (var boundary in Enum.GetValues<CdcWorkflowWriteBoundary>())
        {
            foreach (
                var phase in new[]
                {
                    "retirement",
                    "offsets",
                    "connector",
                    "provider",
                    "acl",
                    "topic",
                    "state",
                    "history",
                    "completion",
                }
            )
            {
                yield return new(boundary, phase, false);
                if (phase is "offsets" or "connector" or "provider" or "acl" or "topic" or "state")
                {
                    yield return new(boundary, phase, true);
                }
            }
        }
    }

    [TestCaseSource(nameof(AtomicBoundaries))]
    public async Task It_recovers_each_atomic_journal_boundary(
        CdcWorkflowWriteBoundary boundary,
        string phase,
        bool completion
    )
    {
        var steps = CdcWorkflowJournalValidation.RetirementSteps(_request.Binding.Provider);
        var step = phase switch
        {
            "offsets" => CdcRetirementStepKind.ConnectSourceOffsets,
            "connector" => CdcRetirementStepKind.KafkaConnectConnector,
            "provider" => steps[2],
            "acl" => CdcRetirementStepKind.PublicTopicAcls,
            "topic" => CdcRetirementStepKind.PublicTopic,
            _ => CdcRetirementStepKind.DeleteState,
        };
        int write = phase switch
        {
            "retirement" => 1,
            "history" => 2 + steps.Length * 2,
            "completion" => 3 + steps.Length * 2,
            _ => 2 + Array.IndexOf(steps, step) * 2 + (completion ? 1 : 0),
        };
        int seen = 0;
        _onWrite = b =>
        {
            if (b == boundary && ++seen == write)
            {
                throw new IOException("secret");
            }
        };
        (await Run()).Succeeded.Should().BeFalse();
        _onWrite = _ => { };
        ResetController();
        (await Run()).Succeeded.Should().BeTrue();
    }

    [TestCase(CdcRetirementStepKind.ConnectSourceOffsets, false)]
    [TestCase(CdcRetirementStepKind.ConnectSourceOffsets, true)]
    [TestCase(CdcRetirementStepKind.KafkaConnectConnector, false)]
    [TestCase(CdcRetirementStepKind.KafkaConnectConnector, true)]
    [TestCase(CdcRetirementStepKind.PublicTopicAcls, false)]
    [TestCase(CdcRetirementStepKind.PublicTopicAcls, true)]
    [TestCase(CdcRetirementStepKind.PublicTopic, false)]
    [TestCase(CdcRetirementStepKind.PublicTopic, true)]
    [TestCase(CdcRetirementStepKind.DeleteState, false)]
    [TestCase(CdcRetirementStepKind.DeleteState, true)]
    public async Task It_resumes_each_persisted_cleanup_checkpoint(
        CdcRetirementStepKind kind,
        bool completion
    )
    {
        _onWrite = boundary =>
        {
            if (boundary != CdcWorkflowWriteBoundary.AfterAtomicReplacement)
            {
                return;
            }
            var step = Journal().Operations.Last().Retirement.Single().Steps.LastOrDefault();
            if (step is not null && step.Kind == kind && (step.VerifiedAt.Length == 1) == completion)
            {
                throw new IOException("secret");
            }
        };
        (await Run()).Succeeded.Should().BeFalse();
        _onWrite = _ => { };
        (await Run()).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_blocks_all_later_lifecycle_intents_after_retirement_begins()
    {
        _onCall = call =>
        {
            if (call == "stop")
            {
                throw new IOException();
            }
        };
        (await Run()).Succeeded.Should().BeFalse();
        await using var session = await Acquire();
        foreach (
            var effect in new[]
            {
                CdcWorkflowEffect.ReserveBinding,
                CdcWorkflowEffect.RegisterConnector,
                CdcWorkflowEffect.ResumeConnector,
                CdcWorkflowEffect.StopConnector,
                CdcWorkflowEffect.Retire,
            }
        )
        {
            Func<Task> intent = () =>
                session.RecordIntentAsync(
                    _request.TargetIdentity,
                    _workflow,
                    Guid.NewGuid(),
                    effect,
                    [],
                    CancellationToken.None
                );
            await intent.Should().ThrowAsync<CdcWorkflowStateException>();
        }
    }

    [Test]
    public async Task It_rejects_a_foreign_artifact_result_and_retains_state()
    {
        A.CallTo(() =>
                _kafka.DeleteAsync(
                    A<CdcArtifactCleanupScope>._,
                    A<CdcGovernedArtifactKind>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Observed(
                    new CdcGovernedArtifact(
                        CdcGovernedArtifactKind.PublicTopic,
                        "foreign-topic",
                        CdcCleanupState.Deleted,
                        "secret"
                    )
                )
            );
        (await Run()).Succeeded.Should().BeFalse();
        File.Exists(BindingPath).Should().BeTrue();
    }

    [Test]
    public async Task It_rejects_a_connector_that_did_not_stop()
    {
        A.CallTo(() => _connect.StopAsync(_request, A<CancellationToken>._))
            .Returns(Observed(new CdcTransportAcknowledgement()));
        (await Run()).Succeeded.Should().BeFalse();
        _trace
            .Should()
            .NotContain("offset-delete")
            .And.NotContain("connector-delete")
            .And.NotContain("PublicTopic");
    }

    [Test]
    public async Task It_retains_the_lock_through_cleanup_and_preserves_cancellation()
    {
        var entered = new TaskCompletionSource();
        var pending = new TaskCompletionSource<CdcTransportResult<CdcGovernedArtifact>>();
        A.CallTo(() =>
                _kafka.DeleteAsync(
                    A<CdcArtifactCleanupScope>._,
                    A<CdcGovernedArtifactKind>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => entered.TrySetResult())
            .Returns(pending.Task);
        using var cancellation = new CancellationTokenSource();
        var running = Run(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Func<Task> contender = () =>
            new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None
            );
        (await contender.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.LockTimeout);
        await cancellation.CancelAsync();
        Func<Task> observe = () => running;
        (await observe.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should()
            .Be(cancellation.Token);
        await using var session = await Acquire();
        File.Exists(BindingPath).Should().BeTrue();
        pending.TrySetResult(
            new CdcTransportResult<CdcGovernedArtifact>.Unavailable(
                new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
            )
        );
    }

    [Test]
    public async Task It_preserves_peer_bindings_and_shared_state()
    {
        var peerNames = CdcArtifactNameGenerator
            .Render(new(_request.Binding.DeploymentKey, "peer", "peer", 2, _request.Binding.Provider))
            .Inventory!;
        var peer = _request.Binding with
        {
            InstanceKey = "peer",
            DataStoreId = "999",
            Generation = 2,
            PhysicalSourceFingerprint = "sha256:" + new string('b', 64),
            ConnectorName = peerNames.ConnectorName,
            TopicName = peerNames.TopicName,
        };
        (await _realBindings.CreateBindingIfAbsentAsync(peer))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        string sharedPath = Path.Combine(_root, "worker-state");
        await File.WriteAllTextAsync(sharedPath, "shared-worker-offsets");
        (await Run()).Succeeded.Should().BeTrue();
        (await _realBindings.ExactMatchBindingAsync(peer))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        (await File.ReadAllTextAsync(sharedPath)).Should().Be("shared-worker-offsets");
    }

    [Test]
    public async Task It_retains_incident_until_cleanup_then_recovers_the_orphan_incident_boundary()
    {
        CdcIncident incident = new(
            1,
            CdcIncidentType.SourceHistoryContinuityLost,
            DateTimeOffset.UtcNow,
            _request.Binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            new(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [CdcIncidentUnavailableFact.ConnectOffset]
            )
        );
        (await _realBindings.LatchSourceHistoryLossAsync(incident))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        bool interrupted = false;
        _onCall = call =>
        {
            if (call == "state-delete" && !interrupted)
            {
                File.Exists(IncidentPath).Should().BeTrue();
                File.Delete(BindingPath);
                interrupted = true;
                throw new IOException("after binding deletion");
            }
        };
        (await Run()).Succeeded.Should().BeFalse();
        File.Exists(IncidentPath).Should().BeTrue();
        _onCall = _ => { };
        (await Run()).Succeeded.Should().BeTrue();
        File.Exists(IncidentPath).Should().BeFalse();
    }

    [TestCase("version")]
    [TestCase("binding")]
    [TestCase("scope")]
    [TestCase("step")]
    public async Task It_rejects_corrupt_or_contradictory_cleanup_provenance(string corruption)
    {
        _onCall = call =>
        {
            if (call == "stop")
            {
                throw new IOException();
            }
        };
        (await Run()).Succeeded.Should().BeFalse();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        var retirement = json["operations"]!.AsArray()[^1]!["retirement"]![0]!;
        switch (corruption)
        {
            case "version":
                json["version"] = 999;
                break;
            case "binding":
                retirement["binding"]!["partitionCount"] = 99;
                break;
            case "scope":
                retirement["binding"]!["physicalSourceFingerprint"] = "sha256:" + new string('b', 64);
                break;
            default:
                retirement["steps"]![0]!["kind"] = "DeleteState";
                break;
        }
        await File.WriteAllTextAsync(JournalPath, json.ToJsonString());
        _trace.Clear();
        _onCall = _ => { };
        (await Run()).Succeeded.Should().BeFalse();
        _trace.Should().BeEmpty();
        File.Exists(BindingPath).Should().BeTrue();
    }

    [TestCase("foreign")]
    [TestCase("stale")]
    [TestCase("task")]
    public async Task It_rejects_mismatched_stale_or_contradictory_stopped_evidence(string mismatch)
    {
        A.CallTo(() => _connect.ReadStatusAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var status = Status();
                var runtime = mismatch switch
                {
                    "foreign" => status.Runtime with { ConnectorName = "foreign" },
                    "stale" => status.Runtime with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                    _ => status.Runtime with { TaskCount = 1 },
                };
                return Observed(new CdcConnectStatus(runtime, status.WorkerId, status.Tasks));
            });
        (await Run()).Succeeded.Should().BeFalse();
        _trace.Should().NotContain("offset-delete").And.NotContain("connector-delete");
    }

    [Test]
    public async Task It_bounds_unavailable_cleanup_and_preserves_component_diagnostics()
    {
        _request = CdcArtifactCleanupTestData.WithTiming(_request);
        _scope = CdcArtifactCleanupTestData.Scope(_request);
        _exists = _offsets = false;
        A.CallTo(() => _connect.ReadConfigurationAsync(_request, A<CancellationToken>._))
            .Returns(new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent());
        using var json = JsonDocument.Parse("{\"offsets\":[]}");
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(_request, A<CancellationToken>._))
            .Returns(Observed(CdcConnectOffsetEvidence.Parse(_request, json.RootElement)));

        var pending = new TaskCompletionSource<CdcTransportResult<CdcGovernedArtifact>>();
        A.CallTo(() =>
                _kafka.DeleteAsync(
                    A<CdcArtifactCleanupScope>._,
                    A<CdcGovernedArtifactKind>._,
                    A<CancellationToken>._
                )
            )
            .Returns(pending.Task);
        var failed = await Run();
        failed.Succeeded.Should().BeFalse();
        failed.Diagnostics.Should().ContainSingle().Which.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        failed.Diagnostics.Single().Component.Should().Be(CdcDeploymentComponent.Kafka);
        File.Exists(BindingPath).Should().BeTrue();
        pending.TrySetResult(
            new CdcTransportResult<CdcGovernedArtifact>.Unavailable(
                new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
            )
        );
    }
}
