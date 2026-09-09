// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using ItemState = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcKafkaPolicyItemState;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Actual effective producer overrides and worker heap, inspected for this invocation. Before
/// registration this evidence may be unavailable; requested configuration is never live evidence.
/// </summary>
public interface ICdcKafkaProducerInspection
{
    Task<CdcTransportResult<CdcKafkaProducerCapacityEvidence>> InspectAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Serializes eligible Kafka setup and fresh policy inspection. Offset preparation is explicitly
/// cluster scoped and must precede worker startup. Binding setup requires the owned initial workflow
/// and current E18 eligibility. Observation never repairs or records completion. Callers must inspect
/// both policies before registration/readiness; neither a preparation receipt nor Observed means ready.
/// </summary>
public sealed class CdcKafkaProvisioning : ICdcKafkaAdministrationTransport
{
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcKafkaAdminAdapter _kafka;
    private readonly ICdcProjectionRuntime _runtime;
    private readonly ICdcKafkaProducerInspection _producer;
    private readonly TimeProvider _time;

    public CdcKafkaProvisioning(
        string stateRoot,
        ICdcKafkaAdminAdapter kafka,
        ICdcProjectionRuntime runtime,
        ICdcKafkaProducerInspection producer
    )
        : this(
            new(stateRoot),
            new CdcBindingLifecycleService(new LocalCdcBindingStateStore(stateRoot), TimeProvider.System),
            kafka,
            runtime,
            producer,
            TimeProvider.System
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
    }

    internal CdcKafkaProvisioning(
        LocalCdcWorkflowJournalStore store,
        ICdcBindingLifecycleService bindings,
        ICdcKafkaAdminAdapter kafka,
        ICdcProjectionRuntime runtime,
        ICdcKafkaProducerInspection producer,
        TimeProvider time
    )
    {
        _store = store;
        _bindings = bindings;
        _kafka = kafka;
        _runtime = runtime;
        _producer = producer;
        _time = time;
    }

    public Task<CdcTransportResult<CdcConnectOffsetStorePolicyObservation>> ObserveOffsetStoreAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ExecuteAsync(request, true, false, CdcDeploymentKafkaPolicy.ObserveOffsetStore, cancellationToken);

    public Task<CdcTransportResult<CdcConnectOffsetStorePolicyObservation>> ProvisionOffsetStoreAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ExecuteAsync(request, true, true, CdcDeploymentKafkaPolicy.ObserveOffsetStore, cancellationToken);

    public Task<CdcTransportResult<CdcKafkaPolicyObservation>> ObserveBindingAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ExecuteAsync(request, false, false, CdcDeploymentKafkaPolicy.ObserveBinding, cancellationToken);

    public Task<CdcTransportResult<CdcKafkaPolicyObservation>> ProvisionBindingAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => ExecuteAsync(request, false, true, CdcDeploymentKafkaPolicy.ObserveBinding, cancellationToken);

