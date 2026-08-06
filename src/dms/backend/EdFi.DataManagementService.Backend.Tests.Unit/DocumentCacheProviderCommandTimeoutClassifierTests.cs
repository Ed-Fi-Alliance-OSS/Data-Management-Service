// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheProviderCommandTimeoutClassifier")]
public class Given_PostgresqlDocumentCacheProviderCommandTimeoutClassifier
{
    private PostgresqlDocumentCacheProviderCommandTimeoutClassifier _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new PostgresqlDocumentCacheProviderCommandTimeoutClassifier();
    }

    [Test]
    public void It_classifies_provider_independent_timeout_exceptions()
    {
        _sut.IsProviderCommandTimeout(new TimeoutException("timed out")).Should().BeTrue();
    }

    [Test]
    public void It_classifies_postgresql_query_cancellation()
    {
        var exception = new PostgresException("query canceled", "ERROR", "ERROR", "57014");

        _sut.IsProviderCommandTimeout(exception).Should().BeTrue();
    }

    [Test]
    public void It_classifies_npgsql_transport_timeouts()
    {
        var exception = new NpgsqlException(
            "Exception while reading from stream.",
            new IOException("transport failed", new TimeoutException("timed out"))
        );

        _sut.IsProviderCommandTimeout(exception).Should().BeTrue();
    }

    [Test]
    public void It_does_not_classify_non_timeout_postgresql_exceptions()
    {
        var exception = new PostgresException("unique violation", "ERROR", "ERROR", "23505");

        _sut.IsProviderCommandTimeout(exception).Should().BeFalse();
    }
}

[TestFixture]
[Parallelizable]
[Category("DocumentCacheProviderCommandTimeoutClassifier")]
public class Given_MssqlDocumentCacheProviderCommandTimeoutClassifier
{
    private MssqlDocumentCacheProviderCommandTimeoutClassifier _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new MssqlDocumentCacheProviderCommandTimeoutClassifier();
    }

    [Test]
    public void It_classifies_provider_independent_timeout_exceptions()
    {
        _sut.IsProviderCommandTimeout(new TimeoutException("timed out")).Should().BeTrue();
    }

    [Test]
    public void It_classifies_sql_server_command_timeouts()
    {
        _sut.IsProviderCommandTimeout(CreateSqlException(-2, "Execution Timeout Expired.")).Should().BeTrue();
    }

    [Test]
    public void It_does_not_classify_non_timeout_sql_server_exceptions()
    {
        _sut.IsProviderCommandTimeout(CreateSqlException(1205, "Transaction was deadlocked."))
            .Should()
            .BeFalse();
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, message);

        var errorCollection = (SqlErrorCollection)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errorCollection, new List<object> { sqlError });

        var sqlException = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, errorCollection);

        return sqlException;
    }
}
