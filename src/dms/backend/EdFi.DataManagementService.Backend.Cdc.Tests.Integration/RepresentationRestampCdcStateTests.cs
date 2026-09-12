// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.DocumentCacheAdmin;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

public abstract class Given_RepresentationRestampCdcStateTests
{
    private const string ExpectedTopic = "edfi.documents.instance.binding-g7.documents.v1";
    private const string ExpectedKey = "9622f938-2c1a-4f99-9bc4-10970b1c2649";

    private protected abstract CdcStateProviderOperations ProviderOperations { get; }

    [Test]
    public async Task It_publishes_a_real_tracking_restamp_after_projector_drain()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using RepresentationRestampCdcStateFixture fixture =
            await RepresentationRestampCdcStateFixture.StartAsync(ProviderOperations, cancellation.Token);

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

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("CdcConnectorTemplateSmoke")]
[Category("PostgresqlIntegration")]
public sealed class Given_PostgresqlRepresentationRestampCdcStateTests
    : Given_RepresentationRestampCdcStateTests
{
    private protected override CdcStateProviderOperations ProviderOperations { get; } =
        new PostgresqlCdcStateProviderOperations();
}

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("CdcConnectorTemplateSmoke")]
[Category("MssqlIntegration")]
public sealed class Given_SqlServerRepresentationRestampCdcStateTests
    : Given_RepresentationRestampCdcStateTests
{
    private protected override CdcStateProviderOperations ProviderOperations { get; } =
        new SqlServerCdcStateProviderOperations();
}

internal sealed record CdcStateRecord(string Topic, string Key, JsonElement Value);

internal abstract class CdcStateProviderOperations(
    CdcProvider provider,
    RelationalProviderToken providerToken,
    string appSettingsDatastore
)
{
    public CdcProvider Provider { get; } = provider;

    public RelationalProviderToken ProviderToken { get; } = providerToken;

    public string AppSettingsDatastore { get; } = appSettingsDatastore;

    public abstract string FullDdl { get; }

    public abstract string ProviderAdminConnectionString(int providerPort, string databaseName);

    public abstract DbConnection OpenConnection(string connectionString);

    public abstract DbParameter Parameter(string parameterName, object value);

    public abstract DocumentCacheAdminCliTarget CreateCliTarget(
        string connectionString,
        long dataStoreId,
        string apiSchemaDirectory
    );

    public abstract void AddDocumentCacheRuntimeServices(
        IServiceCollection services,
        IConfiguration configuration
    );

    public abstract Task ResetDatabaseAsync(
        int providerPort,
        string databaseName,
        CancellationToken cancellationToken
    );

    public abstract IEnumerable<string> SplitDdlBatches(string ddl);

    public abstract string SetTrackingLifecycleSql { get; }

    public abstract string UpsertSourceIdentitySql { get; }

    public abstract string SeedCanonicalDescriptorSql { get; }

    public abstract string ReadCanonicalContentVersionSql { get; }

    public abstract string ReadRequiredContentVersionSql { get; }

    public abstract string ReadProjectionWorkCountSql { get; }

    public abstract string ReadCacheContentVersionSql { get; }

    public object TimestampValue(DateTimeOffset value) =>
        Provider == CdcProvider.SqlServer ? value.UtcDateTime : value;
}

