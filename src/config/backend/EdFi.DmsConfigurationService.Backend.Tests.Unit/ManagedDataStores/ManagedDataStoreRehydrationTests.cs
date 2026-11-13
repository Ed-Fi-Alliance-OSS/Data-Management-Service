// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture]
public class Given_Persisted_Managed_Data_Store
{
    private static readonly DateTimeOffset AcceptedAt = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static DmsTrustedArtifactPin Pin() =>
        new()
        {
            ContractVersion = 1,
            Provider = "postgresql",
            PackageId = "EdFi.Dms.Minimal",
            PackageVersion = "1.0.0",
            PackageSha256 = new('a', 64),
            ArtifactSha256 = new('b', 64),
            TemplateKind = "Minimal",
            DataStandardVersion = "5.2",
            EffectiveSchemaHash = new('c', 64),
            InventorySha256 = new('d', 64),
            ApiSchemaFormatVersion = "1",
            RelationalMappingVersion = "v3",
        };

    // Materialize stored values directly: no create/delete event replay or invented intermediate times.
    private static ManagedDataStorePersistedState State(ManagedDataStoreStatus status)
    {
        bool linked =
            status
            is ManagedDataStoreStatus.Created
                or ManagedDataStoreStatus.PendingDelete
                or ManagedDataStoreStatus.DeleteInProgress
                or ManagedDataStoreStatus.DeleteFailed
                or ManagedDataStoreStatus.DeleteError
                or ManagedDataStoreStatus.Deleted;
        bool delete = linked && status != ManagedDataStoreStatus.Created;
        bool pending = status is ManagedDataStoreStatus.PendingCreate or ManagedDataStoreStatus.PendingDelete;
        ManagedDataStoreFailure? failure = status switch
        {
            ManagedDataStoreStatus.CreateFailed or ManagedDataStoreStatus.DeleteFailed =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.Transient,
                    null,
                    "HandlerFailed"
                ),
            ManagedDataStoreStatus.CreateError or ManagedDataStoreStatus.DeleteError =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.OperatorInspectionRequired,
                    null,
                    "AttemptsExhausted"
                ),
            _ => null,
        };
        DmsTargetDatabaseName
            .TryCreate("EdFi_Ods_School_Minimal", ProtectedDatabaseNames.Create("cms", []), out var target)
            .Should()
            .Be(DmsTargetNameValidation.Valid);
        return new(
            7,
            23,
            "School",
            DmsDatabaseTemplate.Minimal,
            target,
            Guid.Parse("543d9edb-382f-44c2-a0ac-942f83ebea54"),
            new("postgresql", "tcp", "db.example.test", 5432, null, "postgres"),
            status == ManagedDataStoreStatus.PendingCreate ? null : Pin(),
            status,
            linked && status != ManagedDataStoreStatus.Deleted ? 101 : null,
            linked ? 101 : null,
            linked ? "School" : null,
            "retained-create-job",
            delete ? "retained-delete-job" : null,
            pending ? 0 : 3,
            failure,
            AcceptedAt,
            AcceptedAt.AddMinutes(8),
            status == ManagedDataStoreStatus.Deleted ? AcceptedAt.AddMinutes(8) : null,
            AcceptedAt.AddMinutes(6),
            AcceptedAt.AddMinutes(8)
        );
    }

    [TestCase(ManagedDataStoreStatus.PendingCreate)]
    [TestCase(ManagedDataStoreStatus.CreateInProgress)]
    [TestCase(ManagedDataStoreStatus.CreateFailed)]
    [TestCase(ManagedDataStoreStatus.CreateError)]
    [TestCase(ManagedDataStoreStatus.Created)]
    [TestCase(ManagedDataStoreStatus.PendingDelete)]
    [TestCase(ManagedDataStoreStatus.DeleteInProgress)]
    [TestCase(ManagedDataStoreStatus.DeleteFailed)]
    [TestCase(ManagedDataStoreStatus.DeleteError)]
    [TestCase(ManagedDataStoreStatus.Deleted)]
    public void It_restores_every_stored_field_without_replaying_lifecycle_events(
        ManagedDataStoreStatus status
    )
    {
        var state = State(status);

        var restored = ManagedDataStore.Rehydrate(state);

        restored.Should().BeEquivalentTo(state);
        restored.PinnedArtifact.Should().Be(state.PinnedArtifact);
        restored.LastFailure.Should().Be(state.LastFailure);
        restored.ToResponse().DataStoreId.Should().Be(state.HistoricalDataStoreId);
        restored.ToResponse().DataStoreName.Should().Be(state.HistoricalDataStoreName);
        restored
            .ToResponse()
            .DatabaseName.Should()
            .Be(state.HistoricalDataStoreId is null ? null : state.TargetName.Value);
    }

    [Test]
    public void It_resumes_a_retry_with_the_original_identity_and_pin_and_rejects_repinning()
    {
        var state = State(ManagedDataStoreStatus.CreateFailed);
        var restored = ManagedDataStore.Rehydrate(state);

        restored.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt.AddMinutes(9), 4);
        restored.PinArtifact(Pin());
        Action repin = () => restored.PinArtifact(Pin() with { PackageVersion = "2.0.0" });

        repin.Should().Throw<InvalidOperationException>();
        restored.PinnedArtifact.Should().Be(state.PinnedArtifact);
        restored.ExpectedSourceIdentity.Should().Be(state.ExpectedSourceIdentity);
        restored.TargetName.Should().Be(state.TargetName);
        restored.LastFailure.Should().BeNull();
        restored.Publish(101, AcceptedAt.AddMinutes(10));
        restored.Status.Should().Be(ManagedDataStoreStatus.Created);
    }

    [Test]
    public void It_preserves_attempt_and_publication_guards_after_restoring_an_in_progress_create()
    {
        var restored = ManagedDataStore.Rehydrate(State(ManagedDataStoreStatus.CreateInProgress));
        Action staleAttempt = () =>
            restored.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 2);
        staleAttempt.Should().Throw<ArgumentException>();
        restored.PinArtifact(Pin());
        restored.Publish(101, AcceptedAt);
        restored.Status.Should().Be(ManagedDataStoreStatus.Created);
    }

    [Test]
    public void It_keeps_a_restored_created_link_until_delete_acceptance()
    {
        var restored = ManagedDataStore.Rehydrate(State(ManagedDataStoreStatus.Created));
        var refreshed = restored.LastRefreshed;

        restored.AcceptDelete("new-delete-job", AcceptedAt.AddMinutes(10));

        restored.Status.Should().Be(ManagedDataStoreStatus.PendingDelete);
        restored.DataStoreId.Should().Be(101);
        restored.DeleteJobId.Should().Be("new-delete-job");
        restored.LastAttempt.Should().Be(0);
        restored.LastRefreshed.Should().Be(refreshed);
    }

    [Test]
    public void It_rejects_a_stale_create_after_restoring_pending_delete_and_retains_the_tombstone()
    {
        var state = State(ManagedDataStoreStatus.PendingDelete);
        var restored = ManagedDataStore.Rehydrate(state);
        Action staleCreate = () =>
            restored.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 1);
        Action repin = () => restored.PinArtifact(Pin());

        staleCreate.Should().Throw<InvalidOperationException>();
        repin.Should().Throw<InvalidOperationException>();
        restored.Should().BeEquivalentTo(state);
        restored.RecordAttempt(ManagedDataStoreStatus.DeleteInProgress, AcceptedAt.AddMinutes(9), 1);
        restored.CompleteDelete(AcceptedAt.AddMinutes(10));
        restored.DataStoreId.Should().BeNull();
        restored.HistoricalDataStoreId.Should().Be(101);
        restored.HistoricalDataStoreName.Should().Be("School");
        restored.CreateJobId.Should().Be(state.CreateJobId);
        restored.DeleteJobId.Should().Be(state.DeleteJobId);
        restored.PinnedArtifact.Should().Be(state.PinnedArtifact);
        restored.DeletedAt.Should().Be(AcceptedAt.AddMinutes(10));
    }

    [TestCase(ManagedDataStoreStatus.CreateInProgress)]
    [TestCase(ManagedDataStoreStatus.CreateFailed)]
    [TestCase(ManagedDataStoreStatus.CreateError)]
    public void It_restores_create_work_that_has_not_resolved_an_artifact(ManagedDataStoreStatus status)
    {
        var state = State(status) with { PinnedArtifact = null };
        var restored = ManagedDataStore.Rehydrate(state);

        restored.Should().BeEquivalentTo(state);
        if (status == ManagedDataStoreStatus.CreateInProgress)
        {
            Action publish = () => restored.Publish(101, AcceptedAt);
            publish.Should().Throw<InvalidOperationException>();
        }
    }

    [TestCase(ManagedDataStoreStatus.CreateError)]
    [TestCase(ManagedDataStoreStatus.DeleteError)]
    public void It_restores_a_zero_attempt_predispatch_error(ManagedDataStoreStatus status)
    {
        var state = State(status) with
        {
            LastAttempt = 0,
            LastFailure = ManagedDataStoreFailure.FromJob(JobErrorCode.InvalidPayload),
            PinnedArtifact = status == ManagedDataStoreStatus.CreateError ? null : Pin(),
        };

        var restored = ManagedDataStore.Rehydrate(state);

        restored.Should().BeEquivalentTo(state);
        Action retry = () =>
            restored.RecordAttempt(
                status == ManagedDataStoreStatus.CreateError
                    ? ManagedDataStoreStatus.CreateInProgress
                    : ManagedDataStoreStatus.DeleteInProgress,
                AcceptedAt,
                1
            );
        retry.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_copies_stored_timestamps_even_when_the_database_clock_moved_backwards()
    {
        var state = State(ManagedDataStoreStatus.CreateInProgress) with
        {
            UpdatedAt = AcceptedAt.AddSeconds(-1),
            LastModifiedDate = AcceptedAt.AddSeconds(-1),
            LastRefreshed = AcceptedAt.AddSeconds(-1),
        };
        ManagedDataStore.Rehydrate(state).Should().BeEquivalentTo(state);
    }

    [Test]
    public void It_restores_allowlisted_failure_codes_and_categories_without_diagnostics()
    {
        foreach (var code in Enum.GetValues<DmsProvisioningFailureCode>())
        {
            foreach (var category in DmsProvisioningFailure.CategoriesOf(code))
            {
                var restored = ManagedDataStoreFailure.Rehydrate(category, code, null);
                restored.Category.Should().Be(category);
                restored.ProvisioningCode.Should().Be(code);
                restored.JobCode.Should().BeNull();
            }
        }
        foreach (var code in JobErrorCode.Infrastructure)
        {
            var expected = ManagedDataStoreFailure.FromJob(code);
            ManagedDataStoreFailure.Rehydrate(expected.Category, null, code.Code).Should().Be(expected);
        }
        ManagedDataStoreFailure
            .Rehydrate(DmsProvisioningFailureCategory.Transient, null, "HandlerFailed")
            .Should()
            .Be(ManagedDataStoreFailure.RetryableJobFailure());
    }

    [Test]
    public void It_rejects_unknown_or_inconsistent_retained_failure_metadata()
    {
        const string unknown = "Password=secret; /private/path";
        Action[] invalid =
        [
            () => ManagedDataStoreFailure.Rehydrate(DmsProvisioningFailureCategory.Transient, null, null),
            () => ManagedDataStoreFailure.Rehydrate(DmsProvisioningFailureCategory.Transient, null, unknown),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.Transient,
                    DmsProvisioningFailureCode.ServerUnavailable,
                    "HandlerFailed"
                ),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.Permanent,
                    DmsProvisioningFailureCode.ServerUnavailable,
                    null
                ),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.Transient,
                    (DmsProvisioningFailureCode)int.MaxValue,
                    null
                ),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    (DmsProvisioningFailureCategory)int.MaxValue,
                    DmsProvisioningFailureCode.ServerUnavailable,
                    null
                ),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.Transient,
                    null,
                    "InvalidPayload"
                ),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.Permanent,
                    null,
                    "AttemptsExhausted"
                ),
            () =>
                ManagedDataStoreFailure.Rehydrate(
                    DmsProvisioningFailureCategory.OperatorInspectionRequired,
                    null,
                    "HandlerFailed"
                ),
        ];
        foreach (var restore in invalid)
        {
            restore.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(unknown);
        }
    }

    private static IEnumerable<TestCaseData> InvalidStates()
    {
        var pending = State(ManagedDataStoreStatus.PendingCreate);
        var active = State(ManagedDataStoreStatus.CreateInProgress);
        var created = State(ManagedDataStoreStatus.Created);
        var deleting = State(ManagedDataStoreStatus.PendingDelete);
        var failed = State(ManagedDataStoreStatus.CreateFailed);
        var deleted = State(ManagedDataStoreStatus.Deleted);
        ManagedDataStorePersistedState[] invalid =
        [
            pending with
            {
                Id = 0,
            },
            pending with
            {
                Name = " School ",
            },
            pending with
            {
                Name = "",
            },
            pending with
            {
                ExpectedSourceIdentity = Guid.Empty,
            },
            pending with
            {
                TargetName = default,
            },
            pending with
            {
                Template = null!,
            },
            pending with
            {
                Endpoint = null!,
            },
            pending with
            {
                Endpoint = pending.Endpoint with { Provider = "unknown" },
            },
            pending with
            {
                Endpoint = pending.Endpoint with { Host = " " },
            },
            pending with
            {
                CreateJobId = " ",
            },
            pending with
            {
                Status = (ManagedDataStoreStatus)100,
            },
            pending with
            {
                LastAttempt = -1,
            },
            pending with
            {
                LastAttempt = 1,
            },
            pending with
            {
                LastFailure = ManagedDataStoreFailure.RetryableJobFailure(),
            },
            pending with
            {
                PinnedArtifact = Pin(),
            },
            pending with
            {
                DataStoreId = 101,
            },
            pending with
            {
                HistoricalDataStoreId = 101,
            },
            pending with
            {
                HistoricalDataStoreName = "School",
            },
            pending with
            {
                DeleteJobId = "delete-job",
            },
            pending with
            {
                DeletedAt = AcceptedAt,
            },
            active with
            {
                LastAttempt = 0,
            },
            active with
            {
                LastFailure = ManagedDataStoreFailure.RetryableJobFailure(),
            },
            active with
            {
                PinnedArtifact = Pin() with { ContractVersion = 2 },
            },
            active with
            {
                PinnedArtifact = Pin() with { Provider = "mssql" },
            },
            active with
            {
                PinnedArtifact = Pin() with { TemplateKind = "Populated" },
            },
            active with
            {
                PinnedArtifact = Pin() with { PackageId = "" },
            },
            active with
            {
                PinnedArtifact = Pin() with { ArtifactSha256 = null! },
            },
            failed with
            {
                LastAttempt = 0,
            },
            failed with
            {
                LastFailure = null,
            },
            failed with
            {
                LastFailure = ManagedDataStoreFailure.FromJob(JobErrorCode.InvalidPayload),
            },
            State(ManagedDataStoreStatus.CreateError) with
            {
                LastFailure = ManagedDataStoreFailure.RetryableJobFailure(),
            },
            created with
            {
                DataStoreId = null,
            },
            created with
            {
                HistoricalDataStoreId = null,
            },
            created with
            {
                DataStoreId = 102,
            },
            created with
            {
                HistoricalDataStoreName = "Other",
            },
            created with
            {
                PinnedArtifact = null,
            },
            created with
            {
                LastAttempt = 0,
            },
            deleting with
            {
                DeleteJobId = null,
            },
            deleting with
            {
                DeleteJobId = " ",
            },
            deleting with
            {
                LastAttempt = 1,
            },
            deleting with
            {
                DataStoreId = null,
            },
            deleted with
            {
                DataStoreId = 101,
            },
            deleted with
            {
                HistoricalDataStoreId = null,
            },
            deleted with
            {
                DeletedAt = null,
            },
        ];
        for (int i = 0; i < invalid.Length; i++)
        {
            yield return new TestCaseData(invalid[i]).SetName($"It_rejects_inconsistent_persisted_state_{i}");
        }
    }

    [TestCaseSource(nameof(InvalidStates))]
    public void It_rejects_inconsistent_persisted_state(ManagedDataStorePersistedState state)
    {
        Action restore = () => ManagedDataStore.Rehydrate(state);
        restore.Should().Throw<ArgumentException>();
    }
}
