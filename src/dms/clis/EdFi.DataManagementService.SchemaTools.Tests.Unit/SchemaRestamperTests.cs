// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

public abstract class SchemaRestamperTests
{
    protected int _connectionsCreated;
    protected SchemaRestamper _service = null!;
    protected readonly EffectiveSchemaInfo _target = new(
        "1.0.0",
        "v3",
        new string('a', 64),
        0,
        new byte[32],
        [],
        []
    );

    [SetUp]
    public void Initialize()
    {
        _connectionsCreated = 0;
        _service = new SchemaRestamper(
            NullLogger.Instance,
            (_, _) =>
            {
                _connectionsCreated++;
                throw new InvalidOperationException("credential-sentinel");
            }
        );
    }

    [TestFixture((SqlDialect)99, "Host=localhost;Database=sample", 30)]
    [TestFixture(SqlDialect.Pgsql, "Host=localhost;Database=sample", 0)]
    [TestFixture(SqlDialect.Pgsql, "Host=localhost", 30)]
    [TestFixture(SqlDialect.Pgsql, "Host=localhost;Database=", 30)]
    [TestFixture(SqlDialect.Pgsql, "Host=localhost;Database=sample;invalid", 30)]
    [TestFixture(SqlDialect.Mssql, "Server=localhost", 30)]
    [TestFixture(SqlDialect.Mssql, "Server=localhost;Initial Catalog=", 30)]
    [TestFixture(SqlDialect.Mssql, "Server=localhost;Database=sample;invalid", 30)]
    public sealed class Given_Invalid_Inputs(SqlDialect dialect, string connectionString, int timeout)
        : SchemaRestamperTests
    {
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            Func<Task> action = () =>
                _service.RestampAsync(
                    dialect,
                    connectionString,
                    timeout,
                    _target,
                    true,
                    CancellationToken.None
                );

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_reports_validation_failure()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Validation);
        }

        [Test]
        public void It_does_not_create_a_connection()
        {
            _connectionsCreated.Should().Be(0);
        }
    }

    [TestFixture(SqlDialect.Pgsql, "Host=localhost;Database=sample;Password=credential-sentinel")]
    [TestFixture(SqlDialect.Mssql, "Server=localhost;Database=sample;Password=credential-sentinel")]
    [TestFixture(SqlDialect.Mssql, "Server=localhost;Initial Catalog=sample;Password=credential-sentinel")]
    public sealed class Given_Factory_Failure(SqlDialect dialect, string connectionString)
        : SchemaRestamperTests
    {
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            Func<Task> action = () =>
                _service.RestampAsync(dialect, connectionString, 30, _target, true, CancellationToken.None);

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_reports_a_connection_failure()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Connection);
        }

        [Test]
        public void It_keeps_provider_details_private()
        {
            _failure.Message.Should().NotContain("credential-sentinel");
        }

        [Test]
        public void It_attempts_one_connection()
        {
            _connectionsCreated.Should().Be(1);
        }
    }

    [TestFixture]
    public sealed class Given_Wrapped_Provider_Timeout : SchemaRestamperTests
    {
        private SchemaRestampException _failure = null!;

        [SetUp]
        public async Task Setup()
        {
            _service = new SchemaRestamper(
                NullLogger.Instance,
                (_, _) => throw new NpgsqlException("provider timeout", new TimeoutException())
            );

            Func<Task> action = () =>
                _service.RestampAsync(
                    SqlDialect.Pgsql,
                    "Host=localhost;Database=sample",
                    30,
                    _target,
                    true,
                    CancellationToken.None
                );

            _failure = await CaptureFailureAsync(action);
        }

        [Test]
        public void It_reports_timeout()
        {
            _failure.Failure.Should().Be(SchemaRestampFailure.Timeout);
        }
    }

    private static async Task<SchemaRestampException> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SchemaRestampException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected a re-stamp failure.");
    }
}

[TestFixture(SqlDialect.Pgsql, "28P01", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Pgsql, "28000", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Pgsql, "3D000", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Pgsql, "57014", SchemaRestampFailure.Timeout)]
[TestFixture(SqlDialect.Pgsql, "wrapped-timeout", SchemaRestampFailure.Timeout)]
[TestFixture(SqlDialect.Mssql, "18456", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Mssql, "4060", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Mssql, "-1", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Mssql, "2", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Mssql, "53", SchemaRestampFailure.Connection)]
[TestFixture(SqlDialect.Mssql, "-2", SchemaRestampFailure.Timeout)]
public sealed class Given_SchemaRestamper_Provider_Open_Failure(
    SqlDialect dialect,
    string code,
    SchemaRestampFailure expected
)
{
    private SchemaRestampException _failure = null!;

    [SetUp]
    public async Task Setup()
    {
        Exception providerFailure = (dialect, code) switch
        {
            (SqlDialect.Mssql, _) => CreateSqlException(
                int.Parse(code, System.Globalization.CultureInfo.InvariantCulture)
            ),
            (_, "wrapped-timeout") => new NpgsqlException("credential-sentinel", new TimeoutException()),
            _ => new PostgresException("credential-sentinel", "FATAL", "FATAL", code),
        };
        var connection = A.Fake<DbConnection>();
        A.CallTo(() => connection.OpenAsync(A<CancellationToken>._)).Throws(providerFailure);
        var service = new SchemaRestamper(NullLogger.Instance, (_, _) => connection);
        string connectionString =
            dialect == SqlDialect.Pgsql
                ? "Host=localhost;Database=sample"
                : "Server=localhost;Database=sample";
        var target = new EffectiveSchemaInfo("1.0.0", "v3", new string('a', 64), 0, new byte[32], [], []);
        try
        {
            await service.RestampAsync(dialect, connectionString, 30, target, true, CancellationToken.None);
            throw new InvalidOperationException("Expected an open failure.");
        }
        catch (SchemaRestampException exception)
        {
            _failure = exception;
        }
    }

    [Test]
    public void It_classifies_the_open_failure() => _failure.Failure.Should().Be(expected);

    [Test]
    public void It_does_not_reveal_provider_details() =>
        _failure.Message.Should().NotContain("credential-sentinel");

    // SqlClient has no public exception constructor. Follow the provider-error fixture
    // pattern in MssqlDatabaseLifecycleCoordinatorTests without contacting a server.
    private static SqlException CreateSqlException(int number)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, "credential-sentinel");
        var errors = (SqlErrorCollection)RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errors, new List<object> { sqlError });
        var exception = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(exception, errors);
        return exception;
    }
}
