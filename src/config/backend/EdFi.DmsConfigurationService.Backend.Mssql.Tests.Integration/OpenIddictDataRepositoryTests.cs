// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class OpenIddictDataRepositoryTests : DatabaseTest
{
    /// <summary>
    /// The stored secret hash every <see cref="RegisterApplicationAsync"/> application carries,
    /// and so the one a store re-validates against unless a test changed it (DMS-1493 D-11).
    /// </summary>
    protected const string RegisteredSecret = "hashed-secret";

    /// <summary>
    /// How long a grant test waits to observe a store blocked on a held row lock. It stays well
    /// under the store's own 5 s lock timeout (<c>OpenIddictDataRepository.LockTimeoutMilliseconds</c>),
    /// after which the grant stops waiting and answers <see cref="TokenStoreOutcome.LockTimeout"/>;
    /// the probe is raced against the grant, so a grant that finishes first fails the test
    /// naming its outcome instead (DMS-1493 R2-F1).
    /// </summary>
    protected static readonly TimeSpan GrantBlockedObservationTimeout = TimeSpan.FromSeconds(3);

    protected static async Task<Guid> RegisterApplicationAsync(
        OpenIddictDataRepository repository,
        string clientId,
        params string[] scopes
    )
    {
        var applicationId = Guid.NewGuid();

        await repository.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                await repository.InsertApplicationAsync(
                    applicationId,
                    clientId,
                    RegisteredSecret,
                    "Integration Test Client",
                    scopes.Length > 0 ? scopes : ["token", "authorization"],
                    ["require_pkce"],
                    "confidential",
                    """[{"claim.name":"namespacePrefixes","claim.value":"uri://ed-fi.org","jsonType.label":"String"}]""",
                    connection,
                    transaction
                );
                foreach (string scope in scopes)
                {
                    Guid scopeId;
                    if (
                        await repository.FindScopeIdByNameAsync(scope, connection, transaction) is
                        { } existingId
                    )
                    {
                        scopeId = existingId;
                    }
                    else
                    {
                        scopeId = Guid.NewGuid();
                        await repository.InsertScopeAsync(scopeId, scope, connection, transaction);
                    }
                    await repository.InsertApplicationScopeAsync(
                        applicationId,
                        scopeId,
                        connection,
                        transaction
                    );
                }
            }
        );

        return applicationId;
    }

    protected static Task AddApiClientRowAsync(
        OpenIddictDataRepository repository,
        string clientId,
        bool isApproved = true
    ) =>
        repository.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                int vendorId = await connection.ExecuteScalarAsync<int>(
                    "INSERT INTO dmscs.Vendor (Company) OUTPUT INSERTED.Id VALUES (@Company)",
                    new { Company = $"token-limit-{Guid.NewGuid():N}" },
                    transaction
                );
                int applicationId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName)
                    OUTPUT INSERTED.Id VALUES ('Token limit test', @VendorId, 'Test claim set')
                    """,
                    new { VendorId = vendorId },
                    transaction
                );
                await connection.ExecuteAsync(
                    """
                    INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved)
                    VALUES (@ApplicationId, @ClientId, @ClientUuid, 'Token limit test', @IsApproved)
                    """,
                    new
                    {
                        ApplicationId = applicationId,
                        ClientId = clientId,
                        ClientUuid = Guid.NewGuid(),
                        IsApproved = isApproved,
                    },
                    transaction
                );
            }
        );

    protected static OpenIddictTokenManager CreateTokenManager(
        OpenIddictDataRepository dataRepository,
        IdentityOptions options
    ) => CreateTokenManagerOver(dataRepository, options).Manager;

    /// <summary>
    /// A token manager over a fake that wraps <paramref name="dataRepository"/>, returned with that
    /// fake so a test can observe the reads. The manager authenticates from the real credential
    /// query.
    /// </summary>
    protected static (
        OpenIddictTokenManager Manager,
        IOpenIddictDataRepository ObservedRepository
    ) CreateTokenManagerOver(OpenIddictDataRepository dataRepository, IdentityOptions options)
    {
        // All lookup and storage operations use the real dialect repository. Only the signing
        // key read is replaced so these grant tests do not depend on key rotation/encryption.
        IOpenIddictDataRepository signingRepository = A.Fake<IOpenIddictDataRepository>(configuration =>
            configuration.Wrapping(dataRepository)
        );
        using System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
        A.CallTo(() => signingRepository.GetActivePrivateKeyInternalAsync("integration-signing-key"))
            .Returns((Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), "integration-key"));

        IClientSecretHasher secretHasher = A.Fake<IClientSecretHasher>();
        A.CallTo(() => secretHasher.VerifySecretAsync("plain-secret", "hashed-secret")).Returns(true);
        A.CallTo(() => secretHasher.HashSecretAsync(A<string>._)).Returns("integration-placeholder");
        // As the host does at startup; the faked hasher completes synchronously.
        ClientSecretPlaceholderHash placeholderHash = new(secretHasher);
        placeholderHash.InitializeAsync().GetAwaiter().GetResult();
        options.EncryptionKey = "integration-signing-key";
        OpenIddictTokenManager manager = new(
            Options.Create(options),
            NullLogger<OpenIddictTokenManager>.Instance,
            secretHasher,
            new OpenIddictTokenRepository(signingRepository),
            A.Fake<ISigningKeySnapshotProvider>(),
            new DevelopmentCertificateStore(
                Options.Create(options),
                NullLogger<DevelopmentCertificateStore>.Instance
            ),
            placeholderHash
        );
        return (manager, signingRepository);
    }

    protected static Task<TokenResult> RequestGrantAsync(OpenIddictTokenManager manager, string clientId) =>
        manager.GetAccessTokenAsync([new("client_id", clientId), new("client_secret", "plain-secret")]);

    [TestFixture]
    public class Given_width_aliases_in_a_width_insensitive_database : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private ApplicationInfo _byAlias = null!;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            using var connection = await repository.CreateConnectionAsync();
            bool widthInsensitive = await connection.ExecuteScalarAsync<bool>(
                "SELECT CAST(CASE WHEN N'ClientA' = N'\uFF23lientA' THEN 1 ELSE 0 END AS bit)"
            );
            if (!widthInsensitive)
            {
                Assert.Ignore("The database collation distinguishes full-width client IDs.");
            }

            string clientId = $"Client-{Guid.NewGuid():N}";
            _applicationId = await RegisterApplicationAsync(repository, clientId);
            _byAlias =
                await repository.GetApplicationByClientIdAsync("\uFF23" + clientId[1..])
                ?? throw new InvalidOperationException("The width alias did not resolve the seeded client.");
        }

        [Test]
        public void It_resolves_the_same_application_before_secret_verification() =>
            _byAlias.Id.Should().Be(_applicationId);
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_Application_Lookups_With_And_Without_Api_Client_Rows(bool isApproved)
        : OpenIddictDataRepositoryTests
    {
        private ApplicationInfo _apiClientByClientId = null!;
        private ApplicationInfo _apiClientById = null!;
        private ApplicationInfo _noRowByClientId = null!;
        private ApplicationInfo _noRowById = null!;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string apiClientId = Guid.NewGuid().ToString();
            string noRowClientId = Guid.NewGuid().ToString();
            Guid apiApplicationId = await RegisterApplicationAsync(
                repository,
                apiClientId,
                "edfi_admin_api/readonly_access"
            );
            Guid noRowApplicationId = await RegisterApplicationAsync(
                repository,
                noRowClientId,
                "edfi_admin_api/readonly_access"
            );
            await AddApiClientRowAsync(repository, apiClientId, isApproved);
            _apiClientByClientId =
                await repository.GetApplicationByClientIdAsync(apiClientId)
                ?? throw new InvalidOperationException("Seeded API application was not found.");
            _noRowByClientId =
                await repository.GetApplicationByClientIdAsync(noRowClientId)
                ?? throw new InvalidOperationException("Seeded no-row application was not found.");
            using var connection = await repository.CreateConnectionAsync();
            _apiClientById =
                await repository.GetApplicationByIdAsync(apiApplicationId, connection)
                ?? throw new InvalidOperationException("Seeded API application was not found.");
            _noRowById =
                await repository.GetApplicationByIdAsync(noRowApplicationId, connection)
                ?? throw new InvalidOperationException("Seeded no-row application was not found.");
        }

        [Test]
        public void It_limits_row_backed_clients_looked_up_by_client_id() =>
            _apiClientByClientId.IsTokenLimitExempt.Should().BeFalse();

        [Test]
        public void It_limits_row_backed_clients_looked_up_by_application_id() =>
            _apiClientById.IsTokenLimitExempt.Should().BeFalse();

        [Test]
        public void It_exempts_admin_only_no_row_clients_looked_up_by_client_id() =>
            _noRowByClientId.IsTokenLimitExempt.Should().BeTrue();

        [Test]
        public void It_exempts_admin_only_no_row_clients_looked_up_by_application_id() =>
            _noRowById.IsTokenLimitExempt.Should().BeTrue();

        [Test]
        public void It_reports_api_client_row_presence_in_both_lookups()
        {
            _apiClientByClientId.HasNoApiClientRow.Should().BeFalse();
            _apiClientById.HasNoApiClientRow.Should().BeFalse();
        }

        [Test]
        public void It_reports_api_client_row_absence_in_both_lookups()
        {
            _noRowByClientId.HasNoApiClientRow.Should().BeTrue();
            _noRowById.HasNoApiClientRow.Should().BeTrue();
        }

        [Test]
        public void It_preserves_api_client_approval_in_both_lookups()
        {
            _apiClientByClientId.IsApproved.Should().Be(isApproved);
            _apiClientById.IsApproved.Should().Be(isApproved);
        }

        [Test]
        public void It_preserves_default_approval_for_no_row_clients_in_both_lookups()
        {
            _noRowByClientId.IsApproved.Should().BeTrue();
            _noRowById.IsApproved.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Api_Client_Grants_Through_The_Manager_With_Default_Options
        : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private TokenResult[] _admittedGrants = [];
        private TokenResult _sixteenthGrant = null!;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(repository, clientId);
            await AddApiClientRowAsync(repository, clientId);
            OpenIddictTokenManager manager = CreateTokenManager(repository, new IdentityOptions());
            List<TokenResult> admittedGrants = [];
            for (int i = 0; i < 15; i++)
            {
                admittedGrants.Add(await RequestGrantAsync(manager, clientId));
            }
            _admittedGrants = admittedGrants.ToArray();
            _sixteenthGrant = await RequestGrantAsync(manager, clientId);
        }

        [Test]
        public void It_admits_the_first_fifteen_grants() =>
            _admittedGrants.Should().AllBeOfType<TokenResult.Success>();

        [Test]
        public void It_refuses_the_sixteenth_grant_with_the_default_limit() =>
            _sixteenthGrant.Should().BeEquivalentTo(new TokenResult.FailureTokenLimitExceeded(15));

        [Test]
        public async Task It_stores_exactly_fifteen_tokens() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(15);
    }

    [TestFixture(false, 1)]
    [TestFixture(true, -1)]
    [TestFixture(true, 0)]
    public class Given_Manager_Grants_That_Bypass_Enforcement(bool hasApiClientRow, int configuredLimit)
        : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private TokenResult[] _grants = [];

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(
                repository,
                clientId,
                hasApiClientRow ? "EdFiSandbox" : "edfi_admin_api/readonly_access"
            );
            if (hasApiClientRow)
            {
                await AddApiClientRowAsync(repository, clientId);
            }
            OpenIddictTokenManager manager = CreateTokenManager(
                repository,
                new IdentityOptions { BearerTokenPerClientLimit = configuredLimit }
            );
            _grants =
            [
                await RequestGrantAsync(manager, clientId),
                await RequestGrantAsync(manager, clientId),
                await RequestGrantAsync(manager, clientId),
            ];
        }

        [Test]
        public void It_admits_all_three_grants() => _grants.Should().AllBeOfType<TokenResult.Success>();

        [Test]
        public async Task It_stores_all_three_tokens() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(3);
    }

    [TestFixture("EdFiSandbox")]
    [TestFixture("edfi_admin_api/sis")]
    public class Given_No_Api_Client_Row_With_Non_Admin_Scopes(string registeredScope)
        : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private ApplicationInfo _application = null!;
        private TokenResult _firstGrant = null!;
        private TokenResult _secondGrant = null!;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(repository, clientId, registeredScope);
            _application =
                await repository.GetApplicationByClientIdAsync(clientId)
                ?? throw new InvalidOperationException("Seeded no-row application was not found.");
            OpenIddictTokenManager manager = CreateTokenManager(
                repository,
                new IdentityOptions { BearerTokenPerClientLimit = 1 }
            );
            _firstGrant = await RequestGrantAsync(manager, clientId);
            _secondGrant = await RequestGrantAsync(manager, clientId);
        }

        [Test]
        public void It_reports_no_api_client_row() => _application.HasNoApiClientRow.Should().BeTrue();

        [Test]
        public void It_does_not_exempt_the_application() =>
            _application.IsTokenLimitExempt.Should().BeFalse();

        [Test]
        public void It_retains_the_registered_scope() =>
            _application.Scopes.Should().BeEquivalentTo(registeredScope);

        [Test]
        public void It_admits_the_first_grant() => _firstGrant.Should().BeOfType<TokenResult.Success>();

        [Test]
        public void It_refuses_the_second_grant_with_the_configured_limit() =>
            _secondGrant.Should().BeEquivalentTo(new TokenResult.FailureTokenLimitExceeded(1));

        [Test]
        public async Task It_stores_exactly_one_token() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(1);
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
                EnforcementDisabled,
                RegisteredSecret
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

    /// <summary>
    /// Writes active token rows of an arbitrary type directly. <c>StoreTokenAsync</c> cannot seed
    /// these: it always writes <c>access_token</c>.
    /// </summary>
    protected static async Task<DateTime?> RedemptionDateAsync(Guid tokenId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<DateTime?>(
            "SELECT RedemptionDate FROM dmscs.OpenIddictToken WHERE Id = @Id",
            new { Id = tokenId }
        );
    }

    protected static async Task SetRedemptionDateAsync(Guid tokenId, DateTime redemptionDate)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dmscs.OpenIddictToken SET RedemptionDate = @RedemptionDate WHERE Id = @Id",
            new { Id = tokenId, RedemptionDate = redemptionDate }
        );
    }

    protected static async Task SeedTokenOfTypeAsync(Guid applicationId, string type, int count)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        for (int i = 0; i < count; i++)
        {
            Guid tokenId = Guid.NewGuid();
            await connection.ExecuteAsync(
                @"INSERT INTO dmscs.OpenIddictToken
                  (Id, ApplicationId, Subject, Type, CreationDate, ExpirationDate, Status, ReferenceId)
                  VALUES (@Id, @ApplicationId, @Subject, @Type, @CreationDate, @ExpirationDate, 'valid', @ReferenceId)",
                new
                {
                    Id = tokenId,
                    ApplicationId = applicationId,
                    Subject = $"{type}-{i}",
                    Type = type,
                    CreationDate = DateTime.UtcNow,
                    ExpirationDate = FarFuture.UtcDateTime,
                    ReferenceId = tokenId.ToString("N"),
                }
            );
        }
    }

    protected static async Task DeleteApplicationRowAsync(Guid applicationId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dmscs.OpenIddictApplication WHERE Id = @Id",
            new { Id = applicationId }
        );
    }

    protected static async Task SetApiClientApprovalAsync(string clientId, bool isApproved)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dmscs.ApiClient SET IsApproved = @IsApproved WHERE ClientId = @ClientId",
            new { ClientId = clientId, IsApproved = isApproved }
        );
    }

    protected static Task<int> ChangeStoredSecretAsync(
        OpenIddictDataRepository repository,
        Guid applicationId,
        string clientSecret
    ) =>
        repository.ExecuteInTransactionAsync(
            (connection, transaction) =>
                repository.UpdateClientSecretAsync(applicationId, clientSecret, connection, transaction)
        );

    /// <summary>
    /// Waits for every started task without letting its outcome replace the test's own: used in
    /// <c>finally</c> blocks, after the lock a task may be waiting on has been released, so no
    /// task outlives its test against the shared database.
    /// </summary>
    protected static async Task AwaitWithoutObservingAsync(params Task?[] tasks)
    {
        foreach (Task task in tasks.OfType<Task>())
        {
            await Task.WhenAny(task);
        }
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
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

            // EnforcementDisabled: these fixtures are about the cleanup sweep, not the limit.
            await _repository.StoreTokenAsync(
                _expiredValidTokenId,
                applicationId,
                "subject-expired-valid",
                past,
                EnforcementDisabled,
                RegisteredSecret
            );
            await _repository.StoreTokenAsync(
                _expiredRevokedTokenId,
                applicationId,
                "subject-expired-revoked",
                past,
                EnforcementDisabled,
                RegisteredSecret
            );
            await _repository.RevokeTokenAsync(_expiredRevokedTokenId, applicationId);
            await _repository.StoreTokenAsync(
                _unexpiredTokenId,
                applicationId,
                "subject-unexpired",
                future,
                EnforcementDisabled,
                RegisteredSecret
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
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
                EnforcementDisabled,
                RegisteredSecret
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

    // DMS-1327 D-08: revocation is one UPDATE constrained by token id, the stored ApplicationId,
    // and an unrevoked status. These fixtures pin each predicate against the real engine.

    [TestFixture]
    public class Given_RevokeTokenAsync_By_The_Owning_Application : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _tokenId;
        private DateTime? _redemptionDateBefore;
        private bool _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-owner-{Guid.NewGuid():N}"
            );
            _tokenId = Guid.NewGuid();
            await _repository.StoreTokenAsync(
                _tokenId,
                applicationId,
                "revoke-owner-subject",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );
            _redemptionDateBefore = await RedemptionDateAsync(_tokenId);

            _result = await _repository.RevokeTokenAsync(_tokenId, applicationId);
        }

        [Test]
        public void It_reports_that_a_row_changed() => _result.Should().BeTrue();

        [Test]
        public async Task It_marks_the_token_revoked() =>
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().Be("revoked");

        [Test]
        public async Task It_sets_the_redemption_date()
        {
            _redemptionDateBefore.Should().BeNull();
            (await RedemptionDateAsync(_tokenId)).Should().NotBeNull();
        }
    }

    // The other application is a real registered row, so the only thing keeping the UPDATE off the
    // token is the ApplicationId predicate. Remove it and this token is revoked.
    [TestFixture]
    public class Given_RevokeTokenAsync_By_Another_Application : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _tokenId;
        private bool _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid ownerApplicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-victim-{Guid.NewGuid():N}"
            );
            Guid otherApplicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-other-{Guid.NewGuid():N}"
            );
            _tokenId = Guid.NewGuid();
            await _repository.StoreTokenAsync(
                _tokenId,
                ownerApplicationId,
                "revoke-victim-subject",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );

            _result = await _repository.RevokeTokenAsync(_tokenId, otherApplicationId);
        }

        [Test]
        public void It_reports_that_no_row_changed() => _result.Should().BeFalse();

        [Test]
        public async Task It_leaves_the_token_valid() =>
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().Be("valid");

        [Test]
        public async Task It_leaves_the_redemption_date_unset() =>
            (await RedemptionDateAsync(_tokenId)).Should().BeNull();
    }

    // The first redemption date is replaced with a fixed, distinctive value before the repeat, so
    // the assertion does not depend on the clock advancing between two statements: without the
    // status predicate the repeat overwrites it with the current time.
    [TestFixture]
    public class Given_RevokeTokenAsync_Repeated_For_A_Revoked_Token : OpenIddictDataRepositoryTests
    {
        private static readonly DateTime _originalRedemptionDate = new(
            2001,
            2,
            3,
            4,
            5,
            6,
            DateTimeKind.Unspecified
        );

        private OpenIddictDataRepository _repository = null!;
        private Guid _tokenId;
        private bool _firstResult;
        private bool _repeatResult;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-repeat-{Guid.NewGuid():N}"
            );
            _tokenId = Guid.NewGuid();
            await _repository.StoreTokenAsync(
                _tokenId,
                applicationId,
                "revoke-repeat-subject",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );
            _firstResult = await _repository.RevokeTokenAsync(_tokenId, applicationId);
            await SetRedemptionDateAsync(_tokenId, _originalRedemptionDate);

            _repeatResult = await _repository.RevokeTokenAsync(_tokenId, applicationId);
        }

        [Test]
        public void It_revoked_the_token_the_first_time() => _firstResult.Should().BeTrue();

        [Test]
        public void It_reports_that_the_repeat_changed_no_row() => _repeatResult.Should().BeFalse();

        [Test]
        public async Task It_keeps_the_token_revoked() =>
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().Be("revoked");

        [Test]
        public async Task It_preserves_the_original_redemption_date() =>
            (await RedemptionDateAsync(_tokenId)).Should().Be(_originalRedemptionDate);
    }

    [TestFixture]
    public class Given_RevokeTokenAsync_For_An_Unknown_Token : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _storedTokenId;
        private bool _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-unknown-{Guid.NewGuid():N}"
            );
            _storedTokenId = Guid.NewGuid();
            await _repository.StoreTokenAsync(
                _storedTokenId,
                applicationId,
                "revoke-unknown-subject",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );

            _result = await _repository.RevokeTokenAsync(Guid.NewGuid(), applicationId);
        }

        [Test]
        public void It_reports_that_no_row_changed() => _result.Should().BeFalse();

        [Test]
        public async Task It_leaves_the_applications_stored_token_valid() =>
            (await _repository.GetTokenStatusAsync(_storedTokenId)).Should().Be("valid");
    }

    /// <summary>
    /// The manager reaches the UPDATE through <c>OpenIddictTokenRepository</c>; a wrapper that
    /// dropped or swapped the application id would break ownership with every data-repository
    /// fixture above still green.
    /// </summary>
    [TestFixture]
    public class Given_RevokeTokenAsync_Through_The_Token_Repository_Wrapper : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _dataRepository = null!;
        private Guid _ownedTokenId;
        private Guid _foreignTokenId;
        private bool _ownedResult;
        private bool _foreignResult;

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            IOpenIddictTokenRepository tokenRepository = new OpenIddictTokenRepository(_dataRepository);
            Guid callerApplicationId = await RegisterApplicationAsync(
                _dataRepository,
                $"revoke-wrapper-caller-{Guid.NewGuid():N}"
            );
            Guid otherApplicationId = await RegisterApplicationAsync(
                _dataRepository,
                $"revoke-wrapper-other-{Guid.NewGuid():N}"
            );
            _ownedTokenId = Guid.NewGuid();
            _foreignTokenId = Guid.NewGuid();
            await _dataRepository.StoreTokenAsync(
                _ownedTokenId,
                callerApplicationId,
                "revoke-wrapper-owned",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );
            await _dataRepository.StoreTokenAsync(
                _foreignTokenId,
                otherApplicationId,
                "revoke-wrapper-foreign",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );

            _ownedResult = await tokenRepository.RevokeTokenAsync(_ownedTokenId, callerApplicationId);
            _foreignResult = await tokenRepository.RevokeTokenAsync(_foreignTokenId, callerApplicationId);
        }

        [Test]
        public async Task It_revokes_the_callers_token()
        {
            _ownedResult.Should().BeTrue();
            (await _dataRepository.GetTokenStatusAsync(_ownedTokenId)).Should().Be("revoked");
        }

        [Test]
        public async Task It_leaves_another_applications_token_valid()
        {
            _foreignResult.Should().BeFalse();
            (await _dataRepository.GetTokenStatusAsync(_foreignTokenId)).Should().Be("valid");
        }
    }

    // DMS-1493 D-1: lifecycle revocation is one UPDATE constrained by the stored ApplicationId and an
    // unrevoked status, run inside the caller's transaction. These fixtures pin each property against
    // the real engine.

    /// <summary>
    /// A fixed, distinctive redemption date written over a revoked token, so a later revocation that
    /// overwrote it with the current time is caught without depending on the clock advancing.
    /// </summary>
    protected static readonly DateTime SentinelRedemptionDate = new(
        2001,
        2,
        3,
        4,
        5,
        6,
        DateTimeKind.Unspecified
    );

    protected static async Task<Guid> StoreValidTokenAsync(
        OpenIddictDataRepository repository,
        Guid applicationId,
        DateTimeOffset expiration
    )
    {
        Guid tokenId = Guid.NewGuid();
        TokenStoreOutcome outcome = await repository.StoreTokenAsync(
            tokenId,
            applicationId,
            $"subject-{tokenId:N}",
            expiration,
            EnforcementDisabled,
            RegisteredSecret
        );

        outcome.Should().Be(TokenStoreOutcome.Stored);
        return tokenId;
    }

    protected static Task<int> RevokeTokensForApplicationInTransactionAsync(
        OpenIddictDataRepository repository,
        Guid applicationId
    ) =>
        repository.ExecuteInTransactionAsync(
            (connection, transaction) =>
                repository.RevokeTokensForApplicationAsync(applicationId, connection, transaction)
        );

    protected static async Task<List<string?>> TokenStatusesAsync(
        OpenIddictDataRepository repository,
        IEnumerable<Guid> tokenIds
    )
    {
        List<string?> statuses = [];
        foreach (Guid tokenId in tokenIds)
        {
            statuses.Add(await repository.GetTokenStatusAsync(tokenId));
        }
        return statuses;
    }

    protected static async Task<List<DateTime?>> RedemptionDatesAsync(IEnumerable<Guid> tokenIds)
    {
        List<DateTime?> redemptionDates = [];
        foreach (Guid tokenId in tokenIds)
        {
            redemptionDates.Add(await RedemptionDateAsync(tokenId));
        }
        return redemptionDates;
    }

    /// <summary>
    /// One of the target's three unrevoked tokens is already expired: the UPDATE has no expiry or type
    /// filter, so it counts. The pre-revoked token carries the sentinel date; without the status
    /// predicate the result is 4 and the sentinel is overwritten. The bystander is a real registered
    /// application, so only the ApplicationId predicate keeps its tokens valid.
    /// </summary>
    [TestFixture]
    public class Given_RevokeTokensForApplicationAsync_For_An_Application_With_Outstanding_Tokens
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid[] _unrevokedTokenIds = null!;
        private Guid _preRevokedTokenId;
        private Guid[] _bystanderTokenIds = null!;
        private int _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-all-target-{Guid.NewGuid():N}"
            );
            Guid bystanderApplicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-all-bystander-{Guid.NewGuid():N}"
            );

            _unrevokedTokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarPast),
            ];
            _preRevokedTokenId = await StoreValidTokenAsync(_repository, applicationId, FarFuture);
            (await _repository.RevokeTokenAsync(_preRevokedTokenId, applicationId)).Should().BeTrue();
            await SetRedemptionDateAsync(_preRevokedTokenId, SentinelRedemptionDate);
            _bystanderTokenIds =
            [
                await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture),
                await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture),
            ];

            _result = await RevokeTokensForApplicationInTransactionAsync(_repository, applicationId);
        }

        [Test]
        public void It_reports_exactly_the_three_unrevoked_tokens_as_changed() => _result.Should().Be(3);

        [Test]
        public async Task It_revokes_every_unrevoked_token_including_the_expired_one() =>
            (await TokenStatusesAsync(_repository, _unrevokedTokenIds))
                .Should()
                .Equal("revoked", "revoked", "revoked");

        [Test]
        public async Task It_sets_their_redemption_dates() =>
            (await RedemptionDatesAsync(_unrevokedTokenIds))
                .Should()
                .HaveCount(3)
                .And.AllSatisfy(redemptionDate =>
                    redemptionDate.Should().NotBeNull().And.NotBe(SentinelRedemptionDate)
                );

        [Test]
        public async Task It_preserves_the_already_revoked_tokens_redemption_date()
        {
            (await _repository.GetTokenStatusAsync(_preRevokedTokenId)).Should().Be("revoked");
            (await RedemptionDateAsync(_preRevokedTokenId)).Should().Be(SentinelRedemptionDate);
        }

        [Test]
        public async Task It_leaves_the_other_applications_tokens_valid()
        {
            (await TokenStatusesAsync(_repository, _bystanderTokenIds)).Should().Equal("valid", "valid");
            (await RedemptionDatesAsync(_bystanderTokenIds))
                .Should()
                .HaveCount(2)
                .And.AllSatisfy(redemptionDate => redemptionDate.Should().BeNull());
        }
    }

    [TestFixture]
    public class Given_RevokeTokensForApplicationAsync_Repeated : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid[] _tokenIds = null!;
        private int _firstResult;
        private int _repeatResult;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-all-repeat-{Guid.NewGuid():N}"
            );
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            _firstResult = await RevokeTokensForApplicationInTransactionAsync(_repository, applicationId);
            foreach (Guid tokenId in _tokenIds)
            {
                await SetRedemptionDateAsync(tokenId, SentinelRedemptionDate);
            }

            _repeatResult = await RevokeTokensForApplicationInTransactionAsync(_repository, applicationId);
        }

        [Test]
        public void It_revoked_both_tokens_the_first_time() => _firstResult.Should().Be(2);

        [Test]
        public void It_reports_that_the_repeat_changed_no_row() => _repeatResult.Should().Be(0);

        [Test]
        public async Task It_keeps_the_tokens_revoked() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("revoked", "revoked");

        [Test]
        public async Task It_preserves_their_redemption_dates() =>
            (await RedemptionDatesAsync(_tokenIds))
                .Should()
                .Equal(SentinelRedemptionDate, SentinelRedemptionDate);
    }

    /// <summary>
    /// The count read inside the transaction shows the UPDATE ran there, so the assertions after the
    /// rollback fail if the revocation is committed on any other connection or transaction.
    /// </summary>
    [TestFixture]
    public class Given_RevokeTokensForApplicationAsync_Inside_A_Rolled_Back_Transaction
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid[] _tokenIds = null!;
        private int _changedInsideTheTransaction;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-all-rollback-{Guid.NewGuid():N}"
            );
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];

            using var connection = await _repository.CreateConnectionAsync();
            using var transaction = await _repository.BeginTransactionAsync(connection);
            _changedInsideTheTransaction = await _repository.RevokeTokensForApplicationAsync(
                applicationId,
                connection,
                transaction
            );
            transaction.Rollback();
        }

        [Test]
        public void It_revoked_both_tokens_inside_the_transaction() =>
            _changedInsideTheTransaction.Should().Be(2);

        [Test]
        public async Task It_leaves_every_token_valid_after_the_rollback() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("valid", "valid");

        [Test]
        public async Task It_leaves_their_redemption_dates_unset() =>
            (await RedemptionDatesAsync(_tokenIds))
                .Should()
                .HaveCount(2)
                .And.AllSatisfy(redemptionDate => redemptionDate.Should().BeNull());
    }

    [TestFixture]
    public class Given_RevokeTokensForApplicationAsync_For_An_Unknown_Application
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid[] _registeredTokenIds = null!;
        private int _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"revoke-all-unknown-{Guid.NewGuid():N}"
            );
            _registeredTokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];

            _result = await RevokeTokensForApplicationInTransactionAsync(_repository, Guid.NewGuid());
        }

        [Test]
        public void It_reports_that_no_row_changed() => _result.Should().Be(0);

        [Test]
        public async Task It_leaves_the_registered_applications_tokens_valid() =>
            (await TokenStatusesAsync(_repository, _registeredTokenIds)).Should().Equal("valid", "valid");
    }

    /// <summary>
    /// DMS-1493 D-1/D-2: the lock every lifecycle transaction takes first. A second session asks for
    /// the same row lock a grant takes, and <c>WaitUntilASessionIsBlockedBy</c> must see it
    /// waiting on a lock the locking transaction's session owns, so an implementation that only
    /// reads the row fails here.
    /// </summary>
    [TestFixture]
    public class Given_LockApplicationAsync_For_An_Existing_Row : OpenIddictDataRepositoryTests
    {
        /// <summary>
        /// The competing session has no lock timeout, so only its 30 s command timeout can end its
        /// wait; the grant's 5 s lock timeout is not involved. This stays well under the former.
        /// </summary>
        private static readonly TimeSpan _blockedObservationTimeout = TimeSpan.FromSeconds(10);

        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private IDbConnection _connection = null!;
        private IDbTransaction _transaction = null!;
        private int _lockingSessionId;
        private bool _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(_repository, $"lock-existing-{Guid.NewGuid():N}");
            _connection = await _repository.CreateConnectionAsync();
            _transaction = await _repository.BeginTransactionAsync(_connection);
            try
            {
                _result = await _repository.LockApplicationAsync(_applicationId, _connection, _transaction);
                _lockingSessionId = await _connection.ExecuteScalarAsync<int>(
                    "SELECT @@SPID",
                    transaction: _transaction
                );
            }
            catch
            {
                // NUnit skips this fixture's teardown when its setup throws, but still runs the base
                // reset, which would queue behind the lock this transaction holds.
                _transaction.Dispose();
                _connection.Dispose();
                throw;
            }
        }

        [TearDown]
        public void Teardown()
        {
            // Before the base reset, which would otherwise queue behind the lock.
            _transaction.Dispose();
            _connection.Dispose();
        }

        [Test]
        public void It_reports_that_the_row_exists() => _result.Should().BeTrue();

        [Test]
        public async Task It_holds_the_row_lock_until_the_transaction_ends()
        {
            Task<RowLockHolder> competingHolder = Task.Run(() => RowLockHolder.TakeAsync(_applicationId));
            try
            {
                await WaitUntilASessionIsBlockedBy(_lockingSessionId, _blockedObservationTimeout);
                competingHolder
                    .IsCompleted.Should()
                    .BeFalse("the competing session is waiting on the row lock the transaction holds");

                _transaction.Rollback();

                // Completes only once the competing session is granted the lock.
                await competingHolder;
            }
            finally
            {
                // Ends the locking transaction first (a no-op after the rollback), so a competing
                // session still waiting on it is granted and can be awaited and released.
                _transaction.Dispose();
                await using RowLockHolder holder = await competingHolder;
                await holder.ReleaseAsync();
            }
        }
    }

    [TestFixture]
    public class Given_LockApplicationAsync_For_A_Missing_Row : OpenIddictDataRepositoryTests
    {
        private bool _result;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                repository,
                $"lock-missing-{Guid.NewGuid():N}"
            );
            await DeleteApplicationRowAsync(applicationId);

            _result = await repository.ExecuteInTransactionAsync(
                (connection, transaction) =>
                    repository.LockApplicationAsync(applicationId, connection, transaction)
            );
        }

        [Test]
        public void It_reports_that_the_row_does_not_exist() => _result.Should().BeFalse();
    }

    // Introspection reads a token's status through the authenticated application: the status is returned only
    // when the stored ApplicationId matches, so a token row stored for another application reads as unknown.
    [TestFixture]
    public class Given_GetTokenStatusForApplicationAsync_By_The_Owning_Application
        : OpenIddictDataRepositoryTests
    {
        private string? _status;

        [SetUp]
        public async Task Setup()
        {
            var repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                repository,
                $"status-owner-{Guid.NewGuid():N}"
            );
            Guid tokenId = Guid.NewGuid();
            await repository.StoreTokenAsync(
                tokenId,
                applicationId,
                "status-owner-subject",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );

            _status = await repository.GetTokenStatusForApplicationAsync(tokenId, applicationId);
        }

        [Test]
        public void It_returns_the_status() => _status.Should().Be("valid");
    }

    // The other application is a real registered row, so the only thing keeping the read off the token is the
    // ApplicationId predicate.
    [TestFixture]
    public class Given_GetTokenStatusForApplicationAsync_By_Another_Application
        : OpenIddictDataRepositoryTests
    {
        private string? _status;

        [SetUp]
        public async Task Setup()
        {
            var repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid ownerApplicationId = await RegisterApplicationAsync(
                repository,
                $"status-victim-{Guid.NewGuid():N}"
            );
            Guid otherApplicationId = await RegisterApplicationAsync(
                repository,
                $"status-other-{Guid.NewGuid():N}"
            );
            Guid tokenId = Guid.NewGuid();
            await repository.StoreTokenAsync(
                tokenId,
                ownerApplicationId,
                "status-victim-subject",
                FarFuture,
                EnforcementDisabled,
                RegisteredSecret
            );

            _status = await repository.GetTokenStatusForApplicationAsync(tokenId, otherApplicationId);
        }

        [Test]
        public void It_returns_null() => _status.Should().BeNull();
    }

    [TestFixture]
    public class Given_GetTokenStatusForApplicationAsync_For_An_Unknown_Token : OpenIddictDataRepositoryTests
    {
        private string? _status;

        [SetUp]
        public async Task Setup()
        {
            var repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                repository,
                $"status-unknown-{Guid.NewGuid():N}"
            );

            _status = await repository.GetTokenStatusForApplicationAsync(Guid.NewGuid(), applicationId);
        }

        [Test]
        public void It_returns_null() => _status.Should().BeNull();
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(_repository, $"under-limit-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, 2);

            _outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-under-limit",
                FarFuture,
                3,
                RegisteredSecret
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(_repository, $"at-limit-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, 3);

            _outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-at-limit",
                FarFuture,
                3,
                RegisteredSecret
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(_repository, $"over-limit-{Guid.NewGuid():N}");
            await SeedActiveTokensAsync(_repository, _applicationId, 6);

            _outcome = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-over-limit",
                FarFuture,
                3,
                RegisteredSecret
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

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
                maxActiveTokens,
                RegisteredSecret
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
                            EnforcementDisabled,
                            RegisteredSecret
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
                            EnforcementDisabled,
                            RegisteredSecret
                        );
                        await _repository.RevokeTokenAsync(tokenId, applicationId);
                    }
                },
                "revoked-rows"
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
        }

        // Nothing writes a non-access token today, so the row has to be seeded directly. Revert the
        // Type predicate in ConditionalInsertSql and this grant is refused instead of stored.
        [Test]
        public async Task It_ignores_rows_of_another_token_type()
        {
            TokenStoreOutcome outcome = await GrantAfter(
                applicationId => SeedTokenOfTypeAsync(applicationId, "refresh_token", 3),
                "other-token-type"
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
                        EnforcementDisabled,
                        RegisteredSecret
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
                        EnforcementDisabled,
                        RegisteredSecret
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(_repository, $"limit-one-{Guid.NewGuid():N}");

            _first = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-first",
                FarFuture,
                1,
                RegisteredSecret
            );
            _second = await _repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-second",
                FarFuture,
                1,
                RegisteredSecret
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
    /// The limit travelling the production path rather than a direct repository call. Every other
    /// fixture here calls <c>OpenIddictDataRepository</c> itself, but the token manager reaches it
    /// through <c>OpenIddictTokenRepository</c>, and nothing else exercises that wrapper against a
    /// real database: it carries @MaxActiveTokens down and the TokenStoreOutcome back, so a wrapper
    /// that dropped either would break enforcement with every existing test still green.
    /// </summary>
    [TestFixture]
    public class Given_Grants_Through_The_Token_Repository_Wrapper : OpenIddictDataRepositoryTests
    {
        private const int Limit = 2;

        private IOpenIddictTokenRepository _tokenRepository = null!;
        private Guid _applicationId;
        private TokenStoreOutcome _firstOutcome;
        private TokenStoreOutcome _secondOutcome;
        private TokenStoreOutcome _thirdOutcome;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository dataRepository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _tokenRepository = new OpenIddictTokenRepository(dataRepository);
            _applicationId = await RegisterApplicationAsync(
                dataRepository,
                $"wrapper-limit-{Guid.NewGuid():N}"
            );

            _firstOutcome = await GrantAsync();
            _secondOutcome = await GrantAsync();
            _thirdOutcome = await GrantAsync();
        }

        private async Task<TokenStoreOutcome> GrantAsync() =>
            await _tokenRepository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "wrapper-subject",
                FarFuture,
                Limit,
                RegisteredSecret
            );

        [Test]
        public void It_admits_grants_up_to_the_limit()
        {
            _firstOutcome.Should().Be(TokenStoreOutcome.Stored);
            _secondOutcome.Should().Be(TokenStoreOutcome.Stored);
        }

        [Test]
        public void It_refuses_the_grant_that_would_exceed_the_limit() =>
            _thirdOutcome.Should().Be(TokenStoreOutcome.LimitExceeded);

        [Test]
        public async Task It_stores_exactly_the_limit() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(Limit);
    }

    /// <summary>
    /// The opt-out an operator configures with a value below 1. It stores however many active
    /// tokens the client already holds, but under the same row lock and re-validation as the
    /// enforcing path (DMS-1493 D-9).
    /// </summary>
    [TestFixture]
    public class Given_Enforcement_Is_Disabled : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

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
                maxActiveTokens,
                RegisteredSecret
            );

            outcome.Should().Be(TokenStoreOutcome.Stored);
            (await TokenRowCountAsync(applicationId)).Should().Be(21);
        }

        /// <summary>
        /// The disabled path no longer skips the lock: a grant waits behind a session holding the
        /// client's application row, which is what lets credential reset, deletion and
        /// un-approval exclude it. The lock manager must show the grant waiting on the holder, and
        /// the probe is raced against the grant, so a store that takes no lock fails here naming
        /// the outcome it returned rather than waiting out a delay.
        /// </summary>
        [TestCase(0)]
        [TestCase(-1)]
        public async Task It_waits_for_the_application_row_lock(int maxActiveTokens)
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"disabled-lock-{maxActiveTokens}-{Guid.NewGuid():N}"
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(applicationId);
            Task<TokenStoreOutcome>? grant = null;
            Task? probe = null;
            try
            {
                grant = Task.Run(() =>
                    _repository.StoreTokenAsync(
                        Guid.NewGuid(),
                        applicationId,
                        "subject-disabled-lock",
                        FarFuture,
                        maxActiveTokens,
                        RegisteredSecret
                    )
                );
                probe = WaitUntilASessionIsBlockedBy(holder.SessionId, GrantBlockedObservationTimeout);

                if (await Task.WhenAny(probe, grant) == grant)
                {
                    Assert.Fail(
                        $"The grant completed with {await grant} before it was observed waiting on the held row lock."
                    );
                }

                await probe;
                grant.IsCompleted.Should().BeFalse("the grant is waiting on the row lock the holder owns");

                await holder.ReleaseAsync();

                (await grant).Should().Be(TokenStoreOutcome.Stored);
                (await TokenRowCountAsync(applicationId)).Should().Be(1);
            }
            finally
            {
                // Ends the holder's transaction first (a no-op after the release), so a grant still
                // waiting on it can finish before it is awaited.
                await holder.DisposeAsync();
                await AwaitWithoutObservingAsync(grant, probe);
            }
        }
    }

    /// <summary>
    /// DMS-1493 D-11: a credential reset committed before the store took the lock. The grant
    /// authenticated against <see cref="RegisteredSecret"/>, the stored secret is now another
    /// value, and the store must refuse without inserting under either limit setting. The
    /// case-only variant pins the ordinal comparison: the column has the database's
    /// case-insensitive default collation, so a comparison in SQL would accept it.
    /// </summary>
    [TestFixture(EnforcementDisabled, "rotated-secret")]
    [TestFixture(15, "rotated-secret")]
    [TestFixture(EnforcementDisabled, "HASHED-SECRET")]
    [TestFixture(15, "HASHED-SECRET")]
    public class Given_A_Store_After_The_Secret_Changed(int maxActiveTokens, string currentSecret)
        : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(repository, $"secret-changed-{Guid.NewGuid():N}");
            (await ChangeStoredSecretAsync(repository, _applicationId, currentSecret)).Should().Be(1);

            _outcome = await repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-secret-changed",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
        }

        [Test]
        public void It_reports_the_client_as_changed() =>
            _outcome.Should().Be(TokenStoreOutcome.ClientChanged);

        [Test]
        public async Task It_stores_nothing() => (await TokenRowCountAsync(_applicationId)).Should().Be(0);
    }

    /// <summary>
    /// The re-validation runs before the count: a reset client at its limit is answered as
    /// changed (<c>invalid_client</c>), not as over its limit (429). Counting first would report
    /// <see cref="TokenStoreOutcome.LimitExceeded"/> here.
    /// </summary>
    [TestFixture]
    public class Given_A_Store_At_The_Limit_After_The_Secret_Changed : OpenIddictDataRepositoryTests
    {
        private const int Limit = 2;

        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(
                repository,
                $"limit-secret-changed-{Guid.NewGuid():N}"
            );
            await SeedActiveTokensAsync(repository, _applicationId, Limit);
            await ChangeStoredSecretAsync(repository, _applicationId, "rotated-secret");

            _outcome = await repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-limit-secret-changed",
                FarFuture,
                Limit,
                RegisteredSecret
            );
        }

        [Test]
        public void It_reports_the_client_as_changed_rather_than_over_its_limit() =>
            _outcome.Should().Be(TokenStoreOutcome.ClientChanged);

        [Test]
        public async Task It_stores_no_further_token() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(Limit);
    }

    /// <summary>
    /// DMS-1493 D-11: an un-approval committed before the store took the lock. The approval is
    /// re-read from <c>ApiClient</c> under the lock, so the store refuses under either limit
    /// setting.
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Store_After_Un_Approval(int maxActiveTokens) : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            // ApiClient.ClientId is 36 characters wide.
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(repository, clientId);
            await AddApiClientRowAsync(repository, clientId);
            await SetApiClientApprovalAsync(clientId, isApproved: false);

            _outcome = await repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-un-approved",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
        }

        [Test]
        public void It_reports_the_client_as_changed() =>
            _outcome.Should().Be(TokenStoreOutcome.ClientChanged);

        [Test]
        public async Task It_stores_nothing() => (await TokenRowCountAsync(_applicationId)).Should().Be(0);
    }

    /// <summary>
    /// A deletion committed before the store took the lock. The limit-disabled path used to
    /// insert for a deleted client; both settings now answer not found.
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Store_After_Deletion(int maxActiveTokens) : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(repository, $"deleted-{Guid.NewGuid():N}");
            await DeleteApplicationRowAsync(_applicationId);

            _outcome = await repository.StoreTokenAsync(
                Guid.NewGuid(),
                _applicationId,
                "subject-deleted",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
        }

        [Test]
        public void It_reports_the_client_as_not_found() =>
            _outcome.Should().Be(TokenStoreOutcome.ClientNotFound);

        [Test]
        public async Task It_stores_nothing() => (await TokenRowCountAsync(_applicationId)).Should().Be(0);
    }

    /// <summary>
    /// The control for the refusals above: an approved API client row and the registered secret
    /// pass the re-validation under either limit setting, so the refusals are caused by the change
    /// each one makes and not by the re-validation itself.
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Store_With_The_Current_Secret_And_Approval(int maxActiveTokens)
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private Guid _tokenId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(_repository, clientId);
            await AddApiClientRowAsync(_repository, clientId);
            _tokenId = Guid.NewGuid();

            _outcome = await _repository.StoreTokenAsync(
                _tokenId,
                _applicationId,
                "subject-current",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
        }

        [Test]
        public void It_stores_the_token() => _outcome.Should().Be(TokenStoreOutcome.Stored);

        [Test]
        public async Task It_stores_it_as_valid() =>
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().Be("valid");
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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
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
                                Limit,
                                RegisteredSecret
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
        /// <summary>
        /// How long these fixtures wait to observe the grant blocked on the holder's lock. It has
        /// to stay comfortably under the repository's own lock timeout - 5000 ms, in
        /// <c>OpenIddictDataRepository.LockTimeoutMilliseconds</c> - because once that elapses the
        /// grant stops waiting and completes with <see cref="TokenStoreOutcome.LockTimeout"/>.
        /// The "still blocked" assertions below would then fail naming the lock, which is not what
        /// went wrong. Lowering the repository constant means lowering this one too.
        /// </summary>
        private static readonly TimeSpan _blockedObservationTimeout = TimeSpan.FromSeconds(4);

        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

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
                        5,
                        RegisteredSecret
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
                        5,
                        RegisteredSecret
                    )
                );

                await WaitUntilASessionIsBlockedBy(holder.SessionId, _blockedObservationTimeout);

                TokenStoreOutcome otherOutcome = await _repository.StoreTokenAsync(
                    Guid.NewGuid(),
                    otherApplicationId,
                    "subject-b",
                    FarFuture,
                    5,
                    RegisteredSecret
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
        /// reports <see cref="TokenStoreOutcome.LockTimeout"/>, which reaches the caller as a
        /// retriable 409 rather than as "Too Many Tokens" or as a server error.
        /// </summary>
        [Test]
        public async Task It_reports_a_lock_timeout_rather_than_a_limit_rejection_when_the_wait_times_out()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"lock-timeout-{Guid.NewGuid():N}"
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(applicationId);
            try
            {
                // The limit is 5 and the client holds nothing, so a LimitExceeded here could only
                // come from the timeout being mistaken for one.
                TokenStoreOutcome outcome = await _repository.StoreTokenAsync(
                    Guid.NewGuid(),
                    applicationId,
                    "subject-lock-timeout",
                    FarFuture,
                    5,
                    RegisteredSecret
                );

                outcome.Should().Be(TokenStoreOutcome.LockTimeout);

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
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

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
                5,
                RegisteredSecret
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
                                3,
                                RegisteredSecret
                            )
                        )
                    )
            );

            outcomes.Should().AllBeEquivalentTo(TokenStoreOutcome.ClientNotFound);
            (await TokenRowCountAsync(applicationId)).Should().Be(0);
        }
    }

    /// <summary>
    /// A grant chosen as a deadlock victim is contention, exactly like a lock-wait timeout, so it
    /// must come back as the retriable outcome rather than escape as a server error. In production
    /// the other party is the expired-token sweep, whose delete locks a token row's clustered key
    /// before its ApplicationId key while the grant's count takes them in the opposite order. That
    /// interleaving depends on the count's plan shape, so this fixture builds the cycle from locks
    /// every plan must take: the holder X-locks one of the client's token rows, which the count has
    /// to read, and then asks for the application row the grant already holds.
    /// </summary>
    [TestFixture]
    public class Given_A_Grant_Chosen_As_A_Deadlock_Victim : OpenIddictDataRepositoryTests
    {
        private static readonly TimeSpan _blockedObservationTimeout = TimeSpan.FromSeconds(4);

        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

        [Test]
        public async Task It_reports_the_retriable_outcome_and_stores_nothing()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"deadlock-victim-{Guid.NewGuid():N}"
            );
            Guid existingTokenId = Guid.NewGuid();
            (
                await _repository.StoreTokenAsync(
                    existingTokenId,
                    applicationId,
                    "subject-existing",
                    FarPast,
                    EnforcementDisabled,
                    RegisteredSecret
                )
            )
                .Should()
                .Be(TokenStoreOutcome.Stored);

            await using SqlConnection holder = await OpenConnectionAsync();
            await holder.ExecuteAsync("SET DEADLOCK_PRIORITY HIGH");
            await using SqlTransaction holderTransaction = (SqlTransaction)
                await holder.BeginTransactionAsync();

            // RedemptionDate is in no nonclustered index, so this locks only the clustered row.
            await holder.ExecuteAsync(
                "UPDATE dmscs.OpenIddictToken SET RedemptionDate = SYSUTCDATETIME() WHERE Id = @Id",
                new { Id = existingTokenId },
                holderTransaction
            );
            int holderSessionId = await holder.ExecuteScalarAsync<int>(
                "SELECT @@SPID",
                transaction: holderTransaction
            );

            Task<TokenStoreOutcome> grant = Task.Run(() =>
                _repository.StoreTokenAsync(
                    Guid.NewGuid(),
                    applicationId,
                    "subject-victim",
                    FarFuture,
                    5,
                    RegisteredSecret
                )
            );

            // The grant now holds the application row and waits on the token row.
            await WaitUntilASessionIsBlockedBy(holderSessionId, _blockedObservationTimeout);

            // Closing the cycle: SQL Server picks the grant, the lower-priority session, as victim,
            // and the holder's request is then granted.
            await holder.ExecuteAsync(
                "SELECT Id FROM dmscs.OpenIddictApplication WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                new { Id = applicationId },
                holderTransaction
            );

            TokenStoreOutcome outcome = await grant;
            await holderTransaction.RollbackAsync();

            outcome.Should().Be(TokenStoreOutcome.LockTimeout);
            (await TokenRowCountAsync(applicationId)).Should().Be(1);
        }
    }

    // DMS-1556 2.2: SqlClient reports an exhausted pool as InvalidOperationException, not SqlException, so the status
    // read translates it into the typed dependency failure itself. The repository runs over its own one-connection pool
    // (a unique application name keys a separate pool); a held lease exhausts it and releasing the lease recovers it.
    [TestFixture]
    [NonParallelizable]
    public class Given_The_Token_Status_Connection_Pool_Is_Exhausted : OpenIddictDataRepositoryTests
    {
        private string _isolatedConnectionString = null!;
        private Exception? _whileExhausted;
        private string? _afterRelease;

        [SetUp]
        public async Task Setup()
        {
            var seeding = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            Guid applicationId = await RegisterApplicationAsync(
                seeding,
                $"pool-exhaustion-client-{Guid.NewGuid():N}"
            );
            Guid tokenId = Guid.NewGuid();
            (
                await seeding.StoreTokenAsync(
                    tokenId,
                    applicationId,
                    "subject-pool",
                    FarFuture,
                    EnforcementDisabled,
                    RegisteredSecret
                )
            )
                .Should()
                .Be(TokenStoreOutcome.Stored);

            _isolatedConnectionString = new SqlConnectionStringBuilder(
                MssqlTestConfiguration.DatabaseConnectionString
            )
            {
                MaxPoolSize = 1,
                ConnectTimeout = 3,
                ApplicationName = $"dms1556-pool-{Guid.NewGuid():N}",
            }.ConnectionString;
            var repository = new OpenIddictDataRepository(
                Options.Create(
                    new DatabaseOptions
                    {
                        DatabaseConnection = _isolatedConnectionString,
                        EncryptionKey = "IntegrationTestEncryptionKey32Chars",
                    }
                ),
                NullLogger<OpenIddictDataRepository>.Instance
            );

            await using (var lease = new SqlConnection(_isolatedConnectionString))
            {
                await lease.OpenAsync();
                try
                {
                    await repository.GetTokenStatusAsync(tokenId);
                }
                catch (Exception exception)
                {
                    _whileExhausted = exception;
                }
            }

            _afterRelease = await repository.GetTokenStatusAsync(tokenId);
        }

        [TearDown]
        public void ClearIsolatedPool()
        {
            using var connection = new SqlConnection(_isolatedConnectionString);
            SqlConnection.ClearPool(connection);
        }

        [Test]
        public void It_reports_the_token_status_store_unavailable() =>
            _whileExhausted
                .Should()
                .BeOfType<AuthenticationDependencyUnavailableException>()
                .Which.Category.Should()
                .Be(AuthenticationDependencyCategory.TokenStatusStore);

        [Test]
        public void It_carries_the_pool_timeout() =>
            _whileExhausted!.InnerException.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_recovers_once_the_lease_is_released() => _afterRelease.Should().Be("valid");
    }

    /// <summary>
    /// The whole <c>OpenIddictApplication</c> row rendered as one JSON document, so a before/after
    /// comparison covers every column rather than only the ones a fixture happens to read. The
    /// outer SELECT returns the document as a single value, so SqlClient never splits it.
    /// </summary>
    protected static async Task<string?> ApplicationRowJsonAsync(Guid applicationId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            """
            SELECT (
                SELECT * FROM dmscs.OpenIddictApplication WHERE Id = @Id
                FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER
            )
            """,
            new { Id = applicationId }
        );
    }

    protected static async Task<int> ApplicationRowCountForClientIdAsync(string clientId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.OpenIddictApplication WHERE ClientId = @ClientId",
            new { ClientId = clientId }
        );
    }

    protected static async Task<int> ApplicationScopeRowCountAsync(Guid applicationId)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.OpenIddictApplicationScope WHERE ApplicationId = @ApplicationId",
            new { ApplicationId = applicationId }
        );
    }

    protected static async Task<(int Roles, int ClientRoles)> RoleRowCountsAsync(string roleName)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        int roles = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.OpenIddictRole WHERE Name = @Name",
            new { Name = roleName }
        );
        int clientRoles = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM dmscs.OpenIddictClientRole cr
            JOIN dmscs.OpenIddictRole r ON cr.RoleId = r.Id
            WHERE r.Name = @Name
            """,
            new { Name = roleName }
        );
        return (roles, clientRoles);
    }

    protected static async Task<(int Scopes, int ApplicationScopes)> ScopeRowCountsAsync(string scopeName)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        int scopes = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.OpenIddictScope WHERE Name = @Name",
            new { Name = scopeName }
        );
        int applicationScopes = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM dmscs.OpenIddictApplicationScope aps
            JOIN dmscs.OpenIddictScope s ON aps.ScopeId = s.Id
            WHERE s.Name = @Name
            """,
            new { Name = scopeName }
        );
        return (scopes, applicationScopes);
    }

    /// <summary>
    /// DMS-1529 D-5: the data repository reports a collision on
    /// <c>UX_OpenIddictApplication_ClientId</c> as an outcome and writes nothing. Fails against the
    /// implementation that let the engine exception escape.
    /// </summary>
    [TestFixture]
    public class Given_An_Application_Insert_That_Repeats_A_Client_Id : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private string _clientId = null!;
        private Guid _seededApplicationId;
        private string? _seededRowBefore;
        private OpenIddictApplicationInsertOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _clientId = $"repeat-key-{Guid.NewGuid():N}";
            _seededApplicationId = await RegisterApplicationAsync(
                _repository,
                _clientId,
                "edfi_admin_api/full_access"
            );
            _seededRowBefore = await ApplicationRowJsonAsync(_seededApplicationId);

            using var connection = await _repository.CreateConnectionAsync();
            using var transaction = await _repository.BeginTransactionAsync(connection);
            _outcome = await _repository.InsertApplicationAsync(
                Guid.NewGuid(),
                _clientId,
                "other-hashed-secret",
                "Repeat Key Client",
                ["token"],
                [],
                "confidential",
                "[]",
                connection,
                transaction
            );
            transaction.Rollback();
        }

        [Test]
        public void It_reports_duplicate_client_id() =>
            _outcome.Should().Be(OpenIddictApplicationInsertOutcome.DuplicateClientId);

        [Test]
        public async Task It_leaves_the_seeded_row_unchanged()
        {
            _seededRowBefore.Should().NotBeNull();
            (await ApplicationRowJsonAsync(_seededApplicationId)).Should().Be(_seededRowBefore);
        }

        [Test]
        public async Task It_leaves_exactly_one_row_under_the_key() =>
            (await ApplicationRowCountForClientIdAsync(_clientId)).Should().Be(1);
    }

    /// <summary>
    /// Only the client-key constraint is classified. A primary-key collision on the same table and
    /// a role-name collision propagate as the engine exception (2627, naming their own constraint),
    /// which the provider repository reports as an unknown failure.
    /// </summary>
    [TestFixture]
    public class Given_OpenIddict_Inserts_That_Violate_Other_Constraints : OpenIddictDataRepositoryTests
    {
        private const int UniqueConstraintViolation = 2627;

        private OpenIddictDataRepository _repository = null!;
        private Guid _seededApplicationId;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _seededApplicationId = await RegisterApplicationAsync(
                _repository,
                $"other-constraints-{Guid.NewGuid():N}"
            );
        }

        [Test]
        public async Task It_propagates_a_primary_key_collision()
        {
            using var connection = await _repository.CreateConnectionAsync();
            using var transaction = await _repository.BeginTransactionAsync(connection);
            Func<Task> act = () =>
                _repository.InsertApplicationAsync(
                    _seededApplicationId,
                    $"fresh-key-{Guid.NewGuid():N}",
                    "hashed-secret",
                    "Fresh Key Client",
                    ["token"],
                    [],
                    "confidential",
                    "[]",
                    connection,
                    transaction
                );

            SqlException thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
            thrown.Number.Should().Be(UniqueConstraintViolation);
            thrown.Message.Should().Contain("PK_OpenIddictApplication");
            transaction.Rollback();
        }

        [Test]
        public async Task It_propagates_a_role_name_collision()
        {
            string roleName = $"repeat-role-{Guid.NewGuid():N}";
            using var connection = await _repository.CreateConnectionAsync();
            using var transaction = await _repository.BeginTransactionAsync(connection);
            await _repository.InsertRoleAsync(Guid.NewGuid(), roleName, connection, transaction);
            Func<Task> act = () =>
                _repository.InsertRoleAsync(Guid.NewGuid(), roleName, connection, transaction);

            SqlException thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
            thrown.Number.Should().Be(UniqueConstraintViolation);
            thrown.Message.Should().Contain("UX_OpenIddictRole_Name");
            transaction.Rollback();
        }
    }

    /// <summary>
    /// DMS-1529 §8 [R3]: the provider path through the production caller,
    /// <c>OpenIddictClientRepository.CreateClientAsync</c>, against the real engine repository. The
    /// seeded provider client holds the key, and the role and scope names are fresh so the attempt
    /// has to create them; the assertions prove the whole attempt rolled back and the seeded row
    /// is untouched.
    /// </summary>
    [TestFixture]
    public class Given_A_Provider_Client_Creation_Whose_Key_Is_Already_Registered
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _dataRepository = null!;
        private string _clientId = null!;
        private string _roleName = null!;
        private string _scopeName = null!;
        private Guid _seededApplicationId;
        private string? _seededRowBefore;
        private int _seededScopeRowsBefore;
        private ClientCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = new OpenIddictDataRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _clientId = $"provider-taken-key-{Guid.NewGuid():N}";
            _roleName = $"provider-taken-role-{Guid.NewGuid():N}";
            _scopeName = $"provider-taken-scope-{Guid.NewGuid():N}";
            _seededApplicationId = await RegisterApplicationAsync(
                _dataRepository,
                _clientId,
                "edfi_admin_api/full_access"
            );
            _seededRowBefore = await ApplicationRowJsonAsync(_seededApplicationId);
            _seededScopeRowsBefore = await ApplicationScopeRowCountAsync(_seededApplicationId);

            IClientSecretHasher secretHasher = A.Fake<IClientSecretHasher>();
            A.CallTo(() => secretHasher.HashSecretAsync(A<string>._)).Returns("hashed-attempt-secret");
            OpenIddictClientRepository provider = new(
                NullLogger<OpenIddictClientRepository>.Instance,
                secretHasher,
                _dataRepository,
                Options.Create(new ClientSecretValidationOptions())
            );

            _result = await provider.CreateClientAsync(
                _clientId,
                "attempt-secret",
                _roleName,
                "Provider Taken Key Client",
                _scopeName,
                "uri://ed-fi.org",
                "255901",
                [1]
            );
        }

        [Test]
        public void It_returns_failure_duplicate_client_id() =>
            _result.Should().BeOfType<ClientCreateResult.FailureDuplicateClientId>();

        [Test]
        public async Task It_leaves_the_seeded_provider_client_unchanged()
        {
            _seededRowBefore.Should().NotBeNull();
            (await ApplicationRowJsonAsync(_seededApplicationId)).Should().Be(_seededRowBefore);
        }

        [Test]
        public async Task It_leaves_exactly_one_provider_client_under_the_key() =>
            (await ApplicationRowCountForClientIdAsync(_clientId)).Should().Be(1);

        [Test]
        public async Task It_leaves_no_role_or_client_role_from_the_attempt() =>
            (await RoleRowCountsAsync(_roleName)).Should().Be((0, 0));

        [Test]
        public async Task It_leaves_no_scope_or_application_scope_from_the_attempt() =>
            (await ScopeRowCountsAsync(_scopeName)).Should().Be((0, 0));

        [Test]
        public async Task It_leaves_the_seeded_scope_assignments_unchanged()
        {
            _seededScopeRowsBefore.Should().Be(1);
            (await ApplicationScopeRowCountAsync(_seededApplicationId)).Should().Be(_seededScopeRowsBefore);
        }
    }

    /// <summary>
    /// Adds one API client for <paramref name="clientId"/>, linked to
    /// <paramref name="dataStoreCount"/> data stores of its own. The schema allows at most one
    /// API client per client id (<c>UX_ApiClient_ClientId</c>).
    /// </summary>
    protected static Task AddApiClientWithDataStoresAsync(
        OpenIddictDataRepository repository,
        string clientId,
        bool isApproved,
        int dataStoreCount
    ) =>
        repository.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                int vendorId = await connection.ExecuteScalarAsync<int>(
                    "INSERT INTO dmscs.Vendor (Company) OUTPUT INSERTED.Id VALUES (@Company)",
                    new { Company = $"credential-{Guid.NewGuid():N}" },
                    transaction
                );
                int applicationId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName)
                    OUTPUT INSERTED.Id VALUES ('Credential test', @VendorId, 'Test claim set')
                    """,
                    new { VendorId = vendorId },
                    transaction
                );
                int apiClientId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved)
                    OUTPUT INSERTED.Id VALUES (@ApplicationId, @ClientId, @ClientUuid, 'Credential test', @IsApproved)
                    """,
                    new
                    {
                        ApplicationId = applicationId,
                        ClientId = clientId,
                        ClientUuid = Guid.NewGuid(),
                        IsApproved = isApproved,
                    },
                    transaction
                );
                for (int index = 0; index < dataStoreCount; index++)
                {
                    int dataStoreId = await connection.ExecuteScalarAsync<int>(
                        "INSERT INTO dmscs.DataStore (DataStoreType, Name) OUTPUT INSERTED.Id VALUES ('Test', @Name)",
                        new { Name = $"credential-{Guid.NewGuid():N}" },
                        transaction
                    );
                    await connection.ExecuteAsync(
                        "INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId) VALUES (@ApiClientId, @DataStoreId)",
                        new { ApiClientId = apiClientId, DataStoreId = dataStoreId },
                        transaction
                    );
                }
            }
        );

    // The credential query must report what the full lookup reports for every registration the
    // schema allows: no API client (approved by default, not absent), an approved or unapproved
    // one, and either of those linked to several data stores, which the full lookup reads with
    // its own query but which must not change the approval.
    [TestFixture("no-api-client", true)]
    [TestFixture("approved", true)]
    [TestFixture("unapproved", false)]
    [TestFixture("approved-over-two-data-stores", true)]
    [TestFixture("unapproved-over-two-data-stores", false)]
    public class Given_A_Client_Credential_Lookup(string registration, bool expectedApproval)
        : OpenIddictDataRepositoryTests
    {
        private Guid _applicationId;
        private ClientCredential _credential = null!;
        private ApplicationInfo _application = null!;
        private ClientCredential? _throughWrapper;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(
                repository,
                clientId,
                "edfi_admin_api/readonly_access"
            );
            if (registration != "no-api-client")
            {
                await AddApiClientWithDataStoresAsync(
                    repository,
                    clientId,
                    isApproved: registration.StartsWith("approved", StringComparison.Ordinal),
                    dataStoreCount: registration.EndsWith("two-data-stores", StringComparison.Ordinal) ? 2 : 0
                );
            }

            _credential =
                await repository.GetClientCredentialAsync(clientId)
                ?? throw new InvalidOperationException("Seeded application credential was not found.");
            _application =
                await repository.GetApplicationByClientIdAsync(clientId)
                ?? throw new InvalidOperationException("Seeded application was not found.");

            OpenIddictTokenRepository tokenRepository = new(repository);
            _throughWrapper = await tokenRepository.GetClientCredentialAsync(clientId);
        }

        [Test]
        public void It_finds_the_registered_application() =>
            _credential.ApplicationId.Should().Be(_applicationId);

        [Test]
        public void It_reports_the_approval_of_the_registration() =>
            _credential.IsApproved.Should().Be(expectedApproval);

        [Test]
        public void It_reads_the_stored_secret() =>
            _credential.StoredClientSecret.Should().Be("hashed-secret");

        [Test]
        public void It_agrees_with_the_full_lookup() =>
            _credential
                .Should()
                .Be(
                    new ClientCredential(
                        _application.Id,
                        _application.ClientId,
                        _application.ClientSecret,
                        _application.IsApproved
                    )
                );

        [Test]
        public void It_leaves_the_data_stores_to_the_full_lookup() =>
            _application
                .DataStoreIds.Should()
                .HaveCount(registration.EndsWith("two-data-stores", StringComparison.Ordinal) ? 2 : 0);

        [Test]
        public void It_reads_the_same_credential_through_the_token_repository() =>
            _throughWrapper.Should().Be(_credential);
    }

    [TestFixture]
    public class Given_A_Client_Credential_Lookup_For_An_Unregistered_Client : OpenIddictDataRepositoryTests
    {
        private ClientCredential? _credential;
        private ApplicationInfo? _application;
        private ClientCredential? _throughWrapper;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            await RegisterApplicationAsync(repository, Guid.NewGuid().ToString());
            string unregisteredClientId = Guid.NewGuid().ToString();

            _credential = await repository.GetClientCredentialAsync(unregisteredClientId);
            _application = await repository.GetApplicationByClientIdAsync(unregisteredClientId);
            _throughWrapper = await new OpenIddictTokenRepository(repository).GetClientCredentialAsync(
                unregisteredClientId
            );
        }

        [Test]
        public void It_reports_no_credential() => _credential.Should().BeNull();

        [Test]
        public void It_agrees_with_the_full_lookup() => _application.Should().BeNull();

        [Test]
        public void It_reports_no_credential_through_the_token_repository() =>
            _throughWrapper.Should().BeNull();
    }

    [TestFixture]
    public class Given_A_Client_Credential_Lookup_With_No_Stored_Secret : OpenIddictDataRepositoryTests
    {
        private bool _storedSecretIsNull;
        private ClientCredential _credential = null!;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            await RegisterApplicationAsync(repository, clientId);
            await Connection!.ExecuteAsync(
                "UPDATE dmscs.OpenIddictApplication SET ClientSecret = NULL WHERE ClientId = @ClientId",
                new { ClientId = clientId }
            );
            _storedSecretIsNull = await Connection!.ExecuteScalarAsync<bool>(
                "SELECT CAST(CASE WHEN ClientSecret IS NULL THEN 1 ELSE 0 END AS BIT) FROM dmscs.OpenIddictApplication WHERE ClientId = @ClientId",
                new { ClientId = clientId }
            );

            _credential =
                await repository.GetClientCredentialAsync(clientId)
                ?? throw new InvalidOperationException("Seeded application credential was not found.");
        }

        [Test]
        public void It_stores_a_database_null() => _storedSecretIsNull.Should().BeTrue();

        [Test]
        public void It_reads_the_secret_as_an_empty_string() =>
            _credential.StoredClientSecret.Should().Be(string.Empty);
    }

    // Matching follows the database's collation, as the full lookup's does. The test reads the
    // collation rather than assuming one: under a case-insensitive collation (the default) a
    // different casing resolves, and the stored spelling is what comes back.
    [TestFixture]
    public class Given_A_Client_Credential_Lookup_With_Different_Casing : OpenIddictDataRepositoryTests
    {
        private string _registeredClientId = null!;
        private bool _collationIgnoresCase;
        private ClientCredential? _exact;
        private ClientCredential? _differentCasing;
        private ApplicationInfo? _fullLookupWithDifferentCasing;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _registeredClientId = $"Credential-Casing-{Guid.NewGuid():N}";
            await RegisterApplicationAsync(repository, _registeredClientId);
            string lowered = _registeredClientId.ToLowerInvariant();
            _collationIgnoresCase =
                await Connection!.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM dmscs.OpenIddictApplication WHERE ClientId = @Lowered",
                    new { Lowered = lowered }
                ) > 0;

            _exact = await repository.GetClientCredentialAsync(_registeredClientId);
            _differentCasing = await repository.GetClientCredentialAsync(lowered);
            _fullLookupWithDifferentCasing = await repository.GetApplicationByClientIdAsync(lowered);
        }

        [Test]
        public void It_returns_the_stored_spelling() =>
            _exact!.StoredClientId.Should().Be(_registeredClientId);

        [Test]
        public void It_matches_a_different_casing_exactly_when_the_full_lookup_does() =>
            (_differentCasing is null).Should().Be(_fullLookupWithDifferentCasing is null);

        [Test]
        public void It_returns_the_stored_spelling_for_a_different_casing_under_a_case_insensitive_collation()
        {
            Assume.That(_collationIgnoresCase, "The integration database's collation is case-sensitive.");
            _differentCasing!.StoredClientId.Should().Be(_registeredClientId);
        }
    }

    // Through the token manager, the credential query decides authentication: a successful grant
    // reads the credential and then the full application once each; a failed one never reads the
    // full application.
    [TestFixture("approved")]
    [TestFixture("unapproved")]
    [TestFixture("wrong-secret")]
    [TestFixture("absent")]
    public class Given_Grants_Through_The_Credential_Path(string outcome) : OpenIddictDataRepositoryTests
    {
        private string _clientId = null!;
        private IOpenIddictDataRepository _observedRepository = null!;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _clientId = Guid.NewGuid().ToString();
            if (outcome != "absent")
            {
                await RegisterApplicationAsync(repository, _clientId);
                await AddApiClientWithDataStoresAsync(
                    repository,
                    _clientId,
                    isApproved: outcome != "unapproved",
                    dataStoreCount: 1
                );
            }

            (OpenIddictTokenManager manager, _observedRepository) = CreateTokenManagerOver(
                repository,
                new IdentityOptions()
            );
            _result = await manager.GetAccessTokenAsync([
                new("client_id", _clientId),
                new("client_secret", outcome == "wrong-secret" ? "wrong-secret" : "plain-secret"),
            ]);
        }

        [Test]
        public void It_answers_the_grant()
        {
            if (outcome == "approved")
            {
                _result.Should().BeOfType<TokenResult.Success>();
            }
            else
            {
                _result
                    .Should()
                    .BeEquivalentTo(
                        new TokenResult.FailureAuthentication(
                            "invalid_client",
                            "Invalid client or Invalid client credentials"
                        )
                    );
            }
        }

        [Test]
        public void It_reads_the_credential_once() =>
            A.CallTo(() => _observedRepository.GetClientCredentialAsync(_clientId))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_reads_the_full_application_only_after_a_successful_authentication() =>
            A.CallTo(() => _observedRepository.GetApplicationByClientIdAsync(_clientId))
                .MustHaveHappened(outcome == "approved" ? 1 : 0, Times.Exactly);
    }
}
