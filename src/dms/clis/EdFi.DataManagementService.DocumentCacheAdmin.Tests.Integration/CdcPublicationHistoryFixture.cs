// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

/// <summary>
/// Fresh CREATE through published SchemaTools, never a cloned baseline or retrospective receipt.
/// CMS delivery is the existing stand-in; both executable compositions and history provider are real.
/// </summary>
internal sealed class CdcPublicationHistoryFixture(bool mssql) : IAsyncDisposable
{
    private static readonly Lazy<Task<string>> _packages = new(PublishAsync);
    private readonly string _database = $"cdc_history_{Guid.NewGuid():N}";
    private string _admin = string.Empty;
    private string _connection = string.Empty;
    private DocumentCacheAdminCliProcessHarness _harness = null!;
    private DocumentCacheAdminCliTarget _target = null!;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"cdc-history-{Guid.NewGuid():N}");
    public LocalCdcWorkflowJournalStore Store => new(Root);
    public CdcManagedProvisioningResult Receipt { get; private set; } = null!;
    public DocumentCacheAdminCliProcessHarness Harness => _harness;
    public string Fingerprint { get; private set; } = string.Empty;
    public string HistoryPath =>
        Directory
            .GetFiles(Path.Combine(Root, "source-history"), "*.json", SearchOption.AllDirectories)
            .Single();
    public string JournalPath =>
        Directory.GetFiles(Path.Combine(Root, "workflows"), "*.json", SearchOption.AllDirectories).Single();

    public async Task InitializeAsync()
    {
        string variable = mssql ? "ConnectionStrings__MssqlAdmin" : "ConnectionStrings__DatabaseConnection";
        _admin = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_admin))
        {
            Assert.Fail(
                $"CDC packaged-history qualification requires {variable}; missing prerequisites never skip."
            );
        }
        _connection = mssql
            ? new SqlConnectionStringBuilder(_admin)
            {
                InitialCatalog = _database,
                Pooling = false,
            }.ConnectionString
            : new NpgsqlConnectionStringBuilder(_admin)
            {
                Database = _database,
                Pooling = false,
            }.ConnectionString;
        _admin = mssql
            ? new SqlConnectionStringBuilder(_admin)
            {
                InitialCatalog = "master",
                Pooling = false,
            }.ConnectionString
            : new NpgsqlConnectionStringBuilder(_admin)
            {
                Database = "postgres",
                Pooling = false,
            }.ConnectionString;
        await using (DbConnection connection = Connection(_admin))
        {
            await connection.OpenAsync();
            if (mssql)
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))";
                Convert
                    .ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
                    .Should()
                    .BeGreaterThanOrEqualTo(17);
                command.CommandText =
                    "SELECT CONVERT(int,value_in_use) FROM sys.configurations WHERE name = 'nested triggers'";
                Convert
                    .ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
                    .Should()
                    .Be(1);
            }
        }
        string packages = await _packages.Value;
        var schemas = Directory.GetFiles(
            DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory,
            "ApiSchema.json",
            SearchOption.AllDirectories
        );
        var args = new List<string>
        {
            Path.Combine(packages, "SchemaTools", "api-schema-tools.dll"),
            "ddl",
            "provision",
            "--dialect",
            mssql ? "mssql" : "pgsql",
            "--connection-string",
            _connection,
            "--create-database",
            "--managed-state-path",
            Root,
            "--deployment-key",
            "history",
            "--data-store-id",
            "1",
            "--instance-key",
            "primary",
            "--schema",
        };
        args.AddRange(schemas);
        var result = await RunDotnetAsync(args, TimeSpan.FromMinutes(3));
        if (result.ExitCode != 0)
        {
            await TestContext.Error.WriteLineAsync(
                (result.StandardOutput + result.StandardError)
                    .Replace(_connection, "[connection]", StringComparison.Ordinal)
                    .Replace(_admin, "[admin]", StringComparison.Ordinal)
                    .Replace(_database, "[database]", StringComparison.Ordinal)
            );
        }
        result
            .ExitCode.Should()
            .Be(0, "published managed non-CDC provisioning must succeed (raw output withheld)");
        Receipt = JsonSerializer.Deserialize<CdcManagedProvisioningResult>(
            result.StandardOutput,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }
        )!;
        Receipt.CreationReceipt.Outcome.Should().Be(CdcDatabaseCreationOutcome.Created);
        await RefreshFingerprintAsync();
        Fingerprint.Should().Be(Receipt.PhysicalSourceFingerprint);
        await using (var session = await AcquireAsync())
        {
            (
                await session.ReadSourcePublicationHistoryAsync(
                    Receipt.Target,
                    Fingerprint,
                    CancellationToken.None
                )
            )
                .Transitions[^1]
                .Status.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
        }
        _target = DocumentCacheAdminCliTarget.ForManagedSource(mssql, _connection);
        _harness = await DocumentCacheAdminCliProcessHarness.CreateAsync(_target);
        _harness.PublishedAssemblyPath = Path.Combine(
            packages,
            "DocumentCacheAdmin",
            "dms-document-cache.dll"
        );
        _harness.ConfigurePublicationHistory(Root, "history");
    }

    public async Task RefreshFingerprintAsync()
    {
        IDocumentCachePhysicalSourceFingerprintReader reader = mssql
            ? new MssqlDocumentCachePhysicalSourceFingerprintReader(
                NullLogger<MssqlDocumentCachePhysicalSourceFingerprintReader>.Instance
            )
            : new PostgresqlDocumentCachePhysicalSourceFingerprintReader(
                NullLogger<PostgresqlDocumentCachePhysicalSourceFingerprintReader>.Instance
            );
        Fingerprint = (await reader.ReadFingerprintAsync(_connection, CancellationToken.None))
            .Fingerprint!
            .Value;
    }

    public Task<LocalCdcWorkflowJournalStore.Session> AcquireAsync() =>
        Store.AcquireAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), CancellationToken.None);

    public async Task ReserveAsync(LocalCdcWorkflowJournalStore.Session session) =>
        await session.RecordIntentAsync(
            Receipt.Target,
            Receipt.WorkflowId,
            Guid.NewGuid(),
            CdcWorkflowEffect.ReserveBinding,
            [],
            CancellationToken.None
        );

    public string[] Arguments(string command) =>
        [
            command,
            "--data-store-id",
            "1",
            "--confirm",
            command switch
            {
                "activate-offline" => "offlineActivation",
                "deactivate-offline" => "offlineDeactivation",
                _ => "internalCacheAheadRecovery",
            },
            "--offline-writer-admission",
            "closedAndDrained",
            "--expected-physical-source-fingerprint",
            Fingerprint,
            "--json",
            "--command-timeout-seconds",
            "60",
        ];

    public async Task PrepareAsync(string command)
    {
        await ExecuteAsync(
            """
            DELETE FROM dms."DocumentProjectionWork";
            DELETE FROM dms."DocumentCache";
            DELETE FROM dms."Document";
            """
        );
        string lifecycle = command == "activate-offline" ? "Disabled" : "Tracking";
        string clear = mssql ? "0" : "false";
        string set = mssql ? "1" : "true";
        string latch = command == "recover-cache-ahead" ? set : clear;
        await ExecuteAsync(
            $"""
            UPDATE dms."DocumentCacheState" SET "ProjectionLifecycleState"='{lifecycle}', "CacheAheadRecoveryRequired"={latch} WHERE "StateId"=1;
            """
        );
        // Seed all mutable inventories so rejection proves preservation of content, not just empty counts.
        string uuid = mssql ? "NEWID()" : "gen_random_uuid()";
        string now = mssql ? "SYSUTCDATETIME()" : "CURRENT_TIMESTAMP";
        string json = """{"seeded":true}""";
        await ExecuteAsync(
            $"""
            INSERT INTO dms."Document" ("DocumentUuid","ResourceKeyId","ContentVersion","ContentLastModifiedAt")
            SELECT {uuid},"ResourceKeyId",10,{now} FROM dms."ResourceKey" WHERE "ResourceName"='SchoolTypeDescriptor';
            INSERT INTO dms."Descriptor" ("DocumentId","ResourceKeyId","Namespace","CodeValue","ShortDescription","Discriminator","Uri","ContentVersion","ContentLastModifiedAt")
            SELECT "DocumentId","ResourceKeyId",'uri://ed-fi.org/SchoolTypeDescriptor','History','History','SchoolTypeDescriptor','uri://ed-fi.org/SchoolTypeDescriptor#History',"ContentVersion",{now} FROM dms."Document";
            DELETE FROM dms."DocumentProjectionWork";
            INSERT INTO dms."DocumentProjectionWork" ("DocumentId","RequiredContentVersion","FirstEnqueuedAt","LastEnqueuedAt")
            SELECT "DocumentId","ContentVersion",{now},{now} FROM dms."Document";
            INSERT INTO dms."DocumentCache" ("DocumentId","DocumentUuid","ProjectName","ResourceName","ResourceVersion","ContentVersion","StreamEtag","LastModifiedAt","DocumentJson","ComputedAt")
            SELECT d."DocumentId",d."DocumentUuid",r."ProjectName",r."ResourceName",r."ResourceVersion",d."ContentVersion"+1,'history-etag',d."ContentLastModifiedAt",'{json}',{now}
            FROM dms."Document" d JOIN dms."ResourceKey" r ON r."ResourceKeyId"=d."ResourceKeyId";
            """
        );
    }

    public string StateFilesSnapshot()
    {
        if (!Directory.Exists(Root))
        {
            return "{}";
        }
        return JsonSerializer.Serialize(
            Directory
                .GetFiles(Root, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "controller.lock")
                .Order(StringComparer.Ordinal)
                .ToDictionary(
                    path => Path.GetRelativePath(Root, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                )
        );
    }

    public async Task<string> SnapshotAsync()
    {
        List<string> tables = [];
        foreach (
            string table in new[]
            {
                "Document",
                "Descriptor",
                "DocumentCache",
                "DocumentProjectionWork",
                "DocumentCacheState",
                "DataStoreIdentity",
            }
        )
        {
            tables.Add(await QueryAsync($"SELECT * FROM dms.\"{table}\""));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tables))));
    }

    public async Task<string> QueryAsync(string sql)
    {
        await using DbConnection connection = Connection(_connection);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<JsonArray> rows = [];
        while (await reader.ReadAsync())
        {
            object[] values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(
                new JsonArray(
                    values
                        .Select(value => JsonSerializer.SerializeToNode(value is DBNull ? null : value))
                        .ToArray()
                )
            );
        }
        return JsonSerializer.Serialize(rows);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using DbConnection connection = Connection(_connection);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<DbConnection> HoldMutexAsync()
    {
        DbConnection connection = Connection(_connection);
        try
        {
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = mssql
                ? "EXEC sys.sp_getapplock @Resource='EdFi.DMS.DocumentProjection.Administration.v1', @LockMode='Exclusive', @LockOwner='Session';"
                : "SELECT pg_advisory_lock((811646948::bigint << 32) | (SELECT oid::bigint FROM pg_database WHERE datname=current_database()));";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<DbConnection> HoldCacheRowsAsync()
    {
        DbConnection connection = Connection(_connection);
        try
        {
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = mssql
                ? "BEGIN TRANSACTION; SELECT DocumentId FROM dms.DocumentCache WITH (UPDLOCK,HOLDLOCK);"
                : "BEGIN; SELECT \"DocumentId\" FROM dms.\"DocumentCache\" FOR UPDATE;";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<string> CacheDeleteWaitersAsync() =>
        QueryAsync(
            mssql
                ? "SELECT COUNT(*) FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.database_id=DB_ID() AND r.wait_type LIKE 'LCK_M%' AND t.text LIKE '%DELETE target%' AND t.text LIKE '%DocumentCache%'"
                : "SELECT COUNT(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%DELETE FROM%' AND query LIKE '%DocumentCache%'"
        );

    public Task<string> MutexWaitersAsync() =>
        QueryAsync(
            mssql
                ? "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION' AND resource_database_id=DB_ID() AND request_status='WAIT'"
                : "SELECT COUNT(*) FROM pg_locks WHERE locktype='advisory' AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND classid=811646948::oid AND NOT granted"
        );

    private DbConnection Connection(string connectionString) =>
        mssql ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
        if (_target is not null)
        {
            await _target.DisposeAsync();
        }
        try
        {
            if (_admin.Length > 0)
            {
                await using DbConnection connection = Connection(_admin);
                await connection.OpenAsync();
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = mssql
                    ? $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END"
                    : $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE);";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }

    private static async Task<string> PublishAsync()
    {
        string root = FixturePathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        string output = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "cdc-history-packages",
            Guid.NewGuid().ToString("N")
        );
        foreach (string name in new[] { "SchemaTools", "DocumentCacheAdmin" })
        {
            var result = await RunDotnetAsync(
                [
                    "publish",
                    Path.Combine(
                        root,
                        "src",
                        "dms",
                        "clis",
                        $"EdFi.DataManagementService.{name}",
                        $"EdFi.DataManagementService.{name}.csproj"
                    ),
                    "-c",
                    "Release",
                    "-o",
                    Path.Combine(output, name),
                    "--nologo",
                ],
                TimeSpan.FromMinutes(5)
            );
            result
                .ExitCode.Should()
                .Be(
                    0,
                    "both production CLIs must publish successfully: {0}",
                    result.StandardOutput + result.StandardError
                );
        }
        return output;
    }

    internal static async Task<DocumentCacheAdminCliProcessResult> RunDotnetAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout
    )
    {
        using Process process = new()
        {
            StartInfo = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (
            string key in process
                .StartInfo.Environment.Keys.Where(key =>
                    key.StartsWith("Cdc__", StringComparison.OrdinalIgnoreCase)
                )
                .ToArray()
        )
        {
            process.StartInfo.Environment.Remove(key);
        }
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
        return new(process.ExitCode, await stdout, await stderr);
    }
}
