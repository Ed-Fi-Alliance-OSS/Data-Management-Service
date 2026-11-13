// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture]
public class Given_Managed_Data_Store_Lifecycle
{
    private static readonly DateTimeOffset AcceptedAt = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static ManagedDataStore NewStore() =>
        new(
            7,
            null,
            " school 1 ",
            DmsDatabaseTemplate.Minimal,
            ManagedDataStoreNameFormatter.Format(
                "school 1",
                DmsDatabaseTemplate.Minimal,
                ProtectedDatabaseNames.Create("cms", [])
            ),
            Guid.Parse("543d9edb-382f-44c2-a0ac-942f83ebea54"),
            new("postgresql", "tcp", "db.example.test", 5432, null, "postgres"),
            "create-job",
            AcceptedAt
        );

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

    [Test]
    public void It_preserves_identity_target_complete_pin_and_historical_link_through_retries_and_deletion()
    {
        var store = NewStore();
        var identity = store.ExpectedSourceIdentity;
        var endpoint = store.Endpoint;
        var target = store.TargetName;
        store.PinnedArtifact.Should().BeNull();
        store.ToResponse().DataStoreId.Should().BeNull();
        store.ToResponse().DatabaseName.Should().BeNull();
        store.LastRefreshed.Should().Be(AcceptedAt);
        store.LastModifiedDate.Should().Be(AcceptedAt);
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt.AddMinutes(1), 1);
        store.PinArtifact(Pin());
        store.RecordAttempt(
            ManagedDataStoreStatus.CreateFailed,
            AcceptedAt.AddMinutes(2),
            1,
            ManagedDataStoreFailure.RetryableJobFailure()
        );
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt.AddMinutes(3), 1);
        store.PinArtifact(Pin()); // Exact same complete pin is idempotent across retry.
        store.ToResponse().DatabaseName.Should().BeNull();
        store.Publish(101, AcceptedAt.AddMinutes(4));
        store.AcceptDelete("delete-job", AcceptedAt.AddMinutes(5));
        store.LastRefreshed.Should().Be(AcceptedAt.AddMinutes(4));
        store.LastModifiedDate.Should().Be(AcceptedAt.AddMinutes(5));
        store.RecordAttempt(ManagedDataStoreStatus.DeleteInProgress, AcceptedAt.AddMinutes(6), 1);
        store.RecordAttempt(
            ManagedDataStoreStatus.DeleteFailed,
            AcceptedAt.AddMinutes(7),
            1,
            ManagedDataStoreFailure.RetryableJobFailure()
        );
        store.RecordAttempt(ManagedDataStoreStatus.DeleteInProgress, AcceptedAt.AddMinutes(8), 1);
        store.CompleteDelete(AcceptedAt.AddMinutes(9));
        store.DataStoreId.Should().BeNull();
        store.HistoricalDataStoreId.Should().Be(101);
        store.ToResponse().DataStoreId.Should().Be(101);
        store.ToResponse().DataStoreName.Should().Be("school 1");
        store.ToResponse().DatabaseName.Should().Be("EdFi_Ods_school_1_Minimal");
        store.DeletedAt.Should().Be(AcceptedAt.AddMinutes(9));
        store.LastRefreshed.Should().Be(store.LastModifiedDate);
        store.ExpectedSourceIdentity.Should().Be(identity);
        store.Endpoint.Should().Be(endpoint);
        store.TargetName.Should().Be(target);
        store.PinnedArtifact.Should().Be(Pin());
        foreach (
            string property in new[]
            {
                "ExpectedSourceIdentity",
                "Endpoint",
                "TargetName",
                "Name",
                "TenantId",
                "Template",
            }
        )
        {
            typeof(ManagedDataStore).GetProperty(property)!.SetMethod.Should().BeNull();
        }
    }

    [Test]
    public void It_refuses_a_change_to_each_field_of_an_existing_pin()
    {
        var store = NewStore();
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 1);
        var pin = Pin();
        store.PinArtifact(pin);
        DmsTrustedArtifactPin[] replacements =
        [
            pin with
            {
                ContractVersion = 2,
            },
            pin with
            {
                Provider = "mssql",
            },
            pin with
            {
                PackageId = "other",
            },
            pin with
            {
                PackageVersion = "2.0",
            },
            pin with
            {
                PackageSha256 = new('e', 64),
            },
            pin with
            {
                ArtifactSha256 = new('e', 64),
            },
            pin with
            {
                TemplateKind = "Populated",
            },
            pin with
            {
                DataStandardVersion = "6.0",
            },
            pin with
            {
                EffectiveSchemaHash = new('e', 64),
            },
            pin with
            {
                InventorySha256 = new('e', 64),
            },
            pin with
            {
                ApiSchemaFormatVersion = "2",
            },
            pin with
            {
                RelationalMappingVersion = "v4",
            },
        ];
        foreach (var replacement in replacements)
        {
            Action repin = () => store.PinArtifact(replacement);
            repin.Should().Throw<InvalidOperationException>();
            store.PinnedArtifact.Should().Be(pin);
        }
    }

    [Test]
    public void It_checks_every_status_pair_against_the_approved_transition_table()
    {
        string[] allowed =
        [
            "PendingCreate>CreateInProgress",
            "PendingCreate>CreateError",
            "CreateFailed>CreateInProgress",
            "CreateFailed>CreateError",
            "CreateInProgress>CreateInProgress",
            "CreateInProgress>CreateError",
            "CreateInProgress>Created",
            "CreateInProgress>CreateFailed",
            "Created>PendingDelete",
            "PendingDelete>DeleteInProgress",
            "PendingDelete>DeleteError",
            "DeleteFailed>DeleteInProgress",
            "DeleteFailed>DeleteError",
            "DeleteInProgress>DeleteInProgress",
            "DeleteInProgress>DeleteError",
            "DeleteInProgress>Deleted",
            "DeleteInProgress>DeleteFailed",
        ];
        foreach (var current in Enum.GetValues<ManagedDataStoreStatus>())
        {
            foreach (var next in Enum.GetValues<ManagedDataStoreStatus>())
            {
                current
                    .CanTransitionTo(next)
                    .Should()
                    .Be(allowed.Contains($"{current}>{next}"), $"transition {current} to {next}");
            }
        }
        ((ManagedDataStoreStatus)100)
            .CanTransitionTo(ManagedDataStoreStatus.CreateInProgress)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_refuses_premature_publication_and_terminal_or_newer_operation_regression()
    {
        var store = NewStore();
        Action publish = () => store.Publish(101, AcceptedAt);
        publish.Should().Throw<InvalidOperationException>();
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 1);
        store.PinArtifact(Pin());
        store.Publish(101, AcceptedAt);
        store.AcceptDelete("delete-job", AcceptedAt);
        Action staleCreate = () =>
            store.RecordAttempt(
                ManagedDataStoreStatus.CreateError,
                AcceptedAt,
                1,
                ManagedDataStoreFailure.FromJob(JobErrorCode.HandlerFailed)
            );
        staleCreate.Should().Throw<InvalidOperationException>();
        store.Status.Should().Be(ManagedDataStoreStatus.PendingDelete);
        store.RecordAttempt(
            ManagedDataStoreStatus.DeleteError,
            AcceptedAt.AddMinutes(1),
            1,
            ManagedDataStoreFailure.FromJob(JobErrorCode.HandlerFailed)
        );
        Action retryTerminal = () =>
            store.RecordAttempt(ManagedDataStoreStatus.DeleteInProgress, AcceptedAt.AddMinutes(2), 1);
        retryTerminal.Should().Throw<InvalidOperationException>();
        store.LastModifiedDate.Should().Be(AcceptedAt.AddMinutes(1));
        Action delete = () => store.CompleteDelete(AcceptedAt);
        delete.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_uses_versioned_id_only_payloads_with_the_existing_strict_serializer()
    {
        ManagedJobTypes.PayloadVersion.Should().Be(1);
        ManagedJobTypes.Create.Should().NotBe(ManagedJobTypes.Delete);
        JobPayloadContract.Violations(typeof(CreateManagedDataStorePayload)).Should().BeEmpty();
        JobPayloadContract.Violations(typeof(DeleteManagedDataStorePayload)).Should().BeEmpty();
        JobPayloadSerializer
            .Serialize(new CreateManagedDataStorePayload(7))
            .Should()
            .Be(new JobPayloadWriteResult.Success("{\"manageId\":7}"));
        JobPayloadSerializer
            .Serialize(new DeleteManagedDataStorePayload(7))
            .Should()
            .Be(new JobPayloadWriteResult.Success("{\"manageId\":7}"));
        foreach (int id in new[] { 0, -1, int.MinValue })
        {
            new CreateManagedDataStorePayloadValidator().Validate(new(id)).Should().Equal("InvalidManageId");
            new DeleteManagedDataStorePayloadValidator().Validate(new(id)).Should().Equal("InvalidManageId");
        }
        new CreateManagedDataStorePayloadValidator().Validate(new(int.MaxValue)).Should().BeEmpty();
        new DeleteManagedDataStorePayloadValidator().Validate(new(7)).Should().BeEmpty();
        JobPayloadSerializer
            .Deserialize<CreateManagedDataStorePayload>("{\"manageId\":7,\"connectionString\":\"secret\"}")
            .Should()
            .BeOfType<JobPayloadReadResult<CreateManagedDataStorePayload>.Failure>();
        JobPayloadSerializer
            .Deserialize<DeleteManagedDataStorePayload>("{}")
            .Should()
            .BeOfType<JobPayloadReadResult<DeleteManagedDataStorePayload>.Failure>();
    }

    [Test]
    public void It_retains_typed_retry_predispatch_and_exhaustion_evidence_without_free_text()
    {
        const string secret = "Password=metadata-secret; /private/path";
        var providerFailure = DmsProvisioningFailure.Create(
            DmsProvisioningFailureCode.ServerUnavailable,
            DmsProvisioningDiagnostic.ForOperation(secret)
        );
        var observed = ManagedDataStoreFailure.FromProvisioner(providerFailure);
        observed.ProvisioningCode.Should().Be(DmsProvisioningFailureCode.ServerUnavailable);
        observed.Category.Should().Be(DmsProvisioningFailureCategory.Transient);
        observed.JobCode.Should().BeNull();
        JsonSerializer.Serialize(observed).Should().NotContain(secret);

        var store = NewStore();
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 2);
        store.RecordAttempt(ManagedDataStoreStatus.CreateFailed, AcceptedAt.AddMinutes(1), 2, observed);
        store.LastAttempt.Should().Be(2);
        store.LastFailure.Should().Be(observed);
        var exhausted = ManagedDataStoreFailure.FromJob(new(JobErrorCode.AttemptsExhausted.Code, secret));
        exhausted.JobCode.Should().Be("AttemptsExhausted");
        exhausted.ProvisioningCode.Should().BeNull();
        exhausted.Category.Should().Be(DmsProvisioningFailureCategory.OperatorInspectionRequired);
        JsonSerializer.Serialize(exhausted).Should().NotContain(secret);
        store.RecordAttempt(ManagedDataStoreStatus.CreateError, AcceptedAt.AddMinutes(2), 2, exhausted);
        store.LastFailure.Should().Be(exhausted);
        JsonSerializer
            .Serialize(store.ToResponse())
            .Should()
            .NotContain("LastFailure")
            .And.NotContain("LastAttempt");

        foreach (
            var code in JobErrorCode.Infrastructure.Where(code => code != JobErrorCode.AttemptsExhausted)
        )
        {
            var predispatch = NewStore();
            predispatch.RecordAttempt(
                ManagedDataStoreStatus.CreateError,
                AcceptedAt,
                0,
                ManagedDataStoreFailure.FromJob(code)
            );
            predispatch.LastAttempt.Should().Be(0);
            predispatch.LastFailure!.JobCode.Should().Be(code.Code);
            predispatch.LastFailure.Category.Should().Be(DmsProvisioningFailureCategory.Permanent);
        }
        Action unknown = () => ManagedDataStoreFailure.FromJob(new(secret, secret));
        unknown.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(secret);
    }

    [Test]
    public void It_rejects_invalid_observations_without_changing_state_timestamps_or_metadata()
    {
        var store = NewStore();
        var transient = ManagedDataStoreFailure.RetryableJobFailure();
        var terminal = ManagedDataStoreFailure.FromJob(JobErrorCode.InvalidPayload);
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 2);
        store.RecordAttempt(ManagedDataStoreStatus.CreateFailed, AcceptedAt.AddMinutes(1), 2, transient);
        Action[] invalid =
        [
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, -1),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 0),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 1),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt, 3, transient),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateFailed, AcceptedAt, 0, transient),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateFailed, AcceptedAt, 2, terminal),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateError, AcceptedAt, 2, transient),
            () => store.RecordAttempt(ManagedDataStoreStatus.CreateError, AcceptedAt, 2),
        ];
        foreach (Action observe in invalid)
        {
            observe.Should().Throw<ArgumentException>();
            store.Status.Should().Be(ManagedDataStoreStatus.CreateFailed);
            store.LastAttempt.Should().Be(2);
            store.LastFailure.Should().Be(transient);
            store.LastModifiedDate.Should().Be(AcceptedAt.AddMinutes(1));
            store.LastRefreshed.Should().Be(AcceptedAt.AddMinutes(1));
            store.UpdatedAt.Should().Be(AcceptedAt.AddMinutes(1));
        }
        Action wrongLifecycle = () =>
            store.RecordAttempt(ManagedDataStoreStatus.DeleteError, AcceptedAt, 3, terminal);
        wrongLifecycle.Should().Throw<InvalidOperationException>();
        store.Status.Should().Be(ManagedDataStoreStatus.CreateFailed);
        store.LastAttempt.Should().Be(2);
        store.LastFailure.Should().Be(transient);
        store.LastModifiedDate.Should().Be(AcceptedAt.AddMinutes(1));
        store.LastRefreshed.Should().Be(AcceptedAt.AddMinutes(1));
        store.UpdatedAt.Should().Be(AcceptedAt.AddMinutes(1));
        store.RecordAttempt(ManagedDataStoreStatus.CreateInProgress, AcceptedAt.AddMinutes(2), 3);
        store.LastFailure.Should().BeNull();
        store.PinArtifact(Pin());
        store.Publish(101, AcceptedAt.AddMinutes(3));
        store.AcceptDelete("delete-job", AcceptedAt.AddMinutes(4));
        store.LastAttempt.Should().Be(0);
        store.LastFailure.Should().BeNull();
    }
}
