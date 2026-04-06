// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_PostgresqlRelationalWriteSessionFactory
{
    [Test]
    public async Task It_opens_one_connection_and_begins_one_transaction_per_attempt()
    {
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        var openConnectionCallCount = 0;
        var sut = new PostgresqlRelationalWriteSessionFactory(
            cancellationToken =>
            {
                cancellationToken.Should().Be(CancellationToken.None);
                openConnectionCallCount++;
                return Task.FromResult<DbConnection>(connection);
            },
            IsolationLevel.Serializable
        );

        await using var session = await sut.CreateAsync();
        await using var command = session.CreateCommand(
            new RelationalCommand(
                "select * from dms.lookup where referential_id = @referentialId",
                [
                    new RelationalParameter(
                        "@referentialId",
                        Guid.Parse("00000000-0000-0000-0000-000000000111")
                    ),
                ]
            )
        );
        await using var _ = await command.ExecuteReaderAsync();

        openConnectionCallCount.Should().Be(1);
        connection.BeginTransactionCallCount.Should().Be(1);
        connection.LastBeginTransactionIsolationLevel.Should().Be(IsolationLevel.Serializable);
        command.Connection.Should().BeSameAs(connection);
        command.Transaction.Should().BeSameAs(connection.LastTransaction);
        connection.Command.ExecuteReaderCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_commits_the_attempt_transaction_explicitly()
    {
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        var sut = new PostgresqlRelationalWriteSessionFactory(
            _ => Task.FromResult<DbConnection>(connection),
            IsolationLevel.RepeatableRead
        );

        await using var session = await sut.CreateAsync();

        await session.CommitAsync();

        connection.LastTransaction.Should().NotBeNull();
        connection.LastTransaction!.CommitCallCount.Should().Be(1);
        connection.LastTransaction.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_creates_session_commands_with_configured_parameters()
    {
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        var sut = new PostgresqlRelationalWriteSessionFactory(
            _ => Task.FromResult<DbConnection>(connection),
            IsolationLevel.RepeatableRead
        );

        await using var session = await sut.CreateAsync();
        await using var command = session.CreateCommand(
            new RelationalCommand(
                "select 1 where @nullable is null",
                [
                    new RelationalParameter(
                        "@nullable",
                        null,
                        parameter =>
                        {
                            parameter.DbType = DbType.String;
                            parameter.Direction = ParameterDirection.InputOutput;
                        }
                    ),
                ]
            )
        );
        var rowsAffected = await command.ExecuteNonQueryAsync();

        rowsAffected.Should().Be(1);
        connection.Command.Transaction.Should().BeSameAs(connection.LastTransaction);
        connection.Command.CommandText.Should().Be("select 1 where @nullable is null");
        connection.Command.Parameters.Should().ContainSingle();
        connection.Command.Parameters[0].ParameterName.Should().Be("@nullable");
        connection.Command.Parameters[0].Value.Should().Be(DBNull.Value);
        connection.Command.Parameters[0].DbType.Should().Be(DbType.String);
        connection.Command.Parameters[0].Direction.Should().Be(ParameterDirection.InputOutput);
    }
}
