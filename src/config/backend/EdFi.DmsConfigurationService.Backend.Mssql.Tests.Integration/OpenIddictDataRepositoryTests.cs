// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class OpenIddictDataRepositoryTests : DatabaseTest
{
    protected static async Task<Guid> RegisterApplicationAsync(
        OpenIddictDataRepository repository,
        string clientId
    )
    {
        var applicationId = Guid.NewGuid();

        await repository.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                await repository.InsertApplicationAsync(
                    applicationId,
                    clientId,
                    "hashed-secret",
                    "Integration Test Client",
                    ["token", "authorization"],
                    ["require_pkce"],
                    "confidential",
                    """[{"claim.name":"namespacePrefixes","claim.value":"uri://ed-fi.org","jsonType.label":"String"}]""",
                    connection,
                    transaction
                );
            }
        );

        return applicationId;
    }

    /// <summary>
    /// Any value below 1 turns enforcement off, which is what seeding and the sweep fixtures want.
    /// </summary>
    protected const int EnforcementDisabled = 0;

    /// <summary>
    /// Far enough out that no test's runtime can expire it, and far enough from "now" that the
    /// comparison never depends on sub-second precision.
    /// </summary>
    protected static DateTimeOffset FarFuture => DateTimeOffset.UtcNow.AddDays(1);

    protected static DateTimeOffset FarPast => DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>
    /// Stores <paramref name="count"/> active tokens with enforcement off, so seeding a client up
    /// to or past its limit is never itself refused.
    /// </summary>
    protected static async Task SeedActiveTokensAsync(
        OpenIddictDataRepository repository,
        Guid applicationId,
        int count
    )
    {
        for (int i = 0; i < count; i++)
        {
            TokenStoreOutcome outcome = await repository.StoreTokenAsync(
                Guid.NewGuid(),
                applicationId,
                $"seed-subject-{i}",
                FarFuture,
                EnforcementDisabled
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
        }
    }

    protected static async Task<int> TokenRowCountAsync(Guid applicationId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.OpenIddictToken WHERE ApplicationId = @ApplicationId",
            new { ApplicationId = applicationId }
        );
    }

    protected static async Task<int> ActiveTokenCountAsync(Guid applicationId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM dmscs.OpenIddictToken
              WHERE ApplicationId = @ApplicationId
                AND Status = 'valid'
                AND ExpirationDate > SYSUTCDATETIME()",
            new { ApplicationId = applicationId }
        );
    }

    protected static async Task DeleteApplicationRowAsync(Guid applicationId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dmscs.OpenIddictApplication WHERE Id = @Id",
            new { Id = applicationId }
        );
    }

    /// <summary>
    /// A second session holding the client's <c>OpenIddictApplication</c> row lock, which is what
    /// a competing grant has to wait on.
    /// </summary>
    protected sealed class RowLockHolder : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;

        private RowLockHolder(SqlConnection connection, SqlTransaction transaction, int sessionId)
        {
            _connection = connection;
            _transaction = transaction;
            SessionId = sessionId;
        }

        public int SessionId { get; }

        public static async Task<RowLockHolder> TakeAsync(Guid applicationId)
        {
            SqlConnection connection = await OpenConnectionAsync();
            SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            await connection.ExecuteAsync(
                "SELECT Id FROM dmscs.OpenIddictApplication WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                new { Id = applicationId },
                transaction
            );

            int sessionId = await connection.ExecuteScalarAsync<int>(
                "SELECT @@SPID",
                transaction: transaction
            );

            return new RowLockHolder(connection, transaction, sessionId);
        }

        public async Task ReleaseAsync() => await _transaction.RollbackAsync();

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Polls until another session is waiting on a lock this holder owns, which is the only
    /// evidence that establishes the grant is blocked <em>on the lock</em>. "The call has not
    /// returned yet" is not evidence: an unfinished call may simply be waiting on the thread pool
    /// or on a connection, and accepting that would let broken locking pass.
    /// </summary>
    protected static async Task WaitUntilASessionIsBlockedBy(int holderSessionId, TimeSpan timeout)
    {
        // Either direct evidence from the lock manager - a waiting request on a resource the
        // holder already owns - or the engine's own blocking_session_id attribution.
        const string BlockedByHolderSql = """
            SELECT COUNT(*)
            FROM (
                SELECT waiter.request_session_id
                FROM sys.dm_tran_locks waiter
                JOIN sys.dm_tran_locks holder
                  ON waiter.resource_description = holder.resource_description
                 AND waiter.resource_type = holder.resource_type
                 AND waiter.resource_database_id = holder.resource_database_id
                 AND waiter.resource_associated_entity_id = holder.resource_associated_entity_id
                WHERE holder.request_session_id = @HolderSessionId
                  AND holder.request_status = 'GRANT'
                  AND waiter.request_status = 'WAIT'
                  AND waiter.request_session_id <> holder.request_session_id
                UNION
                SELECT session_id
                FROM sys.dm_exec_requests
                WHERE blocking_session_id = @HolderSessionId
            ) blocked
            """;

        await using SqlConnection connection = await OpenConnectionAsync();
        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            int waiters = await connection.ExecuteScalarAsync<int>(
                BlockedByHolderSql,
                new { HolderSessionId = holderSessionId }
            );

            if (waiters > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            $"No session was observed waiting on the row lock held by session {holderSessionId} "
                + $"within {timeout.TotalSeconds:0.#}s, so the grant was never proven to be blocked on it."
        );
    }

    [TestFixture]
    public class Given_An_Inserted_Openiddict_Application : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private readonly Guid _applicationId = Guid.NewGuid();
        private readonly Guid _scopeId = Guid.NewGuid();
        private readonly Guid _roleId = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);

            await _repository.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    await _repository.InsertApplicationAsync(
                        _applicationId,
                        "integration-test-client",
                        "hashed-secret",
                        "Integration Test Client",
                        ["token", "authorization"],
                        ["require_pkce"],
                        "confidential",
                        """[{"claim.name":"namespacePrefixes","claim.value":"uri://ed-fi.org","jsonType.label":"String"}]""",
                        connection,
                        transaction
                    );
                    await _repository.InsertScopeAsync(
                        _scopeId,
                        "edfi_admin_api/full_access",
                        connection,
                        transaction
                    );
                    await _repository.InsertApplicationScopeAsync(
                        _applicationId,
                        _scopeId,
                        connection,
                        transaction
                    );
                    await _repository.InsertRoleAsync(_roleId, "cms-client", connection, transaction);
                    await _repository.InsertClientRoleAsync(_applicationId, _roleId, connection, transaction);
                }
            );
        }

        [Test]
        public async Task It_round_trips_json_array_columns_as_arrays()
        {
            var application = await _repository.GetApplicationByClientIdAsync("integration-test-client");

            application.Should().NotBeNull();
            application!.Permissions.Should().BeEquivalentTo("token", "authorization");
            application.Requirements.Should().BeEquivalentTo("require_pkce");
            application.ProtocolMappers.Should().Contain("namespacePrefixes");
        }

        [Test]
        public async Task It_returns_linked_scopes_and_roles()
        {
            var application = await _repository.GetApplicationByClientIdAsync("integration-test-client");
            var roles = await _repository.GetClientRolesAsync(_applicationId);

            application!.Scopes.Should().BeEquivalentTo("edfi_admin_api/full_access");
            roles.Should().BeEquivalentTo("cms-client");
        }

        [Test]
        public async Task It_defaults_is_approved_to_true_without_api_clients()
        {
            var application = await _repository.GetApplicationByClientIdAsync("integration-test-client");

            application!.IsApproved.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Expired_And_Unexpired_Tokens : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _expiredValidTokenId;
        private Guid _expiredRevokedTokenId;
        private Guid _unexpiredTokenId;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            var applicationId = await RegisterApplicationAsync(
                _repository,
                $"delete-expired-client-{Guid.NewGuid():N}"
            );

            _expiredValidTokenId = Guid.NewGuid();
            _expiredRevokedTokenId = Guid.NewGuid();
            _unexpiredTokenId = Guid.NewGuid();

            var past = DateTimeOffset.UtcNow.AddDays(-1);
            var future = DateTimeOffset.UtcNow.AddDays(1);

            // EnforcementDisabled: these fixtures are about the cleanup sweep, not the limit.
            await _repository.StoreTokenAsync(
                _expiredValidTokenId,
                applicationId,
                "subject-expired-valid",
                past,
                EnforcementDisabled
            );
            await _repository.StoreTokenAsync(
                _expiredRevokedTokenId,
                applicationId,
                "subject-expired-revoked",
                past,
                EnforcementDisabled
            );
            await _repository.RevokeTokenAsync(_expiredRevokedTokenId);
            await _repository.StoreTokenAsync(
                _unexpiredTokenId,
                applicationId,
                "subject-unexpired",
                future,
                EnforcementDisabled
            );
        }

        [Test]
        public async Task It_deletes_only_the_expired_tokens_regardless_of_status()
        {
            var deletedCount = await _repository.DeleteExpiredTokensAsync(DateTimeOffset.UtcNow);

            deletedCount.Should().Be(2);
            (await _repository.GetTokenStatusAsync(_expiredValidTokenId)).Should().BeNull();
            (await _repository.GetTokenStatusAsync(_expiredRevokedTokenId)).Should().BeNull();
            (await _repository.GetTokenStatusAsync(_unexpiredTokenId)).Should().Be("valid");
        }
    }

    [TestFixture]
    public class Given_A_Token_At_The_Expiration_Boundary : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _tokenId;
        private DateTimeOffset _boundary;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            var applicationId = await RegisterApplicationAsync(
                _repository,
                $"delete-expired-boundary-client-{Guid.NewGuid():N}"
            );

            _tokenId = Guid.NewGuid();

            // Exercise precision that legacy SQL DATETIME cannot represent exactly.
            _boundary = new DateTimeOffset(2026, 8, 11, 22, 11, 3, TimeSpan.Zero).AddTicks(12_345);

            await _repository.StoreTokenAsync(
                _tokenId,
                applicationId,
                "subject-boundary",
                _boundary,
                EnforcementDisabled
            );
        }

        [Test]
        public async Task It_preserves_the_full_datetime2_precision()
        {
            await using var connection = await OpenConnectionAsync();
            DateTime storedExpiration = await connection.QuerySingleAsync<DateTime>(
                "SELECT ExpirationDate FROM dmscs.OpenIddictToken WHERE Id = @Id",
                new { Id = _tokenId }
            );

            storedExpiration.Should().Be(_boundary.UtcDateTime);
        }

        [Test]
        public async Task It_deletes_a_token_whose_expiration_exactly_equals_the_bound()
        {
            var deletedCount = await _repository.DeleteExpiredTokensAsync(_boundary);

            deletedCount.Should().Be(1);
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Client_Below_Its_Token_Limit : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            _applicationId = await RegisterApplicationAsync(_repository, $"under-limit-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, 2);

            _outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-under-limit",
                FarFuture,
                3
            );
        }

        [Test]
        public void It_stores_the_token() => _outcome.Should().Be(TokenStoreOutcome.Stored);

        [Test]
        public async Task It_writes_the_row() => (await TokenRowCountAsync(_applicationId)).Should().Be(3);
    }

    [TestFixture]
    public class Given_A_Client_Exactly_At_Its_Token_Limit : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            _applicationId = await RegisterApplicationAsync(_repository, $"at-limit-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, 3);

            _outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-at-limit",
                FarFuture,
                3
            );
        }

        [Test]
        public void It_refuses_the_grant() => _outcome.Should().Be(TokenStoreOutcome.LimitExceeded);

        // The return value alone would also be satisfied by a store that wrote the row and then
        // reported a refusal.
        [Test]
        public async Task It_inserts_no_row() => (await TokenRowCountAsync(_applicationId)).Should().Be(3);
    }

    [TestFixture]
    public class Given_A_Client_Above_Its_Token_Limit : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            _applicationId = await RegisterApplicationAsync(_repository, $"over-limit-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, 6);

            _outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-over-limit",
                FarFuture,
                3
            );
        }

        [Test]
        public void It_refuses_the_grant() => _outcome.Should().Be(TokenStoreOutcome.LimitExceeded);

        [Test]
        public async Task It_inserts_no_row() => (await TokenRowCountAsync(_applicationId)).Should().Be(6);
    }

    /// <summary>
    /// Everything the count must ignore. Each case seeds the client past its limit with rows that
    /// do not qualify as active, so a grant that is refused here means the predicate is wrong.
    /// These are not redundant with the PostgreSQL fixtures: the enforcement is engine-specific
    /// SQL, and this engine binds the comparison as DATETIME2 from <c>expiration.UtcDateTime</c>.
    /// </summary>
    [TestFixture]
    public class Given_Rows_That_Do_Not_Count_Toward_The_Limit : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);

        private async Task<TokenStoreOutcome> GrantAfter(
            Func<Guid, Task> seed,
            string clientId,
            int maxActiveTokens = 1
        )
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"{clientId}-{Guid.NewGuid():N}"
            );
            await seed(applicationId);

            return await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                applicationId,
                "subject-grant",
                FarFuture,
                maxActiveTokens
            );
        }

        [Test]
        public async Task It_ignores_expired_rows()
        {
            TokenStoreOutcome outcome = await GrantAfter(
                async applicationId =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        await _repository.StoreTokenAsync(
                            Guid.NewGuid(),
                            applicationId,
                            $"expired-{i}",
                            FarPast,
                            EnforcementDisabled
                        );
                    }
                },
                "expired-rows"
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
        }

        [Test]
        public async Task It_ignores_revoked_rows()
        {
            TokenStoreOutcome outcome = await GrantAfter(
                async applicationId =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Guid tokenId = Guid.NewGuid();
                        await _repository.StoreTokenAsync(
                            tokenId,
                            applicationId,
                            $"revoked-{i}",
                            FarFuture,
                            EnforcementDisabled
                        );
                        await _repository.RevokeTokenAsync(tokenId);
                    }
                },
                "revoked-rows"
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
        }

        // Multi-second margins on either side of "now": the repository computes its own UtcNow
        // inside the statement, so the comparison instant cannot be pinned without injecting a
        // clock, and a clock abstraction would be production API surface existing only for a test.
        [Test]
        public async Task It_counts_a_row_expiring_two_minutes_from_now()
        {
            TokenStoreOutcome outcome = await GrantAfter(
                async applicationId =>
                    await _repository.StoreTokenAsync(
                        Guid.NewGuid(),
                        applicationId,
                        "expiring-soon",
                        DateTimeOffset.UtcNow.AddMinutes(2),
                        EnforcementDisabled
                    ),
                "future-boundary"
            );

            outcome.Should().Be(TokenStoreOutcome.LimitExceeded);
        }

        [Test]
        public async Task It_does_not_count_a_row_that_expired_two_minutes_ago()
        {
            TokenStoreOutcome outcome = await GrantAfter(
                async applicationId =>
                    await _repository.StoreTokenAsync(
                        Guid.NewGuid(),
                        applicationId,
                        "just-expired",
                        DateTimeOffset.UtcNow.AddMinutes(-2),
                        EnforcementDisabled
                    ),
                "past-boundary"
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
        }

        [Test]
        public async Task It_ignores_another_clients_active_tokens()
        {
            Guid otherApplicationId = await RegisterApplicationAsync(
                _repository,
                $"other-client-{Guid.NewGuid():N}"
            );
            await SeedActiveTokensAsync(_repository, otherApplicationId, 5);

            TokenStoreOutcome outcome = await GrantAfter(_ => Task.CompletedTask, "isolated-count");

            outcome.Should().Be(TokenStoreOutcome.Stored);
        }
    }

    [TestFixture]
    public class Given_A_Limit_Of_One : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private TokenStoreOutcome _first;
        private TokenStoreOutcome _second;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            _applicationId = await RegisterApplicationAsync(_repository, $"limit-one-{Guid.NewGuid():N}");

            _first = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-first",
                FarFuture,
                1
            );
            _second = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-second",
                FarFuture,
                1
            );
        }

        [Test]
        public void It_admits_the_first_grant() => _first.Should().Be(TokenStoreOutcome.Stored);

        [Test]
        public void It_refuses_the_second_grant() => _second.Should().Be(TokenStoreOutcome.LimitExceeded);

        [Test]
        public async Task It_stores_exactly_one_row() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(1);
    }

    /// <summary>
    /// The opt-out an operator configures with a value below 1. It stores unconditionally however
    /// many active tokens the client already holds.
    /// </summary>
    [TestFixture]
    public class Given_Enforcement_Is_Disabled : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);

        [TestCase(0)]
        [TestCase(-1)]
        public async Task It_stores_regardless_of_how_many_active_tokens_exist(int maxActiveTokens)
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"disabled-{maxActiveTokens}-{Guid.NewGuid():N}"
            );
            await SeedActiveTokensAsync(_repository, applicationId, 20);

            TokenStoreOutcome outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                applicationId,
                "subject-disabled",
                FarFuture,
                maxActiveTokens
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
            (await TokenRowCountAsync(applicationId)).Should().Be(21);
        }

        /// <summary>
        /// The zero-cost property operators opt into: the disabled path takes no row lock, so a
        /// grant completes even while another session holds that client's application row.
        /// </summary>
        [TestCase(0)]
        [TestCase(-1)]
        public async Task It_stores_without_taking_the_application_row_lock(int maxActiveTokens)
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"disabled-nolock-{maxActiveTokens}-{Guid.NewGuid():N}"
            );

            await using RowLockHolder holder = await RowLockHolder.TakeAsync(applicationId);

            Task<TokenStoreOutcome> grant = Task.Run(() =>
                _repository.StoreTokenAsync(
                    Guid.NewGuid(),
                    applicationId,
                    "subject-disabled-nolock",
                    FarFuture,
                    maxActiveTokens
                )
            );

            Task completed = await Task.WhenAny(grant, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().BeSameAs(grant, "the disabled path must not wait on the row lock");
            (await grant).Should().Be(TokenStoreOutcome.Stored);

            await holder.ReleaseAsync();
        }
    }

    /// <summary>
    /// Tier 1 of the concurrency coverage: cheap, and loud when enforcement is badly broken. It is
    /// NOT proof of serialization - the thread pool can serialize these calls on its own - which is
    /// what <see cref="Given_A_Held_Application_Row_Lock"/> exists to establish.
    /// </summary>
    [TestFixture]
    public class Given_Competing_Grants_For_One_Remaining_Slot : OpenIddictDataRepositoryTests
    {
        private const int Limit = 3;
        private const int Competitors = 8;

        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private TokenStoreOutcome[] _outcomes = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            _applicationId = await RegisterApplicationAsync(_repository, $"race-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, Limit - 1);

            _outcomes = await Task.WhenAll(
                Enumerable
                    .Range(0, Competitors)
                    .Select(i =>
                        Task.Run(() =>
                            _repository.StoreTokenAsync(
                                Guid.NewGuid(),
                                _applicationId,
                                $"race-subject-{i}",
                                FarFuture,
                                Limit
                            )
                        )
                    )
            );
        }

        [Test]
        public void It_admits_exactly_one_grant() =>
            _outcomes.Count(outcome => outcome == TokenStoreOutcome.Stored).Should().Be(1);

        [Test]
        public void It_refuses_every_other_grant() =>
            _outcomes
                .Count(outcome => outcome == TokenStoreOutcome.LimitExceeded)
                .Should()
                .Be(Competitors - 1);

        [Test]
        public async Task It_leaves_the_active_token_count_exactly_at_the_limit() =>
            (await ActiveTokenCountAsync(_applicationId)).Should().Be(Limit);
    }

    /// <summary>
    /// Tier 2: the proof the strict ceiling actually rests on. A second session holds the client's
    /// application row, and the grant is shown to be waiting on that specific lock before the
    /// holder is released.
    /// </summary>
    [TestFixture]
    public class Given_A_Held_Application_Row_Lock : OpenIddictDataRepositoryTests
    {
        private static readonly TimeSpan _blockedObservationTimeout = TimeSpan.FromSeconds(4);

        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);

        [Test]
        public async Task It_blocks_a_grant_for_that_client_until_the_lock_is_released()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"serialized-{Guid.NewGuid():N}"
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(applicationId);
            try
            {
                Task<TokenStoreOutcome> grant = Task.Run(() =>
                    _repository.StoreTokenAsync(
                        Guid.NewGuid(),
                        applicationId,
                        "subject-serialized",
                        FarFuture,
                        5
                    )
                );

                await WaitUntilASessionIsBlockedBy(holder.SessionId, _blockedObservationTimeout);

                grant.IsCompleted.Should().BeFalse("the grant is waiting on the row lock the holder owns");

                await holder.ReleaseAsync();

                (await grant).Should().Be(TokenStoreOutcome.Stored);
            }
            finally
            {
                await holder.DisposeAsync();
            }
        }

        /// <summary>
        /// The lock is per application, not a global serialization point. "Both eventually
        /// succeed" would prove nothing; client B completing <em>while A is still blocked</em> is
        /// the whole assertion.
        /// </summary>
        [Test]
        public async Task It_does_not_block_a_grant_for_a_different_client()
        {
            Guid blockedApplicationId = await RegisterApplicationAsync(
                _repository,
                $"client-a-{Guid.NewGuid():N}"
            );
            Guid otherApplicationId = await RegisterApplicationAsync(
                _repository,
                $"client-b-{Guid.NewGuid():N}"
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(blockedApplicationId);
            try
            {
                Task<TokenStoreOutcome> blockedGrant = Task.Run(() =>
                    _repository.StoreTokenAsync(
                        Guid.NewGuid(),
                        blockedApplicationId,
                        "subject-a",
                        FarFuture,
                        5
                    )
                );

                await WaitUntilASessionIsBlockedBy(holder.SessionId, _blockedObservationTimeout);

                TokenStoreOutcome otherOutcome = await _repository.StoreTokenAsync(
                    Guid.NewGuid(),
                    otherApplicationId,
                    "subject-b",
                    FarFuture,
                    5
                );

                otherOutcome.Should().Be(TokenStoreOutcome.Stored);
                blockedGrant
                    .IsCompleted.Should()
                    .BeFalse("client B's grant must not have waited for client A's lock");

                await holder.ReleaseAsync();
                (await blockedGrant).Should().Be(TokenStoreOutcome.Stored);
            }
            finally
            {
                await holder.DisposeAsync();
            }
        }

        /// <summary>
        /// Contention is not a limit rejection. Held past the repository's LOCK_TIMEOUT, the grant
        /// throws, which reaches the caller as a sanitized 500 rather than "Too Many Tokens".
        /// </summary>
        [Test]
        public async Task It_throws_rather_than_reporting_a_limit_rejection_when_the_wait_times_out()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"lock-timeout-{Guid.NewGuid():N}"
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(applicationId);
            try
            {
                Func<Task> grant = () =>
                    _repository.StoreTokenAsync(
                        Guid.NewGuid(),
                        applicationId,
                        "subject-lock-timeout",
                        FarFuture,
                        5
                    );

                // 1222 is "Lock request time out period exceeded", which SET LOCK_TIMEOUT raises.
                (await grant.Should().ThrowAsync<SqlException>())
                    .Which.Number.Should()
                    .Be(1222);

                (await TokenRowCountAsync(applicationId)).Should().Be(0);
            }
            finally
            {
                await holder.ReleaseAsync();
                await holder.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The missing-row guard. A client deleted between the application lookup and the store must
    /// be reported as gone, never as over its limit, and must not receive a stored token. On this
    /// engine UPDLOCK+HOLDLOCK takes a key-range lock even for an absent key, so serialization
    /// survives the deletion - but without the guard the client would still be handed a usable
    /// token, which is what these assertions pin.
    /// </summary>
    [TestFixture]
    public class Given_The_Application_Row_Was_Deleted : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);

        [Test]
        public async Task It_reports_the_client_as_not_found_and_stores_nothing()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"deleted-client-{Guid.NewGuid():N}"
            );
            await DeleteApplicationRowAsync(applicationId);

            TokenStoreOutcome outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                applicationId,
                "subject-deleted",
                FarFuture,
                5
            );

            outcome.Should().Be(TokenStoreOutcome.ClientNotFound);
            outcome.Should().NotBe(TokenStoreOutcome.LimitExceeded);
            (await TokenRowCountAsync(applicationId)).Should().Be(0);
        }

        [Test]
        public async Task It_does_not_let_competing_grants_exceed_the_cap_once_the_row_is_gone()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"deleted-race-{Guid.NewGuid():N}"
            );
            await DeleteApplicationRowAsync(applicationId);

            TokenStoreOutcome[] outcomes = await Task.WhenAll(
                Enumerable
                    .Range(0, 8)
                    .Select(i =>
                        Task.Run(() =>
                            _repository.StoreTokenAsync(
                                Guid.NewGuid(),
                                applicationId,
                                $"deleted-race-subject-{i}",
                                FarFuture,
                                3
                            )
                        )
                    )
            );

            outcomes.Should().AllBeEquivalentTo(TokenStoreOutcome.ClientNotFound);
            (await TokenRowCountAsync(applicationId)).Should().Be(0);
        }
    }
}
