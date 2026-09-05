// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
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
    private const string ExpectedKey = "9622f938-2c1a-4f99-9bc4-10970b1c2649";

    [Test]
    public async Task It_publishes_a_real_tracking_restamp_after_projector_drain()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using RepresentationRestampCdcStateFixture fixture =
            await RepresentationRestampCdcStateFixture.StartAsync(cancellation.Token);

        (CdcStateRecord original, CdcStateRecord restamped) = await fixture.CaptureRealRestampAsync(
            cancellation.Token
        );

        using var _ = new AssertionScope();
        original.Topic.Should().Be(ExpectedTopic);
        restamped.Topic.Should().Be(ExpectedTopic);
        original.Key.Should().Be(ExpectedKey);
        restamped.Key.Should().Be(ExpectedKey);
        AssertV1Shape(original.Value);
        AssertV1Shape(restamped.Value);
        original.Value.GetProperty("documentUuid").GetString().Should().Be(ExpectedKey);
        restamped.Value.GetProperty("documentUuid").GetString().Should().Be(ExpectedKey);
        original.Value.GetProperty("document").GetProperty("id").GetString().Should().Be(ExpectedKey);
        restamped.Value.GetProperty("document").GetProperty("id").GetString().Should().Be(ExpectedKey);
        original.Value.GetProperty("projectName").GetString().Should().Be("Ed-Fi");
        restamped.Value.GetProperty("projectName").GetString().Should().Be("Ed-Fi");
        original.Value.GetProperty("resourceName").GetString().Should().Be("SchoolTypeDescriptor");
        restamped.Value.GetProperty("resourceName").GetString().Should().Be("SchoolTypeDescriptor");
        original.Value.GetProperty("resourceVersion").GetString().Should().Be("5.0.0");
        restamped.Value.GetProperty("resourceVersion").GetString().Should().Be("5.0.0");
        restamped
            .Value.GetProperty("contentVersion")
            .GetInt64()
            .Should()
            .BeGreaterThan(original.Value.GetProperty("contentVersion").GetInt64());
        restamped
            .Value.GetProperty("document")
            .GetProperty("codeValue")
            .GetString()
            .Should()
            .Be("RestampCdc");
        AssertEtagMatchesContentVersion(original.Value);
        AssertEtagMatchesContentVersion(restamped.Value);
        restamped
            .Value.GetProperty("document")
            .GetProperty("_etag")
            .GetString()
            .Should()
            .NotBe(original.Value.GetProperty("document").GetProperty("_etag").GetString());
        DateTimeOffset
            .Parse(
                restamped.Value.GetProperty("lastModifiedAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture
            )
            .Should()
            .BeAfter(
                DateTimeOffset.Parse(
                    original.Value.GetProperty("lastModifiedAt").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
        DateTimeOffset
            .Parse(
                restamped.Value.GetProperty("document").GetProperty("_lastModifiedDate").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture
            )
            .Should()
            .BeAfter(
                DateTimeOffset.Parse(
                    original.Value.GetProperty("document").GetProperty("_lastModifiedDate").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
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
            .BeEquivalentTo("namespace", "codeValue", "shortDescription", "id", "_etag", "_lastModifiedDate");
    }

    private static void AssertEtagMatchesContentVersion(JsonElement value)
    {
        string etag = value.GetProperty("document").GetProperty("_etag").GetString()!;
        EtagValue.TryParseHeaderValue(etag, out string opaqueEtag).Should().BeTrue();
        EtagValue.TryParse(opaqueEtag, out string encodedContentVersion, out _).Should().BeTrue();
        encodedContentVersion
            .Should()
            .Be(
                value
                    .GetProperty("contentVersion")
                    .GetInt64()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
    }
}

internal sealed record CdcStateRecord(string Topic, string Key, JsonElement Value);

internal sealed class RepresentationRestampCdcStateFixture : IAsyncDisposable
{
    private const string DatabaseName = "edfi_datastore";
    private const string DatabaseUser = "postgres";
    private const string DocumentUuid = "9622f938-2c1a-4f99-9bc4-10970b1c2649";
    private const long TargetDataStoreId = 1;

    private readonly CdcConnectorTemplatePinnedImageFixture _pinnedFixture;
    private readonly CdcConnectorTemplateRequest _request;
    private readonly DockerCli _docker;
    private readonly string _brokerContainerName;

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
            int providerPort = await ReadMappedProviderPortAsync(pinnedFixture, cancellationToken);
            await ProvisionGeneratedDmsSchemaAsync(providerPort, cancellationToken);
            CdcConnectorTemplateRequest request = await pinnedFixture.CreateRequestAsync(cancellationToken);
            var fixture = new RepresentationRestampCdcStateFixture(pinnedFixture, request, new DockerCli());

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

    public async Task<(CdcStateRecord Original, CdcStateRecord Restamped)> CaptureRealRestampAsync(
        CancellationToken cancellationToken
    )
    {
        SeededDocument source = await SeedCanonicalDescriptorAsync(cancellationToken);
        await DrainOrdinaryProjectorAsync(cancellationToken);
        CdcStateRecord original = (await ConsumeRecordsAsync(1, cancellationToken))[0];
        original.Value.GetProperty("contentVersion").GetInt64().Should().Be(source.ContentVersion);

        await ExecuteTrackingRestampAsync(source.Uuid, cancellationToken);
        long canonicalVersion = await ReadCanonicalContentVersionAsync(source.DocumentId, cancellationToken);
        canonicalVersion.Should().BeGreaterThan(source.ContentVersion);
        (await ReadRequiredContentVersionAsync(source.DocumentId, cancellationToken))
            .Should()
            .Be(canonicalVersion);

        await DrainOrdinaryProjectorAsync(cancellationToken);
        (await ReadProjectionWorkCountAsync(source.DocumentId, cancellationToken)).Should().Be(0);
        (await ReadCacheContentVersionAsync(source.DocumentId, cancellationToken))
            .Should()
            .Be(canonicalVersion);

        IReadOnlyList<CdcStateRecord> records = await ConsumeRecordsAsync(2, cancellationToken);
        return (original, records[^1]);
    }

    public async ValueTask DisposeAsync() => await _pinnedFixture.DisposeAsync();

    private static async Task ProvisionGeneratedDmsSchemaAsync(
        int providerPort,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(ConnectionString(providerPort));
        await connection.OpenAsync(cancellationToken);
        await ExecuteSqlAsync(connection, "DROP SCHEMA IF EXISTS \"dms\" CASCADE;", cancellationToken);
        await ExecuteSqlAsync(
            connection,
            DocumentCacheAdminCliFixture.Shared.PostgresqlDdl,
            cancellationToken
        );
        await ExecuteSqlAsync(
            connection,
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Tracking',
                "CacheAheadRecoveryRequired" = false
            WHERE "StateId" = 1;
            """,
            cancellationToken
        );
        await ExecuteSqlAsync(
            connection,
            $$"""
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
            VALUES (1, '{{CdcConnectorTemplatePinnedImageTestData.SourceIdentity}}')
            ON CONFLICT ("DataStoreIdentitySingletonId") DO UPDATE
            SET "SourceIdentity" = EXCLUDED."SourceIdentity";
            """,
            cancellationToken
        );
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

    private async Task<SeededDocument> SeedCanonicalDescriptorAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            """
            WITH resource_key AS (
                SELECT "ResourceKeyId"
                FROM "dms"."ResourceKey"
                WHERE "ProjectName" = 'Ed-Fi'
                  AND "ResourceName" = 'SchoolTypeDescriptor'
            ),
            inserted_document AS (
                INSERT INTO "dms"."Document" (
                    "DocumentUuid", "ResourceKeyId", "ContentLastModifiedAt"
                )
                SELECT @documentUuid, resource_key."ResourceKeyId", @observedAt
                FROM resource_key
                RETURNING "DocumentId", "ResourceKeyId", "ContentVersion"
            )
            INSERT INTO "dms"."Descriptor" (
                "DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription",
                "Discriminator", "Uri", "ContentVersion", "ContentLastModifiedAt"
            )
            SELECT
                inserted_document."DocumentId", inserted_document."ResourceKeyId", @namespace,
                @codeValue, @shortDescription, 'SchoolTypeDescriptor', @uri,
                inserted_document."ContentVersion", @observedAt
            FROM inserted_document
            RETURNING "DocumentId";
            """,
            connection
        );
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        command.Parameters.Add(
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = Guid.Parse(DocumentUuid) }
        );
        command.Parameters.Add(
            new NpgsqlParameter("observedAt", NpgsqlDbType.TimestampTz) { Value = observedAt }
        );
        command.Parameters.Add(
            new NpgsqlParameter("namespace", NpgsqlDbType.Varchar)
            {
                Value = "uri://ed-fi.org/SchoolTypeDescriptor",
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter("codeValue", NpgsqlDbType.Varchar) { Value = "RestampCdc" }
        );
        command.Parameters.Add(
            new NpgsqlParameter("shortDescription", NpgsqlDbType.Varchar) { Value = "Restamp CDC" }
        );
        command.Parameters.Add(
            new NpgsqlParameter("uri", NpgsqlDbType.Varchar)
            {
                Value = "uri://ed-fi.org/SchoolTypeDescriptor#RestampCdc",
            }
        );

        long documentId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        long contentVersion = await ReadCanonicalContentVersionAsync(documentId, cancellationToken);
        return new SeededDocument(documentId, Guid.Parse(DocumentUuid), contentVersion);
    }

    private async Task ExecuteTrackingRestampAsync(Guid documentUuid, CancellationToken cancellationToken)
    {
        var target = DocumentCacheAdminCliTarget.CreateExternalPostgresql(
            await ConnectionStringAsync(cancellationToken),
            TargetDataStoreId,
            DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory
        );
        await using var harness = await DocumentCacheAdminCliProcessHarness.CreateAsync(target);
        DocumentCacheAdminCliProcessResult preview = await harness.RunAsync(
            "restamp-preview",
            "--data-store-id",
            TargetDataStoreId.ToString(),
            "--mode",
            "tracking",
            "--reason",
            "CDC restamp integration test",
            "--document-uuid",
            documentUuid.ToString(),
            "--offline-writer-admission",
            "closedAndDrained",
            "--json"
        );
        preview.ExitCode.Should().Be(0, preview.StandardError);
        Guid operationId = preview.ReadStandardOutputJsonObject()["result"]!["operationId"]!.GetValue<Guid>();

        DocumentCacheAdminCliProcessResult execute = await harness.RunAsync(
            "restamp-execute",
            "--data-store-id",
            TargetDataStoreId.ToString(),
            "--operation-id",
            operationId.ToString(),
            "--confirm",
            "representationRestamp",
            "--offline-writer-admission",
            "closedAndDrained",
            "--json"
        );
        execute.ExitCode.Should().Be(0, execute.StandardError);
        execute.ReadStandardOutputJsonObject()["result"]!["claimLevel"]!
            .GetValue<string>()
            .Should()
            .Be("projectionWorkQueued");
    }

    private async Task DrainOrdinaryProjectorAsync(CancellationToken cancellationToken)
    {
        string connectionString = await ConnectionStringAsync(cancellationToken);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = "postgresql",
                    ["AppSettings:UseApiSchemaPath"] = "true",
                    ["AppSettings:ApiSchemaPath"] = DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory,
                }
            )
            .Build();
        var services = new ServiceCollection();
        EffectiveSchemaSet effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory
        );
        services.AddLogging();
        services.AddSingleton<IEffectiveSchemaSetProvider>(
            new FixedEffectiveSchemaSetProvider(effectiveSchemaSet)
        );
        services.AddSingleton<IOptions<DocumentCacheOptions>>(Options.Create(new DocumentCacheOptions()));
        services.AddSingleton(
            new DeadlockRetrySettings
            {
                MaxRetryAttempts = 0,
                BaseDelayMilliseconds = 1,
                UseJitter = false,
            }
        );
        services.AddPostgresqlDocumentCacheRuntimeServices(configuration);
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create(string.Empty, TargetDataStoreId);
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                true,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(10),
                10,
                1,
                TimeSpan.FromSeconds(1),
                1000,
                TimeSpan.FromMinutes(1)
            ),
            new DocumentCacheTargetDataStoreMetadata(TargetDataStoreId, "postgresql"),
            new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, connectionString),
            new DocumentCachePhysicalSourceFingerprint(
                "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            ),
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
        await using DocumentCacheProjectionTargetRuntimeContext context = await serviceProvider
            .GetRequiredService<IDocumentCacheProjectionTargetRuntimeContextFactory>()
            .CreateAsync(executionContext, cancellationToken);
        IDocumentCacheProjectionDrainPageProcessor processor =
            serviceProvider.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();

        while (true)
        {
            DocumentCacheProjectionDrainPageResult result = await processor.ProcessPageAsync(
                new DocumentCacheProjectionDrainPageRequest(
                    context,
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                ),
                cancellationToken
            );
            if (result.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
            {
                return;
            }

            result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        }
    }

    private async Task<string> ConnectionStringAsync(CancellationToken cancellationToken) =>
        ConnectionString(await ReadMappedProviderPortAsync(_pinnedFixture, cancellationToken));

    private static string ConnectionString(int providerPort) =>
        $"Host=127.0.0.1;Port={providerPort};Username={DatabaseUser};Password={CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword};Database={DatabaseName}";

    private static async Task<int> ReadMappedProviderPortAsync(
        CdcConnectorTemplatePinnedImageFixture pinnedFixture,
        CancellationToken cancellationToken
    )
    {
        string providerContainerName =
            $"{pinnedFixture.KafkaBootstrapServers.Split(':', 2)[0][..^"-broker".Length]}-provider";
        DockerCommandResult result = await new DockerCli().RunAsync(
            ["port", providerContainerName, "5432/tcp"],
            cancellationToken
        );
        string endpoint = result.StandardOutput.Trim();
        return int.Parse(
            endpoint[(endpoint.LastIndexOf(':') + 1)..],
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    private static async Task ExecuteSqlAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> ReadCanonicalContentVersionAsync(
        long documentId,
        CancellationToken cancellationToken
    ) =>
        await ReadScalarAsync(
            "SELECT \"ContentVersion\" FROM \"dms\".\"Document\" WHERE \"DocumentId\" = @documentId;",
            documentId,
            cancellationToken
        );

    private async Task<long> ReadRequiredContentVersionAsync(
        long documentId,
        CancellationToken cancellationToken
    ) =>
        await ReadScalarAsync(
            "SELECT \"RequiredContentVersion\" FROM \"dms\".\"DocumentProjectionWork\" WHERE \"DocumentId\" = @documentId;",
            documentId,
            cancellationToken
        );

    private async Task<long> ReadProjectionWorkCountAsync(
        long documentId,
        CancellationToken cancellationToken
    ) =>
        await ReadScalarAsync(
            "SELECT COUNT(*) FROM \"dms\".\"DocumentProjectionWork\" WHERE \"DocumentId\" = @documentId;",
            documentId,
            cancellationToken
        );

    private async Task<long> ReadCacheContentVersionAsync(
        long documentId,
        CancellationToken cancellationToken
    ) =>
        await ReadScalarAsync(
            "SELECT \"ContentVersion\" FROM \"dms\".\"DocumentCache\" WHERE \"DocumentId\" = @documentId;",
            documentId,
            cancellationToken
        );

    private async Task<long> ReadScalarAsync(string sql, long documentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId });
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record SeededDocument(long DocumentId, Guid Uuid, long ContentVersion);

    private sealed class FixedEffectiveSchemaSetProvider(EffectiveSchemaSet effectiveSchemaSet)
        : IEffectiveSchemaSetProvider
    {
        public EffectiveSchemaSet EffectiveSchemaSet { get; } = effectiveSchemaSet;

        public bool IsInitialized => true;

        public void Initialize(EffectiveSchemaSet effectiveSchemaSet) => throw new NotSupportedException();
    }
}