    private async Task<CdcTransportResult<T>> ExecuteAsync<T>(
        CdcDeploymentRequest request,
        bool shared,
        bool provision,
        Func<CdcDeploymentRequest, string, DateTimeOffset, CdcKafkaDeploymentEvidence, T> observe,
        CancellationToken cancellationToken
    )
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        var boundary = new Boundary();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.WaitTimeout);
        var token = timeout.Token;
        try
        {
            // The same lock also serializes read passes with setup, retirement and size rollouts.
            await using var session = await _store.AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval < request.Timing.CallTimeout
                    ? request.Timing.PollInterval
                    : request.Timing.CallTimeout,
                token
            );
            if (provision)
            {
                await PrepareAsync(request, shared, session, boundary, token);
            }
            boundary.Component = CdcDeploymentComponent.Kafka;
            var evidence = await InspectAsync(request, shared, includeProducer: !shared, token);
            token.ThrowIfCancellationRequested();
            return new CdcTransportResult<T>.Observed(
                observe(request, Guid.NewGuid().ToString("D"), _time.GetUtcNow(), evidence)
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return new CdcTransportResult<T>.Unavailable(
                new(boundary.Component, CdcDeploymentFailure.Timeout)
            );
        }
        catch (CdcWorkflowStateException exception)
        {
            return new CdcTransportResult<T>.Unavailable(
                new(
                    boundary.Component,
                    exception.Failure switch
                    {
                        CdcWorkflowStateFailure.LockTimeout => CdcDeploymentFailure.Timeout,
                        CdcWorkflowStateFailure.Missing or CdcWorkflowStateFailure.Unavailable =>
                            CdcDeploymentFailure.Unavailable,
                        _ => CdcDeploymentFailure.ValidationFailed,
                    }
                )
            );
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<T>.Unavailable(
                CdcDeploymentDiagnostic.FromException(boundary.Component, exception)
            );
        }
    }

    private async Task PrepareAsync(
        CdcDeploymentRequest request,
        bool shared,
        LocalCdcWorkflowJournalStore.Session session,
        Boundary boundary,
        CancellationToken token
    )
    {
        var plan = CdcDeploymentKafkaPolicy.Build(request);
        IReadOnlyList<CdcKafkaTopicIntent> topics = shared ? [plan.OffsetStore] : plan.BindingTopics;
        string scope = Hash(
            shared
                ? ["cdc-worker-kafka-v1", request.Binding.DeploymentKey, request.WorkerPolicy.WorkerKey.Value]
                :
                [
                    "cdc-binding-kafka-v1",
                    JsonSerializer.Serialize(request.Binding.ToCompleteBindingIdentity()),
                ]
        );
        string topicHash = Hash(
            topics.SelectMany(t =>
                new[]
                {
                    t.Role.ToString(),
                    t.Name,
                    t.PartitionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    t.ReplicationFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    JsonSerializer.Serialize(t.Configuration.OrderBy(k => k.Key, StringComparer.Ordinal)),
                }
            )
        );
        string grantsHash = Hash(
            (shared ? plan.OffsetStoreGrants : plan.BindingGrants)
                .Select(g =>
                    JsonSerializer.Serialize(
                        new[]
                        {
                            g.Principal,
                            g.ResourceType.ToString(),
                            g.ResourceName,
                            g.Operation.ToString(),
                        }
                    )
                )
                .Prepend(plan.AuthorizationProfile.ToString())
        );

        // Shared preparation has no source exposure and does not mutate any binding lifecycle.
        // Its independent receipt survives binding retirement and refuses missing completed topics.
        // There is deliberately no synthetic database workflow for the cluster operation.
        List<CdcWorkflowJournal> workflows = shared
            ? []
            : [await EligibleWorkflowAsync(request, session, boundary, token)];
        boundary.Component = CdcDeploymentComponent.WorkflowState;
        CdcKafkaPreparationJournal journal;
        try
        {
            journal = await session.ReadKafkaAsync(scope, token);
            Require(journal.TopicIntentHash == topicHash && journal.TopicCount == topics.Count);
        }
        catch (CdcWorkflowStateException exception)
            when (exception.Failure == CdcWorkflowStateFailure.Missing)
        {
            // A workflow that already authorized Kafka effects must retain their individual receipts.
            Require(
                shared || !workflows.Single().Operations.Any(o => o.Effect == CdcWorkflowEffect.PrepareKafka)
            );
            journal = await session.WriteKafkaAsync(
                new(1, scope, topicHash, topics.Count, [], grantsHash, false),
                true,
                token
            );
        }
        if (!shared && !workflows.Single().Operations.Any(o => o.Effect == CdcWorkflowEffect.PrepareKafka))
        {
            workflows[0] = await session.RecordIntentAsync(
                request.TargetIdentity,
                workflows.Single().WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.PrepareKafka,
                [],
                token
            );
        }
        if (journal.GrantIntentHash != grantsHash)
        {
            journal = await session.WriteKafkaAsync(
                journal with
                {
                    GrantIntentHash = grantsHash,
                    GrantsReconciled = false,
                },
                false,
                token
            );
        }
        boundary.Component = CdcDeploymentComponent.Kafka;
        var evidence = await InspectAsync(request, shared, includeProducer: false, token);
        CheckSetupPolicy(request, shared, topics, evidence, allowMissing: true);
        foreach (int reconciled in journal.ReconciledTopics)
        {
            RequirePolicy(
                CdcDeploymentKafkaPolicy
                    .ObserveTopic(request, topics[reconciled], evidence.Topics[topics[reconciled].Name])
                    .State
            );
        }
        // Inspect every topic and all ACLs before the first effect, so existing drift cannot be hidden
        // behind successful creation of a different missing artifact.
        for (int i = 0; i < topics.Count; i++)
        {
            var topic = topics[i];
            var current = evidence.Topics[topic.Name];
            bool waitForMetadata = false;
            if (current is CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
            {
                Require(!journal.ReconciledTopics.Contains(i));
                // Acknowledgement, conflict and lost response all lead to an independent read-back.
                var creation = await CallAsync(
                    request,
                    ct => _kafka.CreateMissingTopicAsync(request, topic, ct),
                    token
                );
                waitForMetadata =
                    creation
                        is not CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable
                        {
                            Diagnostic.Failure: CdcDeploymentFailure.AuthenticationFailed
                                or CdcDeploymentFailure.InvalidInput
                                or CdcDeploymentFailure.ValidationFailed,
                        };
            }
            var live = await CallAsync(
                request,
                ct => _kafka.InspectTopicAsync(request, topic.Name, ct),
                token
            );
            // Kafka can acknowledge creation before metadata exposes the new topic. Wait only for
            // that absence, under the invocation deadline, without repeating the create. Existing
            // drift, contradictory metadata and unavailable inspection still reject immediately.
            while (waitForMetadata && live is CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
            {
                await Task.Delay(request.Timing.PollInterval, token);
                live = await CallAsync(
                    request,
                    ct => _kafka.InspectTopicAsync(request, topic.Name, ct),
                    token
                );
            }
            RequirePolicy(CdcDeploymentKafkaPolicy.ObserveTopic(request, topic, live).State);
            if (!journal.ReconciledTopics.Contains(i))
            {
                boundary.Component = CdcDeploymentComponent.WorkflowState;
                journal = await session.WriteKafkaAsync(
                    journal with
                    {
                        ReconciledTopics = journal.ReconciledTopics.Add(i),
                    },
                    false,
                    token
                );
                boundary.Component = CdcDeploymentComponent.Kafka;
            }
        }
        var acls = await CallAsync(request, ct => _kafka.InspectAclsAsync(request, ct), token);
        var aclPolicy = CdcDeploymentKafkaPolicy.ValidateAcls(request, acls, shared);
        if (aclPolicy.CanAddMissingGrants)
        {
            boundary.Component = CdcDeploymentComponent.WorkflowState;
            journal = await session.WriteKafkaAsync(journal with { GrantsReconciled = false }, false, token);
            boundary.Component = CdcDeploymentComponent.Kafka;
            await CallAsync(request, ct => _kafka.ReconcileMissingGrantsAsync(request, shared, ct), token);
        }
        else
        {
            RequirePolicy(aclPolicy.State);
        }
        evidence = await InspectAsync(request, shared, includeProducer: false, token);
        CheckSetupPolicy(request, shared, topics, evidence, allowMissing: false);
        boundary.Component = CdcDeploymentComponent.WorkflowState;
        if (!journal.GrantsReconciled)
        {
            await session.WriteKafkaAsync(journal with { GrantsReconciled = true }, false, token);
        }
        if (!shared)
        {
            // This completion is Kafka infrastructure only: producer/heap evidence remains mandatory
            // in the fresh policy observation consumed by registration and readiness.
            var operation = workflows
                .Single()
                .Operations.Single(o => o.Effect == CdcWorkflowEffect.PrepareKafka);
            await session.ReconcileCompletionAsync(
                request.TargetIdentity,
                workflows.Single().WorkflowId,
                operation.OperationId,
                (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Reconciled()
                        )
                    );
                },
                token
            );
        }
    }

    private async Task<CdcWorkflowJournal> EligibleWorkflowAsync(
        CdcDeploymentRequest request,
        LocalCdcWorkflowJournalStore.Session session,
        Boundary boundary,
        CancellationToken token
    )
    {
        var binding = request.Binding;
        var target = request.TargetIdentity;
        var journal = await session.ReadAsync(target, token);
        Require(journal.Purpose == CdcWorkflowPurpose.InitialCdcProvisioning);
        var history = await session.ReadSourcePublicationHistoryAsync(
            target,
            binding.PhysicalSourceFingerprint,
            token
        );
        Require(
            history.WorkflowId == journal.WorkflowId
                && history.CreationTarget == target
                && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
                && history.Transitions[^1].Status == DocumentCacheDownstreamPublicationStatus.Possible
        );
        Require(
            !journal.Operations.Any(o =>
                o.Effect
                    is CdcWorkflowEffect.RegisterConnector
                        or CdcWorkflowEffect.EstablishConnector
                        or CdcWorkflowEffect.AuthorizeWriterPublication
                        or CdcWorkflowEffect.StopConnector
                        or CdcWorkflowEffect.ResumeConnector
                        or CdcWorkflowEffect.IncreaseRecordSize
                        or CdcWorkflowEffect.Retire
            )
        );
        foreach (
            var effect in new[] { CdcWorkflowEffect.ReserveBinding, CdcWorkflowEffect.ActivateProjection }
        )
        {
            Require(
                journal.Operations.Count(o => o.Effect == effect) == 1
                    && journal.Operations.Single(o => o.Effect == effect).Completions.Length == 1
            );
        }
        var kafka = journal.Operations.Where(o => o.Effect == CdcWorkflowEffect.PrepareKafka).ToArray();
        Require(
            kafka.Length <= 1
                && Array.TrueForAll(
                    kafka,
                    o =>
                        o.IntendedAt
                        >= journal
                            .Operations.Single(a => a.Effect == CdcWorkflowEffect.ActivateProjection)
                            .Completions[0]
                            .ReconciledAt
                )
        );
        var exact = await _bindings.ExactMatchBindingAsync(binding, token);
        Require(
            exact.Status == CdcControlPlaneOperationStatus.Succeeded
                && exact.State?.State == CdcBindingState.BindingPresent
        );
        var initial = new CdcInitialEnablement(_store, _bindings, _time);
        var proof = new InitialCdcProvisioningProof(
            CdcJsonContract.CurrentContractVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            target,
            target.Provider,
            journal.WorkflowId.ToString("D"),
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            _time.GetUtcNow()
        );
        boundary.Component = CdcDeploymentComponent.Projection;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timing.CallTimeout);
        InitialCdcEligibilityObservation eligibility;
        try
        {
            var observation = await initial
                .ObserveAsync(_runtime, binding, proof, request.Timing.MaximumObservationAge, timeout.Token)
                .WaitAsync(timeout.Token);
            eligibility = observation.Eligibility;
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
        Require(
            initial.Classify(binding, proof, eligibility, exact).RetryClassification
                == CdcRetryClassification.ResumeProviderTopicConnectorSetup
        );
        return journal;
    }

    private Task<CdcKafkaDeploymentEvidence> InspectAsync(
        CdcDeploymentRequest request,
        bool shared,
        bool includeProducer,
        CancellationToken token
    ) => InspectAsync(_kafka, _producer, request, shared, includeProducer, token);

    internal static async Task<CdcKafkaDeploymentEvidence> InspectAsync(
        ICdcKafkaAdminAdapter kafka,
        ICdcKafkaProducerInspection producerInspection,
        CdcDeploymentRequest request,
        bool shared,
        bool includeProducer,
        CancellationToken token
    )
    {
        var plan = CdcDeploymentKafkaPolicy.Build(request);
        Dictionary<string, CdcTransportResult<CdcKafkaTopicEvidence>> topics = new(StringComparer.Ordinal);
        var names = (shared ? [plan.OffsetStore] : plan.BindingTopics.Append(plan.OffsetStore)).Select(
            topic => topic.Name
        );
        foreach (string name in names)
        {
            topics.Add(
                name,
                await CallAsync(request, ct => kafka.InspectTopicAsync(request, name, ct), token)
            );
        }
        var acls = await CallAsync(request, ct => kafka.InspectAclsAsync(request, ct), token);
        var brokers = shared
            ? Unknown<CdcKafkaBrokerEvidence>()
            : await CallAsync(request, ct => kafka.InspectBrokersAsync(request, ct), token);
        var producer = includeProducer
            ? await CallAsync(request, ct => producerInspection.InspectAsync(request, ct), token)
            : Unknown<CdcKafkaProducerCapacityEvidence>();
        return new(topics, brokers, producer, acls);
    }

    private static void CheckSetupPolicy(
        CdcDeploymentRequest request,
        bool shared,
        IReadOnlyList<CdcKafkaTopicIntent> topics,
        CdcKafkaDeploymentEvidence evidence,
        bool allowMissing
    )
    {
        foreach (var topic in topics)
        {
            var current = evidence.Topics[topic.Name];
            if (allowMissing && current is CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
            {
                continue;
            }
            RequirePolicy(CdcDeploymentKafkaPolicy.ObserveTopic(request, topic, current).State);
        }
        var acls = CdcDeploymentKafkaPolicy.ValidateAcls(request, evidence.Acls, shared);
        if (!allowMissing || !acls.CanAddMissingGrants)
        {
            RequirePolicy(acls.State);
        }
        if (!shared)
        {
            // A binding operation never provisions or repairs the shared worker topic.
            var offset = CdcDeploymentKafkaPolicy.ObserveOffsetStore(
                request,
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow,
                evidence
            );
            RequirePolicy(
                offset.PolicyState switch
                {
                    CdcConnectOffsetStorePolicyState.Satisfied => ItemState.Satisfied,
                    CdcConnectOffsetStorePolicyState.Unknown => ItemState.Unknown,
                    _ => ItemState.Invalid,
                }
            );
            RequirePolicy(CdcDeploymentKafkaPolicy.ObserveBrokerCapacity(request, evidence));
        }
    }

    private static async Task<CdcTransportResult<T>> CallAsync<T>(
        CdcDeploymentRequest request,
        Func<CancellationToken, Task<CdcTransportResult<T>>> action,
        CancellationToken token
    )
        where T : notnull
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timing.CallTimeout);
        try
        {
            return await action(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return new CdcTransportResult<T>.Unavailable(
                new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Timeout)
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CdcTransportResult<T>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Kafka, exception)
            );
        }
    }

    private static string Hash(IEnumerable<string> values) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));

    private static CdcTransportResult<T> Unknown<T>()
        where T : notnull =>
        new CdcTransportResult<T>.Unavailable(
            new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
        );

    private static void RequirePolicy(ItemState state) =>
        CdcWorkflowJournalValidation.Require(
            state == ItemState.Satisfied,
            state == ItemState.Unknown
                ? CdcWorkflowStateFailure.Unavailable
                : CdcWorkflowStateFailure.Contradictory
        );

    private static void Require(bool condition) =>
        CdcWorkflowJournalValidation.Require(condition, CdcWorkflowStateFailure.Contradictory);

    private sealed class Boundary
    {
        public CdcDeploymentComponent Component { get; set; } = CdcDeploymentComponent.WorkflowState;
    }
}
