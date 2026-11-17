// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;
using Stage = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionReadStage;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Drives the staged read through faked ADO.NET objects to pin behavior neither real driver reaches:
/// a provider that reports the caller's cancellation as one of its own exceptions. Npgsql 8 and
/// SqlClient 6 both raise <see cref="OperationCanceledException"/> themselves, so the conversion is a
/// guarantee of the read, not a workaround for either driver.
/// </summary>
[TestFixture]
public class Given_The_Education_Organization_Projection_Staged_Read_With_A_Provider_That_Reports_Cancellation_As_Its_Own_Failure
{
    private static readonly EducationOrganizationProjectionProvider _provider = new(
        SqlDialect.Pgsql,
        IsolationLevel.RepeatableRead,
        static exception => exception is TestProviderException,
        static exception => exception.GetType().Name,
        static (_, describe) =>
            EducationOrganizationProjectionExecutionClassifier.ClassifyPostgresql(null, describe),
        static seconds => $"SET LOCAL lock_timeout = '{seconds}s'",
        SessionRestoreStatement: null,
        DiscardConnectionAsync: static _ => Task.CompletedTask
    );

    private CancellationTokenSource _cancellation = null!;
    private DbConnection _connection = null!;
    private DbTransaction _transaction = null!;
    private DbCommand _lockTimeout = null!;
    private DbCommand _select = null!;

    [SetUp]
    public void Setup()
    {
        _cancellation = new CancellationTokenSource();
        _transaction = A.Fake<DbTransaction>();
        _lockTimeout = FakeCommand();
        _select = FakeCommand();
        _connection = A.Fake<DbConnection>();
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "BeginDbTransactionAsync")
            .WithReturnType<ValueTask<DbTransaction>>()
            .Returns(new ValueTask<DbTransaction>(_transaction));
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "CreateDbCommand")
            .WithReturnType<DbCommand>()
            .ReturnsNextFromSequence(_lockTimeout, _select);
    }

    [TearDown]
    public void TearDown() => _cancellation.Dispose();

    [Test]
    public async Task It_propagates_cancellation_reported_while_acquiring()
    {
        var act = () =>
            ReadAsync(_ =>
            {
                _cancellation.Cancel();
                throw new TestProviderException();
            });

        await act.Should()
            .ThrowAsync<OperationCanceledException>()
            .WithInnerException(typeof(TestProviderException));
    }

    [Test]
    public async Task It_propagates_cancellation_reported_while_preparing()
    {
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "BeginDbTransactionAsync")
            .WithReturnType<ValueTask<DbTransaction>>()
            .Invokes(() => _cancellation.Cancel())
            .Throws(new TestProviderException());

        var act = () => ReadAsync(Opened);

        await act.Should()
            .ThrowAsync<OperationCanceledException>()
            .WithInnerException(typeof(TestProviderException));
    }

    [Test]
    public async Task It_propagates_cancellation_reported_while_executing()
    {
        FailExecution(cancelFirst: true);

        var act = () => ReadAsync(Opened);

        await act.Should()
            .ThrowAsync<OperationCanceledException>()
            .WithInnerException(typeof(TestProviderException));
    }

    [Test]
    public async Task It_classifies_the_same_execution_failure_when_the_caller_has_not_cancelled()
    {
        FailExecution(cancelFirst: false);

        var result = await ReadAsync(Opened);

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, nameof(TestProviderException)));
    }

    [Test]
    public async Task It_propagates_cancellation_reported_while_committing()
    {
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .ReturnsLazily(static () => Task.FromResult<DbDataReader>(EmptyResult()));
        A.CallTo(() => _transaction.CommitAsync(A<CancellationToken>._))
            .Invokes(() => _cancellation.Cancel())
            .Throws(new TestProviderException());

        var act = () => ReadAsync(Opened);

        await act.Should()
            .ThrowAsync<OperationCanceledException>()
            .WithInnerException(typeof(TestProviderException));
    }

    private Task<EducationOrganizationProjectionConnection> Opened(CancellationToken cancellationToken) =>
        Task.FromResult(new EducationOrganizationProjectionConnection(_connection, _connection));

    private Task<Result> ReadAsync(
        Func<CancellationToken, Task<EducationOrganizationProjectionConnection>> acquireAsync
    ) =>
        EducationOrganizationProjectionStagedRead.ReadAsync(
            _provider,
            acquireAsync,
            new EducationOrganizationProjectionSetReadRequest(
                EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Pgsql),
                MaxProjectionRows: 50_000,
                ReadLockTimeoutSeconds: 5,
                ReadCommandTimeoutSeconds: 60
            ),
            new RecordingLogger<Given_The_Education_Organization_Projection_Staged_Read_With_A_Provider_That_Reports_Cancellation_As_Its_Own_Failure>(),
            observer: null,
            _cancellation.Token
        );

    private void FailExecution(bool cancelFirst)
    {
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .Invokes(() =>
            {
                if (cancelFirst)
                {
                    _cancellation.Cancel();
                }
            })
            .Throws(new TestProviderException());
    }

    internal static DbCommand FakeCommand()
    {
        var command = A.Fake<DbCommand>();
        A.CallTo(command)
            .Where(static call => call.Method.Name == "CreateDbParameter")
            .WithReturnType<DbParameter>()
            .ReturnsLazily(static () => A.Fake<DbParameter>());
        A.CallTo(command)
            .Where(static call => call.Method.Name == "get_DbParameterCollection")
            .WithReturnType<DbParameterCollection>()
            .Returns(A.Fake<DbParameterCollection>());
        A.CallTo(() => command.ExecuteNonQueryAsync(A<CancellationToken>._)).Returns(0);
        return command;
    }

    internal static DbDataReader EmptyResult()
    {
        var columns = EducationOrganizationProjectionResultColumns.Default;
        var table = new DataTable();
        table.Columns.Add(columns.EducationOrganizationId.Value, typeof(long));
        table.Columns.Add(columns.Discriminator.Value, typeof(string));
        table.Columns.Add(columns.NameOfInstitution.Value, typeof(string));
        table.Columns.Add(columns.ShortNameOfInstitution.Value, typeof(string));
        table.Columns.Add(columns.LocalEducationAgencyReference.Value, typeof(long));
        table.Columns.Add(columns.ParentLocalEducationAgencyReference.Value, typeof(long));
        table.Columns.Add(columns.EducationServiceCenterReference.Value, typeof(long));
        table.Columns.Add(columns.StateEducationAgencyReference.Value, typeof(long));
        return table.CreateDataReader();
    }

    /// <summary>A provider exception, as a driver might raise for a cancelled command.</summary>
    private sealed class TestProviderException() : DbException("Provider-reported failure.");
}

