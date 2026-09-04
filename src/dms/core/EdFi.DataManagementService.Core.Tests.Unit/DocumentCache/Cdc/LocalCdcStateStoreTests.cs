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
    public async Task It_removes_temporary_files_after_binding_and_incident_write_failures()
    {
        using TempCdcStateRoot bindingFailureRoot = new();
        FailingWriteCdcLocalStateStoreFileSystem bindingFailureFileSystem = new();
        LocalCdcBindingStateStore bindingFailureStore = new(
            bindingFailureRoot.Path,
            CdcLocalStateStorePermissions.Current,
            bindingFailureFileSystem
        );

        CdcCreateBindingStateStoreResult bindingCreateResult =
            await bindingFailureStore.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        string bindingPath = bindingFailureRoot.BindingPath(SampleBinding);
        bindingCreateResult
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == bindingPath
                && failure
                    .Diagnostics.Single()
                    .Message.Contains("write binding state", StringComparison.Ordinal)
            );
        File.Exists(bindingPath).Should().BeFalse();
        TemporaryStateFiles(bindingFailureRoot.Path).Should().BeEmpty();
        bindingFailureFileSystem
            .WritePaths.Should()
            .ContainSingle(path => path.EndsWith(".tmp", StringComparison.Ordinal));

        using TempCdcStateRoot incidentFailureRoot = new();
        LocalCdcBindingStateStore bindingWriter = new(incidentFailureRoot.Path);
        await bindingWriter.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        FailingWriteCdcLocalStateStoreFileSystem incidentFailureFileSystem = new();
        LocalCdcBindingStateStore incidentFailureStore = new(
            incidentFailureRoot.Path,
            CdcLocalStateStorePermissions.Current,
            incidentFailureFileSystem
        );

        CdcLatchIncidentStateStoreResult incidentLatchResult =
            await incidentFailureStore.LatchSourceHistoryLossAsync(
                CreateIncident(SampleBinding),
                CancellationToken.None
            );

        string incidentPath = incidentFailureRoot.IncidentPath(SampleBinding);
        incidentLatchResult
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Should()
            .Match<CdcStateStoreFailure>(failure =>
                failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                && failure.Diagnostics.Single().Path == incidentPath
                && failure
                    .Diagnostics.Single()
                    .Message.Contains("write incident state", StringComparison.Ordinal)
            );
        File.Exists(incidentPath).Should().BeFalse();
        File.Exists(incidentFailureRoot.BindingPath(SampleBinding)).Should().BeTrue();
        TemporaryStateFiles(incidentFailureRoot.Path).Should().BeEmpty();
        incidentFailureFileSystem
            .WritePaths.Should()
            .ContainSingle(path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Test]
    public async Task It_removes_temporary_files_and_propagates_caller_cancellation_during_publication()
    {
        using TempCdcStateRoot bindingCancellationRoot = new();
        using CancellationTokenSource bindingCancellation = new();
        LocalCdcBindingStateStore bindingCancellationStore = new(
            bindingCancellationRoot.Path,
            CdcLocalStateStorePermissions.Current,
            new CancelingWriteCdcLocalStateStoreFileSystem(bindingCancellation)
        );

        Func<Task> createBinding = async () =>
            await bindingCancellationStore.CreateBindingIfAbsentAsync(
                SampleBinding,
                bindingCancellation.Token
            );

        await createBinding.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(bindingCancellationRoot.BindingPath(SampleBinding)).Should().BeFalse();
        TemporaryStateFiles(bindingCancellationRoot.Path).Should().BeEmpty();

        using TempCdcStateRoot incidentCancellationRoot = new();
        LocalCdcBindingStateStore bindingWriter = new(incidentCancellationRoot.Path);
        await bindingWriter.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        using CancellationTokenSource incidentCancellation = new();
        LocalCdcBindingStateStore incidentCancellationStore = new(
            incidentCancellationRoot.Path,
            CdcLocalStateStorePermissions.Current,
            new CancelingWriteCdcLocalStateStoreFileSystem(incidentCancellation)
        );

        Func<Task> latchIncident = async () =>
            await incidentCancellationStore.LatchSourceHistoryLossAsync(
                CreateIncident(SampleBinding),
                incidentCancellation.Token
            );

        await latchIncident.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(incidentCancellationRoot.BindingPath(SampleBinding)).Should().BeTrue();
        File.Exists(incidentCancellationRoot.IncidentPath(SampleBinding)).Should().BeFalse();
        TemporaryStateFiles(incidentCancellationRoot.Path).Should().BeEmpty();
    }

    [Test]
    public async Task It_accepts_concurrent_final_path_publication_after_temporary_write()
    {
        using TempCdcStateRoot bindingRaceRoot = new();
        string bindingPath = bindingRaceRoot.BindingPath(SampleBinding);
        LocalCdcBindingStateStore bindingRaceStore = new(
            bindingRaceRoot.Path,
            CdcLocalStateStorePermissions.Current,
            new ConcurrentPublishCdcLocalStateStoreFileSystem(
                bindingPath,
                CdcJsonContract.Serialize(SampleBinding)
            )
        );

        CdcCreateBindingStateStoreResult bindingResult = await bindingRaceStore.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );

        bindingResult.Should().BeOfType<CdcCreateBindingStateStoreResult.ExistingExactMatch>();
        File.Exists(bindingPath).Should().BeTrue();
        TemporaryStateFiles(bindingRaceRoot.Path).Should().BeEmpty();

        using TempCdcStateRoot incidentRaceRoot = new();
        LocalCdcBindingStateStore bindingWriter = new(incidentRaceRoot.Path);
        await bindingWriter.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        CdcIncident requestedIncident = CreateIncident(SampleBinding);
        CdcIncident concurrentIncident = requestedIncident with
        {
            FailureCategory = CdcIncidentFailureCategory.ProviderArtifactMissing,
        };
        string incidentPath = incidentRaceRoot.IncidentPath(SampleBinding);
        LocalCdcBindingStateStore incidentRaceStore = new(
            incidentRaceRoot.Path,
            CdcLocalStateStorePermissions.Current,
            new ConcurrentPublishCdcLocalStateStoreFileSystem(
                incidentPath,
                CdcJsonContract.Serialize(concurrentIncident)
            )
        );

        CdcLatchIncidentStateStoreResult incidentResult = await incidentRaceStore.LatchSourceHistoryLossAsync(
            requestedIncident,
            CancellationToken.None
        );

        incidentResult
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.AlreadyLatched>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(concurrentIncident);
        File.Exists(incidentPath).Should().BeTrue();
        TemporaryStateFiles(incidentRaceRoot.Path).Should().BeEmpty();
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

    /// <summary>
    /// A generation is allocated against the retirement records as well as the live bindings. The local
    /// store's root is a deployment path rather than a container volume, so it survives the destructive
    /// volume removal that deletes the source it was bound to, and the next stack asks for the same
    /// first generation of the same instance key against a new physical database - which would reassign
    /// an existing connector name, topic namespace, and consumer state to a different physical source.
    /// </summary>
    [Test]
    public async Task It_refuses_a_binding_for_a_generation_it_already_retired()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);

        // What a completed retirement leaves: the record of the publication, and no binding record.
        tempRoot.WriteRetirement(SampleBinding, SampleObservedAt);

        CdcCreateBindingStateStoreResult result = await store.CreateBindingIfAbsentAsync(
            SampleBinding with
            {
                PhysicalSourceFingerprint =
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            CancellationToken.None
        );

        CdcStateStoreFailure failure = result
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure;
        failure.Kind.Should().Be(CdcStateStoreFailureKind.InvalidOperation);
        failure
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.UnsupportedOperation);

        // Nothing was written for the refused generation.
        File.Exists(tempRoot.BindingPath(SampleBinding)).Should().BeFalse();
    }

    /// <summary>
    /// An existing retirement record is an idempotent retry only when it says the same thing. The
    /// generation and its path are stable across a destructive volume removal, so accepting a record
    /// that names another physical source would report this binding retired while the durable trace
    /// still named the one it replaced, and the second publication would be recorded nowhere.
    /// </summary>
    [Test]
    public async Task It_refuses_a_retirement_whose_existing_record_names_a_different_binding()
    {
        using TempCdcStateRoot tempRoot = new();
        LocalCdcBindingStateStore store = new(tempRoot.Path);

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);

        // The same generation, retired earlier against the source this one replaced.
        tempRoot.WriteRetirement(
            SampleBinding with
            {
                PhysicalSourceFingerprint =
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            SampleObservedAt
        );

        CdcDeleteBindingStateStoreResult result = await store.DeleteStateAfterVerifiedCleanupAsync(
            CreateCleanupProof(SampleBinding),
            CancellationToken.None
        );

        CdcStateStoreFailure failure = result
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure;
        failure.Kind.Should().Be(CdcStateStoreFailureKind.InvalidOperation);
        failure
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.BindingMismatch);

        // The binding record is the last thing a retirement removes, and this one never got there.
        File.Exists(tempRoot.BindingPath(SampleBinding)).Should().BeTrue();
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
        string bindingPath = tempRoot.BindingPath(SampleBinding);
        string incidentPath = tempRoot.IncidentPath(SampleBinding);
        CapturingDeleteCdcLocalStateStoreFileSystem deleteFileSystem = new();
        LocalCdcBindingStateStore deletingStore = new(
            tempRoot.Path,
            CdcLocalStateStorePermissions.Current,
            deleteFileSystem
        );
        CdcDeleteBindingStateStoreResult deleted = await deletingStore.DeleteStateAfterVerifiedCleanupAsync(
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
        deleteFileSystem.DeleteCalls.Should().Equal(incidentPath, bindingPath);
        File.Exists(bindingPath).Should().BeFalse();
        File.Exists(incidentPath).Should().BeFalse();
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
        SetOwnerOnlyStateDirectoriesIfSupported(tempRoot.Path);

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
        SetOwnerOnlyStateDirectoriesIfSupported(orphanRoot.Path);
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
            "\"providerArtifactName\":\"edfi_dms_dms_local_data_store_1_g1_56c4668b1b24_slot\"",
            "\"providerArtifactName\":\"unexpected-artifact\",\"providerArtifactName\":\"edfi_dms_dms_local_data_store_1_g1_56c4668b1b24_slot\""
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
        SetOwnerOnlyStateDirectoriesIfSupported(symlinkRoot.Path);
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
    public async Task It_rejects_group_or_world_writable_state_directory_ancestors()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix directory modes are not supported on Windows.");
        }

        UnixFileMode[] unsafeWriteBits = [UnixFileMode.GroupWrite, UnixFileMode.OtherWrite];
        int unsafeWriteBitIndex = 0;

        foreach (LocalCdcSymlinkAncestor ancestor in Enum.GetValues<LocalCdcSymlinkAncestor>())
        {
            using TempCdcStateRoot tempRoot = new();
            await WriteStateFileAsync(tempRoot.Path, LocalCdcStateKind.Bindings);

            string unsafeDirectory = StateAncestorPath(tempRoot.Path, LocalCdcStateKind.Bindings, ancestor);
            SetDirectoryMode(
                unsafeDirectory,
                CdcLocalStateStoreUnixModes.OwnerOnlyDirectory
                    | unsafeWriteBits[unsafeWriteBitIndex % unsafeWriteBits.Length]
            );
            unsafeWriteBitIndex++;

            LocalCdcBindingStateStore store = new(tempRoot.Path);
            CdcReadBindingStateStoreResult readResult = await store.ReadBindingAsync(
                SampleBinding.ToBindingIdentity(),
                CancellationToken.None
            );

            readResult
                .Should()
                .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
                .Subject.Failure.Should()
                .Match<CdcStateStoreFailure>(failure =>
                    failure.Kind == CdcStateStoreFailureKind.LocalStateUnavailable
                    && failure.Diagnostics.Single().Path == unsafeDirectory
                    && failure
                        .Diagnostics.Single()
                        .Message.Contains("group- or world-writable", StringComparison.Ordinal)
                );
        }
    }

    [Test]
    public async Task It_accepts_shared_readable_state_directory_ancestors_without_group_or_world_write()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix directory modes are not supported on Windows.");
        }

        using TempCdcStateRoot tempRoot = new();
        await WriteStateFileAsync(tempRoot.Path, LocalCdcStateKind.Bindings);

        foreach (LocalCdcSymlinkAncestor ancestor in Enum.GetValues<LocalCdcSymlinkAncestor>())
        {
            SetDirectoryMode(
                StateAncestorPath(tempRoot.Path, LocalCdcStateKind.Bindings, ancestor),
                CdcLocalStateStoreUnixModes.OwnerOnlyDirectory
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute
            );
        }

        LocalCdcBindingStateStore store = new(tempRoot.Path);
        CdcReadBindingStateStoreResult readResult = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        readResult
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Binding.Should()
            .Be(SampleBinding);
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

    /// <summary>
    /// The binding record is deleted last, so a failure anywhere in the final teardown leaves a record
    /// the next retirement can finish from. The other order leaves an incident whose binding is gone,
    /// which fails the whole deployment listing and is retirable by nothing: the retirement refuses on
    /// the missing record before it reaches the state that would clear it.
    /// </summary>
    [Test]
    public async Task It_deletes_incident_before_binding_and_keeps_the_record_when_either_delete_fails()
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
        bindingDeleteFailure
            .DeleteCalls.Should()
            .Equal(bindingFailureIncidentPath, bindingFailureBindingPath);

        // The record that survives is a plain binding: its incident is gone, and the deployment lists
        // normally rather than failing on state nothing names.
        File.Exists(bindingFailureIncidentPath).Should().BeFalse();
        File.Exists(bindingFailureBindingPath).Should().BeTrue();
        bindingFailureRead
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeNull();
        CdcListBindingsStateStoreResult.Listed bindingFailureListed = bindingFailureList
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.Listed>()
            .Subject;
        bindingFailureListed.States.Should().ContainSingle();
        bindingFailureListed.States.Single().Incident.Should().BeNull();

        CdcDeleteBindingStateStoreResult bindingRetryResult =
            await bindingWriter.DeleteStateAfterVerifiedCleanupAsync(cleanupProof, CancellationToken.None);

        bindingRetryResult
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.Deleted>()
            .Subject.BindingIdentity.Should()
            .Be(SampleBinding.ToCompleteBindingIdentity());
        File.Exists(bindingFailureBindingPath).Should().BeFalse();

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

        // The binding was never reached, so both files are still there and the incident is still the
        // record's own rather than an orphan.
        incidentDeleteFailure.DeleteCalls.Should().Equal(incidentFailureIncidentPath);
        File.Exists(incidentFailureIncidentPath).Should().BeTrue();
        File.Exists(incidentFailureBindingPath).Should().BeTrue();
        incidentFailureRead
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Incident.Should()
            .BeEquivalentTo(incident);

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
        File.Exists(incidentFailureBindingPath).Should().BeFalse();
        retryRead.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
        retryList
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.Listed>()
            .Subject.States.Should()
            .BeEmpty();
    }

    /// <summary>
    /// An orphan incident cannot be produced by this order, but a store written by a build that
    /// deleted the binding first can still hold one. The cleanup is kept for that store, and it takes
    /// the incident away rather than leaving a deployment whose listing can never be read.
    /// </summary>
    [Test]
    public async Task It_clears_an_orphan_incident_a_store_already_holds()
    {
        using TempCdcStateRoot root = new();
        LocalCdcBindingStateStore store = new(root.Path);
        CdcIncident incident = CreateIncident(SampleBinding);
        CdcCleanupProof cleanupProof = CreateCleanupProof(SampleBinding);

        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        await store.LatchSourceHistoryLossAsync(incident, CancellationToken.None);

        // The state an interrupted retirement used to leave behind, written directly because nothing
        // in this build produces it any more.
        File.Delete(root.BindingPath(SampleBinding));

        CdcDeleteBindingStateStoreResult orphanDeleteResult =
            await store.DeleteStateAfterVerifiedCleanupAsync(cleanupProof, CancellationToken.None);
        CdcListBindingsStateStoreResult listAfter = await store.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );

        orphanDeleteResult
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.Deleted>()
            .Subject.BindingIdentity.Should()
            .Be(SampleBinding.ToCompleteBindingIdentity());
        File.Exists(root.IncidentPath(SampleBinding)).Should().BeFalse();
        listAfter
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
                "edfi_dms_dms_local_data_store_1_g1_56c4668b1b24_slot",
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
        SetOwnerOnlyStateDirectoriesIfSupported(tempRoot.Path);
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

    private static void SetDirectoryMode(string path, UnixFileMode mode)
    {
#pragma warning disable CA1416
        File.SetUnixFileMode(path, mode);
#pragma warning restore CA1416
    }

    private static void SetOwnerOnlyStateDirectoriesIfSupported(string rootPath)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(rootPath))
        {
            return;
        }

