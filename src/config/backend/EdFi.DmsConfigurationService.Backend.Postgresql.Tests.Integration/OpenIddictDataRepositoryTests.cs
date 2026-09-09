// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

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
            _repository = new OpenIddictDataRepository(Configuration.DatabaseOptions);
            var applicationId = await RegisterApplicationAsync(
                _repository,
                $"delete-expired-client-{Guid.NewGuid():N}"
            );

            _expiredValidTokenId = Guid.NewGuid();
            _expiredRevokedTokenId = Guid.NewGuid();
            _unexpiredTokenId = Guid.NewGuid();

            var past = DateTimeOffset.UtcNow.AddDays(-1);
            var future = DateTimeOffset.UtcNow.AddDays(1);

            await _repository.StoreTokenAsync(
                _expiredValidTokenId,
                applicationId,
                "subject-expired-valid",
                past
            );
            await _repository.StoreTokenAsync(
                _expiredRevokedTokenId,
                applicationId,
                "subject-expired-revoked",
                past
            );
            await _repository.RevokeTokenAsync(_expiredRevokedTokenId);
            await _repository.StoreTokenAsync(_unexpiredTokenId, applicationId, "subject-unexpired", future);
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
            _repository = new OpenIddictDataRepository(Configuration.DatabaseOptions);
            var applicationId = await RegisterApplicationAsync(
                _repository,
                $"delete-expired-boundary-client-{Guid.NewGuid():N}"
            );

            _tokenId = Guid.NewGuid();

            // Truncate to whole microseconds so PostgreSQL's timestamp column round-trips the value
            // exactly, proving the boundary predicate ("<=") deletes a row whose expiration equals the bound.
            var now = DateTimeOffset.UtcNow;
            _boundary = now.AddTicks(-(now.Ticks % 10));

            await _repository.StoreTokenAsync(_tokenId, applicationId, "subject-boundary", _boundary);
        }

        [Test]
        public async Task It_deletes_a_token_whose_expiration_exactly_equals_the_bound()
        {
            var deletedCount = await _repository.DeleteExpiredTokensAsync(_boundary);

            deletedCount.Should().Be(1);
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().BeNull();
        }
    }

    /// <summary>
    /// DMS-1430. The repository binds expirations and the cleanup bound as instants, and the column
    /// stores instants, so neither the stored value nor the sweep may depend on the PostgreSQL
    /// session time zone. This fixture runs the real repository against a connection whose session
    /// zone observes DST, and picks two instants that a wall-clock column provably cannot tell
    /// apart, so the assertions fail if either binding or the column type regresses.
    /// </summary>
    [TestFixture]
    public class Given_A_DST_Observing_PostgreSQL_Session_Time_Zone : OpenIddictDataRepositoryTests
    {
        private const string DstSessionTimeZone = "America/New_York";

        // America/New_York leaves DST at 2026-11-01 06:00Z, where 01:59:59 EDT (-04) is followed by
        // 01:00:00 EST (-05). Both instants below therefore land on local wall clock 01:30 despite
        // being an hour apart, which is exactly the collision a timestamp without time zone column
        // cannot represent. Fixed instants, so the fixture never depends on the current date.
        private static readonly DateTimeOffset EarlierExpiration = new(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset LaterExpiration = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

        // The bound the cleanup service would sweep with at 06:00Z, namely UTC now minus the
        // validator's five-minute clock skew. The earlier expiration sits 25 minutes past this
        // bound and must be deleted, while the later one is still 35 minutes away and must survive.
        private static readonly DateTimeOffset SweepBound = new(2026, 11, 1, 5, 55, 0, TimeSpan.Zero);

        private OpenIddictDataRepository _repository = null!;
        private Guid _earlierTokenId;
        private Guid _laterTokenId;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(DstSessionDatabaseOptions());
            var applicationId = await RegisterApplicationAsync(
                _repository,
                $"dst-session-client-{Guid.NewGuid():N}"
            );

            _earlierTokenId = Guid.NewGuid();
            _laterTokenId = Guid.NewGuid();

            await _repository.StoreTokenAsync(
                _earlierTokenId,
                applicationId,
                "subject-earlier",
                EarlierExpiration
            );
            await _repository.StoreTokenAsync(_laterTokenId, applicationId, "subject-later", LaterExpiration);
        }

        [Test]
        public async Task It_stores_each_expiration_as_a_distinct_instant()
        {
            (await StoredExpirationAsync(_earlierTokenId)).Should().Be(EarlierExpiration.UtcDateTime);
            (await StoredExpirationAsync(_laterTokenId)).Should().Be(LaterExpiration.UtcDateTime);
        }

        [Test]
        public async Task It_deletes_only_the_token_that_is_truly_expired()
        {
            var deletedCount = await _repository.DeleteExpiredTokensAsync(SweepBound);

            deletedCount.Should().Be(1);
            (await _repository.GetTokenStatusAsync(_earlierTokenId)).Should().BeNull();
            (await _repository.GetTokenStatusAsync(_laterTokenId)).Should().Be("valid");
        }

        /// <summary>
        /// Narrow read-path check: the sweep is the safety-critical path, but the same column feeds
        /// TokenInfo, so this pins that the stored instant also comes back out unchanged rather than
        /// being reinterpreted through the session zone on the way.
        /// </summary>
        [Test]
        public async Task It_reads_the_expiration_back_as_the_same_instant()
        {
            var token = await _repository.GetTokenByIdAsync(_laterTokenId);

            token.Should().NotBeNull();
            token!.ExpirationDate.Should().Be(LaterExpiration);
        }

        /// <summary>
        /// Reads the raw column through a connection using the same DST session zone, so a value
        /// that only looks correct because it was read back under UTC cannot pass.
        /// </summary>
        private static async Task<DateTime> StoredExpirationAsync(Guid tokenId)
        {
            await using NpgsqlConnection connection = new(DstSessionConnectionString());
            await connection.OpenAsync();

            return await connection.QuerySingleAsync<DateTime>(
                "SELECT \"ExpirationDate\" FROM \"dmscs\".\"OpenIddictToken\" WHERE \"Id\" = @Id",
                new { Id = tokenId }
            );
        }

        private static string DstSessionConnectionString() =>
            new NpgsqlConnectionStringBuilder(Configuration.DatabaseOptions.Value.DatabaseConnection)
            {
                Timezone = DstSessionTimeZone,
            }.ConnectionString;

        private static IOptions<DatabaseOptions> DstSessionDatabaseOptions() =>
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = DstSessionConnectionString(),
                    EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
                }
            );
    }
}
