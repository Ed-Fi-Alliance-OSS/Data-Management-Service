// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

public class DatabaseProvisionerTests
{
    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_With_Valid_Connection_String
    {
        private PgsqlDatabaseProvisioner _provisioner = null!;
        private string _databaseName = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
            _databaseName = _provisioner.GetDatabaseName(
                "Host=localhost;Port=5432;Database=edfi_dms;Username=postgres;Password=secret"
            );
        }

        [Test]
        public void It_extracts_the_database_name()
        {
            _databaseName.Should().Be("edfi_dms");
        }
    }

    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_With_No_Database_In_Connection_String
    {
        private PgsqlDatabaseProvisioner _provisioner = null!;
        private Action _action = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
            _action = () =>
                _provisioner.GetDatabaseName("Host=localhost;Port=5432;Username=postgres;Password=secret");
        }

        [Test]
        public void It_throws_InvalidOperationException()
        {
            _action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_includes_a_clear_error_message()
        {
            _action.Should().Throw<InvalidOperationException>().WithMessage("*database name*");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_With_Valid_Connection_String
    {
        private MssqlDatabaseProvisioner _provisioner = null!;
        private string _databaseName = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _databaseName = _provisioner.GetDatabaseName(
                "Server=localhost;Initial Catalog=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true"
            );
        }

        [Test]
        public void It_extracts_the_database_name()
        {
            _databaseName.Should().Be("edfi_dms");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_With_Database_Keyword
    {
        private MssqlDatabaseProvisioner _provisioner = null!;
        private string _databaseName = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _databaseName = _provisioner.GetDatabaseName(
                "Server=localhost;Database=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true"
            );
        }

        [Test]
        public void It_extracts_the_database_name()
        {
            _databaseName.Should().Be("edfi_dms");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_With_No_Database_In_Connection_String
    {
        private MssqlDatabaseProvisioner _provisioner = null!;
        private Action _action = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _action = () =>
                _provisioner.GetDatabaseName(
                    "Server=localhost;User Id=sa;Password=secret;TrustServerCertificate=true"
                );
        }

        [Test]
        public void It_throws_InvalidOperationException()
        {
            _action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_includes_a_clear_error_message()
        {
            _action.Should().Throw<InvalidOperationException>().WithMessage("*database name*");
        }
    }

    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_Parsing_Names_Serialized_By_The_Registration_Transport
    {
        private const string ReservedName = "edfi_configurationservice";

        private string _serializedWithTrailingLineFeed = null!;
        private string _exactReservedName = null!;
        private string _mixedCase = null!;
        private string _trailingSpace = null!;
        private string _trailingLineFeed = null!;
        private string _trailingCarriageReturn = null!;
        private string _trailingCarriageReturnLineFeed = null!;
        private string _embeddedLineFeed = null!;
        private string _semicolonBearing = null!;

        [SetUp]
        public void SetUp()
        {
            var provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
            _serializedWithTrailingLineFeed = SerializePgsqlRegistrationConnectionString($"{ReservedName}\n");

            _exactReservedName = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString(ReservedName)
            );
            _mixedCase = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString("EDFI_ConfigurationService")
            );
            _trailingSpace = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString($"{ReservedName} ")
            );
            _trailingLineFeed = provisioner.GetDatabaseName(_serializedWithTrailingLineFeed);
            _trailingCarriageReturn = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString($"{ReservedName}\r")
            );
            _trailingCarriageReturnLineFeed = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString($"{ReservedName}\r\n")
            );
            _embeddedLineFeed = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString("edfi_configuration\nservice")
            );
            _semicolonBearing = provisioner.GetDatabaseName(
                SerializePgsqlRegistrationConnectionString($"edfi_dms;Database={ReservedName}")
            );
        }

        // Serializes with the same ADO.NET writer and key set the PowerShell registration
        // serializer (New-DataStoreConnectionString) uses, so the parses here are measured against
        // the transport's real wire shape rather than hand-authored strings.
        private static string SerializePgsqlRegistrationConnectionString(string databaseName)
        {
            DbConnectionStringBuilder builder = new()
            {
                ["host"] = "dms-postgresql",
                ["port"] = "5432",
                ["username"] = "postgres",
                ["password"] = "abcdefgh1!",
                ["database"] = databaseName,
            };
            return builder.ConnectionString;
        }

        [Test]
        public void It_parses_the_exact_reserved_name_verbatim()
        {
            _exactReservedName.Should().Be(ReservedName);
        }

        [Test]
        public void It_preserves_mixed_case()
        {
            _mixedCase.Should().Be("EDFI_ConfigurationService");
        }

        [Test]
        public void It_preserves_a_trailing_space()
        {
            _trailingSpace.Should().Be($"{ReservedName} ");
        }

        [Test]
        public void It_removes_a_bare_trailing_line_feed()
        {
            _trailingLineFeed.Should().Be(ReservedName);
        }

        [Test]
        public void It_still_carries_the_line_feed_in_the_serialized_text()
        {
            // Non-vacuous: the LF-bearing value is present on the wire (the writer leaves a bare
            // trailing LF unquoted), so the removal above happens at parse time.
            _serializedWithTrailingLineFeed.Should().Contain($"{ReservedName}\n");
        }

        [Test]
        public void It_preserves_a_trailing_carriage_return()
        {
            _trailingCarriageReturn.Should().Be($"{ReservedName}\r");
        }

        [Test]
        public void It_preserves_a_trailing_carriage_return_line_feed_pair()
        {
            _trailingCarriageReturnLineFeed.Should().Be($"{ReservedName}\r\n");
        }

        [Test]
        public void It_preserves_an_embedded_line_feed()
        {
            _embeddedLineFeed.Should().Be("edfi_configuration\nservice");
        }

        [Test]
        public void It_keeps_a_semicolon_bearing_value_in_one_database_key()
        {
            _semicolonBearing.Should().Be($"edfi_dms;Database={ReservedName}");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_Parsing_Names_Serialized_By_The_Registration_Transport
    {
        private const string ReservedName = "edfi_configurationservice";

        private string _serializedWithTrailingLineFeed = null!;
        private string _exactReservedName = null!;
        private string _mixedCase = null!;
        private string _trailingSpace = null!;
        private string _trailingLineFeed = null!;
        private string _trailingCarriageReturn = null!;
        private string _trailingCarriageReturnLineFeed = null!;
        private string _embeddedLineFeed = null!;
        private string _trailingIdeographicSpace = null!;
        private string _semicolonBearing = null!;

        [SetUp]
        public void SetUp()
        {
            var provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _serializedWithTrailingLineFeed = SerializeMssqlRegistrationConnectionString($"{ReservedName}\n");

            _exactReservedName = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString(ReservedName)
            );
            _mixedCase = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString("EDFI_ConfigurationService")
            );
            _trailingSpace = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString($"{ReservedName} ")
            );
            _trailingLineFeed = provisioner.GetDatabaseName(_serializedWithTrailingLineFeed);
            _trailingCarriageReturn = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString($"{ReservedName}\r")
            );
            _trailingCarriageReturnLineFeed = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString($"{ReservedName}\r\n")
            );
            _embeddedLineFeed = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString("edfi_configuration\nservice")
            );
            _trailingIdeographicSpace = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString($"{ReservedName}\u3000")
            );
            _semicolonBearing = provisioner.GetDatabaseName(
                SerializeMssqlRegistrationConnectionString($"edfi_dms;Database={ReservedName}")
            );
        }

        // Serializes with the same ADO.NET writer and key set the PowerShell registration
        // serializer (New-DataStoreConnectionString) uses, so the parses here are measured against
        // the transport's real wire shape rather than hand-authored strings.
        private static string SerializeMssqlRegistrationConnectionString(string databaseName)
        {
            DbConnectionStringBuilder builder = new()
            {
                ["Server"] = "dms-mssql,1433",
                ["Database"] = databaseName,
                ["User Id"] = "sa",
                ["Password"] = "abcdefgh1!",
                ["TrustServerCertificate"] = "true",
            };
            return builder.ConnectionString;
        }

        [Test]
        public void It_parses_the_exact_reserved_name_verbatim()
        {
            _exactReservedName.Should().Be(ReservedName);
        }

        [Test]
        public void It_preserves_mixed_case()
        {
            _mixedCase.Should().Be("EDFI_ConfigurationService");
        }

        [Test]
        public void It_preserves_a_trailing_space()
        {
            _trailingSpace.Should().Be($"{ReservedName} ");
        }

        [Test]
        public void It_removes_a_bare_trailing_line_feed()
        {
            _trailingLineFeed.Should().Be(ReservedName);
        }

        [Test]
        public void It_still_carries_the_line_feed_in_the_serialized_text()
        {
            // Non-vacuous: the LF-bearing value is present on the wire (the writer leaves a bare
            // trailing LF unquoted), so the removal above happens at parse time.
            _serializedWithTrailingLineFeed.Should().Contain($"{ReservedName}\n");
        }

        [Test]
        public void It_preserves_a_trailing_carriage_return()
        {
            _trailingCarriageReturn.Should().Be($"{ReservedName}\r");
        }

        [Test]
        public void It_preserves_a_trailing_carriage_return_line_feed_pair()
        {
            _trailingCarriageReturnLineFeed.Should().Be($"{ReservedName}\r\n");
        }

        [Test]
        public void It_preserves_an_embedded_line_feed()
        {
            _embeddedLineFeed.Should().Be("edfi_configuration\nservice");
        }

        [Test]
        public void It_preserves_a_trailing_ideographic_space()
        {
            // The PARSE preserves U+3000; it is SQL Server's collation-level name comparison that
            // folds it onto a space. Parser coverage here must never be mistaken for collation
            // coverage - the server-side equivalence is owned by the PowerShell predicate tests.
            _trailingIdeographicSpace.Should().Be($"{ReservedName}\u3000");
        }

        [Test]
        public void It_keeps_a_semicolon_bearing_value_in_one_database_key()
        {
            _semicolonBearing.Should().Be($"edfi_dms;Database={ReservedName}");
        }
    }

    [TestFixture]
    public class Given_Pgsql_Registration_Connection_String_With_An_Empty_Password
    {
        private string _serialized = null!;
        private NpgsqlConnectionStringBuilder _parsedByProvider = null!;

        [SetUp]
        public void SetUp()
        {
            // The generic ADO.NET reader drops an empty-valued key on parse, so the wire text and
            // the provider parser are the oracles for an empty password - never generic-reader key
            // presence. A passwordless (trust-authenticated) PostgreSQL server is a real
            // configuration, and registration must preserve the ability to express it.
            DbConnectionStringBuilder builder = new()
            {
                ["host"] = "dms-postgresql",
                ["port"] = "5432",
                ["username"] = "postgres",
                ["password"] = "",
                ["database"] = "edfi_datamanagementservice",
            };
            _serialized = builder.ConnectionString;
            _parsedByProvider = new NpgsqlConnectionStringBuilder(_serialized);
        }

        [Test]
        public void It_serializes_an_explicit_empty_password_segment()
        {
            _serialized.Should().Contain("password=;");
        }

        [Test]
        public void It_reads_no_password_value_back()
        {
            // The CLR Password property is null for an explicitly-empty password= segment (a pwsh
            // probe of the same property reports "" because PowerShell's IDictionary adapter serves
            // the dictionary view instead of the property). Present-but-empty on the wire is pinned
            // by the serialized-text assertion above; here the provider must read no VALUE back.
            _parsedByProvider.Password.Should().BeNullOrEmpty();
        }

        [Test]
        public void It_keeps_the_database_name_intact()
        {
            _parsedByProvider.Database.Should().Be("edfi_datamanagementservice");
        }
    }

    [TestFixture]
    public class Given_Pgsql_Provisioner_CheckOrConfigureMvcc
    {
        private PgsqlDatabaseProvisioner _provisioner = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
        }

        [Test]
        public void It_is_a_no_op_when_database_was_created()
        {
            // Should not throw - PostgreSQL MVCC check is a no-op
            var action = () =>
                _provisioner.CheckOrConfigureMvcc(
                    "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret",
                    databaseWasCreated: true
                );
            action.Should().NotThrow();
        }

        [Test]
        public void It_is_a_no_op_when_database_was_not_created()
        {
            // Should not throw - PostgreSQL MVCC check is a no-op
            var action = () =>
                _provisioner.CheckOrConfigureMvcc(
                    "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret",
                    databaseWasCreated: false
                );
            action.Should().NotThrow();
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Go_Batch_Separators
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql =
                "CREATE TABLE t1 (id INT);\nGO\nCREATE OR ALTER TRIGGER tr1\nON t1\nFOR INSERT AS\nBEGIN\n  RETURN\nEND;\nGO\nINSERT INTO t1 VALUES (1);";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_splits_into_three_batches()
        {
            _batches.Should().HaveCount(3);
        }

        [Test]
        public void It_returns_first_batch_before_go()
        {
            _batches[0].Should().Be("CREATE TABLE t1 (id INT);");
        }

        [Test]
        public void It_returns_trigger_batch()
        {
            _batches[1].Should().Contain("CREATE OR ALTER TRIGGER");
        }

        [Test]
        public void It_returns_last_batch_after_go()
        {
            _batches[2].Should().Be("INSERT INTO t1 VALUES (1);");
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_Without_Go_Separators
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "CREATE TABLE t1 (id INT);\nCREATE TABLE t2 (id INT);";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_returns_single_batch()
        {
            _batches.Should().HaveCount(1);
        }

        [Test]
        public void It_returns_the_full_sql()
        {
            _batches[0].Should().Be("CREATE TABLE t1 (id INT);\nCREATE TABLE t2 (id INT);");
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Case_Insensitive_Go
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "SELECT 1;\ngo\nSELECT 2;\n  Go  \nSELECT 3;";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_splits_on_all_go_variants()
        {
            _batches.Should().HaveCount(3);
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Go_In_Identifier
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "CREATE TABLE category (id INT);";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_does_not_split_on_go_within_words()
        {
            _batches.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Empty_Batches_Between_Go
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "SELECT 1;\nGO\n\nGO\nSELECT 2;";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_filters_out_empty_batches()
        {
            _batches.Should().HaveCount(2);
        }
    }

    private sealed class ExposedPgsqlDatabaseProvisioner : PgsqlDatabaseProvisioner
    {
        public ExposedPgsqlDatabaseProvisioner()
            : base(A.Fake<ILogger>()) { }

        public DialectSql ExposedDialect => Dialect;
    }

    private sealed class ExposedMssqlDatabaseProvisioner : MssqlDatabaseProvisioner
    {
        public ExposedMssqlDatabaseProvisioner()
            : base(A.Fake<ILogger>()) { }

        public DialectSql ExposedDialect => Dialect;
    }

    [TestFixture]
    public class Given_PreflightSeedValidation_With_Unsupported_SourceIdentity_Type
    {
        private InvalidOperationException? _exception;

        [SetUp]
        public void SetUp()
        {
            var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();
            var dialect = CreateScriptedDialect();
            var results = CreateCompletedSchemaResults(dialect, expectedSchema);
            results[dialect.DataStoreIdentitySourceIdentitySql] = ScriptedCommandResult.Rows([43]);
            var provisioner = new ScriptedDatabaseProvisioner(dialect, new ScriptedDbConnection(results));

            _exception = Assert.Catch<InvalidOperationException>(() =>
                provisioner.PreflightSeedValidation("scripted", expectedSchema)
            );
        }

        [Test]
        public void It_throws_InvalidOperationException()
        {
            _exception.Should().NotBeNull();
        }

        [Test]
        public void It_reports_the_unsupported_SourceIdentity_type()
        {
            _exception!
                .Message.Should()
                .Contain("SourceIdentity")
                .And.Contain("UUID-compatible")
                .And.Contain("System.Int32");
        }
    }

    [TestFixture]
    public class Given_PreflightSeedValidation_With_Unsupported_CacheAheadRecoveryRequired_Type
    {
        private InvalidOperationException? _exception;

        [SetUp]
        public void SetUp()
        {
            var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();
            var dialect = CreateScriptedDialect();
            var results = CreateCompletedSchemaResults(dialect, expectedSchema);
            results[dialect.DocumentCacheStateSingletonSql] = ScriptedCommandResult.Rows([
                "Tracking",
                DateTime.UnixEpoch,
            ]);
            var provisioner = new ScriptedDatabaseProvisioner(dialect, new ScriptedDbConnection(results));

            _exception = Assert.Catch<InvalidOperationException>(() =>
                provisioner.PreflightSeedValidation("scripted", expectedSchema)
            );
        }

        [Test]
        public void It_throws_InvalidOperationException()
        {
            _exception.Should().NotBeNull();
        }

        [Test]
        public void It_reports_the_unsupported_boolean_type()
        {
            _exception!
                .Message.Should()
                .Contain("CacheAheadRecoveryRequired")
                .And.Contain("boolean-compatible")
                .And.Contain("System.DateTime")
                .And.NotContain("must not be null");
        }
    }

    private static DialectSql CreateScriptedDialect() =>
        new(
            EffectiveSchemaTableExistsSql: "effective-schema-table-exists",
            SeedTableCheckSql: "seed-table-check",
            EffectiveSchemaFingerprintSql: "effective-schema-fingerprint",
            DataStoreIdentityTableExistsSql: "data-store-identity-table-exists",
            DataStoreIdentitySourceIdentitySql: "data-store-identity-source-identity",
            DocumentCacheStateTableExistsSql: "document-cache-state-table-exists",
            DocumentCacheStateSingletonSql: "document-cache-state-singleton",
            KnownLegacyDocumentCacheArtifactSql: "known-legacy-document-cache-artifact",
            ProviderPrerequisiteSql: "",
            ResourceKeySelectSql: "resource-key-select",
            SchemaComponentSelectSql: "schema-component-select",
            MissingTableDataStoreIdentity: "dms.DataStoreIdentity",
            MissingTableDocumentCacheState: "dms.DocumentCacheState",
            MissingTableResourceKey: "dms.ResourceKey",
            MissingTableSchemaComponent: "dms.SchemaComponent"
        );

    private static Dictionary<string, ScriptedCommandResult> CreateCompletedSchemaResults(
        DialectSql dialect,
        EffectiveSchemaInfo expectedSchema
    ) =>
        new()
        {
            [dialect.EffectiveSchemaTableExistsSql] = ScriptedCommandResult.Rows([1]),
            [dialect.SeedTableCheckSql] = ScriptedCommandResult.Rows(["ResourceKey"], ["SchemaComponent"]),
            [dialect.EffectiveSchemaFingerprintSql] = ScriptedCommandResult.Rows([
                (short)1,
                expectedSchema.ApiSchemaFormatVersion,
                expectedSchema.EffectiveSchemaHash,
                expectedSchema.ResourceKeyCount,
                expectedSchema.ResourceKeySeedHash,
            ]),
            [dialect.KnownLegacyDocumentCacheArtifactSql] = ScriptedCommandResult.Empty,
            [dialect.DataStoreIdentityTableExistsSql] = ScriptedCommandResult.Rows([1]),
            [dialect.DataStoreIdentitySourceIdentitySql] = ScriptedCommandResult.Rows([Guid.NewGuid()]),
            [dialect.DocumentCacheStateTableExistsSql] = ScriptedCommandResult.Rows([1]),
            [dialect.DocumentCacheStateSingletonSql] = ScriptedCommandResult.Rows(["Tracking", false]),
        };

    private sealed class ScriptedDatabaseProvisioner(DialectSql dialect, DbConnection connection)
        : DatabaseProvisionerBase(A.Fake<ILogger>())
    {
        protected override DialectSql Dialect => dialect;

        protected override DbConnection CreateConnection(string connectionString) => connection;

        public override string GetDatabaseName(string connectionString) => "scripted";

        public override bool CreateDatabaseIfNotExists(string connectionString) => false;

        public override void ExecuteInTransaction(
            string connectionString,
            string sql,
            int commandTimeoutSeconds = 300
        ) => throw new NotSupportedException();

        public override void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated) =>
            throw new NotSupportedException();
    }

    private sealed record ScriptedCommandResult(DataTable Table)
    {
        public static ScriptedCommandResult Empty { get; } = new(CreateTable([]));

        public static ScriptedCommandResult Rows(params object?[][] rows) => new(CreateTable(rows));

        private static DataTable CreateTable(IReadOnlyList<object?[]> rows)
        {
            var table = new DataTable();
            var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Length);

            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var sampleValue = rows.Select(row => columnIndex < row.Length ? row[columnIndex] : null)
                    .FirstOrDefault(value => value is not null && value is not DBNull);

                table.Columns.Add($"Column{columnIndex}", sampleValue?.GetType() ?? typeof(object));
            }

            foreach (var row in rows)
            {
                var values = new object[columnCount];
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    var value = columnIndex < row.Length ? row[columnIndex] : null;
                    values[columnIndex] = value ?? DBNull.Value;
                }

                table.Rows.Add(values);
            }

            return table;
        }
    }

    private sealed class ScriptedDbConnection(IReadOnlyDictionary<string, ScriptedCommandResult> results)
        : DbConnection
    {
        private string _connectionString = string.Empty;
        private ConnectionState _state = ConnectionState.Closed;

        [AllowNull]
        public override string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value ?? string.Empty;
        }

        public override string Database => "scripted";

        public override string DataSource => "scripted";

        public override string ServerVersion => "1";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() =>
            new ScriptedDbCommand(results) { Connection = this };
    }

    private sealed class ScriptedDbCommand(IReadOnlyDictionary<string, ScriptedCommandResult> results)
        : DbCommand
    {
        private string _commandText = string.Empty;

        [AllowNull]
        public override string CommandText
        {
            get => _commandText;
            set => _commandText = value ?? string.Empty;
        }

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar()
        {
            var table = GetResult().Table;
            return table.Rows.Count == 0 || table.Columns.Count == 0 ? null : table.Rows[0][0];
        }

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            GetResult().Table.CreateDataReader();

        private ScriptedCommandResult GetResult() =>
            results.TryGetValue(CommandText, out var result)
                ? result
                : throw new InvalidOperationException($"No scripted result for '{CommandText}'.");
    }

    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_Bounded_Preflight_Sql
    {
        private DialectSql _dialect = null!;

        [SetUp]
        public void SetUp()
        {
            _dialect = new ExposedPgsqlDatabaseProvisioner().ExposedDialect;
        }

        [Test]
        public void It_checks_completed_schema_singleton_tables_and_rows()
        {
            _dialect.DataStoreIdentityTableExistsSql.Should().Contain("DataStoreIdentity");
            _dialect.DataStoreIdentitySourceIdentitySql.Should().Contain("\"SourceIdentity\"");
            _dialect.DocumentCacheStateTableExistsSql.Should().Contain("DocumentCacheState");
            _dialect.DocumentCacheStateSingletonSql.Should().Contain("\"ProjectionLifecycleState\"");
            _dialect.DocumentCacheStateSingletonSql.Should().Contain("\"CacheAheadRecoveryRequired\"");
        }

        [Test]
        public void It_checks_known_legacy_document_cache_artifacts()
        {
            _dialect.KnownLegacyDocumentCacheArtifactSql.Should().Contain("column_name = 'Etag'");
            _dialect.KnownLegacyDocumentCacheArtifactSql.Should().Contain("UX_DocumentCache_DocumentUuid");
            _dialect
                .KnownLegacyDocumentCacheArtifactSql.Should()
                .Contain("IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt");
        }

        [Test]
        public void It_checks_enqueue_owner_prerequisites_without_dms_mutation()
        {
            _dialect
                .ProviderPrerequisiteSql.Should()
                .Be(PgsqlEnqueueOwnerPrerequisiteSql.ProviderPrerequisiteSql);
            _dialect.ProviderPrerequisiteSql.Should().Contain("edfi_dms_enqueue_owner");
            _dialect.ProviderPrerequisiteSql.Should().Contain("pg_catalog.pg_auth_members");
            _dialect.ProviderPrerequisiteSql.Should().Contain("SET TRUE, INHERIT FALSE, ADMIN FALSE");
            _dialect.ProviderPrerequisiteSql.Should().Contain("AND NOT membership.admin_option");
            _dialect.ProviderPrerequisiteSql.Should().Contain("AND NOT membership.inherit_option");
            _dialect.ProviderPrerequisiteSql.Should().Contain("AND membership.set_option");
            _dialect.ProviderPrerequisiteSql.Should().NotContain("WITH ADMIN OPTION");
            _dialect.ProviderPrerequisiteSql.Should().NotContain("pg_catalog.pg_has_role");
            _dialect.ProviderPrerequisiteSql.Should().NotContain("CREATE ROLE");
            _dialect.ProviderPrerequisiteSql.Should().NotContain("ALTER ROLE");
            _dialect.ProviderPrerequisiteSql.Should().NotContain("GRANT");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_Bounded_Preflight_Sql
    {
        private DialectSql _dialect = null!;

        [SetUp]
        public void SetUp()
        {
            _dialect = new ExposedMssqlDatabaseProvisioner().ExposedDialect;
        }

        [Test]
        public void It_checks_completed_schema_singleton_tables_and_rows()
        {
            _dialect.DataStoreIdentityTableExistsSql.Should().Contain("DataStoreIdentity");
            _dialect.DataStoreIdentitySourceIdentitySql.Should().Contain("[SourceIdentity]");
            _dialect.DocumentCacheStateTableExistsSql.Should().Contain("DocumentCacheState");
            _dialect.DocumentCacheStateSingletonSql.Should().Contain("[ProjectionLifecycleState]");
            _dialect.DocumentCacheStateSingletonSql.Should().Contain("[CacheAheadRecoveryRequired]");
        }

        [Test]
        public void It_checks_known_legacy_document_cache_artifacts()
        {
            _dialect.KnownLegacyDocumentCacheArtifactSql.Should().Contain("name = N'Etag'");
            _dialect.KnownLegacyDocumentCacheArtifactSql.Should().Contain("UX_DocumentCache_DocumentUuid");
            _dialect
                .KnownLegacyDocumentCacheArtifactSql.Should()
                .Contain("IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt");
        }

        [Test]
        public void It_has_no_provider_specific_prerequisite_query()
        {
            _dialect.ProviderPrerequisiteSql.Should().BeEmpty();
        }
    }
}
