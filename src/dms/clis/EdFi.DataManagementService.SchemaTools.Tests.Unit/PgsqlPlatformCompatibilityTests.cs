// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Commands;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>
/// Pins the PostgreSQL 18 and UTF-8 guards by scripting the server's answers through the real
/// provisioner's connection seam, so no PostgreSQL 16 or 17 server is needed.
/// </summary>
public static class PgsqlPlatformCompatibilityTests
{
    internal const string TargetDatabase = "platform_target";

    internal static string TargetConnectionString =>
        $"Host=localhost;Database={TargetDatabase};Username=postgres";

    internal static string Message(string detected) =>
        $"{PostgresqlPlatformCompatibilityException.RequirementMessage} Detected: {detected}.";

    [TestFixture(160000)]
    [TestFixture(170000)]
    public class Given_Platform_Preconditions_On_An_Older_Server(int serverVersionNum)
    {
        private ScriptedServer _server = null!;
        private PostgresqlPlatformCompatibilityException _exception = null!;

        [SetUp]
        public void SetUp()
        {
            _server = ScriptedServer.Create(
                serverVersionNum,
                targetEncoding: null,
                template1Encoding: "UTF8"
            );
            _exception = Assert.Catch<PostgresqlPlatformCompatibilityException>(() =>
                _server.Provisioner.CheckPlatformPreconditions(TargetConnectionString)
            )!;
        }

        [Test]
        public void It_names_both_requirements_and_the_reported_version() =>
            _exception.Message.Should().Be(Message($"server_version_num {serverVersionNum}"));

        [Test]
        public void It_checks_over_the_maintenance_connection() =>
            _server.OpenedDatabases.Should().Equal("postgres");

        [Test]
        public void It_looks_up_the_target_by_parameter() =>
            _server.Parameters.Should().Equal(("@dbName", (object?)TargetDatabase));

        [Test]
        public void It_creates_nothing() => _server.NonQueries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_Platform_Preconditions_On_A_Compliant_Server_Without_The_Target
    {
        private ScriptedServer _server = null!;
        private bool _created;

        [SetUp]
        public void SetUp()
        {
            _server = ScriptedServer.Create(180000, targetEncoding: null, template1Encoding: "UTF8");
            _server.Provisioner.CheckPlatformPreconditions(TargetConnectionString);
            _created = _server.Provisioner.CreateDatabaseIfNotExists(TargetConnectionString);
        }

        [Test]
        public void It_creates_the_database() => _created.Should().BeTrue();

        [Test]
        public void It_creates_a_UTF8_database_without_a_template_or_locale_override() =>
            _server.NonQueries.Should().Equal($"CREATE DATABASE \"{TargetDatabase}\" ENCODING 'UTF8'");

        [Test]
        public void It_creates_over_the_maintenance_connection() =>
            _server.OpenedDatabases.Should().Equal("postgres", "postgres");
    }

    [TestFixture]
    public class Given_Platform_Preconditions_On_A_PostgreSQL_18_Server_With_A_Non_UTF8_Target
    {
        private ScriptedServer _server = null!;
        private PostgresqlPlatformCompatibilityException _exception = null!;

        [SetUp]
        public void SetUp()
        {
            // template1 is UTF-8 here, so only the existing target's encoding can fail the check.
            _server = ScriptedServer.Create(180000, targetEncoding: "LATIN1", template1Encoding: "UTF8");
            _exception = Assert.Catch<PostgresqlPlatformCompatibilityException>(() =>
                _server.Provisioner.CheckPlatformPreconditions(TargetConnectionString)
            )!;
        }

        [Test]
        public void It_reports_the_target_encoding() =>
            _exception.Message.Should().Be(Message("target database encoding LATIN1"));
    }

    [TestFixture]
    public class Given_Platform_Preconditions_On_A_PostgreSQL_18_Server_With_A_Non_UTF8_Template1
    {
        private ScriptedServer _server = null!;
        private PostgresqlPlatformCompatibilityException _exception = null!;

        [SetUp]
        public void SetUp()
        {
            _server = ScriptedServer.Create(180000, targetEncoding: null, template1Encoding: "SQL_ASCII");
            _exception = Assert.Catch<PostgresqlPlatformCompatibilityException>(() =>
                _server.Provisioner.CheckPlatformPreconditions(TargetConnectionString)
            )!;
        }

        [Test]
        public void It_reports_the_template1_encoding() =>
            _exception.Message.Should().Be(Message("template1 encoding SQL_ASCII"));

        [Test]
        public void It_creates_nothing() => _server.NonQueries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_Platform_Preconditions_On_A_PostgreSQL_18_Server_With_An_Existing_UTF8_Target
    {
        [Test]
        public void It_ignores_a_non_UTF8_template1()
        {
            // An existing target is never created from template1, so template1 does not matter.
            var server = ScriptedServer.Create(180000, targetEncoding: "UTF8", template1Encoding: "LATIN1");

            FluentActions
                .Invoking(() => server.Provisioner.CheckPlatformPreconditions(TargetConnectionString))
                .Should()
                .NotThrow();
        }
    }

