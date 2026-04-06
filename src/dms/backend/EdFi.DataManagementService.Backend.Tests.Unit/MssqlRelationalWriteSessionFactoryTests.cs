// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlRelationalWriteSessionFactory
{
    [Test]
    public async Task It_uses_the_selected_dms_instance_for_sql_server_attempts()
    {
        const string connectionString =
            "Server=localhost;Database=test;User Id=sa;Password=TestPassword1!;TrustServerCertificate=true";

        var dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
        var dmsInstance = new DmsInstance(
            Id: 7,
            InstanceType: "Test",
            InstanceName: "Test Instance",
            ConnectionString: connectionString,
            RouteContext: []
        );
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );

        A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance()).Returns(dmsInstance);

        var sut = new MssqlRelationalWriteSessionFactory(
            dmsInstanceSelection,
            selectedConnectionString =>
            {
                selectedConnectionString.Should().Be(connectionString);
                return connection;
            },
            Options.Create(new DatabaseOptions { IsolationLevel = IsolationLevel.Snapshot })
        );

        await using var session = await sut.CreateAsync();
        await using var command = session.CreateCommand(
            new RelationalCommand(
                "update dms.Document set ContentVersion = ContentVersion where DocumentId = @documentId",
                [new RelationalParameter("@documentId", 101L)]
            )
        );
        var rowsAffected = await command.ExecuteNonQueryAsync();

        A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance()).MustHaveHappenedOnceExactly();
        connection.OpenAsyncCallCount.Should().Be(1);
        connection.LastOpenAsyncCancellationToken.Should().Be(CancellationToken.None);
        connection.BeginTransactionCallCount.Should().Be(1);
        connection.LastBeginTransactionIsolationLevel.Should().Be(IsolationLevel.Snapshot);
        command.Connection.Should().BeSameAs(connection);
        command.Transaction.Should().BeSameAs(connection.LastTransaction);
        connection.Command.ExecuteNonQueryCallCount.Should().Be(1);
        rowsAffected.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_the_attempt_transaction_explicitly()
    {
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        var sut = new MssqlRelationalWriteSessionFactory(
            _ => Task.FromResult<System.Data.Common.DbConnection>(connection),
            IsolationLevel.ReadCommitted
        );

        await using var session = await sut.CreateAsync();

        await session.RollbackAsync();

        connection.LastTransaction.Should().NotBeNull();
        connection.LastTransaction!.RollbackCallCount.Should().Be(1);
        connection.LastTransaction.CommitCallCount.Should().Be(0);
    }
}
