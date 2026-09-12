// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
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
public class Given_CdcInitialEnablement(Ddl.CdcProvider provider)
{
    private string _root = null!;
    private CdcDeploymentRequest _request = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ServiceProvider _services = null!;
    private ICdcBindingLifecycleService _realBindings = null!;
    private ICdcBindingLifecycleService _bindings = null!;
    private ICdcProjectionRuntime _runtime = null!;
    private CdcInitialEnablement _controller = null!;
    private CdcManagedProvisioningResult _created = null!;
    private DocumentCacheLifecycleState _lifecycle;
    private bool _latch;
    private DocumentCacheGuardedNewEmptyActivationState _tables = null!;
    private List<string> _trace = null!;
    private List<string> _calls = null!;
    private Func<string, CancellationToken, Task> _beforeCall = null!;
    private CdcTargetIdentity Target => _request.TargetIdentity;
    private DocumentCacheTargetKey TargetKey =>
        DocumentCacheTargetKey.Create(
            Target.TenantKey,
            long.Parse(Target.DataStoreId, CultureInfo.InvariantCulture)
        );

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-initial-" + Guid.NewGuid().ToString("N"));
        _request = CdcDeploymentRequestTestData.Request(provider);
        _store = new(_root);
        _trace = [];
        _calls = [];
        _beforeCall = (_, _) => Task.CompletedTask;
        int exactReads = 0;
        int observations = 0;
        _lifecycle = DocumentCacheLifecycleState.Disabled;
        _latch = false;
        _tables = new(true, true, true);
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        _created = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        var services = new ServiceCollection();
        services.AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(o => o.RootPath = _root);
        _services = services.BuildServiceProvider();
        _realBindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _bindings = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => _bindings.ListBindingsAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (string deployment, CancellationToken token) =>
                {
                    await BeforeCallAsync("list", token);
                    return await _realBindings.ListBindingsAsync(deployment, token);
                }
            );
        A.CallTo(() => _bindings.ExactMatchBindingAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcBinding binding, CancellationToken token) =>
                {
                    await BeforeCallAsync($"exact-{++exactReads}", token);
                    return await _realBindings.ExactMatchBindingAsync(binding, token);
                }
            );
        A.CallTo(() => _bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcBinding binding, CancellationToken token) =>
                {
                    await BeforeCallAsync("create", token);
                    _trace.Add("binding");
                    var history = await ReadHistoryWithoutLockAsync();
                    history
                        .Transitions[^1]
                        .Status.Should()
                        .Be(DocumentCacheDownstreamPublicationStatus.Possible);
                    ReadJournalWithoutLock()
                        .Operations.Should()
                        .Contain(o => o.Effect == CdcWorkflowEffect.ReserveBinding);
                    return await _realBindings.CreateBindingIfAbsentAsync(binding, token);
                }
            );
        _runtime = A.Fake<ICdcProjectionRuntime>();
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(
                async (CancellationToken token) =>
                {
                    await BeforeCallAsync($"observe-{++observations}", token);
                    _trace.Add("eligibility");
                    return Observation();
                }
            );
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (DocumentCacheGuardedNewEmptyActivationRequest request, CancellationToken token) =>
                {
                    await BeforeCallAsync("activate", token);
                    _trace.Add("activation");
                    (await _realBindings.ExactMatchBindingAsync(_request.Binding, token))
                        .Status.Should()
                        .Be(CdcControlPlaneOperationStatus.Succeeded);
                    ReadJournalWithoutLock()
                        .Operations.Should()
                        .Contain(o => o.Effect == CdcWorkflowEffect.ReserveBinding && !o.Completions.IsEmpty);
                    request
                        .ExpectedPhysicalSourceFingerprint!.Value.Should()
                        .Be(_request.Binding.PhysicalSourceFingerprint);
                    request
                        .Confirmation.Should()
                        .Be(DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation);
                    _lifecycle = DocumentCacheLifecycleState.Tracking;
                    return Success();
                }
            );
        _controller = new(_store, _bindings, TimeProvider.System);
    }

    [TearDown]
    public async Task Teardown()
    {
        await _runtime.DisposeAsync();
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private CdcInitialDatabaseObservation Observation() =>
        new(
            TargetKey,
            provider == Ddl.CdcProvider.Postgresql
                ? RelationalProviderToken.Postgresql
                : RelationalProviderToken.SqlServer,
            _request.Binding.PhysicalSourceFingerprint,
            DateTimeOffset.UtcNow,
            new(_lifecycle, _latch),
            _tables,
            Guid.NewGuid().ToString("D")
        );

    private DocumentCacheAdministrativeCommandResult Success() =>
        new(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            DocumentCacheAdministrativeTargetKey.FromTargetKey(TargetKey),
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            true
        );

    private CdcWorkflowJournal ReadJournalWithoutLock() =>
        JsonSerializer.Deserialize<CdcWorkflowJournal>(
            File.ReadAllText(
                Directory
                    .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                    .Single()
            ),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }
        )!;

    private async Task<CdcSourcePublicationHistory> ReadHistoryWithoutLockAsync() =>
        JsonSerializer.Deserialize<CdcSourcePublicationHistory>(
            await File.ReadAllTextAsync(
                Directory
                    .GetFiles(Path.Combine(_root, "source-history"), "*.json", SearchOption.AllDirectories)
                    .Single()
            ),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }
        )!;

    private Task<CdcTransportResult<CdcInitialEnablementEvidence>> RunAsync() =>
        _controller.ActivateAsync(_request, _runtime);

    private async Task IntentAsync(CdcWorkflowEffect effect, bool complete = false)
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        Guid id = Guid.NewGuid();
        await session.RecordIntentAsync(Target, _created.WorkflowId, id, effect, [], CancellationToken.None);
        if (complete)
        {
            await session.ReconcileCompletionAsync(
                Target,
                _created.WorkflowId,
                id,
                (_, _) =>
                    Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Reconciled()
                        )
                    ),
                CancellationToken.None
            );
        }
    }

    [Test]
    public async Task It_reserves_durably_before_activation_and_returns_fresh_tracking_evidence()
    {
        var result = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcInitialEnablementEvidence>.Observed>()
            .Subject.Value;
        result
            .Retry.RetryClassification.Should()
            .Be(CdcRetryClassification.ResumeProviderTopicConnectorSetup);
        result.Eligibility.LifecycleState.Should().Be(CdcLifecycleState.Tracking);
        result.ProvisioningProof.SetupControllerRunId.Should().Be(_created.WorkflowId.ToString("D"));
        _trace.Should().Equal("eligibility", "binding", "activation", "eligibility");
        ReadJournalWithoutLock().Operations.Last().Completions.Should().ContainSingle();
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [TestCase(DocumentCacheLifecycleState.Tracking)]
    [TestCase(DocumentCacheLifecycleState.Resetting)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding)]
    public async Task It_rejects_unbound_non_disabled_lifecycle_before_mutation(
        DocumentCacheLifecycleState state
    )
    {
        _lifecycle = state;
        await AssertNoMutationAsync();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task It_rejects_each_nonempty_table_before_reservation(int table)
    {
        _tables = new(table != 0, table != 1, table != 2);
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_rejects_the_cache_ahead_latch()
    {
        _latch = true;
        await AssertNoMutationAsync();
    }

    [TestCase("source")]
    [TestCase("provider")]
    [TestCase("target")]
    [TestCase("old")]
    [TestCase("future")]
    [TestCase("token")]
    public async Task It_rejects_mismatched_or_stale_provider_evidence(string mismatch)
    {
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
                mismatch switch
                {
                    "source" => Observation() with
                    {
                        PhysicalSourceFingerprint = "sha256:" + new string('f', 64),
                    },
                    "provider" => Observation() with
                    {
                        Provider =
                            provider == Ddl.CdcProvider.Postgresql
                                ? RelationalProviderToken.SqlServer
                                : RelationalProviderToken.Postgresql,
                    },
                    "target" => Observation() with
                    {
                        TargetKey = DocumentCacheTargetKey.Create("other", 999),
                    },
                    "old" => Observation() with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
                    "future" => Observation() with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(2) },
                    _ => Observation() with { TransactionObservationId = "" },
                }
            );
        await AssertNoMutationAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_missing_provenance_without_reconstruction(bool history)
    {
        Directory.Delete(Path.Combine(_root, history ? "source-history" : "workflows"), true);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_a_surviving_binding_without_reservation_provenance()
    {
        await _realBindings.CreateBindingIfAbsentAsync(_request.Binding);
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_rejects_a_missing_completed_binding()
    {
        await IntentAsync(CdcWorkflowEffect.ReserveBinding, true);
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_rejects_tracking_without_our_activation_intent()
    {
        await IntentAsync(CdcWorkflowEffect.ReserveBinding);
        await _realBindings.CreateBindingIfAbsentAsync(_request.Binding);
        _lifecycle = DocumentCacheLifecycleState.Tracking;
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_rejects_binding_drift_without_repair()
    {
        await IntentAsync(CdcWorkflowEffect.ReserveBinding);
        await _realBindings.CreateBindingIfAbsentAsync(
            _request.Binding with
            {
                PartitionCount = _request.Binding.PartitionCount + 1,
            }
        );
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_uses_existing_duplicate_source_alias_validation()
    {
        var alias = CdcConnectorTemplateTestData.BuildBinding(
            provider,
            tenantKey: "alias",
            dataStoreId: "2",
            instanceKey: "alias"
        );
        (await _realBindings.CreateBindingIfAbsentAsync(alias))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        (await _realBindings.ExactMatchBindingAsync(alias))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_recovers_a_lost_binding_create_response_without_rewriting_the_binding(bool committed)
    {
        A.CallTo(() => _bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                async Task<CdcBindingLifecycleResult> (CdcBinding binding, CancellationToken token) =>
                {
                    if (committed)
                    {
                        await _realBindings.CreateBindingIfAbsentAsync(binding, token);
                    }
                    throw new IOException("Password=secret");
                }
            );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        var bytes = committed ? BindingBytes() : [];
        A.CallTo(() => _bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcBinding binding, CancellationToken token) =>
                    _realBindings.CreateBindingIfAbsentAsync(binding, token)
            );
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        if (committed)
        {
            BindingBytes().Should().Equal(bytes);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_recovers_activation_interruptions_on_either_side_of_commit(bool committed)
    {
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(DocumentCacheAdministrativeCommandResult () =>
            {
                if (committed)
                {
                    _lifecycle = DocumentCacheLifecycleState.Tracking;
                }
                throw new IOException("Host=private;Password=secret");
            });
        var first = await RunAsync();
        first.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(first).Should().NotContain("secret").And.NotContain("private");
        byte[] bytes = BindingBytes();
        int calls = 0;
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                calls++;
                _lifecycle = DocumentCacheLifecycleState.Tracking;
                return Success();
            });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        calls.Should().Be(committed ? 0 : 1);
        BindingBytes().Should().Equal(bytes);
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task It_resumes_after_each_durable_journal_boundary(int write)
    {
        int writes = 0;
        var interrupted = new LocalCdcWorkflowJournalStore(
            _root,
            TimeProvider.System,
            boundary =>
            {
                if (boundary == CdcWorkflowWriteBoundary.AfterAtomicReplacement && ++writes == write)
                {
                    throw new IOException("Simulated controller crash");
                }
            }
        );
        var result = await new CdcInitialEnablement(
            interrupted,
            _bindings,
            TimeProvider.System
        ).ActivateAsync(_request, _runtime);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [Test]
    public async Task It_reobserves_completed_activation_without_reusing_proof_or_mutating_files()
    {
        var first = (CdcTransportResult<CdcInitialEnablementEvidence>.Observed)await RunAsync();
        var files = Snapshot();
        _trace.Clear();
        var second = (CdcTransportResult<CdcInitialEnablementEvidence>.Observed)await RunAsync();
        second.Value.ProvisioningProof.ProofId.Should().NotBe(first.Value.ProvisioningProof.ProofId);
        second
            .Value.Eligibility.ProviderConsistencyToken.Should()
            .NotBe(first.Value.Eligibility.ProviderConsistencyToken);
        _trace.Should().Equal("eligibility", "eligibility");
        Snapshot().Should().BeEquivalentTo(files);
    }

    [Test]
    public async Task It_does_not_trust_a_successful_activation_response_without_a_live_commit()
    {
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Success());
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        ReadJournalWithoutLock().Operations.Last().Completions.Should().BeEmpty();
    }

    [Test]
    public async Task It_propagates_cancellation_and_releases_the_controller_lock()
    {
        using var cancellation = new CancellationTokenSource();
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(
                async (CancellationToken token) =>
                {
                    await cancellation.CancelAsync();
                    token.ThrowIfCancellationRequested();
                    return Observation();
                }
            );
        Func<Task> run = () => _controller.ActivateAsync(_request, _runtime, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
    }

    [TestCase("")]
    [TestCase("DeFaUlT")]
    public async Task It_maps_E18_tenant_identity_forward_and_activates_the_actual_runtime_target(
        string tenant
    )
    {
        var runtimeTarget = DocumentCacheTargetKey.Create(tenant, 1);
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(() => Observation() with { TargetKey = runtimeTarget });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>.That.Matches(r =>
                        r.TargetKey.TargetKey.Equals(runtimeTarget)
                    ),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_source_history_only_creation_before_observations_or_mutations(
        bool previouslyWritten
    )
    {
        Directory.Delete(_root, true);
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(Target, provisioner);
        // Current table contents cannot upgrade creation purpose, with or without prior writer use.
        _tables = new(!previouslyWritten, true, true);
        await AssertNoMutationAsync();
        _tables = new(true, true, true);
        await AssertNoMutationAsync();
        _trace.Should().BeEmpty();
        ReadJournalWithoutLock().Purpose.Should().Be(CdcWorkflowPurpose.SourceHistoryOnly);
        (await ReadHistoryWithoutLockAsync())
            .Transitions[^1]
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
    }

    [TestCase("missing")]
    [TestCase("unknown")]
    [TestCase("contradictory")]
    public async Task It_rejects_missing_or_contradictory_creation_purpose(string mutation)
    {
        if (mutation == "contradictory")
        {
            (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
            _trace.Clear();
            Fake.ClearRecordedCalls(_runtime);
            Fake.ClearRecordedCalls(_bindings);
        }
        string path = Directory
            .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
            .Single();
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        if (mutation == "missing")
        {
            json.Remove("purpose");
        }
        else
        {
            json["purpose"] = mutation == "unknown" ? "Unknown" : "SourceHistoryOnly";
        }
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await AssertNoMutationAsync();
        _trace.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_reused_database_receipts_even_when_empty()
    {
        Directory.Delete(_root, true);
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(false);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        await AssertNoMutationAsync();
        _trace.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_corrupt_history_or_workflow(bool history)
    {
        string path = Directory
            .GetFiles(
                Path.Combine(_root, history ? "source-history" : "workflows"),
                "*.json",
                SearchOption.AllDirectories
            )
            .Single();
        await File.WriteAllTextAsync(path, "{corrupt-secret");
        await AssertNoMutationAsync();
    }

    [TestCase(DocumentCacheLifecycleState.Resetting)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding)]
    [TestCase(DocumentCacheLifecycleState.Disabled)]
    public async Task It_rejects_drift_after_completed_activation(DocumentCacheLifecycleState state)
    {
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        Fake.ClearRecordedCalls(_bindings);
        Fake.ClearRecordedCalls(_runtime);
        _lifecycle = state;
        await AssertNoMutationAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_latch_or_rows_on_a_bound_retry(bool latch)
    {
        await IntentAsync(CdcWorkflowEffect.ReserveBinding);
        await _realBindings.CreateBindingIfAbsentAsync(_request.Binding);
        _latch = latch;
        _tables = new(latch, true, true);
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_retains_possible_exposure_if_crash_precedes_reservation_intent()
    {
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            )
        )
        {
            await session.RecordSourceExposureAsync(
                Target,
                _created.WorkflowId,
                _request.Binding.PhysicalSourceFingerprint,
                CancellationToken.None
            );
        }
        await AssertNoMutationAsync();
    }

    [Test]
    public async Task It_holds_the_controller_lock_through_provider_activation()
    {
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async () =>
            {
                Func<Task> competing = async () =>
                {
                    await using var session = await _store.AcquireAsync(
                        TimeSpan.FromMilliseconds(50),
                        TimeSpan.FromMilliseconds(10),
                        CancellationToken.None
                    );
                };
                (await competing.Should().ThrowAsync<CdcWorkflowStateException>())
                    .Which.Failure.Should()
                    .Be(CdcWorkflowStateFailure.LockTimeout);
                _lifecycle = DocumentCacheLifecycleState.Tracking;
                return Success();
            });
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [Test]
    public async Task It_allows_individually_bounded_calls_to_exceed_one_call_budget_in_total()
    {
        SetTiming(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        _beforeCall = (_, token) => Task.Delay(TimeSpan.FromMilliseconds(100), token);
        var elapsed = Stopwatch.StartNew();

        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);

        elapsed.Elapsed.Should().BeGreaterThan(_request.Timing.CallTimeout);
        _calls
            .Should()
            .Equal("observe-1", "list", "exact-1", "create", "exact-2", "activate", "observe-2", "exact-3");
        _trace.Should().Equal("eligibility", "binding", "activation", "eligibility");
        ReadJournalWithoutLock()
            .Operations.Where(o =>
                o.Effect is CdcWorkflowEffect.ReserveBinding or CdcWorkflowEffect.ActivateProjection
            )
            .Should()
            .OnlyContain(o => !o.Completions.IsEmpty);
        await AssertUnpublishedAndLockReleasedAsync();
    }

    [TestCase("observe-1", CdcDeploymentComponent.Projection)]
    [TestCase("list", CdcDeploymentComponent.WorkflowState)]
    [TestCase("exact-1", CdcDeploymentComponent.WorkflowState)]
    [TestCase("create", CdcDeploymentComponent.WorkflowState)]
    [TestCase("exact-2", CdcDeploymentComponent.WorkflowState)]
    [TestCase("activate", CdcDeploymentComponent.Projection)]
    [TestCase("observe-2", CdcDeploymentComponent.Projection)]
    [TestCase("exact-3", CdcDeploymentComponent.WorkflowState)]
    public async Task It_bounds_each_external_call_and_reconciles_the_interrupted_workflow(
        string stage,
        CdcDeploymentComponent component
    )
    {
        SetTiming(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
        CancellationToken observedCallToken = CancellationToken.None;
        _beforeCall = async (current, token) =>
        {
            if (current == stage)
            {
                observedCallToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var elapsed = Stopwatch.StartNew();

        var result = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcInitialEnablementEvidence>.Unavailable>()
            .Subject;

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        result.Diagnostic.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        result.Diagnostic.Component.Should().Be(component);
        observedCallToken.IsCancellationRequested.Should().BeTrue();
        _calls[^1].Should().Be(stage);
        await AssertUnpublishedAndLockReleasedAsync();
        byte[] bytes = _calls.Contains("exact-2") ? BindingBytes() : [];
        _beforeCall = (_, _) => Task.CompletedTask;
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Observed);
        if (bytes.Length > 0)
        {
            BindingBytes().Should().Equal(bytes);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_an_earlier_enclosing_deadline_or_actual_caller_cancellation(bool deadline)
    {
        SetTiming(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        _beforeCall = async (_, token) =>
        {
            if (deadline)
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            }
            else
            {
                await cancellation.CancelAsync();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var elapsed = Stopwatch.StartNew();

        Func<Task> run = () => _controller.ActivateAsync(_request, _runtime, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();

        elapsed.Elapsed.Should().BeLessThan(_request.Timing.CallTimeout);
        _calls.Should().Equal("observe-1");
        await AssertUnpublishedAndLockReleasedAsync();
    }

    [Test]
    public async Task It_bounds_the_complete_workflow_even_when_each_call_fits_its_budget()
    {
        SetTiming(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(750));
        _beforeCall = (_, token) => Task.Delay(TimeSpan.FromMilliseconds(200), token);
        var elapsed = Stopwatch.StartNew();

        var result = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcInitialEnablementEvidence>.Unavailable>()
            .Subject;

        elapsed
            .Elapsed.Should()
            .BeGreaterThan(_request.Timing.CallTimeout)
            .And.BeLessThan(TimeSpan.FromSeconds(2));
        result.Diagnostic.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        result.Diagnostic.Component.Should().Be(CdcDeploymentComponent.WorkflowState);
        _calls.Should().Equal("observe-1", "list", "exact-1", "create");
        await AssertUnpublishedAndLockReleasedAsync();
    }

    [Test]
    public async Task It_bounds_lock_acquisition_by_the_call_budget()
    {
        SetTiming(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
        await using (
            var held = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            )
        )
        {
            var elapsed = Stopwatch.StartNew();
            var result = (await RunAsync())
                .Should()
                .BeOfType<CdcTransportResult<CdcInitialEnablementEvidence>.Unavailable>()
                .Subject;
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            result.Diagnostic.Failure.Should().Be(CdcDeploymentFailure.Timeout);
            result.Diagnostic.Component.Should().Be(CdcDeploymentComponent.WorkflowState);
            _calls.Should().BeEmpty();
        }
        await AssertUnpublishedAndLockReleasedAsync();
    }

    private async Task BeforeCallAsync(string stage, CancellationToken token)
    {
        _calls.Add(stage);
        await _beforeCall(stage, token);
    }

    private void SetTiming(TimeSpan call, TimeSpan wait) =>
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
            new(call, wait, TimeSpan.FromMilliseconds(10))
        );

    private async Task AssertUnpublishedAndLockReleasedAsync()
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        var journal = await session.ReadAsync(Target, CancellationToken.None);
        journal.Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.AuthorizeWriterPublication);
    }

    [Test]
    public async Task It_bounds_provider_observation_and_releases_the_lock_after_timeout()
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
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10))
        );
        A.CallTo(() => _runtime.ObserveInitialDatabaseAsync(A<CancellationToken>._))
            .ReturnsLazily(
                async (CancellationToken token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Observation();
                }
            );
        var result = (await RunAsync())
            .Should()
            .BeOfType<CdcTransportResult<CdcInitialEnablementEvidence>.Unavailable>()
            .Subject;
        result.Diagnostic.Failure.Should().Be(CdcDeploymentFailure.Timeout);
        result.Diagnostic.Component.Should().Be(CdcDeploymentComponent.Projection);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
    }

    [Test]
    public async Task It_rejects_writer_publication_intent_even_without_handoff_completion()
    {
        var names = CdcArtifactNameGenerator
            .Render(
                new(Target.DeploymentKey, "journal", Target.InstanceKey, Target.Generation, Target.Provider)
            )
            .Inventory!;
        var identities = names
            .GovernedArtifacts.Where(a =>
                a.Kind
                    is CdcGovernedArtifactKind.PostgresqlLogicalSlot
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                        or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
            )
            .Select(a => new CdcRetainedProviderIdentity(
                a.Kind,
                a.Name,
                _request.Binding.PhysicalSourceFingerprint
            ))
            .ToImmutableArray();
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            )
        )
        {
            Guid providerId = Guid.NewGuid();
            await session.RecordIntentAsync(
                Target,
                _created.WorkflowId,
                providerId,
                CdcWorkflowEffect.CreateProvider,
                [],
                CancellationToken.None
            );
            var providerProof = new CdcWorkflowCompletion.Provider(
                identities,
                provider == Ddl.CdcProvider.Postgresql
                    ?
                    [
                        new(
                            new(names.PostgresqlLogicalSlotName!),
                            new(
                                Ddl.CdcSourceFingerprintMetadata.Version,
                                _request.Binding.PhysicalSourceFingerprint
                            ),
                            Ddl.CdcPostgresqlInitialReplicationSlotProof.CreateDatabaseIdentityToken(
                                "test-database"
                            ),
                            "0/10",
                            "0/10"
                        ),
                    ]
                    : []
            );
            await session.ReconcileCompletionAsync(
                Target,
                _created.WorkflowId,
                providerId,
                (_, _) =>
                    Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(providerProof)
                    ),
                CancellationToken.None
            );
            Guid connectorId = Guid.NewGuid();
            await session.RecordIntentAsync(
                Target,
                _created.WorkflowId,
                connectorId,
                CdcWorkflowEffect.EstablishConnector,
                [],
                CancellationToken.None
            );
            await session.ReconcileCompletionAsync(
                Target,
                _created.WorkflowId,
                connectorId,
                (_, _) =>
                    Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Connector(_request.Binding.PhysicalSourceFingerprint)
                        )
                    ),
                CancellationToken.None
            );
            await session.RecordIntentAsync(
                Target,
                _created.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.AuthorizeWriterPublication,
                [],
                CancellationToken.None
            );
        }
        await AssertNoMutationAsync();
        _trace.Should().BeEmpty();
    }

    private Dictionary<string, string> Snapshot() =>
        Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllText);

    private byte[] BindingBytes() =>
        File.ReadAllBytes(
            Directory
                .GetFiles(Path.Combine(_root, "bindings"), "*.json", SearchOption.AllDirectories)
                .Single()
        );

    private async Task AssertNoMutationAsync()
    {
        var files = Snapshot();
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        Snapshot().Should().BeEquivalentTo(files);
        A.CallTo(() => _bindings.CreateBindingIfAbsentAsync(A<CdcBinding>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }
}
