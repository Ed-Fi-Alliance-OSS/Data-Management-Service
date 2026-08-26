// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

public abstract class Given_PinnedImageConnectorTemplateFixture
{
    protected abstract CdcProvider Provider { get; }

    [Test]
    public async Task It_validates_registers_and_reads_back_the_rendered_config_against_the_pinned_runtime()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(Provider, cancellation.Token);

        CdcConnectorTemplateRequest request = await fixture.CreateRequestAsync(cancellation.Token);
        CdcConnectorTemplateResult rendered = fixture.Render(request);

        await fixture.AssertRuntimeLoadsRequiredClassesAsync(rendered, cancellation.Token);
        await fixture.AssertKafkaMurmur2PartitionerVectorsAsync(cancellation.Token);
        await fixture.AssertConnectorConfigValidatesAsync(rendered, cancellation.Token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(rendered, cancellation.Token);
        await fixture.AssertKafkaConnectReadBackMatchesExpectedConfigAsync(request, cancellation.Token);
    }

    [Test]
    public async Task It_observes_provider_smoke_heartbeat_offset_progress_and_restart_validates_retained_template_state()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(Provider, cancellation.Token);

        CdcConnectorTemplateRequest request = await fixture.CreateRequestAsync(cancellation.Token);
        CdcConnectorTemplateResult rendered = fixture.Render(request);

        await fixture.AssertConnectorConfigValidatesAsync(rendered, cancellation.Token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(rendered, cancellation.Token);
        var committedOffset = await fixture.AssertHeartbeatAndCommittedOffsetProgressAsync(
            request,
            cancellation.Token
        );
        await fixture.RestartRegisteredConnectorAndAssertTemplateStillValidAsync(
            request,
            committedOffset,
            cancellation.Token
        );
    }
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
[Parallelizable]
public sealed class Given_OfflinePinnedImageConnectorTemplateFixture(CdcProvider provider)
{
    private CdcProvider Provider { get; } = provider;

    [Test]
    public async Task It_configures_kafka_connect_env_config_provider_for_externalized_database_passwords()
    {
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            CdcConnectorTemplatePinnedImageFixture.CreateOffline(Provider);

        fixture.AssertKafkaConnectWorkerConfigProviderStartupEnvironmentIsPinned();
    }
}

[TestFixture]
[Parallelizable]
public sealed class Given_PinnedImageFixtureDockerCommandResult
{
    [Test]
    public void It_sanitizes_stdout_and_stderr_before_formatting_failure_messages()
    {
        DockerCommandResult result = new(
            1,
            $"stdout leaked {CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}",
            $"stderr leaked {CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}"
        );

        string failureMessage = result.ToFailureMessage();

        using var _ = new AssertionScope();
        failureMessage.Should().Contain("[redacted]");
        failureMessage.Should().NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
    }
}

[TestFixture]
[Parallelizable]
public sealed class Given_PinnedImageSmokeDiagnostics
{
    [Test]
    public void It_builds_stable_pinned_image_smoke_diagnostics()
    {
        CdcConnectorTemplateDiagnostic diagnostic = CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
            code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorRegistrationFailure,
            category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
            provider: CdcProvider.Postgresql,
            propertyName: "kafkaConnect.registration",
            safeArtifactOrObjectName: new CdcSafeName("dms-binding-g7"),
            expectedValue: "Created or OK",
            observedValue: "InternalServerError",
            redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
        );

        using var _ = new AssertionScope();
        diagnostic
            .Code.Should()
            .Be(CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorRegistrationFailure);
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch);
        diagnostic.Severity.Should().Be(CdcConnectorTemplateDiagnosticSeverity.Error);
        diagnostic.Provider.Should().Be(CdcProvider.Postgresql);
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.PinnedImageSmoke);
        diagnostic.PropertyName.Should().Be("kafkaConnect.registration");
        diagnostic.SafeArtifactOrObjectName.Should().Be(new CdcSafeName("dms-binding-g7"));
        diagnostic.ExpectedValue.Should().Be("Created or OK");
        diagnostic.ObservedValue.Should().Be("InternalServerError");
        diagnostic.RedactionClassification.Should().Be(CdcConnectorTemplateRedactionClassification.Safe);
    }

    [Test]
    public void It_preserves_diagnostic_evidence_in_sanitized_assertion_failures()
    {
        CdcConnectorTemplateDiagnostic diagnostic = CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
            code: CdcConnectorTemplateDiagnosticCodes.PinnedImageDockerPrerequisiteFailure,
            category: CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput,
            provider: CdcProvider.SqlServer,
            propertyName: "pinnedImage.prerequisite",
            safeArtifactOrObjectName: null,
            expectedValue: "configured pinned-image smoke prerequisites",
            observedValue: CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword,
            redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
        );

        Action act = () =>
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                diagnostic,
                $"Docker prerequisite leaked {CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}"
            );

        CdcConnectorTemplatePinnedImageSmokeAssertionException exception = act.Should()
            .Throw<CdcConnectorTemplatePinnedImageSmokeAssertionException>()
            .Which;

        using var _ = new AssertionScope();
        exception.Diagnostic.Should().BeSameAs(diagnostic);
        exception
            .Message.Should()
            .Contain(CdcConnectorTemplateDiagnosticCodes.PinnedImageDockerPrerequisiteFailure);
        exception.Message.Should().Contain(nameof(CdcConnectorTemplateSourcePhase.PinnedImageSmoke));
        exception.Message.Should().Contain("[redacted]");
        exception
            .Message.Should()
            .NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
    }
}

