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
[Category("LocalCdcStateStore")]
public class Given_LocalCdcStateStore
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
    public async Task It_creates_bindings_with_create_new_semantics_and_owner_only_permissions()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);

        CdcCreateBindingStateStoreResult created = await store.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcCreateBindingStateStoreResult existing = await store.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcCreateBindingStateStoreResult mismatch = await store.CreateBindingIfAbsentAsync(
            SampleBinding with
            {
                PhysicalSourceFingerprint =
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult read = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        string bindingPath = tempRoot.BindingPath(SampleBinding);
        created
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.Created>()
            .Subject.State.Binding.Should()
            .Be(SampleBinding);
        existing.Should().BeOfType<CdcCreateBindingStateStoreResult.ExistingExactMatch>();
        mismatch
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.BindingMismatch>()
            .Subject.Mismatch.Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.DifferentValue
                && difference.FieldName == "physicalSourceFingerprint"
            );
        read.Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Binding.Should()
            .Be(SampleBinding);
        string bindingJson = await File.ReadAllTextAsync(bindingPath);
        bindingJson.Should().Contain("\"connectorName\":\"dms-local-data-store-1-g1\"");

        if (!OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            File.GetUnixFileMode(bindingPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(Path.GetDirectoryName(bindingPath)!)
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        }
    }

    [Test]
    public async Task It_lists_bindings_under_a_deployment_key_and_fails_the_whole_list_on_malformed_state()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcBinding secondBinding = SampleBinding with
        {
            TenantKey = "district-a",
            DataStoreId = "2",
            InstanceKey = "data-store-2",
            Generation = 2,
            ConnectorName = "dms-local-data-store-2-g2",
            TopicName = "edfi.dms.instance.data-store-2-g2.documents.v1",
        };

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        await store.CreateBindingIfAbsentAsync(secondBinding, CancellationToken.None);

        CdcListBindingsStateStoreResult listed = await store.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );
        await File.WriteAllTextAsync(tempRoot.BindingPath(secondBinding), "{ invalid json");
        CdcListBindingsStateStoreResult listedAfterCorruption = await store.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        listed
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.Listed>()
            .Subject.States.Should()
            .BeEquivalentTo(
                new[]
                {
                    new CdcStoredBindingState(SampleBinding, null),
                    new CdcStoredBindingState(secondBinding, null),
                }
            );
        listedAfterCorruption
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidPersistedBinding);
    }

    [Test]
    public async Task It_latches_incidents_idempotently_and_deletes_state_after_verified_cleanup()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcIncident incident = CreateIncident(SampleBinding);
        CdcCleanupProof cleanupProof = CreateCleanupProof(SampleBinding);

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcLatchIncidentStateStoreResult latched = await store.LatchSourceHistoryLossAsync(
            incident,
            CancellationToken.None
        );
        CdcLatchIncidentStateStoreResult alreadyLatched = await store.LatchSourceHistoryLossAsync(
            incident,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readWithIncident = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcDeleteBindingStateStoreResult deleted = await store.DeleteStateAfterVerifiedCleanupAsync(
            cleanupProof,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readAfterDelete = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        latched
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.Latched>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);
        alreadyLatched.Should().BeOfType<CdcLatchIncidentStateStoreResult.AlreadyLatched>();
        readWithIncident
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);
        deleted
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.Deleted>()
            .Subject.BindingIdentity.Should()
            .Be(SampleBinding.ToCompleteBindingIdentity());
        File.Exists(tempRoot.BindingPath(SampleBinding)).Should().BeFalse();
        File.Exists(tempRoot.IncidentPath(SampleBinding)).Should().BeFalse();
        readAfterDelete.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
    }

    [Test]
    public async Task It_rejects_invalid_import_and_cleanup_proofs_without_mutating_state()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcAdoptionProof invalidAdoptionProof = CreateAdoptionProof(SampleBinding) with
        {
            OperationId = "bad/operation",
        };
        CdcCleanupProof cleanupProof = CreateCleanupProof(SampleBinding);
        CdcCleanupProof incompleteCleanupProof = cleanupProof with
        {
            GovernedArtifacts = cleanupProof.GovernedArtifacts.Take(1).ToArray(),
        };

        CdcImportBindingStateStoreResult importResult = await store.ImportVerifiedBindingAsync(
            invalidAdoptionProof,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readAfterRejectedImport = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcDeleteBindingStateStoreResult deleteResult = await store.DeleteStateAfterVerifiedCleanupAsync(
            incompleteCleanupProof,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readAfterRejectedDelete = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        importResult
            .Should()
            .BeOfType<CdcImportBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidOperation);
        readAfterRejectedImport.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
        deleteResult
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InventoryIncomplete);
        readAfterRejectedDelete.Should().BeOfType<CdcReadBindingStateStoreResult.Found>();
    }

    [Test]
    public async Task It_rejects_invalid_binding_input_without_writing_local_state()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcBinding nonDeterministicBinding = SampleBinding with
        {
            ConnectorName = "dms-local-data-store-1-g1-other",
        };
        CdcAdoptionProof invalidBindingProof = CreateAdoptionProof(SampleBinding with { Version = 2 });

        CdcCreateBindingStateStoreResult createResult = await store.CreateBindingIfAbsentAsync(
            nonDeterministicBinding,
            CancellationToken.None
        );
        CdcImportBindingStateStoreResult importResult = await store.ImportVerifiedBindingAsync(
            invalidBindingProof,
            CancellationToken.None
        );

        createResult
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.InvalidOperation
                && failure.Diagnostics.Any(diagnostic =>
                    diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                    && diagnostic.Path == "$.connectorName"
                )
            );
        importResult
            .Should()
            .BeOfType<CdcImportBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidOperation);
        File.Exists(tempRoot.BindingPath(SampleBinding)).Should().BeFalse();
    }

    [Test]
    public async Task It_treats_contract_invalid_persisted_bindings_as_store_failures()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcBinding nonDeterministicBinding = SampleBinding with
        {
            ConnectorName = "dms-local-data-store-1-g1-other",
        };

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        await File.WriteAllTextAsync(
            tempRoot.BindingPath(SampleBinding),
            CdcJsonContract.Serialize(nonDeterministicBinding)
        );

        CdcReadBindingStateStoreResult readResult = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcExactMatchBindingStateStoreResult exactMatchResult = await store.ExactMatchBindingAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcListBindingsStateStoreResult listResult = await store.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        readResult
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.InvalidPersistedBinding
                && failure.Diagnostics.Any(diagnostic =>
                    diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                    && diagnostic.Path == "$.connectorName"
                )
            );
        exactMatchResult
            .Should()
            .BeOfType<CdcExactMatchBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidPersistedBinding);
        listResult
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidPersistedBinding);
    }

    [Test]
    public async Task It_treats_duplicate_json_properties_case_collisions_and_orphan_incidents_as_store_failures()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);
        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        string duplicateContractVersionJson = CdcJsonContract
            .Serialize(SampleBinding)
            .Replace(
                "\"contractVersion\":1",
                "\"contractVersion\":1,\"contractVersion\":1",
                StringComparison.Ordinal
            );
        await File.WriteAllTextAsync(tempRoot.BindingPath(SampleBinding), duplicateContractVersionJson);
        CdcReadBindingStateStoreResult duplicateRead = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        await File.WriteAllTextAsync(
            tempRoot.BindingPath(SampleBinding),
            CdcJsonContract.Serialize(SampleBinding)
        );
        Directory.Move(
            Path.Combine(tempRoot.Path, "bindings", SampleBinding.DeploymentKey),
            Path.Combine(tempRoot.Path, "bindings", SampleBinding.DeploymentKey.ToUpperInvariant())
        );
        CdcReadBindingStateStoreResult caseCollisionRead = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        using TempCdcStateRoot orphanRoot = new();
        string orphanIncidentPath = orphanRoot.IncidentPath(SampleBinding);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanIncidentPath)!);
        await File.WriteAllTextAsync(
            orphanIncidentPath,
            CdcJsonContract.Serialize(CreateIncident(SampleBinding))
        );
        LocalCdcBindingStateStore orphanStore = new(orphanRoot.Path);
        CdcListBindingsStateStoreResult orphanList = await orphanStore.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        duplicateRead
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidPersistedBinding);
        caseCollisionRead
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.LocalStateUnavailable);
        orphanList
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidPersistedIncident);
    }

    [Test]
    public async Task It_rejects_symlink_state_files_and_permits_permission_fallback_when_modes_are_unsupported()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore fallbackStore = new(
            tempRoot.Path,
            UnsupportedCdcLocalStateStorePermissions.Instance
        );

        CdcCreateBindingStateStoreResult createdWithPermissionFallback =
            await fallbackStore.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        createdWithPermissionFallback.Should().BeOfType<CdcCreateBindingStateStoreResult.Created>();

        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation is permission-sensitive on Windows.");
        }

        using TempCdcStateRoot symlinkRoot = new();
        string bindingPath = symlinkRoot.BindingPath(SampleBinding);
        string targetPath = Path.Combine(symlinkRoot.Path, "target.json");
        Directory.CreateDirectory(Path.GetDirectoryName(bindingPath)!);
        await File.WriteAllTextAsync(targetPath, CdcJsonContract.Serialize(SampleBinding));
        File.CreateSymbolicLink(bindingPath, targetPath);

        LocalCdcBindingStateStore symlinkStore = new(symlinkRoot.Path);
        CdcReadBindingStateStoreResult symlinkRead = await symlinkStore.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        symlinkRead
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.LocalStateUnavailable);
    }

    private static CdcIncident CreateIncident(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            new CdcIncidentPositionMetadata(
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
            )
        );

    private static CdcAdoptionProof CreateAdoptionProof(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding,
            Enum.GetValues<CdcAdoptionVerificationKind>()
                .Select(kind => new CdcAdoptionVerificationResult(
                    kind,
                    CdcAdoptionVerificationState.ExactMatch,
                    "verified"
                ))
                .ToArray()
        );

    private static CdcCleanupProof CreateCleanupProof(CdcBinding binding)
    {
        CdcArtifactInventory inventory = CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!;

        return new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            inventory
                .GovernedArtifacts.Select(artifact => new CdcGovernedArtifact(
                    artifact.Kind,
                    artifact.Name,
                    CdcCleanupState.Deleted,
                    "deleted"
                ))
                .ToArray()
        );
    }

    private sealed class TempCdcStateRoot : IDisposable
    {
        public TempCdcStateRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"local-cdc-state-store-{Guid.NewGuid():N}"
            );
        }

        public string Path { get; }

        public string BindingPath(CdcBinding binding) =>
            System.IO.Path.Combine(
                Path,
                "bindings",
                binding.DeploymentKey,
                binding.InstanceKey,
                $"{binding.Generation}.json"
            );

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

    private sealed class UnsupportedCdcLocalStateStorePermissions : ICdcLocalStateStorePermissions
    {
        public static UnsupportedCdcLocalStateStorePermissions Instance { get; } = new();

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
            CdcLocalStateStorePermissionResult.UnsupportedPlatform;

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.UnsupportedPlatform;
    }
}
