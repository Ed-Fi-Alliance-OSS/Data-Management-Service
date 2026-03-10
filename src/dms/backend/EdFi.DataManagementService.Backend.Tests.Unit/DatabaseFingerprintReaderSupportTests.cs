// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DatabaseFingerprintReaderSupportTests
{
    private const string TableDisplayName = "dms.EffectiveSchema";

    private static readonly DatabaseFingerprintColumnNames _columnNames = new(
        EffectiveSchemaSingletonId: "EffectiveSchemaSingletonId",
        ApiSchemaFormatVersion: "ApiSchemaFormatVersion",
        EffectiveSchemaHash: "EffectiveSchemaHash",
        ResourceKeyCount: "ResourceKeyCount",
        ResourceKeySeedHash: "ResourceKeySeedHash"
    );

    private static FingerprintRow CreateValidRow() =>
        new(
            EffectiveSchemaSingletonId: 1,
            ApiSchemaFormatVersion: "1.0",
            EffectiveSchemaHash: new string('a', 64),
            ResourceKeyCount: 42,
            ResourceKeySeedHash: Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()
        );

    private static DbDataReader CreateReader(params FingerprintRow[] rows) =>
        CreateReader(
            [
                _columnNames.EffectiveSchemaSingletonId,
                _columnNames.ApiSchemaFormatVersion,
                _columnNames.EffectiveSchemaHash,
                _columnNames.ResourceKeyCount,
                _columnNames.ResourceKeySeedHash,
            ],
            rows
        );

    private static DbDataReader CreateReader(IReadOnlyList<string> columnNames, params FingerprintRow[] rows)
    {
        var table = new DataTable();

        foreach (var columnName in columnNames)
        {
            table.Columns.Add(columnName, GetColumnType(columnName));
        }

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();

            foreach (var columnName in columnNames)
            {
                dataRow[columnName] = GetColumnValue(row, columnName);
            }

            table.Rows.Add(dataRow);
        }

        return table.CreateDataReader();
    }

    private static Type GetColumnType(string columnName) =>
        columnName switch
        {
            nameof(FingerprintRow.EffectiveSchemaSingletonId) => typeof(short),
            nameof(FingerprintRow.ApiSchemaFormatVersion) => typeof(string),
            nameof(FingerprintRow.EffectiveSchemaHash) => typeof(string),
            nameof(FingerprintRow.ResourceKeyCount) => typeof(short),
            nameof(FingerprintRow.ResourceKeySeedHash) => typeof(byte[]),
            _ => throw new ArgumentOutOfRangeException(nameof(columnName), columnName, "Unsupported column."),
        };

    private static object GetColumnValue(FingerprintRow row, string columnName) =>
        columnName switch
        {
            nameof(FingerprintRow.EffectiveSchemaSingletonId) => row.EffectiveSchemaSingletonId,
            nameof(FingerprintRow.ApiSchemaFormatVersion) => row.ApiSchemaFormatVersion,
            nameof(FingerprintRow.EffectiveSchemaHash) => row.EffectiveSchemaHash,
            nameof(FingerprintRow.ResourceKeyCount) => row.ResourceKeyCount,
            nameof(FingerprintRow.ResourceKeySeedHash) => row.ResourceKeySeedHash,
            _ => throw new ArgumentOutOfRangeException(nameof(columnName), columnName, "Unsupported column."),
        };

    private sealed record FingerprintRow(
        short EffectiveSchemaSingletonId,
        string ApiSchemaFormatVersion,
        string EffectiveSchemaHash,
        short ResourceKeyCount,
        byte[] ResourceKeySeedHash
    );

    private sealed class ProjectionFailureException(string message) : InvalidOperationException(message);

    private sealed class StubDbConnection(params DbCommand[] commands) : DbConnection
    {
        private readonly Queue<DbCommand> _commands = new(commands);
        private ConnectionState _state = ConnectionState.Closed;
        private string _connectionString = string.Empty;

        [AllowNull]
        public override string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value ?? string.Empty;
        }

        public override string Database => "test";

        public override string DataSource => "test";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
            _state = ConnectionState.Closed;
        }

        public override void Open()
        {
            _state = ConnectionState.Open;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() =>
            _commands.TryDequeue(out var command)
                ? command
                : throw new InvalidOperationException("No command configured.");
    }

    private sealed class StubDbCommand(Func<object?> executeScalar, Func<DbDataReader> executeReader)
        : DbCommand
    {
        private string _commandText = string.Empty;

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } =
            new StubDbParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

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

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => executeScalar();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => executeReader();
    }

    private sealed class StubDbParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot => this;

        public override int Add(object value) => throw new NotSupportedException();

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() { }

        public override bool Contains(string value) => false;

        public override bool Contains(object value) => false;

        public override void CopyTo(Array array, int index) => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

        public override int IndexOf(string parameterName) => -1;

        public override int IndexOf(object value) => -1;

        public override void Insert(int index, object value) => throw new NotSupportedException();

        public override void Remove(object value) => throw new NotSupportedException();

        public override void RemoveAt(string parameterName) => throw new NotSupportedException();

        public override void RemoveAt(int index) => throw new NotSupportedException();

        protected override DbParameter GetParameter(string parameterName) =>
            throw new IndexOutOfRangeException();

        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();

        protected override void SetParameter(string parameterName, DbParameter value) =>
            throw new NotSupportedException();

        protected override void SetParameter(int index, DbParameter value) =>
            throw new NotSupportedException();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Shared_EffectiveSchema_Query_Metadata : DatabaseFingerprintReaderSupportTests
    {
        [TestCase(SqlDialect.Pgsql)]
        [TestCase(SqlDialect.Mssql)]
        public void It_matches_the_provisioned_effective_schema_definition(SqlDialect dialect)
        {
            var query = DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(dialect);

            query.TableDisplayName.Should().Be(EffectiveSchemaTableDefinition.TableDisplayName);
            query
                .ExistsCommandText.Should()
                .Be(EffectiveSchemaTableDefinition.RenderExistsCommandText(dialect));
            query
                .ReadCommandText.Should()
                .Be(EffectiveSchemaTableDefinition.RenderReadFingerprintCommandText(dialect));
            query
                .ColumnNames.Should()
                .BeEquivalentTo(
                    new DatabaseFingerprintColumnNames(
                        EffectiveSchemaSingletonId: EffectiveSchemaTableDefinition
                            .EffectiveSchemaSingletonId
                            .Value,
                        ApiSchemaFormatVersion: EffectiveSchemaTableDefinition.ApiSchemaFormatVersion.Value,
                        EffectiveSchemaHash: EffectiveSchemaTableDefinition.EffectiveSchemaHash.Value,
                        ResourceKeyCount: EffectiveSchemaTableDefinition.ResourceKeyCount.Value,
                        ResourceKeySeedHash: EffectiveSchemaTableDefinition.ResourceKeySeedHash.Value
                    )
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Fingerprint_Row_Has_Multiple_Contract_Issues
        : DatabaseFingerprintReaderSupportTests
    {
        private DatabaseFingerprintValidationException _exception = null!;

        [SetUp]
        public async Task Setup()
        {
            var act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaSingletonId = 2,
                        ApiSchemaFormatVersion = " ",
                        EffectiveSchemaHash = "invalid-hash",
                        ResourceKeyCount = -1,
                        ResourceKeySeedHash = new byte[31],
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };

            _exception = (await act.Should().ThrowAsync<DatabaseFingerprintValidationException>()).Which;
        }

        [Test]
        public void It_preserves_the_ordered_contract_validation_issues()
        {
            _exception
                .ValidationIssues.Should()
                .Equal(
                    "dms.EffectiveSchema must contain a singleton row with EffectiveSchemaSingletonId = 1, but found 2.",
                    "dms.EffectiveSchema.ApiSchemaFormatVersion must not be empty.",
                    "dms.EffectiveSchema.EffectiveSchemaHash must be 64 lowercase hex characters.",
                    "dms.EffectiveSchema.ResourceKeyCount must be non-negative, but found -1.",
                    "dms.EffectiveSchema.ResourceKeySeedHash must be exactly 32 bytes, but found 31."
                );
        }

        [Test]
        public void It_uses_the_first_issue_as_the_exception_message()
        {
            _exception
                .Message.Should()
                .Be(
                    "dms.EffectiveSchema must contain a singleton row with EffectiveSchemaSingletonId = 1, but found 2."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Well_Formed_Fingerprint_Row : DatabaseFingerprintReaderSupportTests
    {
        private DatabaseFingerprint? _result;

        [SetUp]
        public async Task Setup()
        {
            await using var reader = CreateReader(CreateValidRow());
            _result = await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                reader,
                _columnNames,
                TableDisplayName
            );
        }

        [Test]
        public void It_returns_the_fingerprint()
        {
            _result.Should().NotBeNull();
            _result!.ApiSchemaFormatVersion.Should().Be("1.0");
            _result.EffectiveSchemaHash.Should().Be(new string('a', 64));
            _result.ResourceKeyCount.Should().Be(42);
            _result.ResourceKeySeedHash.Should().HaveCount(32);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Table_Has_No_Rows : DatabaseFingerprintReaderSupportTests
    {
        private DatabaseFingerprint? _result;

        [SetUp]
        public async Task Setup()
        {
            await using var reader = CreateReader();
            _result = await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                reader,
                _columnNames,
                TableDisplayName
            );
        }

        [Test]
        public void It_returns_null()
        {
            _result.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Fingerprint_Rows : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow(),
                    CreateValidRow() with
                    {
                        EffectiveSchemaSingletonId = 2,
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_fails_the_singleton_contract()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage(
                    "dms.EffectiveSchema must contain exactly one singleton row, but multiple rows were found."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Singleton_Id_Is_Not_One : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaSingletonId = 2,
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_singleton_id()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage(
                    "dms.EffectiveSchema must contain a singleton row with EffectiveSchemaSingletonId = 1, but found 2."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_ApiSchemaFormatVersion_Is_Empty : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(CreateValidRow() with { ApiSchemaFormatVersion = " " });

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_empty_version()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage("dms.EffectiveSchema.ApiSchemaFormatVersion must not be empty.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_EffectiveSchemaHash_Is_Not_Lowercase_Hex : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaHash = $"{new string('a', 63)}G",
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_hash_format()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage("dms.EffectiveSchema.EffectiveSchemaHash must be 64 lowercase hex characters.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_EffectiveSchemaHash_Has_The_Wrong_Length : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        EffectiveSchemaHash = new string('a', 63),
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_hash_length()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage("dms.EffectiveSchema.EffectiveSchemaHash must be 64 lowercase hex characters.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_ResourceKeyCount_Is_Negative : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(CreateValidRow() with { ResourceKeyCount = -1 });

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_count()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage("dms.EffectiveSchema.ResourceKeyCount must be non-negative, but found -1.");
        }

        [Test]
        public async Task It_preserves_the_single_issue_without_changing_the_message_contract()
        {
            var exception = (await _act.Should().ThrowAsync<DatabaseFingerprintValidationException>()).Which;

            exception
                .ValidationIssues.Should()
                .Equal("dms.EffectiveSchema.ResourceKeyCount must be non-negative, but found -1.");
            exception
                .Message.Should()
                .Be("dms.EffectiveSchema.ResourceKeyCount must be non-negative, but found -1.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_ResourceKeySeedHash_Has_The_Wrong_Length : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    CreateValidRow() with
                    {
                        ResourceKeySeedHash = new byte[31],
                    }
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_invalid_seed_hash_length()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage(
                    "dms.EffectiveSchema.ResourceKeySeedHash must be exactly 32 bytes, but found 31."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Fingerprint_Result_Is_Missing_A_Required_Column
        : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
            {
                await using var reader = CreateReader(
                    [
                        _columnNames.EffectiveSchemaSingletonId,
                        _columnNames.ApiSchemaFormatVersion,
                        _columnNames.EffectiveSchemaHash,
                        _columnNames.ResourceKeyCount,
                    ],
                    CreateValidRow()
                );

                await DatabaseFingerprintReaderSupport.ReadValidatedFingerprintAsync(
                    reader,
                    _columnNames,
                    TableDisplayName
                );
            };
        }

        [Test]
        public async Task It_reports_the_missing_projection_column()
        {
            await _act.Should()
                .ThrowAsync<DatabaseFingerprintValidationException>()
                .WithMessage("dms.EffectiveSchema is missing required column ResourceKeySeedHash.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExecuteReader_Fails_Because_The_Runtime_Projection_Is_Invalid
        : DatabaseFingerprintReaderSupportTests
    {
        private const string ProjectionFailureMessage =
            "dms.EffectiveSchema does not match the expected fingerprint projection. Required fingerprint columns may be missing, renamed, or incompatible with the runtime query.";

        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
                await DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
                    () =>
                        new StubDbConnection(
                            new StubDbCommand(() => 1, () => throw new NotSupportedException()),
                            new StubDbCommand(
                                () => throw new NotSupportedException(),
                                () => throw new ProjectionFailureException("provider projection failure")
                            )
                        ),
                    new DatabaseFingerprintReaderQuery(
                        TableDisplayName,
                        "select 1",
                        "select 2",
                        _columnNames
                    ),
                    NullLogger.Instance,
                    static exception => exception is ProjectionFailureException
                );
        }

        [Test]
        public async Task It_reclassifies_the_projection_failure_as_a_validation_error()
        {
            var exception = await _act.Should().ThrowAsync<DatabaseFingerprintValidationException>();

            exception.Which.Message.Should().Be(ProjectionFailureMessage);
            exception.Which.InnerException.Should().BeOfType<ProjectionFailureException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ExecuteReader_Fails_With_A_Non_Validation_Exception
        : DatabaseFingerprintReaderSupportTests
    {
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _act = async () =>
                await DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
                    () =>
                        new StubDbConnection(
                            new StubDbCommand(() => 1, () => throw new NotSupportedException()),
                            new StubDbCommand(
                                () => throw new NotSupportedException(),
                                () => throw new TimeoutException("temporary failure")
                            )
                        ),
                    new DatabaseFingerprintReaderQuery(
                        TableDisplayName,
                        "select 1",
                        "select 2",
                        _columnNames
                    ),
                    NullLogger.Instance,
                    static exception => exception is ProjectionFailureException
                );
        }

        [Test]
        public async Task It_leaves_the_failure_on_the_non_validation_path()
        {
            await _act.Should().ThrowAsync<TimeoutException>().WithMessage("temporary failure");
        }
    }
}
