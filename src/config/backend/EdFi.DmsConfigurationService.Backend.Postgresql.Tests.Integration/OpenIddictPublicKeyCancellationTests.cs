// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The cancellable public-key read (spec D-8) against a real server: the query and results are unchanged, and a
/// canceled token stops the open or the running query on the server. The respawner does not reset
/// <c>OpenIddictKey</c>, so these fixtures delete the rows they insert.
/// </summary>
[NonParallelizable]
public class OpenIddictPublicKeyCancellationTests : DatabaseTest
{
    private const string KeyIdPrefix = "dms1556-cancellation-";

    protected OpenIddictDataRepository Repository { get; } = new(Configuration.DatabaseOptions);

    protected async Task InsertKeyAsync(string keyId, bool isActive) =>
        await Connection!.ExecuteAsync(
            """
            INSERT INTO "dmscs"."OpenIddictKey" ("KeyId", "PublicKey", "PrivateKey", "IsActive")
            VALUES (@KeyId, @PublicKey, 'unused', @IsActive)
            """,
            new
            {
                KeyId = KeyIdPrefix + keyId,
                PublicKey = new byte[] { 1, 2, 3 },
                IsActive = isActive,
            }
        );

    [TearDown]
    public async Task DeleteInsertedKeys()
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM \"dmscs\".\"OpenIddictKey\" WHERE \"KeyId\" LIKE @Prefix",
            new { Prefix = KeyIdPrefix + "%" }
        );
    }

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
            _withToken = Ours(await Repository.GetActivePublicKeysInternalAsync(source.Token));
            _withoutToken = Ours(await Repository.GetActivePublicKeysInternalAsync());
        }

        private static List<string> Ours(IEnumerable<(string KeyId, byte[] PublicKey)> keys) =>
            [
                .. keys.Select(key => key.KeyId)
                    .Where(keyId => keyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal)),
            ];

        [Test]
        public void It_returns_only_active_keys_with_a_token() =>
            _withToken.Should().Equal(KeyIdPrefix + "active");

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
        private NpgsqlTransaction _lock = null!;
        private int _blockedPid;
        private Exception? _exception;
        private TimeSpan _cancelToFault;

        [SetUp]
        public async Task Act()
        {
            _lock = await Connection!.BeginTransactionAsync();
            await Connection.ExecuteAsync(
                "LOCK TABLE \"dmscs\".\"OpenIddictKey\" IN ACCESS EXCLUSIVE MODE",
                transaction: _lock
            );

            using CancellationTokenSource source = new();
            Task read = Repository.GetActivePublicKeysInternalAsync(source.Token);

            _blockedPid = await WaitForBlockedReadAsync();

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

        private async Task<int> WaitForBlockedReadAsync()
        {
            await using NpgsqlConnection observer = await DataSource!.OpenConnectionAsync();
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                int? pid = await observer.QuerySingleOrDefaultAsync<int?>(
                    """
                    SELECT pid FROM pg_stat_activity
                    WHERE wait_event_type = 'Lock' AND state = 'active'
                      AND query LIKE 'SELECT "KeyId", "PublicKey" FROM "dmscs"."OpenIddictKey"%'
                    """
                );
                if (pid is not null)
                {
                    return pid.Value;
                }

                await Task.Delay(50);
            }

            throw new AssertionException("The public-key read never reached the server's lock wait.");
        }

        [Test]
        public void It_fails_the_read_as_canceled() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();

        // SQLSTATE 57014 (query_canceled) is the server's answer to the cancel request, so the token reached the
        // running statement, not just the client-side await.
        [Test]
        public void It_cancels_the_statement_on_the_server() =>
            _exception!
                .InnerException.Should()
                .BeOfType<PostgresException>()
                .Which.SqlState.Should()
                .Be("57014");

        [Test]
        public void It_ends_promptly_after_the_cancel() =>
            _cancelToFault.Should().BeLessThan(TimeSpan.FromSeconds(5));

        [Test]
        public async Task It_leaves_no_statement_waiting_on_the_lock()
        {
            await using NpgsqlConnection observer = await DataSource!.OpenConnectionAsync();
            int waiting = await observer.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pg_stat_activity WHERE pid = @Pid AND wait_event_type = 'Lock' AND state = 'active'",
                new { Pid = _blockedPid }
            );

            waiting.Should().Be(0);
        }
    }
}