/// <summary>
/// A connection lost underneath the Prepare or Execute stage: the provider raises
/// <see cref="InvalidOperationException"/> and the connection is no longer open. That is the target
/// becoming unavailable in that stage, with no rows returned; the same exception on a connection that
/// is still open is a defect and propagates.
/// </summary>
[TestFixture]
public class Given_The_Education_Organization_Projection_Staged_Read_When_The_Connection_Is_Lost
{
    private const string Hostile = "Hostile-Secret-5d1e";

    private static readonly EducationOrganizationProjectionProvider _provider = new(
        SqlDialect.Pgsql,
        IsolationLevel.RepeatableRead,
        static _ => false,
        static exception => exception.GetType().Name,
        static (_, describe) =>
            EducationOrganizationProjectionExecutionClassifier.ClassifyPostgresql(null, describe),
        static seconds => $"SET LOCAL lock_timeout = '{seconds}s'",
        SessionRestoreStatement: null,
        DiscardConnectionAsync: static _ => Task.CompletedTask
    );

    private CancellationTokenSource _cancellation = null!;
    private ConnectionState _state;
    private DbConnection _connection = null!;
    private DbCommand _lockTimeout = null!;
    private DbCommand _select = null!;
    private RecordingLogger<Given_The_Education_Organization_Projection_Staged_Read_When_The_Connection_Is_Lost> _logger =
        null!;