internal sealed class PostgresqlCdcStateProviderOperations()
    : CdcStateProviderOperations(
        CdcProvider.Postgresql,
        RelationalProviderToken.Postgresql,
        RelationalProviderToken.Postgresql.Value
    )
{
    public override string FullDdl => DocumentCacheAdminCliFixture.Shared.PostgresqlDdl;

    public override string ProviderAdminConnectionString(int providerPort, string databaseName) =>
        $"Host=127.0.0.1;Port={providerPort};Username=postgres;Password={CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword};Database={databaseName}";

    public override DbConnection OpenConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);

    public override DbParameter Parameter(string parameterName, object value) =>
        new NpgsqlParameter(parameterName, value);

    public override DocumentCacheAdminCliTarget CreateCliTarget(
        string connectionString,
        long dataStoreId,
        string apiSchemaDirectory
    ) =>
        DocumentCacheAdminCliTarget.CreateExternalPostgresql(
            connectionString,
            dataStoreId,
            apiSchemaDirectory
        );

    public override void AddDocumentCacheRuntimeServices(
        IServiceCollection services,
        IConfiguration configuration
    ) => services.AddPostgresqlDocumentCacheRuntimeServices(configuration);

    public override async Task ResetDatabaseAsync(
        int providerPort,
        string databaseName,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = OpenConnection(
            ProviderAdminConnectionString(providerPort, databaseName)
        );
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "DROP SCHEMA IF EXISTS \"dms\" CASCADE;", cancellationToken);
    }

    public override IEnumerable<string> SplitDdlBatches(string ddl) => [ddl];

    public override string SetTrackingLifecycleSql =>
        """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Tracking',
                "CacheAheadRecoveryRequired" = false
            WHERE "StateId" = 1;
            """;

    public override string UpsertSourceIdentitySql =>
        $$"""
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
            VALUES (1, '{{CdcConnectorTemplatePinnedImageTestData.SourceIdentity}}')
            ON CONFLICT ("DataStoreIdentitySingletonId") DO UPDATE
            SET "SourceIdentity" = EXCLUDED."SourceIdentity";
            """;

    public override string SeedCanonicalDescriptorSql =>
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
            """;

    public override string ReadCanonicalContentVersionSql =>
        """SELECT "ContentVersion" FROM "dms"."Document" WHERE "DocumentId" = @documentId;""";

    public override string ReadRequiredContentVersionSql =>
        """SELECT "RequiredContentVersion" FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @documentId;""";

    public override string ReadProjectionWorkCountSql =>
        """SELECT COUNT(*) FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @documentId;""";

    public override string ReadCacheContentVersionSql =>
        """SELECT "ContentVersion" FROM "dms"."DocumentCache" WHERE "DocumentId" = @documentId;""";

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal sealed class SqlServerCdcStateProviderOperations()
    : CdcStateProviderOperations(
        CdcProvider.SqlServer,
        RelationalProviderToken.SqlServer,
        DocumentCacheAdminCommandSurface.MssqlAppSettingsDatastoreValue
    )
{
    public override string FullDdl => DocumentCacheAdminCliFixture.Shared.MssqlDdl;

    public override string ProviderAdminConnectionString(int providerPort, string databaseName) =>
        $"Server=127.0.0.1,{providerPort};Database={databaseName};User Id=sa;Password={CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword};Encrypt=True;TrustServerCertificate=True";

    public override DbConnection OpenConnection(string connectionString) =>
        new SqlConnection(connectionString);

    public override DbParameter Parameter(string parameterName, object value) =>
        new SqlParameter(parameterName, value);

    public override DocumentCacheAdminCliTarget CreateCliTarget(
        string connectionString,
        long dataStoreId,
        string apiSchemaDirectory
    ) => DocumentCacheAdminCliTarget.CreateExternalMssql(connectionString, dataStoreId, apiSchemaDirectory);

    public override void AddDocumentCacheRuntimeServices(
        IServiceCollection services,
        IConfiguration configuration
    ) => services.AddMssqlDocumentCacheRuntimeServices(configuration);

    public override async Task ResetDatabaseAsync(
        int providerPort,
        string databaseName,
        CancellationToken cancellationToken
    )
    {
        await using DbConnection connection = OpenConnection(
            ProviderAdminConnectionString(providerPort, "master")
        );
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END;
            """,
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON; ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON;",
            cancellationToken
        );
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override IEnumerable<string> SplitDdlBatches(string ddl)
    {
        List<string> batches = [];
        List<string> current = [];
        foreach (string line in ddl.Split('\n'))
        {
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                string batch = string.Join('\n', current);
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    batches.Add(batch);
                }
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        string finalBatch = string.Join('\n', current);
        if (!string.IsNullOrWhiteSpace(finalBatch))
        {
            batches.Add(finalBatch);
        }

        return batches;
    }

    public override string SetTrackingLifecycleSql =>
        """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = 'Tracking',
                [CacheAheadRecoveryRequired] = 0
            WHERE [StateId] = 1;
            """;

    public override string UpsertSourceIdentitySql =>
        $$"""
            UPDATE [dms].[DataStoreIdentity]
            SET [SourceIdentity] = '{{CdcConnectorTemplatePinnedImageTestData.SourceIdentity}}'
            WHERE [DataStoreIdentitySingletonId] = 1;
            """;

    public override string SeedCanonicalDescriptorSql =>
        """
            DECLARE @inserted_document TABLE
            (
                [DocumentId] bigint NOT NULL,
                [ResourceKeyId] smallint NOT NULL,
                [ContentVersion] bigint NOT NULL
            );

            WITH [resource_key] AS (
                SELECT [ResourceKeyId]
                FROM [dms].[ResourceKey]
                WHERE [ProjectName] = N'Ed-Fi'
                  AND [ResourceName] = N'SchoolTypeDescriptor'
            )
            INSERT INTO [dms].[Document] (
                [DocumentUuid], [ResourceKeyId], [ContentLastModifiedAt]
            )
            OUTPUT inserted.[DocumentId], inserted.[ResourceKeyId], inserted.[ContentVersion]
            INTO @inserted_document
            SELECT @documentUuid, [resource_key].[ResourceKeyId], @observedAt
            FROM [resource_key];

            INSERT INTO [dms].[Descriptor] (
                [DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription],
                [Discriminator], [Uri], [ContentVersion], [ContentLastModifiedAt]
            )
            SELECT
                [DocumentId], [ResourceKeyId], @namespace, @codeValue, @shortDescription,
                N'SchoolTypeDescriptor', @uri, [ContentVersion], @observedAt
            FROM @inserted_document;

            SELECT [DocumentId]
            FROM @inserted_document;
            """;

    public override string ReadCanonicalContentVersionSql =>
        """SELECT [ContentVersion] FROM [dms].[Document] WHERE [DocumentId] = @documentId;""";

    public override string ReadRequiredContentVersionSql =>
        """SELECT [RequiredContentVersion] FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId;""";

    public override string ReadProjectionWorkCountSql =>
        """SELECT COUNT(*) FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId;""";

    public override string ReadCacheContentVersionSql =>
        """SELECT [ContentVersion] FROM [dms].[DocumentCache] WHERE [DocumentId] = @documentId;""";
}

