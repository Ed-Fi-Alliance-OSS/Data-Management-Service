// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_SchemaRestamper_Invalid_Inputs
{
    private int _connectionsCreated;
    private SchemaRestamper _service = null!;
    private readonly EffectiveSchemaInfo _target = new(
        "1.0.0",
        "v3",
        new string('a', 64),
        0,
        new byte[32],
        [],
        []
    );

    [SetUp]
    public void Setup()
    {
        _connectionsCreated = 0;
        _service = new SchemaRestamper(
            NullLogger.Instance,
            (_, _) =>
            {
                _connectionsCreated++;
                throw new InvalidOperationException("Connection factory should not be called.");
            }
        );
    }

    [TestCase((SqlDialect)99, "Host=localhost;Database=sample", 30)]
    [TestCase(SqlDialect.Pgsql, "Host=localhost;Database=sample", 0)]
    [TestCase(SqlDialect.Pgsql, "Host=localhost", 30)]
    [TestCase(SqlDialect.Pgsql, "Host=localhost;Database=", 30)]
    [TestCase(SqlDialect.Pgsql, "Host=localhost;Database=sample;invalid", 30)]
    [TestCase(SqlDialect.Mssql, "Server=localhost", 30)]
    [TestCase(SqlDialect.Mssql, "Server=localhost;Initial Catalog=", 30)]
    [TestCase(SqlDialect.Mssql, "Server=localhost;Database=sample;invalid", 30)]
    public async Task It_rejects_invalid_arguments_before_opening(
        SqlDialect dialect,
        string connectionString,
        int timeout
    )
    {
        Func<Task> action = () =>
            _service.RestampAsync(dialect, connectionString, timeout, _target, true, CancellationToken.None);

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Validation);
        _connectionsCreated.Should().Be(0);
    }

    [TestCase(SqlDialect.Pgsql, "Host=localhost;Database=sample;Password=credential-sentinel")]
    [TestCase(SqlDialect.Mssql, "Server=localhost;Database=sample;Password=credential-sentinel")]
    [TestCase(SqlDialect.Mssql, "Server=localhost;Initial Catalog=sample;Password=credential-sentinel")]
    public async Task It_reports_connection_factory_failure_without_leaking_its_message(
        SqlDialect dialect,
        string connectionString
    )
    {
        Func<Task> action = () =>
            _service.RestampAsync(dialect, connectionString, 30, _target, true, CancellationToken.None);

        var failure = (await action.Should().ThrowAsync<SchemaRestampException>()).Which;
        failure.Failure.Should().Be(SchemaRestampFailure.Connection);
        failure.Message.Should().NotContain("credential-sentinel");
        _connectionsCreated.Should().Be(1);
    }

    [Test]
    public async Task It_classifies_a_postgresql_timeout_wrapped_by_the_provider()
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

        (await action.Should().ThrowAsync<SchemaRestampException>())
            .Which.Failure.Should()
            .Be(SchemaRestampFailure.Timeout);
    }
}