    [TestFixture(160000, "UTF8", "server_version_num 160000")]
    [TestFixture(170000, "UTF8", "server_version_num 170000")]
    [TestFixture(180000, "LATIN1", "target database encoding LATIN1")]
    public class Given_Preflight_On_An_Incompatible_Target(
        int serverVersionNum,
        string targetEncoding,
        string detected
    )
    {
        private ScriptedServer _server = null!;
        private PostgresqlPlatformCompatibilityException _exception = null!;

        [SetUp]
        public void SetUp()
        {
            _server = ScriptedServer.Create(serverVersionNum, targetEncoding, template1Encoding: "UTF8");
            _exception = Assert.Catch<PostgresqlPlatformCompatibilityException>(() =>
                _server.Provisioner.PreflightSeedValidation(
                    TargetConnectionString,
                    EffectiveSchemaValidationTestData.BuildExpectedSchema()
                )
            )!;
        }

        [Test]
        public void It_names_both_requirements_and_the_failure() =>
            _exception.Message.Should().Be(Message(detected));

        [Test]
        public void It_checks_the_target_before_any_other_preflight_query() =>
            _server.Queries.Should().Equal(PgsqlDatabaseProvisioner.TargetPlatformSql);

        [Test]
        public void It_reads_the_target_database() => _server.OpenedDatabases.Should().Equal(TargetDatabase);
    }

    [TestFixture]
    public class Given_Preflight_On_A_Compliant_New_Target
    {
        [Test]
        public void It_continues_to_the_fresh_database_checks()
        {
            var server = ScriptedServer.Create(180000, targetEncoding: "UTF8", template1Encoding: "UTF8");

            server.Provisioner.PreflightSeedValidation(
                TargetConnectionString,
                EffectiveSchemaValidationTestData.BuildExpectedSchema()
            );

            server.Queries.Should().HaveCountGreaterThan(1);
            server.Queries[0].Should().Be(PgsqlDatabaseProvisioner.TargetPlatformSql);
            server.Queries[1].Should().Be(server.Provisioner.ExposedDialect.EffectiveSchemaTableExistsSql);
        }
    }

    [TestFixture(false, 160000)]
    [TestFixture(true, 160000)]
    [TestFixture(false, 170000)]
    [TestFixture(true, 170000)]
    [NonParallelizable]
    public class Given_Ddl_Provision_With_Create_Database_On_An_Older_Server(
        bool managed,
        int serverVersionNum
    )
    {
        private ScriptedServer _server = null!;
        private string _root = null!;
        private int _exitCode;
        private string _output = null!;
        private string _error = null!;

        [SetUp]
        public async Task SetUp()
        {
            _server = ScriptedServer.Create(
                serverVersionNum,
                targetEncoding: null,
                template1Encoding: "UTF8"
            );
            _root = Path.Combine(Path.GetTempPath(), "platform-command-" + Guid.NewGuid().ToString("N"));
            using StringWriter output = new();
            using StringWriter error = new();
            TextWriter originalOutput = Console.Out;
            TextWriter originalError = Console.Error;
            try
            {
                Console.SetOut(output);
                Console.SetError(error);
                var command = DdlProvisionCommand.Create(
                    NullLogger.Instance,
                    new ApiSchemaFileLoader(
                        new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
                        NullLogger<ApiSchemaFileLoader>.Instance
                    ),
                    new EffectiveSchemaSetBuilder(
                        new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
                        new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
                    ),
                    (_, _) => _server.Provisioner
                );
                _exitCode = await command.Parse(Arguments()).InvokeAsync();
            }
            finally
            {
                Console.SetOut(originalOutput);
                Console.SetError(originalError);
            }
            _output = output.ToString();
            _error = error.ToString();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private string[] Arguments()
        {
            List<string> arguments =
            [
                "--schema",
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "minimal-api-schema.json"),
                "--connection-string",
                TargetConnectionString,
                "--dialect",
                "pgsql",
                "--create-database",
            ];
            if (managed)
            {
                arguments.AddRange([
                    "--managed-state-path",
                    _root,
                    "--data-store-id",
                    "42",
                    "--instance-key",
                    "datastore-42",
                ]);
            }
            return [.. arguments];
        }

        [Test]
        public void It_fails() => _exitCode.Should().Be(1);

        [Test]
        public void It_reports_the_compatibility_message() =>
            _error
                .Should()
                .Be(
                    (managed ? "" : "Error: DDL provisioning failed: ")
                        + Message($"server_version_num {serverVersionNum}")
                        + Environment.NewLine
                );

        [Test]
        public void It_emits_no_success_output() => _output.Should().BeEmpty();

        [Test]
        public void It_never_issues_CREATE_DATABASE() => _server.NonQueries.Should().BeEmpty();

        [Test]
        public void It_never_connects_to_the_target_database() =>
            _server.OpenedDatabases.Should().Equal("postgres");

        [Test]
        public void It_records_no_creation_intent()
        {
            // Direct provisioning keeps no journal; managed provisioning may keep an empty one.
            foreach (
                string path in Directory.Exists(_root)
                    ? Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories)
                    : []
            )
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                json.RootElement.GetProperty("operations").GetArrayLength().Should().Be(0);
            }
        }
    }

