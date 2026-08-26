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
    public async Task It_uses_owner_only_unix_create_modes_before_permission_hardening()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix create modes are not supported on Windows.");
        }

        using TempCdcStateRoot tempRoot = new();
        CapturingCreateModeCdcLocalStateStorePermissions permissions = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path, permissions);

        CdcCreateBindingStateStoreResult created = await store.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcLatchIncidentStateStoreResult latched = await store.LatchSourceHistoryLossAsync(
            CreateIncident(SampleBinding),
            CancellationToken.None
        );

        created.Should().BeOfType<CdcCreateBindingStateStoreResult.Created>();
        latched.Should().BeOfType<CdcLatchIncidentStateStoreResult.Latched>();
        permissions
            .DirectoryModes.Should()
            .NotBeEmpty()
            .And.OnlyContain(mode => mode == CdcLocalStateStoreUnixModes.OwnerOnlyDirectory);
        permissions
            .FileModes.Should()
            .Equal(CdcLocalStateStoreUnixModes.OwnerOnlyFile, CdcLocalStateStoreUnixModes.OwnerOnlyFile);
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
    public async Task It_removes_new_binding_file_when_permission_hardening_fails()
    {
        using TempCdcStateRoot tempRoot = new();
        FailingFilePermissionCdcLocalStateStorePermissions permissions = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path, permissions);

        CdcCreateBindingStateStoreResult createResult = await store.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readResult = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        string bindingPath = tempRoot.BindingPath(SampleBinding);
        createResult
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == bindingPath
            );
        File.Exists(bindingPath).Should().BeFalse();
        readResult.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
    }

    [Test]
    public async Task It_rejects_existing_binding_files_without_validated_owner_only_permissions()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore writer = new(tempRoot.Path);
        RejectingValidationCdcLocalStateStorePermissions permissions = new();
        LocalCdcBindingStateStore reader = new(tempRoot.Path, permissions);

        await writer.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcReadBindingStateStoreResult readResult = await reader.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcCreateBindingStateStoreResult createResult = await reader.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );

        string bindingPath = tempRoot.BindingPath(SampleBinding);
        readResult
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == bindingPath
            );
        createResult
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Diagnostics.Single()
            .Path.Should()
            .Be(bindingPath);
    }

    [Test]
    public async Task It_rejects_existing_incident_files_without_validated_owner_only_permissions()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore writer = new(tempRoot.Path);
        CdcIncident incident = CreateIncident(SampleBinding);

        await writer.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        await writer.LatchSourceHistoryLossAsync(incident, CancellationToken.None);

        string incidentPath = tempRoot.IncidentPath(SampleBinding);
        LocalCdcBindingStateStore reader = new(
            tempRoot.Path,
            new RejectingPathValidationCdcLocalStateStorePermissions(incidentPath)
        );

        CdcReadBindingStateStoreResult readResult = await reader.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcListBindingsStateStoreResult listResult = await reader.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        readResult
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == incidentPath
            );
        listResult
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == incidentPath
            );
    }

    [Test]
    public async Task It_validates_deployment_incident_file_permissions_before_parsing()
    {
        using TempCdcStateRoot tempRoot = new();
        string incidentPath = tempRoot.IncidentPath(SampleBinding);
        Directory.CreateDirectory(Path.GetDirectoryName(incidentPath)!);
        await File.WriteAllTextAsync(incidentPath, CdcJsonContract.Serialize(CreateIncident(SampleBinding)));

        LocalCdcBindingStateStore reader = new(
            tempRoot.Path,
            new RejectingPathValidationCdcLocalStateStorePermissions(incidentPath)
        );

        CdcListBindingsStateStoreResult listResult = await reader.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        listResult
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == incidentPath
            );
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
        SetOwnerOnlyFilePermissionsIfSupported(orphanIncidentPath);
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
    public async Task It_rejects_nested_duplicate_json_properties_in_persisted_incidents()
    {
        string incidentJson = CdcJsonContract.Serialize(CreateIncident(SampleBinding));
        string duplicateBindingIdentityJson = ReplaceRequired(
            incidentJson,
            "\"physicalSourceFingerprint\":\"sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851\"",
            "\"physicalSourceFingerprint\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"physicalSourceFingerprint\":\"sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851\""
        );
        string duplicatePositionMetadataJson = ReplaceRequired(
            incidentJson,
            "\"providerArtifactName\":\"edfi_dms_dms_local_data_store_1_g1_slot\"",
            "\"providerArtifactName\":\"unexpected-artifact\",\"providerArtifactName\":\"edfi_dms_dms_local_data_store_1_g1_slot\""
        );

        CdcStateStoreFailure bindingIdentityFailure = await ReadPersistedIncidentFailureAsync(
            duplicateBindingIdentityJson
        );
        CdcStateStoreFailure positionMetadataFailure = await ReadPersistedIncidentFailureAsync(
            duplicatePositionMetadataJson
        );

        bindingIdentityFailure
            .Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.InvalidPersistedIncident
                && failure.Diagnostics.Any(diagnostic =>
                    diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                    && diagnostic.Path == "$.bindingIdentity.physicalSourceFingerprint"
                )
            );
        positionMetadataFailure
            .Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.InvalidPersistedIncident
                && failure.Diagnostics.Any(diagnostic =>
                    diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
                    && diagnostic.Path == "$.positionMetadata.providerArtifactName"
                )
            );
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

    [Test]
    public async Task It_rejects_symlinked_binding_ancestors_before_missing_or_present_generation_state()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation is permission-sensitive on Windows.");
        }

        foreach (LocalCdcSymlinkAncestor ancestor in Enum.GetValues<LocalCdcSymlinkAncestor>())
        {
            foreach (bool createGenerationFile in new[] { false, true })
            {
                await WithSymlinkedAncestorAsync(
                    LocalCdcStateKind.Bindings,
                    ancestor,
                    createGenerationFile,
                    async root =>
                    {
                        LocalCdcBindingStateStore store = new(root.Path);

                        CdcReadBindingStateStoreResult read = await store.ReadBindingAsync(
                            SampleBinding.ToBindingIdentity(),
                            CancellationToken.None
                        );
                        CdcCreateBindingStateStoreResult create = await store.CreateBindingIfAbsentAsync(
                            SampleBinding,
                            CancellationToken.None
                        );
                        CdcExactMatchBindingStateStoreResult exactMatch = await store.ExactMatchBindingAsync(
                            SampleBinding,
                            CancellationToken.None
                        );
                        CdcImportBindingStateStoreResult import = await store.ImportVerifiedBindingAsync(
                            CreateAdoptionProof(SampleBinding),
                            CancellationToken.None
                        );
                        CdcListBindingsStateStoreResult list = await store.ListBindingsAsync(
                            SampleBinding.DeploymentKey,
                            CancellationToken.None
                        );

                        ShouldBeLocalStateUnavailable(
                            read.Should()
                                .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            create
                                .Should()
                                .BeOfType<CdcCreateBindingStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            exactMatch
                                .Should()
                                .BeOfType<CdcExactMatchBindingStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            import
                                .Should()
                                .BeOfType<CdcImportBindingStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            list.Should()
                                .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                    }
                );
            }
        }
    }

    [Test]
    public async Task It_rejects_symlinked_incident_ancestors_before_missing_or_present_generation_state()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation is permission-sensitive on Windows.");
        }

        foreach (LocalCdcSymlinkAncestor ancestor in Enum.GetValues<LocalCdcSymlinkAncestor>())
        {
            foreach (bool createGenerationFile in new[] { false, true })
            {
                await WithSymlinkedAncestorAsync(
                    LocalCdcStateKind.Incidents,
                    ancestor,
                    createGenerationFile,
                    async root =>
                    {
                        if (ancestor is not LocalCdcSymlinkAncestor.Root)
                        {
                            await WriteStateFileAsync(root.Path, LocalCdcStateKind.Bindings);
                        }

                        LocalCdcBindingStateStore store = new(root.Path);

                        CdcReadBindingStateStoreResult read = await store.ReadBindingAsync(
                            SampleBinding.ToBindingIdentity(),
                            CancellationToken.None
                        );
                        CdcLatchIncidentStateStoreResult latch = await store.LatchSourceHistoryLossAsync(
                            CreateIncident(SampleBinding),
                            CancellationToken.None
                        );
                        CdcListBindingsStateStoreResult list = await store.ListBindingsAsync(
                            SampleBinding.DeploymentKey,
                            CancellationToken.None
                        );
                        CdcDeleteBindingStateStoreResult delete =
                            await store.DeleteStateAfterVerifiedCleanupAsync(
                                CreateCleanupProof(SampleBinding),
                                CancellationToken.None
                            );

                        ShouldBeLocalStateUnavailable(
                            read.Should()
                                .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            latch
                                .Should()
                                .BeOfType<CdcLatchIncidentStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            list.Should()
                                .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                        ShouldBeLocalStateUnavailable(
                            delete
                                .Should()
                                .BeOfType<CdcDeleteBindingStateStoreResult.StateStoreFailure>()
                                .Subject.Failure
                        );
                    }
                );
            }
        }
    }

    [Test]
    public async Task It_preserves_incident_latch_on_partial_retirement_and_retries_orphan_cleanup()
    {
        using TempCdcStateRoot bindingFailureRoot = new();
        LocalCdcBindingStateStore bindingWriter = new(bindingFailureRoot.Path);
        CdcIncident incident = CreateIncident(SampleBinding);
        CdcCleanupProof cleanupProof = CreateCleanupProof(SampleBinding);

        await bindingWriter.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        await bindingWriter.LatchSourceHistoryLossAsync(incident, CancellationToken.None);

        string bindingFailureBindingPath = bindingFailureRoot.BindingPath(SampleBinding);
        string bindingFailureIncidentPath = bindingFailureRoot.IncidentPath(SampleBinding);
        FailingDeleteCdcLocalStateStoreFileSystem bindingDeleteFailure = new(bindingFailureBindingPath);
        LocalCdcBindingStateStore bindingDeletingStore = new(
            bindingFailureRoot.Path,
            CdcLocalStateStorePermissions.Current,
            bindingDeleteFailure
        );

        CdcDeleteBindingStateStoreResult bindingDeleteResult =
            await bindingDeletingStore.DeleteStateAfterVerifiedCleanupAsync(
                cleanupProof,
                CancellationToken.None
            );
        CdcReadBindingStateStoreResult bindingFailureRead = await bindingWriter.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcListBindingsStateStoreResult bindingFailureList = await bindingWriter.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        bindingDeleteResult
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == bindingFailureBindingPath
                && failure
                    .Diagnostics.Single()
                    .Message.Contains("delete binding state", StringComparison.Ordinal)
            );
        bindingDeleteFailure.DeleteCalls.Should().Equal(bindingFailureBindingPath);
        File.Exists(bindingFailureIncidentPath).Should().BeTrue();
        File.Exists(bindingFailureBindingPath).Should().BeTrue();
        bindingFailureRead
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);
        CdcListBindingsStateStoreResult.Listed bindingFailureListed = bindingFailureList
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.Listed>()
            .Subject;
        bindingFailureListed.States.Should().ContainSingle();
        bindingFailureListed.States.Single().Incident.Should().BeEquivalentTo(incident);

        using TempCdcStateRoot incidentFailureRoot = new();
        LocalCdcBindingStateStore incidentWriter = new(incidentFailureRoot.Path);
        await incidentWriter.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        await incidentWriter.LatchSourceHistoryLossAsync(incident, CancellationToken.None);

        string incidentFailureBindingPath = incidentFailureRoot.BindingPath(SampleBinding);
        string incidentFailureIncidentPath = incidentFailureRoot.IncidentPath(SampleBinding);
        FailingDeleteCdcLocalStateStoreFileSystem incidentDeleteFailure = new(incidentFailureIncidentPath);
        LocalCdcBindingStateStore incidentDeletingStore = new(
            incidentFailureRoot.Path,
            CdcLocalStateStorePermissions.Current,
            incidentDeleteFailure
        );

        CdcDeleteBindingStateStoreResult incidentDeleteResult =
            await incidentDeletingStore.DeleteStateAfterVerifiedCleanupAsync(
                cleanupProof,
                CancellationToken.None
            );
        CdcReadBindingStateStoreResult incidentFailureRead = await incidentWriter.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcListBindingsStateStoreResult incidentFailureList = await incidentWriter.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        incidentDeleteResult
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == incidentFailureIncidentPath
                && failure
                    .Diagnostics.Single()
                    .Message.Contains("delete incident state", StringComparison.Ordinal)
            );
        incidentDeleteFailure
            .DeleteCalls.Should()
            .Equal(incidentFailureBindingPath, incidentFailureIncidentPath);
        File.Exists(incidentFailureIncidentPath).Should().BeTrue();
        File.Exists(incidentFailureBindingPath).Should().BeFalse();
        incidentFailureRead.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
        incidentFailureList
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.InvalidPersistedIncident);

        CdcDeleteBindingStateStoreResult retryDeleteResult =
            await incidentWriter.DeleteStateAfterVerifiedCleanupAsync(cleanupProof, CancellationToken.None);
        CdcReadBindingStateStoreResult retryRead = await incidentWriter.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        CdcListBindingsStateStoreResult retryList = await incidentWriter.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        retryDeleteResult
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.Deleted>()
            .Subject.BindingIdentity.Should()
            .Be(SampleBinding.ToCompleteBindingIdentity());
        File.Exists(incidentFailureIncidentPath).Should().BeFalse();
        retryRead.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
        retryList
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.Listed>()
            .Subject.States.Should()
            .BeEmpty();
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

    private static async Task<CdcStateStoreFailure> ReadPersistedIncidentFailureAsync(string incidentJson)
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        string incidentPath = tempRoot.IncidentPath(SampleBinding);
        Directory.CreateDirectory(Path.GetDirectoryName(incidentPath)!);
        await File.WriteAllTextAsync(incidentPath, incidentJson);
        SetOwnerOnlyFilePermissionsIfSupported(incidentPath);

        CdcReadBindingStateStoreResult readResult = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        return readResult
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure;
    }

    private static void SetOwnerOnlyFilePermissionsIfSupported(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

#pragma warning disable CA1416
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
    }

    private static string ReplaceRequired(string source, string oldValue, string newValue)
    {
        int index = source.IndexOf(oldValue, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0);

        return string.Concat(source.AsSpan(0, index), newValue, source.AsSpan(index + oldValue.Length));
    }

    private static void ShouldBeLocalStateUnavailable(CdcStateStoreFailure failure)
    {
        failure.Kind.Should().Be(CdcStateStoreFailureKind.LocalStateUnavailable);
        failure
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Category == CdcDiagnosticCategory.LocalStateUnavailable);
    }

    private static async Task WithSymlinkedAncestorAsync(
        LocalCdcStateKind stateKind,
        LocalCdcSymlinkAncestor ancestor,
        bool createGenerationFile,
        Func<TempCdcStateRoot, Task> action
    )
    {
        TempCdcStateRoot root = new();
        TempCdcStateRoot target = new();

        try
        {
            if (createGenerationFile)
            {
                await WriteStateFileAsync(target.Path, stateKind);
            }

            CreateSymlinkedAncestor(root.Path, target.Path, stateKind, ancestor);
            await action(root);
        }
        finally
        {
            root.Dispose();
            target.Dispose();
        }
    }

    private static void CreateSymlinkedAncestor(
        string rootPath,
        string targetRootPath,
        LocalCdcStateKind stateKind,
        LocalCdcSymlinkAncestor ancestor
    )
    {
        string stateDirectoryName = StateDirectoryName(stateKind);
        string targetDirectory = ancestor switch
        {
            LocalCdcSymlinkAncestor.Root => targetRootPath,
            LocalCdcSymlinkAncestor.StateKind => System.IO.Path.Combine(targetRootPath, stateDirectoryName),
            LocalCdcSymlinkAncestor.Deployment => System.IO.Path.Combine(
                targetRootPath,
                stateDirectoryName,
                SampleBinding.DeploymentKey
            ),
            LocalCdcSymlinkAncestor.Instance => System.IO.Path.Combine(
                targetRootPath,
                stateDirectoryName,
                SampleBinding.DeploymentKey,
                SampleBinding.InstanceKey
            ),
            _ => throw new InvalidOperationException("Unsupported symlink ancestor."),
        };
        string linkPath = ancestor switch
        {
            LocalCdcSymlinkAncestor.Root => rootPath,
            LocalCdcSymlinkAncestor.StateKind => System.IO.Path.Combine(rootPath, stateDirectoryName),
            LocalCdcSymlinkAncestor.Deployment => System.IO.Path.Combine(
                rootPath,
                stateDirectoryName,
                SampleBinding.DeploymentKey
            ),
            LocalCdcSymlinkAncestor.Instance => System.IO.Path.Combine(
                rootPath,
                stateDirectoryName,
                SampleBinding.DeploymentKey,
                SampleBinding.InstanceKey
            ),
            _ => throw new InvalidOperationException("Unsupported symlink ancestor."),
        };

        Directory.CreateDirectory(targetDirectory);
        string? parentDirectory = System.IO.Path.GetDirectoryName(linkPath);
        if (parentDirectory is not null)
        {
            Directory.CreateDirectory(parentDirectory);
        }

        Directory.CreateSymbolicLink(linkPath, targetDirectory);
    }

    private static async Task WriteStateFileAsync(string rootPath, LocalCdcStateKind stateKind)
    {
        string path = StatePath(rootPath, stateKind);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            stateKind is LocalCdcStateKind.Bindings
                ? CdcJsonContract.Serialize(SampleBinding)
                : CdcJsonContract.Serialize(CreateIncident(SampleBinding))
        );
        SetOwnerOnlyFilePermissionsIfSupported(path);
    }

    private static string StatePath(string rootPath, LocalCdcStateKind stateKind) =>
        System.IO.Path.Combine(
            rootPath,
            StateDirectoryName(stateKind),
            SampleBinding.DeploymentKey,
            SampleBinding.InstanceKey,
            $"{SampleBinding.Generation}.json"
        );

    private static string StateDirectoryName(LocalCdcStateKind stateKind) =>
        stateKind switch
        {
            LocalCdcStateKind.Bindings => "bindings",
            LocalCdcStateKind.Incidents => "incidents",
            _ => throw new InvalidOperationException("Unsupported local CDC state kind."),
        };

    private enum LocalCdcStateKind
    {
        Bindings,
        Incidents,
    }

    private enum LocalCdcSymlinkAncestor
    {
        Root,
        StateKind,
        Deployment,
        Instance,
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

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.UnsupportedPlatform;
    }

    private sealed class CapturingCreateModeCdcLocalStateStorePermissions : ICdcLocalStateStorePermissions
    {
        public List<UnixFileMode> DirectoryModes { get; } = [];

        public List<UnixFileMode> FileModes { get; } = [];

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path)
        {
            DirectoryModes.Add(GetUnixFileMode(path));
            return CdcLocalStateStorePermissionResult.Success;
        }

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path)
        {
            FileModes.Add(GetUnixFileMode(path));
            return CdcLocalStateStorePermissionResult.Success;
        }

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        private static UnixFileMode GetUnixFileMode(string path)
        {
#pragma warning disable CA1416
            return File.GetUnixFileMode(path);
#pragma warning restore CA1416
        }
    }

    private sealed class FailingFilePermissionCdcLocalStateStorePermissions : ICdcLocalStateStorePermissions
    {
        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Failure("Injected file permission failure.");

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Success;
    }

    private sealed class RejectingValidationCdcLocalStateStorePermissions : ICdcLocalStateStorePermissions
    {
        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Failure("Injected file permission validation failure.");
    }

    private sealed class RejectingPathValidationCdcLocalStateStorePermissions(string failingPath)
        : ICdcLocalStateStorePermissions
    {
        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            string.Equals(path, failingPath, StringComparison.Ordinal)
                ? CdcLocalStateStorePermissionResult.Failure("Injected file permission validation failure.")
                : CdcLocalStateStorePermissionResult.Success;
    }

    private sealed class FailingDeleteCdcLocalStateStoreFileSystem(string failingPath)
        : ICdcLocalStateStoreFileSystem
    {
        private readonly List<string> _deleteCalls = [];

        public IReadOnlyList<string> DeleteCalls => _deleteCalls;

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path)
        {
            _deleteCalls.Add(path);
            if (string.Equals(path, failingPath, StringComparison.Ordinal))
            {
                throw new IOException("Injected delete failure.");
            }

            File.Delete(path);
        }
    }
}
