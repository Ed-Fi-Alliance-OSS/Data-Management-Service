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
        CdcConnectorTemplateResult rendered = fixture.Render(request);

        await fixture.CreateMinimalTopicsAndProviderObjectsAsync(request, cancellation.Token);
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
        CdcConnectorTemplateResult rendered = fixture.Render(request);

        await fixture.CreateMinimalTopicsAndProviderObjectsAsync(request, cancellation.Token);
        await fixture.AssertConnectorConfigValidatesAsync(rendered, cancellation.Token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(rendered, cancellation.Token);
        await fixture.AssertHeartbeatAndCommittedOffsetProgressAsync(request, cancellation.Token);
        await fixture.RestartRegisteredConnectorAndAssertTemplateStillValidAsync(request, cancellation.Token);
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
        rendered.Config["table.include.list"].Should().Be("dms.DocumentCache,dms.Document,dms.CdcHeartbeat");
        rendered
            .Config["message.key.columns"]
            .Should()
            .Be("dms.DocumentCache:DocumentUuid;dms.Document:DocumentUuid");

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
