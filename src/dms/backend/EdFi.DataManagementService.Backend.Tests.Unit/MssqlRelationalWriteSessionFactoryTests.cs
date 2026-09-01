// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlRelationalWriteSessionFactory
{
    [Test]
    public async Task It_uses_the_selected_data_store_for_sql_server_attempts()
    {
        const string connectionString =
            "Server=localhost;Database=test;User Id=sa;Password=TestPassword1!;TrustServerCertificate=true";

        var dataStoreSelection = A.Fake<IDataStoreSelection>();
        var dataStore = new DataStore(
            Id: 7,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: connectionString,
            RouteContext: []
        );
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );

        A.CallTo(() => dataStoreSelection.GetEffectiveTarget())
            .Returns(EffectiveDataStoreTarget.Primary(dataStore.ConnectionString!));

        var sut = new MssqlRelationalWriteSessionFactory(
            dataStoreSelection,
            new MssqlConnectionAcquisition(
                new SqlClientPoolClearing(),
                NullLogger<MssqlConnectionAcquisition>.Instance,
                effectiveConnectionString =>
                {
                    // A Primary target realizes byte-for-byte, so the effective string the acquisition
                    // boundary opens is the configured one.
                    effectiveConnectionString.Should().Be(connectionString);
                    return connection;
                }
            ),
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

        A.CallTo(() => dataStoreSelection.GetEffectiveTarget()).MustHaveHappenedOnceExactly();
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

    [Test]
    public async Task It_still_rolls_back_physically_when_a_failure_is_reported_on_a_non_sql_server_transaction()
    {
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        var sut = new MssqlRelationalWriteSessionFactory(
            _ => Task.FromResult<DbConnection>(connection),
            IsolationLevel.ReadCommitted
        );

        await using var session = await sut.CreateAsync();
        session.ReportDatabaseFailure(new ProbeTestDbException());

        await session.RollbackAsync();

        // The factory wires the SQL Server probe, and the probe positively proves completion or defers. A
        // reported failure alone never skips the physical rollback.
        connection.LastTransaction!.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public void It_refuses_tolerance_when_the_connection_is_not_open()
    {
        var connection = new ProbeTestDbConnection(ConnectionState.Closed);

        MssqlTransactionStateProbe
            .Instance.IsAlreadyCompleted(connection, new ProbeTestDbTransaction(), new ProbeTestDbException())
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_refuses_tolerance_for_a_transaction_that_is_not_a_sql_server_transaction()
    {
        var connection = new ProbeTestDbConnection(ConnectionState.Open);

        // Type-tested so the tolerance cannot leak to a provider whose transaction happens to null its
        // connection for an unrelated reason.
        MssqlTransactionStateProbe
            .Instance.IsAlreadyCompleted(connection, new ProbeTestDbTransaction(), new ProbeTestDbException())
            .Should()
            .BeFalse();
    }

    private sealed class ProbeTestDbException : DbException
    {
        public ProbeTestDbException()
            : base("probe test provider failure") { }
    }

    private sealed class ProbeTestDbTransaction : DbTransaction
    {
        protected override DbConnection? DbConnection => null;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public override void Commit() { }

        public override void Rollback() { }
    }

    private sealed class ProbeTestDbConnection(ConnectionState state) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "probe";

        public override string Database => "probe";

        public override string DataSource => "probe";

        public override string ServerVersion => "probe";

        public override ConnectionState State => state;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open() { }

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
    }
}
