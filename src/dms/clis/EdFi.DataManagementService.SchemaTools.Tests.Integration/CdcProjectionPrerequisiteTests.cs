// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("CdcProjectionPrerequisite")]
public class Given_CdcProjectionPrerequisite_On_An_Isolated_SqlServer
{
    private string _database = null!;
    private string _connection = null!;
    private string _root = null!;
    private int _originalNestedTriggers;
    private static readonly DocumentCacheTargetKey Target = DocumentCacheTargetKey.Create("", 42);

    [SetUp]
    public void SetUp()
    {
        // This suite changes a SERVER setting. Never run against a shared integration server.
        if (Environment.GetEnvironmentVariable("DMS_CDC_ISOLATED_SQLSERVER") != "1")
        {
            Assert.Ignore(
                "Requires DMS_CDC_ISOLATED_SQLSERVER=1 and an exclusively owned SQL Server 2025 fixture."
            );
        }
        MssqlTestDatabaseHelper.IsConfigured().Should().BeTrue();
        Scalar("SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'))").Should().Be(17);
        _originalNestedTriggers = Scalar(
            "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'"
        );
        _database = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connection = MssqlTestDatabaseHelper.BuildConnectionString(_database);
        _root = Path.Combine(Path.GetTempPath(), "cdc-prerequisites-" + Guid.NewGuid().ToString("N"));
        Execute("EXEC sys.sp_configure N'nested triggers', 0; RECONFIGURE;");
        Scalar("SELECT CONVERT(int, is_read_committed_snapshot_on) FROM sys.databases WHERE name = 'model'")
            .Should()
            .Be(0);
    }

    [TearDown]
    public void TearDown()
    {
        if (_database is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_database);
            Execute($"EXEC sys.sp_configure N'nested triggers', {_originalNestedTriggers}; RECONFIGURE;");
        }
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private (int ExitCode, string Output, string Error) Provision(
        string mode = "owned-local-sql-server",
        string connectionOverride = ""
    ) =>
        CliTestHelper.RunCli(
            "ddl",
            "provision",
            "--schema",
            CliTestHelper.GetMinimalSchemaPath(),
            "--connection-string",
            connectionOverride.Length == 0 ? _connection : connectionOverride,
            "--dialect",
            "mssql",
            "--create-database",
            "--managed-state-path",
            _root,
            "--data-store-id",
            "42",
            "--instance-key",
            "datastore-42",
            "--cdc-projection-prerequisites",
            mode
        );

