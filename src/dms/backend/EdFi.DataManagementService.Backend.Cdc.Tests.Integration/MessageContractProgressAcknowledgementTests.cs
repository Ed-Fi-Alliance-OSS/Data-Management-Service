// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;
using CdcProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, "MC-PROGRESS-ACK-PG", Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, "MC-PROGRESS-ACK-SQL", Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractKafka")]
public sealed class Given_MessageContractProgressAcknowledgement(CdcProvider provider, string scenarioPrefix)
{
    private readonly List<object> _observations = [];
    private MessageContractKafkaScan _progress = null!;
    private MessageContractKafkaScan _public = null!;
    private CdcAdmission _blocked = null!;
    private CdcAdmission _incomplete = null!;
    private CdcAdmission _recovered = null!;
    private MessageContractProducerProxyState _fault = null!;
    private CdcProviderBarrierCaptureResult _capture = null!;
    private bool _sourceObserved;
    private int _blockedSamples;
    private string _phase = "startup";

    [OneTimeSetUp]
    public async Task Setup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        CancellationToken token = timeout.Token;
        await using var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
            provider,
            token,
            isolateSourceProducer: true
        );
        try
        {
            var request = await fixture.CreateRequestAsync(token);
            await fixture.InstallSourceObserverAsync(token);
            await fixture.StartProducerProxyAsync(token);
            var rendered = fixture.Render(request);
            await fixture.RegisterRenderedConnectorConfigDirectlyAsync(
                rendered,
                token,
                observeSourceRecords: true
            );
            await fixture.AssertRegisteredConnectorReachesRunningStateAsync(request, token);
            await fixture.AssertProducerPathIsIsolatedAsync(request, token);
            await fixture.AssertObservedConnectorConfigAsync(rendered, token);
            if (provider == CdcProvider.Postgresql)
            {
                await fixture.FencePostgresqlSourceAsync(request, "ACK-BASELINE", token);
            }
            else
            {
                await fixture.FenceSqlServerSourceAsync(request, "ACK-BASELINE", token);
            }

            string catalog = request.ProviderConnectionProperties.Properties[
                provider == CdcProvider.Postgresql ? "database.dbname" : "database.names"
            ];
            using MessageContractAdmissionFixture admission = new(
                request.Binding.Provider,
                request.Binding,
                request.ArtifactInventory,
                catalog
            );
            _phase = "install-producer-fault";
            try
            {
                // Confirmation closes every existing producer socket before the later provider barrier.
                _fault = await fixture.SetProducerBlockedAsync(true, token);
                _fault
                    .Connected.Should()
                    .BeGreaterThan(0, "baseline progress traversed the isolated listener");
                DateTimeOffset installedAt = DateTimeOffset.UtcNow;
                var blockedBounds = await fixture.CaptureKafkaBoundariesAsync(
                    request.ProgressTopicName,
                    token
                );
                int sourceBefore = (await fixture.ReadSourceObservationsAsync(token)).Count;
                // Focused caught-up observation: no API/projector execution is claimed by this fixture.
                DateTimeOffset firstProjectionAt = admission.ObserveFocusedProjection().ObservedAt;
                if (provider == CdcProvider.Postgresql)
                {
                    await fixture.AdvanceHeartbeatAsync(token);
                    string wal = await fixture.CapturePostgresqlWalAsync(token);
                    _capture = CdcProviderBarrierCaptureResult.PostgresqlSuccess(wal, DateTimeOffset.UtcNow);
                    await fixture.AdvanceHeartbeatAsync(token);
                }
                else
                {
                    var position = await fixture.CaptureSqlServerHeartbeatBarrierAsync(token);
                    _capture = CdcProviderBarrierCaptureResult.SqlServerSuccess(
                        position.CommitLsn,
                        position.ChangeLsn,
                        DateTimeOffset.UtcNow
                    );
                }
                installedAt.Should().BeBefore(firstProjectionAt);
                firstProjectionAt.Should().BeBefore(_capture.BarrierCapturedAt);
                _phase = "observe-unacknowledged-heartbeat";
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                DateTimeOffset sampleStart = DateTimeOffset.UtcNow;
                bool gatingObserved = false;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var state = await fixture.ReadProducerProxyAsync(token);
                    state.Blocked.Should().BeTrue();
                    var source = (await fixture.ReadSourceObservationsAsync(token))
                        .Skip(sourceBefore)
                        .ToArray();
                    _sourceObserved = Array.Exists(source, IsBarrierHeartbeat);
                    var input = await ObserveAsync();
                    input.ProviderBarrier!.BarrierState.Should().Be(CdcProviderBarrierState.NotReached);
                    _blocked = CdcInitialAdmissionEvaluator.Evaluate(input);
                    _blocked.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
                    _blockedSamples++;
                    // Multiple successful REST/offset-store reads span several 1s worker flush intervals.
                    // The fault confirmation and source observation establish causality; elapsed time alone does not.
                    if (
                        _sourceObserved
                        && state.Rejected > _fault.Rejected
                        && _blockedSamples >= 4
                        && DateTimeOffset.UtcNow - sampleStart >= TimeSpan.FromSeconds(4)
                    )
                    {
                        _fault = state;
                        gatingObserved = true;
                        break;
                    }
                    await Task.Delay(250, token);
                }
                gatingObserved
                    .Should()
                    .BeTrue(
                        "source arrival, rejected Produce and repeated below-barrier offset reads must all be observed within the deadline"
                    );
                _sourceObserved
                    .Should()
                    .BeTrue("a retained heartbeat at/beyond the actual provider barrier reached Connect");
                _fault.Rejected.Should().BeGreaterThan(0);
                var duringFault = await fixture.CaptureKafkaBoundariesAsync(request.ProgressTopicName, token);
                duringFault.Select(b => b.EndOffset).Should().Equal(blockedBounds.Select(b => b.EndOffset));
                _observations.Add(
                    new
                    {
                        Phase = "fault-installed",
                        InstalledAt = installedAt,
                        FirstProjectionAt = firstProjectionAt,
                        Capture = new
                        {
                            _capture.BarrierCapturedAt,
                            _capture.PostgresqlBarrierLsn,
                            _capture.SqlServerCommitLsn,
                            _capture.SqlServerChangeLsn,
                            _capture.SqlServerEventSerialNo,
                        },
                        SourceObserved = _sourceObserved,
                        Proxy = _fault,
                        Samples = _blockedSamples,
                    }
                );

                _phase = "recover-producer";
                await fixture.SetProducerBlockedAsync(false, token);
                deadline = DateTimeOffset.UtcNow.AddSeconds(90);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var input = await ObserveAsync();
                    if (input.ProviderBarrier!.BarrierState == CdcProviderBarrierState.Reached)
                    {
                        _incomplete = CdcInitialAdmissionEvaluator.Evaluate(
                            input with
                            {
                                SecondProjectionCaughtUp = null,
                            }
                        );
                        _recovered = CdcInitialAdmissionEvaluator.Evaluate(input);
                        break;
                    }
                    await Task.Delay(250, token);
                }
                _recovered
                    .Should()
                    .NotBeNull("retained heartbeat must acknowledge and commit after producer recovery");
                var recoveredBounds = (
                    await fixture.CaptureKafkaBoundariesAsync(request.ProgressTopicName, token)
                )
                    .Select(b =>
                        b with
                        {
                            StartOffset = blockedBounds.Single(p => p.Partition == b.Partition).EndOffset,
                        }
                    )
                    .ToArray();
                _progress = await fixture.ConsumeThroughAsync(recoveredBounds, token);
                _observations.Add(
                    new
                    {
                        Phase = "recovered-bounds",
                        Bounds = recoveredBounds
                            .Select(b => new
                            {
                                b.Partition,
                                b.StartOffset,
                                b.EndOffset,
                            })
                            .ToArray(),
                        RecoveredEvaluatorClassification = _recovered.AdmissionState.ToString(),
                        AdmissionDiagnostics = _recovered.Diagnostics.Select(d => d.Code).ToArray(),
                    }
                );
                _public = await fixture.ConsumeThroughAsync(
                    await fixture.CaptureKafkaBoundariesAsync(request.PublicTopicName, token),
                    token
                );
                await fixture.AssertRegisteredConnectorReachesRunningStateAsync(request, token);
                await fixture.AssertProducerPathIsIsolatedAsync(request, token);
                _phase = "complete";

                async Task<CdcInitialAdmissionEvaluationInput> ObserveAsync()
                {
                    var snapshot = await fixture.TryReadCommittedSourceOffsetAsync(request, token);
                    snapshot
                        .Should()
                        .NotBeNull(
                            "the worker offset-store path remains accessible during the source producer fault"
                        );
                    var status = await fixture.ReadConnectorStatusAsync(request, token);
                    status.ConnectorState.Should().Be("RUNNING");
                    status.TaskStates.Should().Equal("RUNNING");
                    MessageContractOffsetEntry entry = new(
                        JsonSerializer.SerializeToElement(snapshot!.SourcePartitionEvidence.Properties),
                        JsonSerializer.Deserialize<JsonElement>(snapshot.CanonicalOffsetJson)
                    );
                    var input = admission.ObserveLiveProgress(
                        entry,
                        status.ConnectorState,
                        status.TaskStates,
                        _capture,
                        firstProjectionAt
                    );
                    _observations.Add(
                        new
                        {
                            Phase = _phase,
                            BarrierState = input.ProviderBarrier!.BarrierState.ToString(),
                            Committed = input.ProviderBarrier.CommittedPosition,
                            CommittedProviderPosition = snapshot.ProviderPosition.ToString(),
                            Status = status,
                            PartitionMatches = snapshot.SourcePartitionEvidence.Properties["server"]
                                == request.ConnectorName.Value
                                && (
                                    provider == CdcProvider.Postgresql
                                    || snapshot.SourcePartitionEvidence.Properties["database"] == catalog
                                ),
                        }
                    );
                    return input;
                }
            }
            finally
            {
                // Caller cancellation must not leave the isolated network path disabled. Container disposal owns the JVM.
                using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await fixture.SetProducerBlockedAsync(false, restore.Token);
            }
        }
        catch (Exception failure) when (failure is not IgnoreException)
        {
            _observations.Add(
                new
                {
                    FailureFrames = new System.Diagnostics.StackTrace(failure, true)
                        .GetFrames()
                        .Take(8)
                        .Select(frame => new
                        {
                            Method = frame.GetMethod()!.Name,
                            Line = frame.GetFileLineNumber(),
                        })
                        .ToArray(),
                }
            );
            await RetainEvidenceAsync();
            throw new AssertionException(
                $"{scenarioPrefix} failed in {_phase}; evidence retains bounded observations; exception type={failure.GetType().Name}; details redacted."
            );
        }
        await RetainEvidenceAsync();
    }

    private bool IsBarrierHeartbeat(JsonElement record)
    {
        if (
            record.GetProperty("kind").GetString() != "CdcHeartbeat"
            || record.GetProperty("operation").GetString() != "u"
        )
        {
            return false;
        }
        record.GetProperty("serverMatches").GetBoolean().Should().BeTrue();
        record.GetProperty("databaseMatches").GetBoolean().Should().BeTrue();
        if (provider == CdcProvider.Postgresql)
        {
            var parsed = CdcPostgresqlProviderPosition.ParseWalLsn(
                _capture.PostgresqlBarrierLsn,
                "$.barrier"
            );
            parsed.Succeeded.Should().BeTrue();
            return record.GetProperty("lsn").GetUInt64() >= parsed.Position!.Value.Value;
        }
        string observed = JsonSerializer.Serialize(
            new
            {
                commit_lsn = record.GetProperty("commitLsn").GetString(),
                change_lsn = record.GetProperty("changeLsn").GetString(),
                event_serial_no = record.GetProperty("eventSerialNo").GetInt64(),
            }
        );
        string barrier = new MessageContractSqlServerPosition(
            _capture.SqlServerCommitLsn!,
            _capture.SqlServerChangeLsn!,
            2
        ).OffsetJson;
        return CdcConnectorTemplatePinnedImageFixture.CommittedSourceOffsetRetainsOrAdvances(
            provider,
            barrier,
            observed
        );
    }

    [Test]
    [Property("ScenarioSuffix", "GATING")]
    public void It_keeps_live_committed_offsets_below_the_barrier_while_only_source_production_is_blocked()
    {
        _sourceObserved.Should().BeTrue();
        _blockedSamples.Should().BeGreaterThanOrEqualTo(4);
        _fault.Blocked.Should().BeTrue();
        _fault.Rejected.Should().BeGreaterThan(0);
        _blocked.Steps.ProviderBarrier.State.Should().NotBe(CdcComponentState.Satisfied);
        _blocked.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
    }

    [Test]
    [Property("ScenarioSuffix", "IDLE-RECOVERY")]
    public void It_publishes_keyed_progress_and_commits_without_any_public_document_activity()
    {
        _public.Records.Count.Should().Be(0);
        _public.CompletedBoundaries.All(b => b.StartOffset == b.EndOffset).Should().BeTrue();
        _progress.Records.Should().NotBeEmpty();
        foreach (var record in _progress.Records)
        {
            record.Partition.Should().Be(0);
            record.Key.IsNull.Should().BeFalse();
            record.Key.Bytes.Should().Equal(Encoding.UTF8.GetBytes("cdc-progress"));
            record.Value.IsNull.Should().BeFalse();
            record.Value.Bytes.Should().NotBeEmpty();
        }
        _recovered.Steps.ProviderBarrier.State.Should().Be(CdcComponentState.Satisfied);
    }

    [Test]
    [Property("ScenarioSuffix", "READINESS")]
    public void It_requires_the_complete_focused_observation_sequence_after_acknowledgement()
    {
        _incomplete.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        _recovered.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
    }

    private async Task RetainEvidenceAsync()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractProgressAcknowledgement"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"cdc-message-contract-{scenarioPrefix}-{Guid.NewGuid():N}.json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    EvidenceScope = "Live committed offsets and task status; synthetic ownership, projection, history and lag; evaluator classification only",
                    AuthorizationProfile = "AuthorizationDisabledLocal",
                    ScenarioIds = new[]
                    {
                        $"{scenarioPrefix}-GATING",
                        $"{scenarioPrefix}-IDLE-RECOVERY",
                        $"{scenarioPrefix}-READINESS",
                    },
                    ConnectImage = Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable)
                        ?? "",
                    Phase = _phase,
                    Observations = _observations,
                    Progress = _progress is null
                        ? []
                        : _progress
                            .Records.Select(r => new
                            {
                                r.Partition,
                                r.Offset,
                                KeyBytes = r.Key.Bytes.Length,
                                ValueBytes = r.Value.Bytes.Length,
                                KafkaNull = r.Value.IsNull,
                            })
                            .ToArray(),
                    RecoveredEvaluatorClassification = _recovered is null
                        ? "unobserved"
                        : _recovered.AdmissionState.ToString(),
                },
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
        TestContext.AddTestAttachment(
            path,
            "Isolated source producer fault, live REST offsets and focused admission; payloads omitted"
        );
    }
}