internal sealed class RepresentationRestampCdcStateFixture : IAsyncDisposable
{
    private const string DatabaseName = "edfi_datastore";
    private const string DocumentUuid = "9622f938-2c1a-4f99-9bc4-10970b1c2649";
    private const long TargetDataStoreId = 1;

    private readonly CdcStateProviderOperations _providerOperations;
    private readonly CdcConnectorTemplatePinnedImageFixture _pinnedFixture;
    private readonly CdcConnectorTemplateRequest _request;
    private readonly DockerCli _docker;
    private readonly string _brokerContainerName;

    private RepresentationRestampCdcStateFixture(
        CdcStateProviderOperations providerOperations,
        CdcConnectorTemplatePinnedImageFixture pinnedFixture,
        CdcConnectorTemplateRequest request,
        DockerCli docker
    )
    {
        _providerOperations = providerOperations;
        _pinnedFixture = pinnedFixture;
        _request = request;
        _docker = docker;
        _brokerContainerName = pinnedFixture.KafkaBootstrapServers.Split(':', 2)[0];
    }

    public static async Task<RepresentationRestampCdcStateFixture> StartAsync(
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        CdcConnectorTemplatePinnedImageFixture pinnedFixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                providerOperations.Provider,
                cancellationToken
            );

        try
        {
            int providerPort = await ReadMappedProviderPortAsync(
                pinnedFixture,
                providerOperations,
                cancellationToken
            );
            await ProvisionGeneratedDmsSchemaAsync(providerPort, providerOperations, cancellationToken);
            CdcConnectorTemplateRequest request = await pinnedFixture.CreateRequestAsync(
                cancellationToken,
                generatedSchema: true
            );
            var fixture = new RepresentationRestampCdcStateFixture(
                providerOperations,
                pinnedFixture,
                request,
                new DockerCli()
            );

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
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        await providerOperations.ResetDatabaseAsync(providerPort, DatabaseName, cancellationToken);
        await using DbConnection connection = providerOperations.OpenConnection(
            providerOperations.ProviderAdminConnectionString(providerPort, DatabaseName)
        );
        await connection.OpenAsync(cancellationToken);
        await ExecuteSqlScriptAsync(
            connection,
            providerOperations.FullDdl,
            providerOperations,
            cancellationToken
        );
        await ExecuteSqlAsync(connection, providerOperations.SetTrackingLifecycleSql, cancellationToken);
        await ExecuteSqlAsync(connection, providerOperations.UpsertSourceIdentitySql, cancellationToken);
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
        await using DbConnection connection = _providerOperations.OpenConnection(
            await ConnectionStringAsync(cancellationToken)
        );
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = _providerOperations.SeedCanonicalDescriptorSql;
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        command.Parameters.Add(_providerOperations.Parameter("documentUuid", Guid.Parse(DocumentUuid)));
        command.Parameters.Add(
            _providerOperations.Parameter("observedAt", _providerOperations.TimestampValue(observedAt))
        );
        command.Parameters.Add(
            _providerOperations.Parameter("namespace", "uri://ed-fi.org/SchoolTypeDescriptor")
        );
        command.Parameters.Add(_providerOperations.Parameter("codeValue", "RestampCdc"));
        command.Parameters.Add(_providerOperations.Parameter("shortDescription", "Restamp CDC"));
        command.Parameters.Add(
            _providerOperations.Parameter("uri", "uri://ed-fi.org/SchoolTypeDescriptor#RestampCdc")
        );

        long documentId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        long contentVersion = await ReadCanonicalContentVersionAsync(documentId, cancellationToken);
        return new SeededDocument(documentId, Guid.Parse(DocumentUuid), contentVersion);
    }

