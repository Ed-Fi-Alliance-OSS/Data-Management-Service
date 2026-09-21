// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
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
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
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

    // Client lookup accepts any casing on PostgreSQL, matching SQL Server's default collation, so
    // a client is not rejected on one engine and accepted on the other. An exact match always
    // wins, because the unique constraint on "ClientId" is case-sensitive and a database may
    // already hold rows differing only by case.
    [TestFixture]
    public class Given_A_Client_Registered_With_Mixed_Casing : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private string _canonicalClientId = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _canonicalClientId = $"Acme-Client-{Guid.NewGuid():N}";
            await RegisterApplicationAsync(_repository, _canonicalClientId);
        }

        [Test]
        public async Task It_resolves_the_client_when_the_casing_matches_exactly()
        {
            var found = await _repository.GetApplicationByClientIdAsync(_canonicalClientId);

            found!.ClientId.Should().Be(_canonicalClientId);
        }

        [Test]
        public async Task It_resolves_the_client_when_the_casing_differs()
        {
            var found = await _repository.GetApplicationByClientIdAsync(
                _canonicalClientId.ToLowerInvariant()
            );

            found!.ClientId.Should().Be(_canonicalClientId);
        }
    }

    [TestFixture]
    public class Given_Two_Clients_Differing_Only_By_Case : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private string _lowerClientId = null!;
        private string _upperClientId = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

            // The case-sensitive unique constraint permits both of these to exist at once.
            string suffix = Guid.NewGuid().ToString("N");
            _lowerClientId = $"casing-pair-{suffix}";
            _upperClientId = $"CASING-PAIR-{suffix}";
            await RegisterApplicationAsync(_repository, _lowerClientId);
            await RegisterApplicationAsync(_repository, _upperClientId);
        }

        [Test]
        public async Task It_prefers_the_exact_match_for_the_lowercase_client()
        {
            var found = await _repository.GetApplicationByClientIdAsync(_lowerClientId);

            found!.ClientId.Should().Be(_lowerClientId);
        }

        [Test]
        public async Task It_prefers_the_exact_match_for_the_uppercase_client()
        {
            var found = await _repository.GetApplicationByClientIdAsync(_upperClientId);

            found!.ClientId.Should().Be(_upperClientId);
        }

        // Neither row matches exactly, and the case-insensitive fallback matches both. Picking one
        // would make authentication depend on row order, so the lookup refuses. It must fail
        // cleanly rather than throwing out of QuerySingleOrDefaultAsync.
        [Test]
        public async Task It_refuses_an_ambiguous_case_insensitive_match_without_throwing()
        {
            string neitherExact = $"Casing-Pair-{_lowerClientId.Split('-')[^1]}";

            var found = await _repository.GetApplicationByClientIdAsync(neitherExact);

            found.Should().BeNull();
        }
    }
}
