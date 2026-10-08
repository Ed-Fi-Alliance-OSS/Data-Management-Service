// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The cancellable public-key read (spec D-8) against a real server: the query and results are unchanged, and a
/// canceled token stops the open or the running query on the server.
/// </summary>
[NonParallelizable]
public class OpenIddictPublicKeyCancellationTests : DatabaseTest
{
    protected OpenIddictDataRepository Repository { get; } = new(MssqlTestConfiguration.DatabaseOptions);

    protected async Task InsertKeyAsync(string keyId, bool isActive) =>
        await Connection!.ExecuteAsync(
            """
            INSERT INTO dmscs.OpenIddictKey (KeyId, PublicKey, PrivateKey, IsActive)
            VALUES (@KeyId, @PublicKey, 0x00, @IsActive)
            """,
            new
            {
                KeyId = keyId,
                PublicKey = new byte[] { 1, 2, 3 },
                IsActive = isActive,
            }
        );

    [TestFixture]
    public class Given_active_and_inactive_keys : OpenIddictPublicKeyCancellationTests
    {
        private List<string> _withToken = null!;
        private List<string> _withoutToken = null!;

        [SetUp]
        public async Task Act()
        {
            await InsertKeyAsync("active", isActive: true);
            await InsertKeyAsync("inactive", isActive: false);

            using CancellationTokenSource source = new();
            _withToken =
            [
                .. (await Repository.GetActivePublicKeysInternalAsync(source.Token)).Select(key => key.KeyId),
            ];
            _withoutToken =
            [
                .. (await Repository.GetActivePublicKeysInternalAsync()).Select(key => key.KeyId),
            ];
        }

        [Test]
        public void It_returns_only_active_keys_with_a_token() => _withToken.Should().Equal("active");

        [Test]
        public void It_returns_the_same_keys_without_a_token() => _withoutToken.Should().Equal(_withToken);
    }

    [TestFixture]
    public class Given_an_already_canceled_token : OpenIddictPublicKeyCancellationTests
    {
        [Test]
        public async Task It_throws_before_reading()
        {
            using CancellationTokenSource source = new();
            await source.CancelAsync();

            Func<Task> read = () => Repository.GetActivePublicKeysInternalAsync(source.Token);

            await read.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    // The read is held behind an exclusive table lock, so it is running on the server when the token is canceled.
    [TestFixture]
    public class Given_a_read_blocked_on_the_server_when_canceled : OpenIddictPublicKeyCancellationTests
    {
        private SqlTransaction _lock = null!;
        private short _lockSession;
        private Exception? _exception;
        private TimeSpan _cancelToFault;

        [SetUp]
        public async Task Act()
        {
            _lock = (SqlTransaction)await Connection!.BeginTransactionAsync();
            _lockSession = await Connection.ExecuteScalarAsync<short>("SELECT @@SPID", transaction: _lock);
            await Connection.ExecuteAsync(
                "SELECT COUNT(*) FROM dmscs.OpenIddictKey WITH (TABLOCKX, HOLDLOCK)",
                transaction: _lock
            );

            using CancellationTokenSource source = new();
            Task read = Repository.GetActivePublicKeysInternalAsync(source.Token);

            await WaitForBlockedReadAsync();

            Stopwatch stopwatch = Stopwatch.StartNew();
            await source.CancelAsync();
            try
            {
                await read.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                _exception = exception;
            }

            _cancelToFault = stopwatch.Elapsed;
        }

        [TearDown]
        public async Task ReleaseLock() => await _lock.DisposeAsync();

        private async Task<int> BlockedRequestCountAsync()
        {
            await using SqlConnection observer = await OpenConnectionAsync();
            return await observer.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @Session",
                new { Session = _lockSession }
            );
        }

        private async Task WaitForBlockedReadAsync()
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (await BlockedRequestCountAsync() > 0)
                {
                    return;
                }

                await Task.Delay(50);
            }

            throw new AssertionException("The public-key read never reached the server's lock wait.");
        }

        // Unlike Npgsql, SqlClient reports a cancel that interrupts a running command as a SqlException, not an
        // OperationCanceledException ("A severe error occurred on the current command..." followed by "Operation
        // cancelled by user."). The second error is the server-side attention, so the token reached the running
        // statement. Callers that classify cancellation cannot rely on the exception type on this engine.
        [Test]
        public void It_fails_the_read_with_the_attention_error() =>
            _exception
                .Should()
                .BeOfType<SqlException>()
                .Which.Message.Should()
                .Contain("Operation cancelled by user.");

        [Test]
        public void It_ends_promptly_after_the_cancel() =>
            _cancelToFault.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // The server no longer holds a request waiting on the lock: the cancel (an attention signal) ended the
        // statement on the server, not just the client wait.
        [Test]
        public async Task It_leaves_no_request_waiting_on_the_lock() =>
            (await BlockedRequestCountAsync()).Should().Be(0);
    }
}
