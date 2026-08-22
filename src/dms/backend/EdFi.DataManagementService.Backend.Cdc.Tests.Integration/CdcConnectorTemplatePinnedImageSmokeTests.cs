// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

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

        CdcConnectorTemplateRequest request = fixture.BuildRequest();
        await fixture.CreateMinimalTopicsAndProviderObjectsAsync(request, cancellation.Token);
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

        CdcConnectorTemplateRequest request = fixture.BuildRequest();
        await fixture.CreateMinimalTopicsAndProviderObjectsAsync(request, cancellation.Token);
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

        CdcConnectorTemplateResult rendered = fixture.Render(fixture.BuildRequest());

        using var _ = new AssertionScope();
        rendered.Config["database.password"].Should().Be("${env:CDC_DATABASE_PASSWORD}");
        rendered
            .Config.Any(property => string.Equals(property.Value, "EdFi_Dms1!", StringComparison.Ordinal))
            .Should()
            .BeFalse("rendered connector configs must not contain the raw provider password");
    }

    [Test]
    public async Task It_exposes_reusable_render_preflight_and_live_validation_assertions()
    {
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            CdcConnectorTemplatePinnedImageFixture.CreateOffline(Provider);

        CdcConnectorTemplateRequest request = CdcConnectorTemplatePinnedImageFixture.BuildRequest(
            Provider,
            "broker:9092",
            OfflineProviderConnectionProperties(Provider)
        );
        CdcConnectorTemplateResult rendered = fixture.Render(request);

        using var _ = new AssertionScope();
        rendered.RegistrationPayload.Should().NotBeNull();
        rendered
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        rendered
            .Config.Keys.Should()
            .NotContain(key => key.Contains("offset.storage", StringComparison.Ordinal));
        rendered
            .Config["table.include.list"]
            .Should()
            .Be(@"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
        rendered
            .Config["message.key.columns"]
            .Should()
            .Be(@"dms\.DocumentCache:DocumentUuid;dms\.Document:DocumentUuid");
        if (Provider == CdcProvider.SqlServer)
        {
            rendered
                .Config["heartbeat.action.query"]
                .Should()
                .Be(
                    "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1"
                );
        }

        fixture.AssertRenderedTemplateCanBeValidatedFromReadBack(
            request,
            rendered.Config,
            BuildOfflineSourcePartitionEvidence(request)
        );
    }

    private static CdcConnectorTemplateSourcePartitionEvidence BuildOfflineSourcePartitionEvidence(
        CdcConnectorTemplateRequest request
    )
    {
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["server"] = request.ConnectorName.Value,
        };

        if (request.Provider == CdcProvider.SqlServer)
        {
            properties["database"] = request.ProviderConnectionProperties.Properties["database.names"];
        }

        return new CdcConnectorTemplateSourcePartitionEvidence(properties);
    }

    private static IReadOnlyDictionary<string, string> OfflineProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.port"] = "5432",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
            },
            CdcProvider.SqlServer => new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };
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
            safeArtifactOrObjectName: new CdcSafeName("dms_binding_connector"),
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
        diagnostic.SafeArtifactOrObjectName.Should().Be(new CdcSafeName("dms_binding_connector"));
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
public sealed class Given_CdcConnectorProviderOffsetRetentionComparison
{
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

    private static bool RetainsOrAdvances(CdcProvider provider, string minimum, string observed) =>
        CdcConnectorTemplatePinnedImageFixture.CommittedSourceOffsetRetainsOrAdvances(
            provider,
            minimum,
            observed
        );

    private static string PostgresqlOffset(ulong lsnProc, string snapshot = "false") =>
        $@"{{""snapshot"":""{snapshot}"",""lsn_proc"":{lsnProc}}}";

    private static string SqlServerOffset(
        string commitLsn,
        string changeLsn,
        long eventSerialNo,
        string snapshot = "false"
    ) =>
        $@"{{""snapshot"":""{snapshot}"",""commit_lsn"":""{commitLsn}"",""change_lsn"":""{changeLsn}"",""event_serial_no"":{eventSerialNo}}}";
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
