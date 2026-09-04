// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("CdcConnectorTemplateSmoke")]
[Category("PostgresqlIntegration")]
public sealed class Given_RepresentationRestampCdcStateTests
{
    private const string ExpectedTopic = "edfi.documents.instance.binding-g7.documents.v1";
    private const string ExpectedKey = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    [Test]
    public async Task It_preserves_the_kafka_v1_shape_with_a_higher_content_version()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using RepresentationRestampCdcStateFixture fixture =
            await RepresentationRestampCdcStateFixture.StartAsync(cancellation.Token);

        (CdcStateRecord original, CdcStateRecord restamped) = await fixture.CaptureAsync(cancellation.Token);

        using var _ = new AssertionScope();
        original.Topic.Should().Be(ExpectedTopic);
        restamped.Topic.Should().Be(ExpectedTopic);
        original.Key.Should().Be(ExpectedKey);
        restamped.Key.Should().Be(ExpectedKey);
        AssertV1Shape(original.Value);
        AssertV1Shape(restamped.Value);
        original.Value.GetProperty("contentVersion").GetInt64().Should().Be(123456);
        restamped.Value.GetProperty("contentVersion").GetInt64().Should().Be(123457);
        restamped
            .Value.GetProperty("contentVersion")
            .GetInt64()
            .Should()
            .BeGreaterThan(original.Value.GetProperty("contentVersion").GetInt64());
        restamped.Value.GetProperty("documentUuid").GetString().Should().Be(ExpectedKey);
        original.Value.GetProperty("projectName").GetString().Should().Be("EdFi");
        restamped.Value.GetProperty("projectName").GetString().Should().Be("EdFi");
        original.Value.GetProperty("resourceName").GetString().Should().Be("Student");
        restamped.Value.GetProperty("resourceName").GetString().Should().Be("Student");
        original.Value.GetProperty("resourceVersion").GetString().Should().Be("5.2.0");
        restamped.Value.GetProperty("resourceVersion").GetString().Should().Be("5.2.0");
        restamped
            .Value.GetProperty("document")
            .GetProperty("studentUniqueId")
            .GetString()
            .Should()
            .Be(original.Value.GetProperty("document").GetProperty("studentUniqueId").GetString());
    }

    private static void AssertV1Shape(JsonElement value)
    {
        value.ValueKind.Should().Be(JsonValueKind.Object);
        value
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(
                "contractVersion",
                "documentUuid",
                "projectName",
                "resourceName",
                "resourceVersion",
                "contentVersion",
                "lastModifiedAt",
                "document"
            );
        value.GetProperty("contractVersion").ValueKind.Should().Be(JsonValueKind.Number);
        value.GetProperty("contractVersion").GetInt32().Should().Be(1);
        value.GetProperty("documentUuid").ValueKind.Should().Be(JsonValueKind.String);
        value.GetProperty("projectName").ValueKind.Should().Be(JsonValueKind.String);
        value.GetProperty("resourceName").ValueKind.Should().Be(JsonValueKind.String);
        value.GetProperty("resourceVersion").ValueKind.Should().Be(JsonValueKind.String);
        value.GetProperty("contentVersion").ValueKind.Should().Be(JsonValueKind.Number);
        value.GetProperty("lastModifiedAt").ValueKind.Should().Be(JsonValueKind.String);
        value.GetProperty("document").ValueKind.Should().Be(JsonValueKind.Object);
        value
            .GetProperty("document")
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("id", "_etag", "_lastModifiedDate", "studentUniqueId");
    }
}

internal sealed record CdcStateRecord(string Topic, string Key, JsonElement Value);

internal sealed class RepresentationRestampCdcStateFixture : IAsyncDisposable
{
    private const string DatabaseName = "edfi_datastore";
    private const string DatabaseUser = "postgres";
    private const string DocumentUuid = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private const long OriginalContentVersion = 123456;
    private const long RestampedContentVersion = 123457;
    private const string OriginalTimestamp = "2026-07-06T15:30:45Z";
    private const string RestampedTimestamp = "2026-07-06T15:31:45Z";

    private readonly CdcConnectorTemplatePinnedImageFixture _pinnedFixture;
    private readonly CdcConnectorTemplateRequest _request;
    private readonly DockerCli _docker;
    private readonly string _brokerContainerName;
    private readonly string _providerContainerName;

    private RepresentationRestampCdcStateFixture(
        CdcConnectorTemplatePinnedImageFixture pinnedFixture,
        CdcConnectorTemplateRequest request,
        DockerCli docker
    )
    {
        _pinnedFixture = pinnedFixture;
        _request = request;
        _docker = docker;
        _brokerContainerName = pinnedFixture.KafkaBootstrapServers.Split(':', 2)[0];
        _providerContainerName = $"{_brokerContainerName[..^"-broker".Length]}-provider";
    }

    public static async Task<RepresentationRestampCdcStateFixture> StartAsync(
        CancellationToken cancellationToken
    )
    {
        CdcConnectorTemplatePinnedImageFixture pinnedFixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                cancellationToken
            );

