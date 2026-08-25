// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcIncidentLatch")]
public class Given_CdcIncidentLatch
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static CdcBinding SampleBinding =>
        new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            CdcProvider.Postgresql,
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            "dms-local-data-store-1-g1",
            "edfi.dms.instance.data-store-1-g1.documents.v1",
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );

    [Test]
    public async Task It_latches_source_history_loss_once_without_rewriting_the_incident()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcIncident incident = CreateIncident(SampleBinding);
        CdcIncident laterCandidate = incident with
        {
            LatchedAt = SampleObservedAt.AddMinutes(5),
            FailureCategory = CdcIncidentFailureCategory.RetainedHistoryGap,
            PositionMetadata = CreatePositionMetadata(SampleBinding) with
            {
                LsnProc = "0/16B6C50",
                RetainedRangeStart = "0/16B6C00",
                RetainedRangeEnd = "0/16B6D00",
            },
        };

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcLatchIncidentStateStoreResult latched = await store.LatchSourceHistoryLossAsync(
            incident,
            CancellationToken.None
        );
        CdcLatchIncidentStateStoreResult alreadyLatched = await store.LatchSourceHistoryLossAsync(
            laterCandidate,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readBack = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        latched
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.Latched>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);
        alreadyLatched
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.AlreadyLatched>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);
        readBack
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);
    }

    [Test]
    public async Task It_distinguishes_missing_binding_and_complete_binding_identity_mismatch()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcIncident incident = CreateIncident(SampleBinding);
        CdcIncident mismatchedIncident = incident with
        {
            BindingIdentity = SampleBinding.ToCompleteBindingIdentity() with
            {
                ConnectorName = "different-connector",
            },
        };

        CdcLatchIncidentStateStoreResult missingBinding = await store.LatchSourceHistoryLossAsync(
            incident,
            CancellationToken.None
        );
        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcLatchIncidentStateStoreResult mismatch = await store.LatchSourceHistoryLossAsync(
            mismatchedIncident,
            CancellationToken.None
        );

        missingBinding.Should().BeOfType<CdcLatchIncidentStateStoreResult.BindingMissing>();
        mismatch
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.BindingMismatch>()
            .Subject.Mismatch.Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.DifferentValue
                && difference.FieldName == "bindingIdentity"
            );
        File.Exists(tempRoot.IncidentPath(SampleBinding)).Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_invalid_latch_payload_without_persisting_an_incident()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcIncident invalidIncident = CreateIncident(SampleBinding) with { ContractVersion = 2 };

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcLatchIncidentStateStoreResult latchResult = await store.LatchSourceHistoryLossAsync(
            invalidIncident,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readBack = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        latchResult
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidOperation);
        readBack
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeNull();
        File.Exists(tempRoot.IncidentPath(SampleBinding)).Should().BeFalse();
    }

    [Test]
    public async Task It_treats_malformed_persisted_incident_json_as_state_store_failure()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcIncident invalidIncident = CreateIncident(SampleBinding) with
        {
            PositionMetadata = CreatePositionMetadata(SampleBinding) with
            {
                ProviderArtifactName = "Database=EdFi;Password=secret",
            },
        };

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(tempRoot.IncidentPath(SampleBinding))!);
        await File.WriteAllTextAsync(
            tempRoot.IncidentPath(SampleBinding),
            CdcJsonContract.Serialize(invalidIncident)
        );
        CdcReadBindingStateStoreResult readBack = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        CdcReadBindingStateStoreResult.StateStoreFailure failure = readBack
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject;
        failure.Failure.Kind.Should().Be(CdcStateStoreFailureKind.InvalidPersistedIncident);
        failure
            .Failure.Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("Password=secret", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_does_not_leave_a_valid_latch_when_file_permission_hardening_fails()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        LocalCdcBindingStateStore failingPermissionStore = new(
            tempRoot.Path,
            FailingIncidentFilePermissions.Instance
        );
        CdcIncident incident = CreateIncident(SampleBinding);

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcLatchIncidentStateStoreResult latchResult =
            await failingPermissionStore.LatchSourceHistoryLossAsync(incident, CancellationToken.None);
        CdcReadBindingStateStoreResult readBack = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        latchResult
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.LocalStateUnavailable);
        readBack
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeNull();
        File.Exists(tempRoot.IncidentPath(SampleBinding)).Should().BeFalse();
    }

    private static CdcIncident CreateIncident(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            CreatePositionMetadata(binding)
        );

    private static CdcIncidentPositionMetadata CreatePositionMetadata(CdcBinding binding) =>
        new(
            binding.ConnectorName,
            binding.TopicName,
            $"{binding.TopicName}.cdc-progress",
            null,
            "edfi_dms_dms_local_data_store_1_g1_slot",
            "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40",
            "42",
            null,
            null,
            null,
            "40",
            "50",
            [CdcIncidentUnavailableFact.SchemaHistory]
        );

    private sealed class TempCdcStateRoot : IDisposable
    {
        public TempCdcStateRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cdc-incident-latch-{Guid.NewGuid():N}"
            );
        }

        public string Path { get; }

        public string IncidentPath(CdcBinding binding) =>
            System.IO.Path.Combine(
                Path,
                "incidents",
                binding.DeploymentKey,
                binding.InstanceKey,
                $"{binding.Generation}.json"
            );

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class FailingIncidentFilePermissions : ICdcLocalStateStorePermissions
    {
        public static FailingIncidentFilePermissions Instance { get; } = new();

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Failure(
                "CDC local state owner-only permissions could not be applied."
            );
    }
}