    [Test]
    public async Task It_prepares_then_initializes_E18_then_activates_then_captures()
    {
        var provisioned = Provision();
        provisioned.ExitCode.Should().Be(0, provisioned.Error);
        Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
            .Should()
            .Be(1);
        Scalar(
                "SELECT CONVERT(int, is_read_committed_snapshot_on) FROM sys.databases WHERE name = DB_NAME()",
                _connection
            )
            .Should()
            .Be(1);
        Scalar("SELECT CONVERT(int, is_cdc_enabled) FROM sys.databases WHERE name = DB_NAME()", _connection)
            .Should()
            .Be(0);
        var retry = Provision();
        retry.ExitCode.Should().Be(0, retry.Error);
        retry.Output.Should().Be(provisioned.Output);

        // Only schema-file and CMS delivery are stand-ins; E18 initialization, mapping compilation,
        // prerequisite reads, supervisor context, guarded command, mutex and SQL are production services.
        var runtime = CreateRuntime();
        var opened = await CdcProjectionRuntimeFactory.OpenAsync(runtime, Target, default);
        opened.State.Should().Be(CdcTransportEvidenceState.Observed);
        await using var projection = ((CdcTransportResult<ICdcProjectionRuntime>.Observed)opened).Value;
        var registry = runtime.GetRequiredService<IDocumentCacheTargetRegistry>();
        var context = registry.CurrentRuntimeSnapshot.GetExecutionContext(Target);
        context.Should().NotBeNull();
        context!.Lifecycle.State.Should().Be(DocumentCacheLifecycleState.Disabled);
        var activation = await projection.ActivateAsync(
            new(
                DocumentCacheAdministrativeTargetKey.FromTargetKey(Target),
                context.PhysicalSourceFingerprint,
                DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
            ),
            default
        );
        activation.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.Completed, "{0}", activation);
        activation.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
        await EnableCaptureAsync();
        Scalar("SELECT CONVERT(int, is_cdc_enabled) FROM sys.databases WHERE name = DB_NAME()", _connection)
            .Should()
            .Be(1);
        Scalar("SELECT COUNT(*) FROM cdc.change_tables", _connection).Should().Be(3);
    }

    [Test]
    public async Task It_requires_affirmative_external_prerequisites_and_rejects_before_E18_initialization()
    {
        var result = Provision("inspect");
        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
            .Should()
            .Be(0);
        Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'DocumentCacheState'", _connection)
            .Should()
            .Be(0);
        Scalar("SELECT CONVERT(int, is_cdc_enabled) FROM sys.databases WHERE name = DB_NAME()", _connection)
            .Should()
            .Be(0);
        var journal = await ReadJournalAsync();
        journal.Operations.Should().ContainSingle();
        journal.Operations[0].Completions.Should().ContainSingle();
        Provision().ExitCode.Should().Be(1, "missing source association cannot authorize retry repair");
        Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
            .Should()
            .Be(0);
    }

    [TestCase("nested triggers")]
    [TestCase("rcsi")]
    public async Task It_rejects_drift_on_retry_and_E18_initialization_without_repair(string setting)
    {
        Provision().ExitCode.Should().Be(0);
        if (setting == "nested triggers")
        {
            Execute("EXEC sys.sp_configure N'nested triggers', 0; RECONFIGURE;");
        }
        else
        {
            SqlConnection.ClearAllPools();
            Execute($"ALTER DATABASE [{_database}] SET READ_COMMITTED_SNAPSHOT OFF;");
        }
        var retry = Provision();
        retry.ExitCode.Should().Be(1);
        await using var runtime = CreateRuntime();
        await DocumentCacheRuntimeInitializer.InitializeAsync(runtime);
        var registry = runtime.GetRequiredService<IDocumentCacheTargetRegistry>();
        var snapshot = await registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        snapshot
            .Targets.Single()
            .Diagnostics.Should()
            .Contain(d => d.Category == DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
        registry.CurrentRuntimeSnapshot.GetExecutionContext(Target).Should().BeNull();
        var validator = new MssqlDocumentCacheProviderPrerequisiteValidator(
            NullLogger<MssqlDocumentCacheProviderPrerequisiteValidator>.Instance
        );
        (await validator.ValidateActivationPreflightAsync(_connection)).IsSatisfied.Should().BeFalse();
        Scalar("SELECT CONVERT(int, is_cdc_enabled) FROM sys.databases WHERE name = DB_NAME()", _connection)
            .Should()
            .Be(0);
    }

    [Test]
    public void It_does_not_configure_a_reused_database_or_server()
    {
        new MssqlDatabaseProvisioner(NullLogger.Instance)
            .CreateDatabaseIfNotExists(_connection)
            .Should()
            .BeTrue();
        Provision().ExitCode.Should().Be(1);
        Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
            .Should()
            .Be(0);
        Scalar(
                "SELECT CONVERT(int, is_read_committed_snapshot_on) FROM sys.databases WHERE name = DB_NAME()",
                _connection
            )
            .Should()
            .Be(0);
    }

    [Test]
    public void It_accepts_prepared_external_server_without_changing_it()
    {
        Execute("EXEC sys.sp_configure N'nested triggers', 1; RECONFIGURE;");
        Provision("inspect").ExitCode.Should().Be(0);
        Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
            .Should()
            .Be(1);
    }

    [Test]
    public void It_leaves_ordinary_managed_provisioning_unchanged()
    {
        Provision("none").ExitCode.Should().Be(0);
        Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_rejects_unavailable_server_setup_authority_after_receipting_create()
    {
        string login = "prerequisite_" + Guid.NewGuid().ToString("N");
        Execute(
            $"CREATE LOGIN [{login}] WITH PASSWORD = 'EdFi_Dms1!'; ALTER SERVER ROLE dbcreator ADD MEMBER [{login}];"
        );
        try
        {
            var connection = new SqlConnectionStringBuilder(_connection) { UserID = login };
            var result = Provision(connectionOverride: connection.ConnectionString);
            result.ExitCode.Should().Be(1);
            result.Output.Should().BeEmpty();
            result.Error.Should().NotContain(login).And.NotContain(_database).And.NotContain("EdFi_Dms1!");
            var journal = await ReadJournalAsync();
            ((CdcWorkflowCompletion.Database)journal.Operations.Single().Completions.Single().Evidence)
                .Receipt.Outcome.Should()
                .Be(CdcDatabaseCreationOutcome.Created);
            Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'DocumentCacheState'", _connection)
                .Should()
                .Be(0);
            Scalar(
                    "SELECT CONVERT(int, is_cdc_enabled) FROM sys.databases WHERE name = DB_NAME()",
                    _connection
                )
                .Should()
                .Be(0);
            Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
                .Should()
                .Be(0);
        }
        finally
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_database);
            Execute($"DROP LOGIN [{login}];");
        }
    }

    [Test]
    public void It_does_not_apply_an_unrelated_pending_server_change()
    {
        int original = Scalar(
            "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'remote access'"
        );
        Execute($"EXEC sys.sp_configure N'remote access', {1 - original};");
        try
        {
            Provision().ExitCode.Should().Be(1);
            Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'remote access'")
                .Should()
                .Be(original);
            Scalar("SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'nested triggers'")
                .Should()
                .Be(0);
        }
        finally
        {
            Execute($"EXEC sys.sp_configure N'remote access', {original}; RECONFIGURE;");
        }
    }

    private ServiceProvider CreateRuntime()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = "mssql",
                    ["AppSettings:DefaultPartitionCount"] = "10",
                    ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
                    ["ConfigurationServiceSettings:ClientId"] = "test",
                    ["ConfigurationServiceSettings:ClientSecret"] = "test",
                    ["ConfigurationServiceSettings:Scope"] = "test",
                    ["ConfigurationServiceSettings:EncryptionKey"] =
                        "TestEncryptionKey123456789012345678901234567890",
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "42",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentCacheRuntimeServices(
            config,
            new LoggerConfiguration().CreateLogger(),
            Target,
            DocumentCacheRuntimeTargetSelection.RequireConfiguredMembership
        );
        var loader = new ApiSchemaFileLoader(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        );
        var loaded = (ApiSchemaFileLoadResult.SuccessResult)
            loader.Load(CliTestHelper.GetMinimalSchemaPath(), []);
        var schema = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => schema.GetApiSchemaNodes()).Returns(loaded.NormalizedNodes);
        A.CallTo(() => schema.SchemaLoadId).Returns(Guid.NewGuid());
        A.CallTo(() => schema.IsSchemaValid).Returns(true);
        A.CallTo(() => schema.ApiSchemaFailures).Returns([]);
        services.Replace(ServiceDescriptor.Singleton(schema));
        var stores = A.Fake<IDataStoreProvider>();
        var store = new DataStore(
            42,
            "mssql",
            "test",
            _connection,
            [],
            RelationalProviderToken.SqlServer,
            RelationalProviderMetadataStatus.Supported
        );
        A.CallTo(() => stores.GetById(42, A<string>._)).Returns(store);
        A.CallTo(() => stores.GetAll(A<string>._)).Returns([store]);
        A.CallTo(() => stores.LoadDataStores(A<string>._, A<CancellationToken>._))
            .Returns(new List<DataStore> { store });
        services.RemoveAll<ConfigurationServiceDataStoreProvider>();
        services.Replace(ServiceDescriptor.Singleton(stores));
        return services.BuildServiceProvider();
    }

    private async Task EnableCaptureAsync()
    {
        // Database user without server login keeps this fixture's principal cleanup database-scoped.
        Execute("CREATE USER [cdc_prerequisite_reader] WITHOUT LOGIN;", _connection);
        await using var connection = new SqlConnection(_connection);
        await connection.OpenAsync();
        var emission = CdcSchemaToolsTestMetadata.BuildMinimalDdlEmission(SqlDialect.Mssql);
        var fingerprint = await new MssqlDocumentCachePhysicalSourceFingerprintReader(
            NullLogger<MssqlDocumentCachePhysicalSourceFingerprintReader>.Instance
        ).ReadFingerprintAsync(_connection);
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        var result = await service.SetupAsync(
            new(
                EdFi.DataManagementService.Backend.Ddl.CdcProvider.SqlServer,
                CdcProviderSetupMode.InitialCreateOrExactMatch,
                new CdcSourceFingerprint(
                    CdcSourceFingerprintMetadata.Version,
                    fingerprint.Fingerprint!.Value
                ),
                new CdcSetupPrincipalContext(new CdcSafeName("sa")),
                new CdcConnectorPrincipal(new CdcSafeName("cdc_prerequisite_reader")),
                CdcProviderArtifactNames.ForSqlServer(
                    new CdcSafeName("cdc_prerequisite_gate"),
                    new Dictionary<CdcSourceTableKind, CdcSafeName>
                    {
                        [CdcSourceTableKind.Document] = new("cdc_prerequisite_document"),
                        [CdcSourceTableKind.DocumentCache] = new("cdc_prerequisite_cache"),
                        [CdcSourceTableKind.CdcHeartbeat] = new("cdc_prerequisite_heartbeat"),
                    }
                ),
                new CdcProviderArtifactOutputRequest(false),
                emission.CdcSourceInventory,
                emission.CdcDmsManagedTableInventory,
                databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(connection)
            )
        );
        result.Diagnostics.Should().NotContain(d => d.Severity == CdcProviderDiagnosticSeverity.Error);
        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
    }

    private async Task<CdcWorkflowJournal> ReadJournalAsync()
    {
        await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            default
        );
        return await session.ReadAsync(
            new("local", "default", "42", "datastore-42", 1, Core.DocumentCache.Cdc.CdcProvider.SqlServer),
            default
        );
    }

    private static int Scalar(string sql, string connectionString = "")
    {
        using var connection = new SqlConnection(
            connectionString.Length == 0
                ? DatabaseConfiguration.MssqlAdminConnectionString!
                : connectionString
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(string sql, string connectionString = "")
    {
        using var connection = new SqlConnection(
            connectionString.Length == 0
                ? DatabaseConfiguration.MssqlAdminConnectionString!
                : connectionString
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