    [SetUp]
    public void Setup()
    {
        _cancellation = new CancellationTokenSource();
        _state = ConnectionState.Open;
        _logger = new();
        _lockTimeout =
            Given_The_Education_Organization_Projection_Staged_Read_With_A_Provider_That_Reports_Cancellation_As_Its_Own_Failure.FakeCommand();
        _select =
            Given_The_Education_Organization_Projection_Staged_Read_With_A_Provider_That_Reports_Cancellation_As_Its_Own_Failure.FakeCommand();
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .ReturnsLazily(static () =>
                Task.FromResult(
                    Given_The_Education_Organization_Projection_Staged_Read_With_A_Provider_That_Reports_Cancellation_As_Its_Own_Failure.EmptyResult()
                )
            );

        _connection = A.Fake<DbConnection>();
        A.CallTo(() => _connection.State).ReturnsLazily(() => _state);
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "BeginDbTransactionAsync")
            .WithReturnType<ValueTask<DbTransaction>>()
            .Returns(new ValueTask<DbTransaction>(A.Fake<DbTransaction>()));
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "CreateDbCommand")
            .WithReturnType<DbCommand>()
            .ReturnsNextFromSequence(_lockTimeout, _select);
    }

    [TearDown]
    public void TearDown() => _cancellation.Dispose();

    [TestCase(Stage.Prepare, ConnectionState.Closed)]
    [TestCase(Stage.Prepare, ConnectionState.Broken)]
    [TestCase(Stage.Execute, ConnectionState.Closed)]
    [TestCase(Stage.Execute, ConnectionState.Broken)]
    public async Task It_answers_target_unavailable_in_the_stage_that_lost_the_connection(
        Stage stage,
        ConnectionState lostState
    )
    {
        Fail(stage, lostState, new InvalidOperationException(Hostile));

        var result = await ReadAsync();

        result.Should().Be(new Result.TargetUnavailable(stage, nameof(InvalidOperationException)));
        foreach (var record in _logger.Records)
        {
            record.Exception.Should().BeNull();
            record.Message.Should().NotContain(Hostile);
        }
    }

    [TestCase(Stage.Prepare)]
    [TestCase(Stage.Execute)]
    public async Task It_propagates_an_invalid_operation_on_a_connection_that_is_still_open(Stage stage)
    {
        Fail(stage, ConnectionState.Open, new InvalidOperationException("defect"));

        var act = ReadAsync;

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("defect");
    }

    [TestCase(Stage.Prepare)]
    [TestCase(Stage.Execute)]
    public async Task It_propagates_disposal_even_when_the_connection_is_closed(Stage stage)
    {
        Fail(stage, ConnectionState.Closed, new ObjectDisposedException("connection"));

        var act = ReadAsync;

        await act.Should().ThrowExactlyAsync<ObjectDisposedException>();
    }

    [TestCase(Stage.Prepare)]
    [TestCase(Stage.Execute)]
    public async Task It_propagates_the_callers_cancellation_over_a_lost_connection(Stage stage)
    {
        Fail(stage, ConnectionState.Closed, new InvalidOperationException(Hostile), cancelFirst: true);

        var act = ReadAsync;

        await act.Should()
            .ThrowAsync<OperationCanceledException>()
            .WithInnerException(typeof(InvalidOperationException));
    }

    private void Fail(
        Stage stage,
        ConnectionState stateAfterFailure,
        Exception exception,
        bool cancelFirst = false
    )
    {
        void Lose()
        {
            if (cancelFirst)
            {
                _cancellation.Cancel();
            }
            _state = stateAfterFailure;
        }

        if (stage == Stage.Prepare)
        {
            A.CallTo(() => _lockTimeout.ExecuteNonQueryAsync(A<CancellationToken>._))
                .Invokes(Lose)
                .Throws(exception);
        }
        else
        {
            A.CallTo(_select)
                .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
                .WithReturnType<Task<DbDataReader>>()
                .Invokes(Lose)
                .Throws(exception);
        }
    }

    private Task<Result> ReadAsync() =>
        EducationOrganizationProjectionStagedRead.ReadAsync(
            _provider,
            _ => Task.FromResult(new EducationOrganizationProjectionConnection(_connection, _connection)),
            new EducationOrganizationProjectionSetReadRequest(
                EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Pgsql),
                MaxProjectionRows: 50_000,
                ReadLockTimeoutSeconds: 5,
                ReadCommandTimeoutSeconds: 60
            ),
            _logger,
            observer: null,
            _cancellation.Token
        );
}
