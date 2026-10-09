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
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;
using Stage = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionReadStage;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// The staged read's cleanup through faked ADO.NET objects: ending the transaction, restoring the
/// session and releasing the connection never replace the read's outcome, and a connection whose
/// cleanup is not confirmed is excluded from reuse before it is released.
/// </summary>
[TestFixture]
public class Given_The_Education_Organization_Projection_Staged_Read_Cleanup
{
    private const string Hostile = "Hostile-Secret-9f2c";
    private const string RestoreStatement = "RESTORE SESSION";

    private List<string> _events = null!;
    private CancellationTokenSource _cancellation = null!;
    private DbConnection _connection = null!;
    private DbTransaction _transaction = null!;
    private DbCommand _lockTimeout = null!;
    private DbCommand _select = null!;
    private DbCommand _restore = null!;
    private IAsyncDisposable _owner = null!;
    private RecordingLogger<Given_The_Education_Organization_Projection_Staged_Read_Cleanup> _logger = null!;
    private EducationOrganizationProjectionProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _events = [];
        _cancellation = new CancellationTokenSource();
        _logger = new();
        _transaction = A.Fake<DbTransaction>();
        _lockTimeout = FakeCommand();
        _select = FakeCommand();
        _restore = FakeCommand();
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .ReturnsLazily(static () => Task.FromResult<DbDataReader>(EmptyResult()));
        A.CallTo(() => _restore.ExecuteNonQueryAsync(A<CancellationToken>._))
            .Invokes(() => _events.Add("restore"))
            .Returns(0);

        _connection = A.Fake<DbConnection>();
        A.CallTo(() => _connection.State).Returns(ConnectionState.Open);
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "BeginDbTransactionAsync")
            .WithReturnType<ValueTask<DbTransaction>>()
            .Returns(new ValueTask<DbTransaction>(_transaction));
        A.CallTo(_connection)
            .Where(static call => call.Method.Name == "CreateDbCommand")
            .WithReturnType<DbCommand>()
            .ReturnsNextFromSequence(_lockTimeout, _select, _restore);

        _owner = A.Fake<IAsyncDisposable>();
        A.CallTo(() => _owner.DisposeAsync())
            .Invokes(() => _events.Add("release"))
            .Returns(ValueTask.CompletedTask);

        _provider = new(
            SqlDialect.Mssql,
            IsolationLevel.Serializable,
            static exception => exception is TestProviderException,
            static exception => exception.GetType().Name,
            static (_, describe) =>
                EducationOrganizationProjectionExecutionClassifier.ClassifyMssql(null, describe),
            static seconds => $"SET LOCK_TIMEOUT {seconds * 1000}",
            RestoreStatement,
            _ =>
            {
                _events.Add("discard");
                return Task.CompletedTask;
            }
        );
    }

    [TearDown]
    public void TearDown() => _cancellation.Dispose();

    [Test]
    public async Task It_restores_the_session_and_reuses_the_connection_when_cleanup_succeeds()
    {
        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>();
        _events.Should().Equal("restore", "release");
        _logger.Records.Should().NotContain(static record => record.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task It_excludes_the_connection_from_reuse_before_release_when_restoring_the_session_fails()
    {
        A.CallTo(() => _restore.ExecuteNonQueryAsync(A<CancellationToken>._))
            .Invokes(() => _events.Add("restore"))
            .Throws(new TestProviderException());

        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>();
        _events.Should().Equal("restore", "discard", "release");
        AssertCleanupWarning("restoring the session", nameof(TestProviderException));
    }

    [Test]
    public async Task It_excludes_the_connection_from_reuse_when_restoring_the_session_raises_any_exception()
    {
        A.CallTo(() => _restore.ExecuteNonQueryAsync(A<CancellationToken>._))
            .Invokes(() => _events.Add("restore"))
            .Throws(new InvalidOperationException(Hostile));

        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>();
        _events.Should().Equal("restore", "discard", "release");
        AssertCleanupWarning("restoring the session", nameof(InvalidOperationException));
    }

    [Test]
    public async Task It_excludes_the_connection_from_reuse_when_it_is_no_longer_open_to_restore()
    {
        A.CallTo(() => _connection.State).Returns(ConnectionState.Broken);

        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>();
        _events.Should().Equal("discard", "release");
        AssertCleanupWarning("restoring the session", "ConnectionNotOpen");
    }

    [Test]
    public async Task It_excludes_the_connection_from_reuse_when_ending_the_transaction_fails()
    {
        A.CallTo(() => _transaction.DisposeAsync()).Throws(new TestProviderException());

        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>();
        _events.Should().Equal("restore", "discard", "release");
        AssertCleanupWarning("ending the transaction", nameof(TestProviderException));
    }

    [Test]
    public async Task It_keeps_a_committed_result_and_excludes_the_connection_when_release_fails()
    {
        FailRelease();

        var result = await ReadAsync();

        result.Should().BeOfType<Result.Set>();
        _events.Should().Equal("restore", "release", "discard");
        AssertCleanupWarning("releasing the connection", nameof(InvalidOperationException));
    }

    [Test]
    public async Task It_keeps_a_classified_failure_when_release_also_fails()
    {
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .Throws(new TestProviderException());
        FailRelease();

        var result = await ReadAsync();

        result.Should().Be(new Result.TargetUnavailable(Stage.Execute, nameof(TestProviderException)));
        _events.Should().Contain("release").And.Contain("discard");
        AssertRedacted();
    }

    [Test]
    public async Task It_keeps_the_callers_cancellation_when_release_also_fails()
    {
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .Invokes(() => _cancellation.Cancel())
            .Throws(new OperationCanceledException(_cancellation.Token));
        FailRelease();

        var act = ReadAsync;

        await act.Should().ThrowAsync<OperationCanceledException>();
        _events.Should().Contain("release").And.Contain("discard");
        AssertRedacted();
    }

    [Test]
    public async Task It_keeps_a_defect_when_release_also_fails()
    {
        A.CallTo(_select)
            .Where(static call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .Throws(new NotSupportedException("defect"));
        FailRelease();

        var act = ReadAsync;

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("defect");
        _events.Should().Contain("release");
    }

    private void FailRelease() =>
        A.CallTo(() => _owner.DisposeAsync())
            .Invokes(() => _events.Add("release"))
            .Throws(new InvalidOperationException(Hostile));

    private void AssertCleanupWarning(string step, string failure)
    {
        AssertRedacted();
        _logger
            .Records.Where(static record => record.Level == LogLevel.Warning)
            .Should()
            .ContainSingle()
            .Which.Message.Should()
            .Contain(step)
            .And.Contain(failure);
    }

    private void AssertRedacted()
    {
        foreach (var record in _logger.Records)
        {
            record.Exception.Should().BeNull();
            record.Message.Should().NotContain(Hostile);
        }
    }

    private Task<Result> ReadAsync() =>
        EducationOrganizationProjectionStagedRead.ReadAsync(
            _provider,
            _ => Task.FromResult(new EducationOrganizationProjectionConnection(_connection, _owner)),
            new EducationOrganizationProjectionSetReadRequest(
                EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Mssql),
                MaxProjectionRows: 50_000,
                ReadLockTimeoutSeconds: 5,
                ReadCommandTimeoutSeconds: 60
            ),
            _logger,
            observer: null,
            _cancellation.Token
        );

    private static DbCommand FakeCommand()
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

    private static DbDataReader EmptyResult()
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

    private sealed class TestProviderException() : DbException("Provider-reported failure.");
}
