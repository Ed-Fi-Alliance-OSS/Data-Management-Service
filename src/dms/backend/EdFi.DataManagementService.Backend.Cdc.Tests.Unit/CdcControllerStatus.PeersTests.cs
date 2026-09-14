// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal partial class Given_CdcControllerStatus
{
    [TestCase("terminal")]
    [TestCase("provider-unavailable")]
    [TestCase("lag")]
    [TestCase("missing-journal")]
    public async Task It_preserves_an_unaffected_peer_when_one_target_fails(string failure)
    {
        var peer = await PeerAsync();
        try
        {
            if (failure == "terminal")
            {
                _identity = new('b', 64);
            }
            if (failure == "provider-unavailable")
            {
                _onCall = n =>
                {
                    if (n == "provider")
                    {
                        throw new IOException("private-provider");
                    }
                };
            }
            if (failure == "lag")
            {
                _lag = 1001;
            }
            if (failure == "missing-journal")
            {
                var path = Directory
                    .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
                    .Single(p =>
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                        return doc.RootElement.GetProperty("target").GetProperty("instanceKey").GetString()
                            == Target.InstanceKey;
                    });
                File.Delete(path);
            }
            var result = await _status.StatusAsync([Selection(), peer.Selection]);
            result.Aggregate.Readiness.Should().Be(CdcReadiness.NotReady);
            result.Targets.Should().HaveCount(2);
            var unaffected = result.Targets.Single(t =>
                t.Status.TargetIdentity == peer.Selection.Request.TargetIdentity
            );
            unaffected.Status.Readiness.Should().Be(CdcReadiness.Ready);
            unaffected.Diagnostics.Should().BeEmpty();
            unaffected.Containment.Should().Be(CdcConnectorContainmentState.NotRequired);
            A.CallTo(() => peer.Connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .MustNotHaveHappened();
            if (failure == "terminal")
            {
                AssertContained(result.Targets.Single(t => t.Status.TargetIdentity == Target));
            }
        }
        finally
        {
            await peer.TeardownReadiness();
            await peer.Teardown();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task It_marks_all_bindings_of_a_shared_worker_not_ready_for_missing_or_unknown_offset_policy(
        bool unavailable,
        bool reverse
    )
    {
        var peer = await PeerAsync();
        try
        {
            A.CallTo(() =>
                    _kafka.InspectTopicAsync(
                        A<CdcDeploymentRequest>._,
                        _request.WorkerPolicy.OffsetStorageTopic.Value,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    unavailable
                        ? new CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable(
                            new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                        )
                        : new CdcTransportResult<CdcKafkaTopicEvidence>.Absent()
                );
            var result = await _status.StatusAsync(
                reverse ? [peer.Selection, Selection()] : [Selection(), peer.Selection]
            );
            result
                .Targets.Should()
                .OnlyContain(t => t.Status.Readiness == CdcReadiness.NotReady && t.HasSharedOffsetStoreIssue);
            result
                .Targets.Should()
                .OnlyContain(t => t.Status.ConnectOffsetStore.State == CdcComponentState.NotSatisfied);
            result
                .Targets.Should()
                .OnlyContain(t => t.Status.SourceHistory.Continuity == CdcSourceHistoryContinuity.Healthy);
            _stops.Should().Be(0);
            A.CallTo(() => peer.Connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
        finally
        {
            await peer.TeardownReadiness();
            await peer.Teardown();
        }
    }

    [Test]
    public async Task It_does_not_propagate_a_shared_offset_failure_to_a_different_worker()
    {
        var peer = await PeerAsync("another-worker");
        try
        {
            A.CallTo(() =>
                    _kafka.InspectTopicAsync(
                        A<CdcDeploymentRequest>._,
                        _request.WorkerPolicy.OffsetStorageTopic.Value,
                        A<CancellationToken>._
                    )
                )
                .Returns(new CdcTransportResult<CdcKafkaTopicEvidence>.Absent());
            var result = await _status.StatusAsync([Selection(), peer.Selection]);
            result
                .Targets.Single(t => t.Status.TargetIdentity == peer.Selection.Request.TargetIdentity)
                .Status.Readiness.Should()
                .Be(CdcReadiness.Ready);
        }
        finally
        {
            await peer.TeardownReadiness();
            await peer.Teardown();
        }
    }

    private async Task<PeerHarness> PeerAsync(string workerKey = "worker")
    {
        var peer = new PeerHarness(
            Provider == Ddl.CdcProvider.Postgresql ? Ddl.CdcProvider.SqlServer : Ddl.CdcProvider.Postgresql,
            workerKey
        );
        await peer.Setup();
        await peer.SetupReadiness();
        foreach (var file in Directory.GetFiles(peer.Root, "*.json", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(_root, Path.GetRelativePath(peer.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var parent = Path.GetDirectoryName(destination)!;
                while (parent != _root)
                {
                    File.SetUnixFileMode(
                        parent,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    );
                    parent = Path.GetDirectoryName(parent)!;
                }
            }
        }
        var connect = A.Fake<ICdcConnectTransport>();
        A.CallTo(() => connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcDeploymentRequest request, CancellationToken ct) =>
                    request.TargetIdentity == Target
                        ? _connect.StopAsync(request, ct)
                        : peer.Connect.StopAsync(request, ct)
            );
        A.CallTo(() => connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcDeploymentRequest request, CancellationToken ct) =>
                    request.TargetIdentity == Target
                        ? _connect.ReadStatusAsync(request, ct)
                        : peer.Connect.ReadStatusAsync(request, ct)
            );
        _status = new(
            _store,
            _bindings,
            connect,
            p => p == _request.Binding.Provider ? Validation() : peer.Validation(_store, _bindings),
            TimeProvider.System
        );
        return peer;
    }

    private sealed class PeerHarness(Ddl.CdcProvider provider, string workerKey)
        : CdcReadinessTestBase(provider)
    {
        internal string Root => _root;
        internal ICdcConnectTransport Connect => _connect;
        internal CdcControllerStatusTarget Selection => new(_request, _runtime, 1000);

        internal CdcEstablishedValidation Validation(
            LocalCdcWorkflowJournalStore store,
            ICdcBindingLifecycleService bindings
        ) =>
            new(
                store,
                bindings,
                _provider,
                _templates,
                _kafka,
                _connect,
                _worker,
                _metrics,
                _positions,
                TimeProvider.System
            );

        protected override CdcDeploymentRequest CreateRequest()
        {
            var binding = CdcConnectorTemplateTestData.BuildBinding(
                Provider,
                instanceKey: "peer",
                dataStoreId: "2"
            );
            return CdcDeploymentRequestTestData.Request(
                Provider,
                changeBinding: _ => binding,
                setupArtifacts: CdcConnectorTemplateTestData.BuildProviderArtifactNames(Provider, binding),
                worker: CdcDeploymentRequestTestData.Worker(
                    digest: CdcQualifiedWorkerImage.Digests.Single(),
                    workerKey: workerKey
                )
            );
        }
    }
}