[TestFixture]
[Parallelizable]
public sealed class Given_PinnedImageConnectorProviderOffsetRetentionComparison
{
    [Test]
    public void It_compares_postgresql_offset_progress_by_provider_position()
    {
        string starting = PostgresqlOffset(100, metadataToken: 1);

        using var _ = new AssertionScope();
        Advances(CdcProvider.Postgresql, starting, PostgresqlOffset(100, metadataToken: 2))
            .Should()
            .BeFalse("equal PostgreSQL lsn_proc values are not provider position progress");
        Advances(CdcProvider.Postgresql, starting, PostgresqlOffset(101, metadataToken: 2))
            .Should()
            .BeTrue("greater PostgreSQL lsn_proc values are provider position progress");
        Advances(CdcProvider.Postgresql, starting, PostgresqlOffset(99, metadataToken: 2))
            .Should()
            .BeFalse("older PostgreSQL lsn_proc values are not provider position progress");
    }

    [Test]
    public void It_compares_postgresql_lsn_proc_monotonically()
    {
        string minimum = PostgresqlOffset(100);

        using var _ = new AssertionScope();
        RetainsOrAdvances(CdcProvider.Postgresql, minimum, PostgresqlOffset(100))
            .Should()
            .BeTrue("equal PostgreSQL lsn_proc values retain the pre-restart source position");
        RetainsOrAdvances(CdcProvider.Postgresql, minimum, PostgresqlOffset(101))
            .Should()
            .BeTrue("greater PostgreSQL lsn_proc values advance from the pre-restart source position");
        RetainsOrAdvances(CdcProvider.Postgresql, minimum, PostgresqlOffset(99))
            .Should()
            .BeFalse("older PostgreSQL lsn_proc values indicate lost retained source position");
        RetainsOrAdvances(CdcProvider.Postgresql, minimum, PostgresqlOffset(101, snapshot: "true"))
            .Should()
            .BeFalse("snapshot offsets are not committed streaming progress");
        RetainsOrAdvances(CdcProvider.Postgresql, minimum, "null")
            .Should()
            .BeFalse("null offsets fail closed");
        RetainsOrAdvances(CdcProvider.Postgresql, minimum, """{"snapshot":"false"}""")
            .Should()
            .BeFalse("missing PostgreSQL lsn_proc fails closed");
        RetainsOrAdvances(
                CdcProvider.Postgresql,
                minimum,
                """{"snapshot":"false","lsn_proc":"not-a-number"}"""
            )
            .Should()
            .BeFalse("malformed PostgreSQL lsn_proc fails closed");
    }

    [Test]
    public void It_parses_postgresql_lsn_proc_unsigned_strings_and_signed_bit_patterns()
    {
        string maximumUnsigned = PostgresqlOffset(ulong.MaxValue);

        using var _ = new AssertionScope();
        RetainsOrAdvances(
                CdcProvider.Postgresql,
                PostgresqlOffset(100),
                """{"snapshot":"false","lsn_proc":"101"}"""
            )
            .Should()
            .BeTrue("positive string PostgreSQL lsn_proc values are accepted");
        RetainsOrAdvances(
                CdcProvider.Postgresql,
                PostgresqlOffset(ulong.MaxValue - 1),
                """{"snapshot":"false","lsn_proc":"18446744073709551615"}"""
            )
            .Should()
            .BeTrue("unsigned string PostgreSQL lsn_proc values are accepted");
        RetainsOrAdvances(CdcProvider.Postgresql, maximumUnsigned, """{"snapshot":"false","lsn_proc":-1}""")
            .Should()
            .BeTrue("negative numeric PostgreSQL lsn_proc values are reinterpreted as unsigned bit patterns");
        RetainsOrAdvances(CdcProvider.Postgresql, maximumUnsigned, """{"snapshot":"false","lsn_proc":"-1"}""")
            .Should()
            .BeTrue("negative string PostgreSQL lsn_proc values are reinterpreted as unsigned bit patterns");
    }