#pragma warning disable CA1416
        foreach (
            string directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
        )
        {
            File.SetUnixFileMode(directoryPath, CdcLocalStateStoreUnixModes.OwnerOnlyDirectory);
        }

        File.SetUnixFileMode(rootPath, CdcLocalStateStoreUnixModes.OwnerOnlyDirectory);
#pragma warning restore CA1416
    }

    private static IReadOnlyList<string> TemporaryStateFiles(string rootPath) =>
        Directory.Exists(rootPath)
            ? Directory.EnumerateFiles(rootPath, "*.tmp", SearchOption.AllDirectories).ToArray()
            : [];

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
            SetOwnerOnlyStateDirectoriesIfSupported(rootPath);
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
        SetOwnerOnlyStateDirectoriesIfSupported(rootPath);
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

    private static string StateAncestorPath(
        string rootPath,
        LocalCdcStateKind stateKind,
        LocalCdcSymlinkAncestor ancestor
    )
    {
        string stateDirectoryName = StateDirectoryName(stateKind);
        return ancestor switch
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
            _ => throw new InvalidOperationException("Unsupported local CDC state ancestor."),
        };
    }

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

        public string RetirementPath(CdcBinding binding) =>
            System.IO.Path.Combine(
                Path,
                "retirements",
                binding.DeploymentKey,
                binding.InstanceKey,
                $"{binding.Generation}.json"
            );

        /// <summary>
        /// Plants the retirement record a completed retirement leaves behind, without the binding
        /// record it removed.
        /// </summary>
        public void WriteRetirement(CdcBinding binding, DateTimeOffset retiredAt)
        {
            string path = RetirementPath(binding);
            Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(path)
                    ?? throw new InvalidOperationException("Retirement path has no parent directory.")
            );
            File.WriteAllText(path, CdcJsonContract.Serialize(CdcRetirement.FromBinding(binding, retiredAt)));
        }

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

        public CdcLocalStateStorePermissionResult ValidateDirectoryNotSharedWritable(string path) =>
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

        public CdcLocalStateStorePermissionResult ValidateDirectoryNotSharedWritable(string path) =>
            CdcLocalStateStorePermissionResult.Success;

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

        public CdcLocalStateStorePermissionResult ValidateDirectoryNotSharedWritable(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Success;
    }

    private sealed class RejectingValidationCdcLocalStateStorePermissions : ICdcLocalStateStorePermissions
    {
        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyDirectory(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ApplyOwnerOnlyFile(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ValidateDirectoryNotSharedWritable(string path) =>
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

        public CdcLocalStateStorePermissionResult ValidateDirectoryNotSharedWritable(string path) =>
            CdcLocalStateStorePermissionResult.Success;

        public CdcLocalStateStorePermissionResult ValidateOwnerOnlyFile(string path) =>
            string.Equals(path, failingPath, StringComparison.Ordinal)
                ? CdcLocalStateStorePermissionResult.Failure("Injected file permission validation failure.")
                : CdcLocalStateStorePermissionResult.Success;
    }

    private class DelegatingCdcLocalStateStoreFileSystem : ICdcLocalStateStoreFileSystem
    {
        public virtual bool FileExists(string path) => File.Exists(path);

        public virtual Task WriteAllTextCreateNewFlushAsync(
            string path,
            string payload,
            FileStreamOptions options,
            CancellationToken cancellationToken
        ) =>
            CdcLocalStateStoreFileSystem.Current.WriteAllTextCreateNewFlushAsync(
                path,
                payload,
                options,
                cancellationToken
            );

        public virtual void MoveFileWithoutOverwrite(string sourcePath, string destinationPath) =>
            File.Move(sourcePath, destinationPath, overwrite: false);

        public virtual void DeleteFile(string path) => File.Delete(path);
    }

    private sealed class FailingWriteCdcLocalStateStoreFileSystem : DelegatingCdcLocalStateStoreFileSystem
    {
        private readonly List<string> _writePaths = [];

        public IReadOnlyList<string> WritePaths => _writePaths;

        public override Task WriteAllTextCreateNewFlushAsync(
            string path,
            string payload,
            FileStreamOptions options,
            CancellationToken cancellationToken
        )
        {
            _writePaths.Add(path);
            File.WriteAllText(path, "{ partial");
            throw new IOException("Injected write failure.");
        }
    }

    private sealed class CancelingWriteCdcLocalStateStoreFileSystem(
        CancellationTokenSource cancellationTokenSource
    ) : DelegatingCdcLocalStateStoreFileSystem
    {
        public override Task WriteAllTextCreateNewFlushAsync(
            string path,
            string payload,
            FileStreamOptions options,
            CancellationToken cancellationToken
        )
        {
            File.WriteAllText(path, "{ partial");
            cancellationTokenSource.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class ConcurrentPublishCdcLocalStateStoreFileSystem(
        string publishedDestinationPath,
        string destinationPayload
    ) : DelegatingCdcLocalStateStoreFileSystem
    {
        public override void MoveFileWithoutOverwrite(string sourcePath, string destinationPath)
        {
            if (!string.Equals(destinationPath, publishedDestinationPath, StringComparison.Ordinal))
            {
                base.MoveFileWithoutOverwrite(sourcePath, destinationPath);
                return;
            }

            File.WriteAllText(publishedDestinationPath, destinationPayload);
            SetOwnerOnlyFilePermissionsIfSupported(publishedDestinationPath);
            throw new IOException("Injected concurrent final-path publication.");
        }
    }

    private class CapturingDeleteCdcLocalStateStoreFileSystem : DelegatingCdcLocalStateStoreFileSystem
    {
        private readonly List<string> _deleteCalls = [];

        public IReadOnlyList<string> DeleteCalls => _deleteCalls;

        public override void DeleteFile(string path)
        {
            RecordDeleteCall(path);
            File.Delete(path);
        }

        protected void RecordDeleteCall(string path) => _deleteCalls.Add(path);
    }

    private sealed class FailingDeleteCdcLocalStateStoreFileSystem(string failingPath)
        : CapturingDeleteCdcLocalStateStoreFileSystem
    {
        public override void DeleteFile(string path)
        {
            RecordDeleteCall(path);
            if (string.Equals(path, failingPath, StringComparison.Ordinal))
            {
                throw new IOException("Injected delete failure.");
            }

            File.Delete(path);
        }
    }
}