    private async Task ExecuteTrackingRestampAsync(Guid documentUuid, CancellationToken cancellationToken)
    {
        var target = _providerOperations.CreateCliTarget(
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
                    ["AppSettings:Datastore"] = _providerOperations.AppSettingsDatastore,
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
        services.AddSingleton<IDocumentLinkSlugResolver, DescriptorOnlySlugResolver>();
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
        _providerOperations.AddDocumentCacheRuntimeServices(services, configuration);
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
            new DocumentCacheTargetDataStoreMetadata(
                TargetDataStoreId,
                _providerOperations.AppSettingsDatastore
            ),
            new DocumentCacheTargetConnectionInput(_providerOperations.ProviderToken, connectionString),
            new DocumentCachePhysicalSourceFingerprint(
                CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(_providerOperations.Provider).Value
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
        _providerOperations.ProviderAdminConnectionString(
            await ReadMappedProviderPortAsync(_pinnedFixture, _providerOperations, cancellationToken),
            DatabaseName
        );

    private static async Task<int> ReadMappedProviderPortAsync(
        CdcConnectorTemplatePinnedImageFixture pinnedFixture,
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        string providerContainerName =
            $"{pinnedFixture.KafkaBootstrapServers.Split(':', 2)[0][..^"-broker".Length]}-provider";
        string containerPort =
            providerOperations.Provider == CdcProvider.Postgresql ? "5432/tcp" : "1433/tcp";
        DockerCommandResult result = await new DockerCli().RunAsync(
            ["port", providerContainerName, containerPort],
            cancellationToken
        );
        string endpoint = result.StandardOutput.Trim();
        return int.Parse(
            endpoint[(endpoint.LastIndexOf(':') + 1)..],
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    private static async Task ExecuteSqlScriptAsync(
        DbConnection connection,
        string sql,
        CdcStateProviderOperations providerOperations,
        CancellationToken cancellationToken
    )
    {
        foreach (string batch in providerOperations.SplitDdlBatches(sql))
        {
            await ExecuteSqlAsync(connection, batch, cancellationToken);
        }
    }

    private static async Task ExecuteSqlAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> ReadCanonicalContentVersionAsync(
        long documentId,
        CancellationToken cancellationToken
    ) =>
        await ReadScalarAsync(
            _providerOperations.ReadCanonicalContentVersionSql,
            documentId,
            cancellationToken
        );

    private async Task<long> ReadRequiredContentVersionAsync(
        long documentId,
        CancellationToken cancellationToken
    ) =>
        await ReadScalarAsync(
            _providerOperations.ReadRequiredContentVersionSql,
            documentId,
            cancellationToken
        );

    private async Task<long> ReadProjectionWorkCountAsync(
        long documentId,
        CancellationToken cancellationToken
    ) => await ReadScalarAsync(_providerOperations.ReadProjectionWorkCountSql, documentId, cancellationToken);

    private async Task<long> ReadCacheContentVersionAsync(
        long documentId,
        CancellationToken cancellationToken
    ) => await ReadScalarAsync(_providerOperations.ReadCacheContentVersionSql, documentId, cancellationToken);

    private async Task<long> ReadScalarAsync(string sql, long documentId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = _providerOperations.OpenConnection(
            await ConnectionStringAsync(cancellationToken)
        );
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(_providerOperations.Parameter("documentId", documentId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed class DescriptorOnlySlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            throw new InvalidOperationException(
                "The descriptor restamp fixture must not emit resource links."
            );
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