    /// <summary>
    /// A <see cref="PgsqlDatabaseProvisioner"/> whose connections answer the platform queries from a
    /// script and record what they opened and executed.
    /// </summary>
    internal sealed class ScriptedServer
    {
        private readonly Dictionary<string, DataTable> _results = [];

        public List<string> OpenedDatabases { get; } = [];
        public List<string> Queries { get; } = [];
        public List<string> NonQueries { get; } = [];
        public List<(string Name, object? Value)> Parameters { get; } = [];
        public ScriptedPgsqlProvisioner Provisioner { get; }

        private ScriptedServer() => Provisioner = new ScriptedPgsqlProvisioner(this);

        public static ScriptedServer Create(
            int serverVersionNum,
            string? targetEncoding,
            string template1Encoding
        )
        {
            ScriptedServer server = new();
            server._results[PgsqlDatabaseProvisioner.PlatformPreconditionSql] = Table([
                serverVersionNum,
                targetEncoding,
                template1Encoding,
            ]);
            server._results[PgsqlDatabaseProvisioner.TargetPlatformSql] = Table([
                serverVersionNum,
                targetEncoding,
            ]);
            server._results["SELECT 1 FROM pg_database WHERE datname = @dbName"] = Table(
                targetEncoding is null ? [] : [1]
            );
            DialectSql dialect = server.Provisioner.ExposedDialect;
            server._results[dialect.EffectiveSchemaTableExistsSql] = Table([]);
            server._results[dialect.KnownLegacyDocumentCacheArtifactSql] = Table([]);
            server._results[dialect.ProviderPrerequisiteSql] = Table([]);
            return server;
        }

        private static DataTable Table(object?[] row)
        {
            DataTable table = new();
            for (int index = 0; index < row.Length; index++)
            {
                table.Columns.Add($"Column{index}", row[index]?.GetType() ?? typeof(string));
            }
            if (row.Length > 0)
            {
                table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
            }
            return table;
        }

        internal DataTable Query(DbCommand command)
        {
            Queries.Add(command.CommandText);
            Parameters.AddRange(
                command
                    .Parameters.Cast<DbParameter>()
                    .Select(parameter => (parameter.ParameterName, parameter.Value))
            );
            return _results.TryGetValue(command.CommandText, out var table)
                ? table
                : throw new InvalidOperationException($"No scripted result for '{command.CommandText}'.");
        }

        internal sealed class ScriptedPgsqlProvisioner(ScriptedServer server)
            : PgsqlDatabaseProvisioner(NullLogger.Instance)
        {
            public DialectSql ExposedDialect => Dialect;

            protected override DbConnection CreateConnection(string connectionString)
            {
                server.OpenedDatabases.Add(new NpgsqlConnectionStringBuilder(connectionString).Database!);
                return new ScriptedConnection(server);
            }
        }

        private sealed class ScriptedConnection(ScriptedServer server) : DbConnection
        {
            private ConnectionState _state = ConnectionState.Closed;

            [AllowNull]
            public override string ConnectionString { get; set; } = string.Empty;

            public override string Database => "scripted";

            public override string DataSource => "scripted";

            public override string ServerVersion => "scripted";

            public override ConnectionState State => _state;

            public override void ChangeDatabase(string databaseName) { }

            public override void Close() => _state = ConnectionState.Closed;

            public override void Open() => _state = ConnectionState.Open;

            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
                throw new NotSupportedException();

            protected override DbCommand CreateDbCommand() =>
                new ScriptedCommand(server) { Connection = this };
        }

        private sealed class ScriptedCommand(ScriptedServer server) : DbCommand
        {
            private readonly NpgsqlParameterCollection _parameters = new NpgsqlCommand().Parameters;

            [AllowNull]
            public override string CommandText { get; set; } = string.Empty;

            public override int CommandTimeout { get; set; }

            public override CommandType CommandType { get; set; }

            public override bool DesignTimeVisible { get; set; }

            public override UpdateRowSource UpdatedRowSource { get; set; }

            protected override DbConnection? DbConnection { get; set; }

            protected override DbParameterCollection DbParameterCollection => _parameters;

            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel() { }

            public override int ExecuteNonQuery()
            {
                server.NonQueries.Add(CommandText);
                return 1;
            }

            public override object? ExecuteScalar()
            {
                DataTable table = server.Query(this);
                return table.Rows.Count == 0 ? null : table.Rows[0][0];
            }

            public override void Prepare() { }

            protected override DbParameter CreateDbParameter() => new NpgsqlParameter();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                server.Query(this).CreateDataReader();
        }
    }
}