        try
        {
            CdcConnectorTemplateRequest request = await pinnedFixture.CreateRequestAsync(cancellationToken);
            var fixture = new RepresentationRestampCdcStateFixture(pinnedFixture, request, new DockerCli());
            await fixture.PrepareDocumentCacheStateRecordSourceAsync(cancellationToken);

            CdcConnectorTemplateResult rendered = pinnedFixture.Render(request);
            await pinnedFixture.AssertConnectorConfigValidatesAsync(rendered, cancellationToken);
            await pinnedFixture.RegisterRenderedConnectorConfigDirectlyAsync(rendered, cancellationToken);
            await pinnedFixture.AssertRegisteredConnectorReachesRunningStateAsync(request, cancellationToken);
            return fixture;
        }
        catch
        {
            await pinnedFixture.DisposeAsync();
            throw;
        }
    }

    public async Task<(CdcStateRecord Original, CdcStateRecord Restamped)> CaptureAsync(
        CancellationToken cancellationToken
    )
    {
        await ExecutePostgresqlAsync(InsertOriginalStateSql(), cancellationToken);
        CdcStateRecord original = (await ConsumeRecordsAsync(1, cancellationToken))[0];

        await ExecutePostgresqlAsync(UpdateRestampedStateSql(), cancellationToken);
        IReadOnlyList<CdcStateRecord> records = await ConsumeRecordsAsync(2, cancellationToken);
        return (original, records[^1]);
    }

    public async ValueTask DisposeAsync() => await _pinnedFixture.DisposeAsync();

    private async Task PrepareDocumentCacheStateRecordSourceAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE "dms"."DocumentCache"
                ALTER COLUMN "DocumentUuid" TYPE uuid USING "DocumentUuid"::uuid,
                ADD COLUMN IF NOT EXISTS "DocumentId" bigint,
                ADD COLUMN IF NOT EXISTS "ProjectName" text,
                ADD COLUMN IF NOT EXISTS "ResourceName" text,
                ADD COLUMN IF NOT EXISTS "ResourceVersion" text,
                ADD COLUMN IF NOT EXISTS "ContentVersion" bigint,
                ADD COLUMN IF NOT EXISTS "StreamEtag" text,
                ADD COLUMN IF NOT EXISTS "LastModifiedAt" timestamptz,
                ADD COLUMN IF NOT EXISTS "DocumentJson" jsonb,
                ADD COLUMN IF NOT EXISTS "ComputedAt" timestamptz;
            """;

        await ExecutePostgresqlAsync(sql, cancellationToken);
    }

    private async Task<IReadOnlyList<CdcStateRecord>> ConsumeRecordsAsync(
        int expectedCount,
        CancellationToken cancellationToken
    )
    {
        DockerCommandResult result = await _docker.RunAsync(
            [
                "exec",
                _brokerContainerName,
                "rpk",
                "topic",
                "consume",
                _request.PublicTopicName,
                "--brokers",
                $"{_brokerContainerName}:9092",
                "--offset",
                "start",
                "--num",
                expectedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--format",
                "%t\t%k\t%v\n",
            ],
            cancellationToken
        );

        CdcStateRecord[] records = result
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseRecord)
            .ToArray();
        records.Should().HaveCount(expectedCount);
        return records;
    }

    private static CdcStateRecord ParseRecord(string line)
    {
        string[] fields = line.TrimEnd('\r').Split('\t', 3);
        fields.Should().HaveCount(3);
        using JsonDocument document = JsonDocument.Parse(fields[2]);
        return new CdcStateRecord(fields[0], fields[1], document.RootElement.Clone());
    }

    private async Task ExecutePostgresqlAsync(string sql, CancellationToken cancellationToken) =>
        _ = await _docker.RunAsync(
            [
                "exec",
                "-e",
                $"PGPASSWORD={CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}",
                _providerContainerName,
                "psql",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                DatabaseUser,
                "-d",
                DatabaseName,
                "-c",
                sql,
            ],
            cancellationToken
        );

    private static string InsertOriginalStateSql() =>
        $$"""
            INSERT INTO "dms"."DocumentCache"
            (
                "DocumentId", "DocumentUuid", "ProjectName", "ResourceName", "ResourceVersion",
                "ContentVersion", "StreamEtag", "LastModifiedAt", "DocumentJson", "ComputedAt"
            )
            VALUES
            (
                101, '{{DocumentUuid}}', 'EdFi', 'Student', '5.2.0',
                {{OriginalContentVersion}}, '{{OriginalContentVersion}}-a1b2c3d4.j._.l.i',
                '{{OriginalTimestamp}}',
                '{"id":"{{DocumentUuid}}","_lastModifiedDate":"{{OriginalTimestamp}}","studentUniqueId":"604822"}'::jsonb,
                '{{OriginalTimestamp}}'
            );
            """;

    private static string UpdateRestampedStateSql() =>
        $$"""
            UPDATE "dms"."DocumentCache"
            SET "ContentVersion" = {{RestampedContentVersion}},
                "StreamEtag" = '{{RestampedContentVersion}}-a1b2c3d4.j._.l.i',
                "LastModifiedAt" = '{{RestampedTimestamp}}',
                "DocumentJson" = '{"id":"{{DocumentUuid}}","_lastModifiedDate":"{{RestampedTimestamp}}","studentUniqueId":"604822"}'::jsonb,
                "ComputedAt" = '{{RestampedTimestamp}}'
            WHERE "DocumentUuid" = '{{DocumentUuid}}';
            """;
}
