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

/// <summary>
/// SQL Server write-session creation opens through the same guarded helper the read seams use, so the
/// guard's snapshot-only condition is what keeps the write contract still. These pin that contract
/// where it is actually reachable, rather than leaving it to be inferred from the condition.
/// </summary>
/// <remarks>
/// <para>
/// The contract is <c>DefaultRelationalWriteExecutor</c>'s <c>catch (DbException)</c> around
/// <c>CreateAsync</c>, which routes a session-creation failure through the write-failure mapper. That
/// catch would stop firing if acquisition wrapped the failure, because
/// <see cref="DatabaseConnectionUnavailableException" /> is deliberately not a <c>DbException</c>.
/// </para>
/// <para>
/// The executor half - a <c>DbException</c> from the factory becoming a retryable write conflict - is
/// already pinned by <c>DefaultRelationalWriteExecutorTests</c>. What these add is the input that half
/// assumes: the factory really does still raise a <c>DbException</c> now that its open is guarded.
/// </para>
/// <para>
/// A mutation can never select a snapshot - target selection rejects the request before any target is
/// assigned, which the effective-target selector's own tests pin - so the primary is the only kind
/// these paths ever see. The snapshot case below is here to show the difference is the target kind and
/// not the shape of the failure.
/// </para>
/// </remarks>
[TestFixture]
[Parallelizable]
public class WriteSessionAcquisitionContractTests
{
    private const string TargetConnectionString =
        "Server=127.0.0.1,1;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true;Connect Timeout=1";

    /// <summary>
    /// The failure shape that actually reaches the write-failure mapper: a connection timeout or a
    /// failed login, raised by the driver as a DbException while opening. A malformed connection string
    /// cannot stand in for it - that is a parsing ArgumentException, which the mapper's catch has never
    /// accepted.
    /// </summary>
    private static DbException OpenFailure() => new StubDbException("Login failed for user 'sa'.");

    private static IDataStoreSelection SelectionOf(EffectiveTargetKind targetKind)
    {
        var selection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => selection.GetEffectiveTarget())
            .Returns(new EffectiveDataStoreTarget(targetKind, TargetConnectionString));
        return selection;
    }

    private static IMssqlConnectionAcquisition AcquisitionOpening(DbConnection connection) =>
        new MssqlConnectionAcquisition(
            new SqlClientPoolClearing(),
            NullLogger<MssqlConnectionAcquisition>.Instance,
            _ => connection
        );

    [Test]
    public async Task It_raises_the_open_failure_as_a_db_exception_on_a_primary_target()
    {
        DbException failure = OpenFailure();
        using FailingOpenConnection connection = new(failure);

        Func<Task> open = () =>
            MssqlSeamConnection.OpenAsync(
                SelectionOf(EffectiveTargetKind.Primary),
                AcquisitionOpening(connection),
                NullLogger.Instance,
                CancellationToken.None
            );

        var thrown = await open.Should().ThrowAsync<DbException>();

        thrown.Which.Should().BeSameAs(failure);
        thrown
            .Which.Should()
            .NotBeOfType<DatabaseConnectionUnavailableException>(
                "the write-failure mapper's catch would stop firing"
            );
    }

    /// <summary>
    /// The same failure through the factory the executor actually calls, so the contract is pinned at
    /// the call site rather than one layer below it.
    /// </summary>
    [Test]
    public async Task It_raises_a_db_exception_from_write_session_creation()
    {
        DbException failure = OpenFailure();
        using FailingOpenConnection connection = new(failure);

        MssqlRelationalWriteSessionFactory factory = new(
            SelectionOf(EffectiveTargetKind.Primary),
            AcquisitionOpening(connection),
            Options.Create(new DatabaseOptions { IsolationLevel = IsolationLevel.ReadCommitted }),
            NullLogger<MssqlRelationalWriteSessionFactory>.Instance
        );

        Func<Task> create = () => factory.CreateAsync();

        (await create.Should().ThrowAsync<DbException>()).Which.Should().BeSameAs(failure);
    }

    /// <summary>
    /// The identical failure on a snapshot target is wrapped, which is what makes the two cases above a
    /// statement about the target kind rather than about the failure.
    /// </summary>
    [Test]
    public async Task It_wraps_the_identical_failure_on_a_snapshot_target()
    {
        using FailingOpenConnection connection = new(OpenFailure());

        Func<Task> open = () =>
            MssqlSeamConnection.OpenAsync(
                SelectionOf(EffectiveTargetKind.Snapshot),
                AcquisitionOpening(connection),
                NullLogger.Instance,
                CancellationToken.None
            );

        (await open.Should().ThrowAsync<DatabaseConnectionUnavailableException>())
            .Which.TargetKind.Should()
            .Be(EffectiveTargetKind.Snapshot);
    }

    private sealed class StubDbException(string message) : DbException(message);

    /// <summary>
    /// A connection the acquisition boundary can hand out but that fails to open, which is how a
    /// connection timeout and a failed login both arrive.
    /// </summary>
    private sealed class FailingOpenConnection(DbException openFailure) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() => throw openFailure;

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            Task.FromException(openFailure);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
