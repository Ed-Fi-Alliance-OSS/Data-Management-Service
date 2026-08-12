// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using FluentAssertions;

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
            _repository = new OpenIddictDataRepository(MssqlTestConfiguration.DatabaseOptions);
            var applicationId = await RegisterApplicationAsync(
                _repository,
                $"delete-expired-boundary-client-{Guid.NewGuid():N}"
            );

            _tokenId = Guid.NewGuid();

            // Exercise precision that legacy SQL DATETIME cannot represent exactly.
            _boundary = new DateTimeOffset(2026, 8, 11, 22, 11, 3, TimeSpan.Zero).AddTicks(12_345);

            await _repository.StoreTokenAsync(_tokenId, applicationId, "subject-boundary", _boundary);
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
}