    [Test]
    public void It_compares_sqlserver_offset_progress_by_provider_position()
    {
        string starting = SqlServerOffset(
            commitLsn: "00000027:00000758:0005",
            changeLsn: "00000027:00000758:0006",
            eventSerialNo: 1,
            metadataToken: 1
        );

        using var _ = new AssertionScope();
        Advances(
                CdcProvider.SqlServer,
                starting,
                SqlServerOffset("00000027:00000758:0005", "00000027:00000758:0006", 1, metadataToken: 2)
            )
            .Should()
            .BeFalse("equal SQL Server provider positions are not progress even when unrelated JSON changes");
        Advances(
                CdcProvider.SqlServer,
                starting,
                SqlServerOffset("00000027:00000758:0005", "00000027:00000758:0006", 2, metadataToken: 2)
            )
            .Should()
            .BeTrue("greater SQL Server event_serial_no values are provider position progress");
        Advances(
                CdcProvider.SqlServer,
                starting,
                SqlServerOffset("00000027:00000758:0005", "00000027:00000758:0006", 0, metadataToken: 2)
            )
            .Should()
            .BeFalse("older SQL Server event_serial_no values are not provider position progress");
    }

    [Test]
    public void It_compares_sqlserver_lsn_tuple_monotonically()
    {
        string minimum = SqlServerOffset(
            commitLsn: "00000027:00000758:0005",
            changeLsn: "00000027:00000758:0006",
            eventSerialNo: 1
        );

        using var _ = new AssertionScope();
        RetainsOrAdvances(CdcProvider.SqlServer, minimum, minimum)
            .Should()
            .BeTrue("equal SQL Server LSN tuples retain the pre-restart source position");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000027:00000758:0005", "00000027:00000758:0006", 2)
            )
            .Should()
            .BeTrue("greater SQL Server event_serial_no values advance the provider position");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000027:00000758:0005", "00000027:00000759:0000", 0)
            )
            .Should()
            .BeTrue("greater SQL Server change_lsn values advance the provider position");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000028:00000000:0000", "00000028:00000000:0000", 0)
            )
            .Should()
            .BeTrue("greater SQL Server commit_lsn values advance the provider position");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000027:00000758:0005", "00000027:00000758:0006", 0)
            )
            .Should()
            .BeFalse("older SQL Server event_serial_no values indicate lost retained source position");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000026:ffffffff:ffff", "00000026:ffffffff:ffff", 9)
            )
            .Should()
            .BeFalse("older SQL Server commit_lsn values indicate lost retained source position");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000028:00000000:0000", "00000028:00000000:0000", 0, snapshot: "true")
            )
            .Should()
            .BeFalse("snapshot offsets are not committed streaming progress");
        RetainsOrAdvances(CdcProvider.SqlServer, minimum, "null")
            .Should()
            .BeFalse("null offsets fail closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                """{"snapshot":"false","change_lsn":"00000027:00000758:0006","event_serial_no":1}"""
            )
            .Should()
            .BeFalse("missing SQL Server commit_lsn fails closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000027:00000758:0005", "not-a-lsn", 1)
            )
            .Should()
            .BeFalse("malformed SQL Server LSN fields fail closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                """{"snapshot":"false","commit_lsn":"00000027:00000758:0005","change_lsn":"00000027:00000758:0006","event_serial_no":"not-a-number"}"""
            )
            .Should()
            .BeFalse("malformed SQL Server event_serial_no fails closed");
    }

    [Test]
    public void It_rejects_sqlserver_variable_width_oversized_empty_and_non_hex_lsn_components()
    {
        string minimum = SqlServerOffset(
            commitLsn: "00000027:00000758:0005",
            changeLsn: "00000027:00000758:0006",
            eventSerialNo: 1
        );

        using var _ = new AssertionScope();
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("0000027:00000758:0005", "00000027:00000758:0006", 2)
            )
            .Should()
            .BeFalse("variable-width SQL Server commit_lsn components fail closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("000000027:00000758:0005", "00000027:00000758:0006", 2)
            )
            .Should()
            .BeFalse("oversized SQL Server commit_lsn components fail closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("00000027::0005", "00000027:00000758:0006", 2)
            )
            .Should()
            .BeFalse("empty SQL Server commit_lsn components fail closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                SqlServerOffset("0000002g:00000758:0005", "00000027:00000758:0006", 2)
            )
            .Should()
            .BeFalse("non-hex SQL Server commit_lsn components fail closed");
        RetainsOrAdvances(
                CdcProvider.SqlServer,
                minimum,
                """{"snapshot":"false","commit_lsn":"00000027:00000758:0005","change_lsn":"00000027:00000758:0006","event_serial_no":"-1"}"""
            )
            .Should()
            .BeFalse("negative SQL Server event_serial_no string values fail closed");
    }

    private static bool RetainsOrAdvances(CdcProvider provider, string minimum, string observed) =>
        CdcConnectorTemplatePinnedImageFixture.CommittedSourceOffsetRetainsOrAdvances(
            provider,
            minimum,
            observed
        );

    private static bool Advances(CdcProvider provider, string starting, string observed) =>
        CdcConnectorTemplatePinnedImageFixture.CommittedSourceOffsetAdvances(provider, starting, observed);

    private static string PostgresqlOffset(ulong lsnProc, string snapshot = "false", int metadataToken = 1) =>
        $@"{{""snapshot"":""{snapshot}"",""lsn_proc"":{lsnProc},""metadata_token"":{metadataToken}}}";

    private static string SqlServerOffset(
        string commitLsn,
        string changeLsn,
        long eventSerialNo,
        string snapshot = "false",
        int metadataToken = 1
    ) =>
        $@"{{""snapshot"":""{snapshot}"",""commit_lsn"":""{commitLsn}"",""change_lsn"":""{changeLsn}"",""event_serial_no"":{eventSerialNo},""metadata_token"":{metadataToken}}}";
}

[TestFixture]
[Parallelizable]
public sealed class Given_PinnedImageConnectorCommittedSourceOffsetSelection
{
    [Test]
    public void It_rejects_duplicate_matching_source_partitions_before_offset_validation()
    {
        CdcConnectorTemplateRequest request = BuildPostgresqlRequest();
        using JsonDocument document = JsonDocument.Parse(
            """
            [
              {
                "partition": { "server": "dms-binding-g7" },
                "offset": { "snapshot": "false", "lsn_proc": 100 }
              },
              {
                "partition": { "server": "dms-binding-g7" },
                "offset": { "snapshot": "true", "lsn_proc": 101 }
              }
            ]
            """
        );

        Action act = () =>
            CdcConnectorTemplatePinnedImageFixture.TrySelectCommittedSourceOffset(
                request,
                document.RootElement
            );

        CdcConnectorTemplatePinnedImageSmokeAssertionException exception = act.Should()
            .Throw<CdcConnectorTemplatePinnedImageSmokeAssertionException>()
            .Which;

        using var _ = new AssertionScope();
        exception.Diagnostic.PropertyName.Should().Be("kafkaConnect.sourcePartition");
        exception.Diagnostic.ObservedValue.Should().Be("2");
    }

    private static CdcConnectorTemplateRequest BuildPostgresqlRequest()
    {
        CdcSourceFingerprint sourceFingerprint = CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(
            CdcProvider.Postgresql
        );
        var providerSetupResult = new CdcProviderSetupResult(
            CdcProvider.Postgresql,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            CdcProviderSetupOutcome.CreatedOrMatched,
            sourceFingerprint,
            sourceFingerprint,
            ArtifactInventory: [],
            GrantInventory: [],
            SourceTableInventory: [],
            ExpectedMessageKeyColumns: [],
            HeartbeatActionQuery: null,
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );

        CoreCdc.CdcArtifactInventory artifactInventory = CoreCdc
            .CdcArtifactNameGenerator.Render(
                new CoreCdc.CdcArtifactNameInput(
                    "dms",
                    "edfi.documents",
                    "binding",
                    7,
                    CoreCdc.CdcProvider.Postgresql
                )
            )
            .Inventory!;
        var binding = new CoreCdc.CdcBinding(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            "dms",
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            "binding",
            7,
            CoreCdc.CdcProvider.Postgresql,
            sourceFingerprint.Value,
            artifactInventory.ConnectorName,
            artifactInventory.TopicName,
            PartitionCount: 1,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );

        return new CdcConnectorTemplateRequest(
            binding,
            new CdcConnectorProviderSetupEvidence(bindingGeneration: 7, providerSetupResult),
            new CdcConnectorTemplateDeploymentPolicy("localhost:9092", maxRecordBytes: 33_554_432),
            new CdcProviderConnectionProperties(
                CdcProvider.Postgresql,
                new Dictionary<string, string> { ["database.dbname"] = "edfi_datastore" }
            ),
            CdcKafkaClientSecurityProperties.Empty
        );
    }
}

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("CdcConnectorTemplateSmoke")]
[Category("PostgresqlIntegration")]
public sealed class Given_PostgresqlPinnedImageConnectorTemplateFixture
    : Given_PinnedImageConnectorTemplateFixture
{
    protected override CdcProvider Provider => CdcProvider.Postgresql;
}

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("CdcConnectorTemplateSmoke")]
[Category("MssqlIntegration")]
public sealed class Given_SqlServerPinnedImageConnectorTemplateFixture
    : Given_PinnedImageConnectorTemplateFixture
{
    protected override CdcProvider Provider => CdcProvider.SqlServer;
}
