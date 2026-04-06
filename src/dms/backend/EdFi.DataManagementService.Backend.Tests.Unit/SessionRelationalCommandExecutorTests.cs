// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_SessionRelationalCommandExecutor
{
    [Test]
    public async Task It_binds_the_session_transaction_and_configures_parameters()
    {
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        var transaction = new RecordingDbTransaction(connection, IsolationLevel.ReadCommitted);
        var sut = new SessionRelationalCommandExecutor(connection, transaction);

        var result = await sut.ExecuteReaderAsync(
            new RelationalCommand(
                "select * from dms.lookup where referential_id = @referentialId and soft_deleted = @softDeleted",
                [
                    new RelationalParameter(
                        "@referentialId",
                        Guid.Parse("00000000-0000-0000-0000-000000000123")
                    ),
                    new RelationalParameter(
                        "@softDeleted",
                        null,
                        parameter =>
                        {
                            parameter.DbType = DbType.Boolean;
                            parameter.Direction = ParameterDirection.InputOutput;
                        }
                    ),
                ]
            ),
            static (_, _) => Task.FromResult("done")
        );

        result.Should().Be("done");
        connection.CreateCommandCallCount.Should().Be(1);
        connection
            .Command.CommandText.Should()
            .Be(
                "select * from dms.lookup where referential_id = @referentialId and soft_deleted = @softDeleted"
            );
        connection.Command.Transaction.Should().BeSameAs(transaction);
        connection.Command.Parameters.Should().HaveCount(2);
        connection.Command.Parameters[0].ParameterName.Should().Be("@referentialId");
        connection
            .Command.Parameters[0]
            .Value.Should()
            .Be(Guid.Parse("00000000-0000-0000-0000-000000000123"));
        connection.Command.Parameters[1].ParameterName.Should().Be("@softDeleted");
        connection.Command.Parameters[1].Value.Should().Be(DBNull.Value);
        connection.Command.Parameters[1].DbType.Should().Be(DbType.Boolean);
        connection.Command.Parameters[1].Direction.Should().Be(ParameterDirection.InputOutput);
        connection.Command.ExecuteReaderCallCount.Should().Be(1);
        connection.Command.DisposeCallCount.Should().Be(1);
    }
}
