// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

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

    /// <summary>
    /// The name every <see cref="AddApiClientRowAsync"/> row starts with.
    /// </summary>
    protected const string ApiClientRowName = "Token limit test";

    /// <summary>
    /// An API client row and the CMS application it belongs to.
    /// </summary>
    protected sealed record ApiClientRow(int ApiClientId, int ApplicationId);

    /// <summary>
    /// Adds a vendor, a CMS application and an API client row for <paramref name="clientId"/>. A
    /// fixture that updates the row through the CMS repository passes the OpenIddict application id
    /// as <paramref name="clientUuid"/>, linking the two as the module does.
    /// </summary>
    protected static Task<ApiClientRow> AddApiClientRowAsync(
        OpenIddictDataRepository repository,
        string clientId,
        bool isApproved = true,
        Guid? clientUuid = null
    ) =>
        repository.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                int vendorId = await connection.ExecuteScalarAsync<int>(
                    """INSERT INTO "dmscs"."Vendor" ("Company") VALUES (@Company) RETURNING "Id" """,
                    new { Company = $"token-limit-{Guid.NewGuid():N}" },
                    transaction
                );
                int applicationId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO "dmscs"."Application" ("ApplicationName", "VendorId", "ClaimSetName")
                    VALUES ('Token limit test', @VendorId, 'Test claim set') RETURNING "Id"
                    """,
                    new { VendorId = vendorId },
                    transaction
                );
                int apiClientId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "ClientId", "ClientUuid", "Name", "IsApproved")
                    VALUES (@ApplicationId, @ClientId, @ClientUuid, @Name, @IsApproved) RETURNING "Id"
                    """,
                    new
                    {
                        ApplicationId = applicationId,
                        ClientId = clientId,
                        ClientUuid = clientUuid ?? Guid.NewGuid(),
                        Name = ApiClientRowName,
                        IsApproved = isApproved,
                    },
                    transaction
                );
                return new ApiClientRow(apiClientId, applicationId);
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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

    protected async Task<int> TokenRowCountAsync(Guid applicationId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""dmscs"".""OpenIddictToken"" WHERE ""ApplicationId"" = @ApplicationId",
            new { ApplicationId = applicationId }
        );
    }

    protected async Task<int> ActiveTokenCountAsync(Guid applicationId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM ""dmscs"".""OpenIddictToken""
              WHERE ""ApplicationId"" = @ApplicationId
                AND ""Status"" = 'valid'
                AND ""ExpirationDate"" > now()",
            new { ApplicationId = applicationId }
        );
    }

    protected async Task<DateTime?> RedemptionDateAsync(Guid tokenId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<DateTime?>(
            @"SELECT ""RedemptionDate"" FROM ""dmscs"".""OpenIddictToken"" WHERE ""Id"" = @Id",
            new { Id = tokenId }
        );
    }

    protected async Task SetRedemptionDateAsync(Guid tokenId, DateTime redemptionDate)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            @"UPDATE ""dmscs"".""OpenIddictToken"" SET ""RedemptionDate"" = @RedemptionDate WHERE ""Id"" = @Id",
            new { Id = tokenId, RedemptionDate = redemptionDate }
        );
    }

    /// <summary>
    /// Writes active token rows of an arbitrary type directly. <c>StoreTokenAsync</c> cannot seed
    /// these: it always writes <c>access_token</c>.
    /// </summary>
    protected async Task SeedTokenOfTypeAsync(Guid applicationId, string type, int count)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        for (int i = 0; i < count; i++)
        {
            Guid tokenId = Guid.NewGuid();
            await connection.ExecuteAsync(
                @"INSERT INTO ""dmscs"".""OpenIddictToken""
                  (""Id"", ""ApplicationId"", ""Subject"", ""Type"", ""CreationDate"", ""ExpirationDate"", ""Status"", ""ReferenceId"")
                  VALUES (@Id, @ApplicationId, @Subject, @Type, @CreationDate, @ExpirationDate, 'valid', @ReferenceId)",
                new
                {
                    Id = tokenId,
                    ApplicationId = applicationId,
                    Subject = $"{type}-{i}",
                    Type = type,
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = FarFuture,
                    ReferenceId = tokenId.ToString("N"),
                }
            );
        }
    }

    protected async Task DeleteApplicationRowAsync(Guid applicationId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            @"DELETE FROM ""dmscs"".""OpenIddictApplication"" WHERE ""Id"" = @Id",
            new { Id = applicationId }
        );
    }

    protected async Task SetApiClientApprovalAsync(string clientId, bool isApproved)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            @"UPDATE ""dmscs"".""ApiClient"" SET ""IsApproved"" = @IsApproved WHERE ""ClientId"" = @ClientId",
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
    /// The hash the <see cref="CreateClientRepository"/> hasher returns for every new secret, so a
    /// reset's write is told apart from <see cref="RegisteredSecret"/>.
    /// </summary>
    protected const string ResetSecretHash = "reset-secret-hash";

    /// <summary>
    /// The production lifecycle caller, <see cref="OpenIddictClientRepository"/>, over
    /// <paramref name="dataRepository"/>: the real engine repository, or a fake wrapping it to
    /// inject a failure. Only the secret hashing is faked.
    /// </summary>
    protected static OpenIddictClientRepository CreateClientRepository(
        IOpenIddictDataRepository dataRepository
    )
    {
        IClientSecretHasher secretHasher = A.Fake<IClientSecretHasher>();
        A.CallTo(() => secretHasher.HashSecretAsync(A<string>._)).Returns(ResetSecretHash);
        return new OpenIddictClientRepository(
            NullLogger<OpenIddictClientRepository>.Instance,
            secretHasher,
            dataRepository,
            Options.Create(new ClientSecretValidationOptions())
        );
    }

    protected static async Task<string?> StoredSecretAsync(
        OpenIddictDataRepository repository,
        string clientId
    ) => (await repository.GetClientCredentialAsync(clientId))?.StoredClientSecret;

    /// <summary>
    /// Grants <paramref name="roleName"/> to the application through the production role writes,
    /// creating the role on first use, so a deletion has a real client-role row to remove.
    /// </summary>
    protected static Task AddClientRoleAsync(
        OpenIddictDataRepository repository,
        Guid applicationId,
        string roleName
    ) =>
        repository.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                Guid roleId;
                if (
                    await repository.FindRoleIdByNameAsync(roleName, connection, transaction) is
                    { } existingId
                )
                {
                    roleId = existingId;
                }
                else
                {
                    roleId = Guid.NewGuid();
                    await repository.InsertRoleAsync(roleId, roleName, connection, transaction);
                }
                await repository.InsertClientRoleAsync(applicationId, roleId, connection, transaction);
            }
        );

    /// <summary>
    /// The name the un-approval fixtures' updates give the client, so a committed update is told
    /// apart from one that rolled back.
    /// </summary>
    protected const string RenamedApiClientName = "Renamed token limit test";

    /// <summary>
    /// The production un-approval caller, the CMS <see cref="ApiClientRepository"/>, with the audit
    /// and tenant fakes <c>ApiClientTests</c> uses, over <paramref name="participant"/>: the real
    /// OpenIddict participant, or a fake wrapping it to pause or fail inside the update's
    /// transaction.
    /// </summary>
    protected static ApiClientRepository CreateApiClientRepository(IUnapprovalTokenRevocation participant) =>
        new(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext(),
            new TenantContextProvider(),
            participant
        );

    protected static OpenIddictUnapprovalTokenRevocation CreateUnapprovalParticipant(
        OpenIddictDataRepository repository,
        ILogger<OpenIddictUnapprovalTokenRevocation>? logger = null
    ) => new(repository, logger ?? NullLogger<OpenIddictUnapprovalTokenRevocation>.Instance);

    /// <summary>
    /// The update the module sends for <paramref name="apiClient"/>, setting the approval and, when
    /// given, a new name or target application; nothing else changes.
    /// </summary>
    protected static ApiClientUpdateCommand UpdateCommandFor(
        ApiClientRow apiClient,
        bool isApproved,
        string name = ApiClientRowName,
        int? applicationId = null
    ) =>
        new()
        {
            Id = apiClient.ApiClientId,
            ApplicationId = applicationId ?? apiClient.ApplicationId,
            Name = name,
            IsApproved = isApproved,
            DataStoreIds = [],
        };

    protected async Task<(bool IsApproved, string Name, int ApplicationId)> ApiClientStateAsync(
        int apiClientId
    )
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(bool, string, int)>(
            @"SELECT ""IsApproved"", ""Name"", ""ApplicationId"" FROM ""dmscs"".""ApiClient"" WHERE ""Id"" = @Id",
            new { Id = apiClientId }
        );
    }

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
    /// The client changes that revoke its tokens and must refuse every grant that waited behind
    /// them (DMS-1493 §8.1).
    /// </summary>
    public enum LifecycleChange
    {
        CredentialReset,
        Deletion,
        UnApproval,
    }

    /// <summary>
    /// A second session holding the client's <c>OpenIddictApplication</c> row lock, which is what
    /// a competing grant has to wait on.
    /// </summary>
    protected sealed class RowLockHolder : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly Guid _applicationId;

        private RowLockHolder(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid applicationId,
            int backendPid
        )
        {
            _connection = connection;
            _transaction = transaction;
            _applicationId = applicationId;
            BackendPid = backendPid;
        }

        public int BackendPid { get; }

        public static async Task<RowLockHolder> TakeAsync(NpgsqlDataSource dataSource, Guid applicationId)
        {
            NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

            await connection.ExecuteAsync(
                @"SELECT 1 FROM ""dmscs"".""OpenIddictApplication"" WHERE ""Id"" = @Id FOR UPDATE",
                new { Id = applicationId },
                transaction
            );

            int backendPid = await connection.ExecuteScalarAsync<int>(
                "SELECT pg_backend_pid()",
                transaction: transaction
            );

            return new RowLockHolder(connection, transaction, applicationId, backendPid);
        }

        /// <summary>
        /// Inserts a valid token for the locked application inside the holder's transaction, as a
        /// grant that holds the row lock does; it becomes visible only on <see cref="CommitAsync"/>.
        /// </summary>
        public async Task InsertTokenAsync(Guid tokenId) =>
            await _connection.ExecuteAsync(
                @"INSERT INTO ""dmscs"".""OpenIddictToken""
                  (""Id"", ""ApplicationId"", ""Subject"", ""Type"", ""CreationDate"", ""ExpirationDate"", ""Status"", ""ReferenceId"")
                  VALUES (@Id, @ApplicationId, @Subject, 'access_token', @CreationDate, @ExpirationDate, 'valid', @ReferenceId)",
                new
                {
                    Id = tokenId,
                    ApplicationId = _applicationId,
                    Subject = $"holder-{tokenId:N}",
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = FarFuture,
                    ReferenceId = tokenId.ToString("N"),
                },
                _transaction
            );

        /// <summary>
        /// Ends the holder's transaction by committing what it wrote, unlike
        /// <see cref="ReleaseAsync"/>.
        /// </summary>
        public async Task CommitAsync() => await _transaction.CommitAsync();

        /// <summary>
        /// Ends the holder's transaction by rolling it back: nothing it wrote survives.
        /// </summary>
        public async Task ReleaseAsync() => await _transaction.RollbackAsync();

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Polls <c>pg_locks</c> until some other backend is waiting on a lock this holder owns, which
    /// is the only evidence that establishes the grant is blocked <em>on the lock</em>. "The call
    /// has not returned yet" is not evidence: an unfinished call may simply be waiting on the
    /// thread pool or on a connection, and accepting that would let broken locking pass.
    /// </summary>
    protected async Task WaitUntilABackendIsBlockedBy(int holderBackendPid, TimeSpan timeout)
    {
        const string BlockedByHolderSql = """
            SELECT COUNT(*)
            FROM pg_locks waiter
            JOIN pg_locks holder
              ON waiter.locktype = holder.locktype
             AND waiter.database IS NOT DISTINCT FROM holder.database
             AND waiter.relation IS NOT DISTINCT FROM holder.relation
             AND waiter.page IS NOT DISTINCT FROM holder.page
             AND waiter.tuple IS NOT DISTINCT FROM holder.tuple
             AND waiter.virtualxid IS NOT DISTINCT FROM holder.virtualxid
             AND waiter.transactionid IS NOT DISTINCT FROM holder.transactionid
             AND waiter.classid IS NOT DISTINCT FROM holder.classid
             AND waiter.objid IS NOT DISTINCT FROM holder.objid
             AND waiter.objsubid IS NOT DISTINCT FROM holder.objsubid
            WHERE holder.pid = @HolderPid
              AND holder.granted
              AND NOT waiter.granted
              AND waiter.pid <> holder.pid
            """;

        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            int waiters = await connection.ExecuteScalarAsync<int>(
                BlockedByHolderSql,
                new { HolderPid = holderBackendPid }
            );

            if (waiters > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            $"No backend was observed waiting on the row lock held by pid {holderBackendPid} "
                + $"within {timeout.TotalSeconds:0.#}s, so the grant was never proven to be blocked on it."
        );
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
        public async Task It_deletes_a_token_whose_expiration_exactly_equals_the_bound()
        {
            var deletedCount = await _repository.DeleteExpiredTokensAsync(_boundary);

            deletedCount.Should().Be(1);
            (await _repository.GetTokenStatusAsync(_tokenId)).Should().BeNull();
        }

        // RFC 6749: "Unless otherwise noted, all the protocol parameter names and values are case
        // sensitive." client_id is one of those values, so a mis-cased id must not resolve. This is
        // pinned deliberately rather than left as an accident of the SQL, because an earlier revision
        // of this branch made the lookup case-insensitive and that was reversed on purpose.
        [TestFixture]
        public class Given_A_Client_Looked_Up_With_Different_Casing : OpenIddictDataRepositoryTests
        {
            private OpenIddictDataRepository _repository = null!;
            private string _registeredClientId = null!;

            [SetUp]
            public async Task Setup()
            {
                _repository = new OpenIddictDataRepository(
                    Configuration.DatabaseOptions,
                    NullLogger<OpenIddictDataRepository>.Instance
                );
                _registeredClientId = $"Casing-Client-{Guid.NewGuid():N}";
                await RegisterApplicationAsync(_repository, _registeredClientId);
            }

            [Test]
            public async Task It_resolves_the_client_on_an_exact_match()
            {
                var found = await _repository.GetApplicationByClientIdAsync(_registeredClientId);

                found!.ClientId.Should().Be(_registeredClientId);
            }

            [Test]
            public async Task It_does_not_resolve_the_client_when_the_casing_differs()
            {
                var found = await _repository.GetApplicationByClientIdAsync(
                    _registeredClientId.ToLowerInvariant()
                );

                found.Should().BeNull();
            }
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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

    protected async Task<List<DateTime?>> RedemptionDatesAsync(IEnumerable<Guid> tokenIds)
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
    /// the same row lock a grant takes, and <c>WaitUntilABackendIsBlockedBy</c> must see it
    /// waiting on a lock the locking transaction's backend owns, so an implementation that only
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
        private int _lockingBackendPid;
        private bool _result;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _applicationId = await RegisterApplicationAsync(_repository, $"lock-existing-{Guid.NewGuid():N}");
            _connection = await _repository.CreateConnectionAsync();
            _transaction = await _repository.BeginTransactionAsync(_connection);
            try
            {
                _result = await _repository.LockApplicationAsync(_applicationId, _connection, _transaction);
                _lockingBackendPid = await _connection.ExecuteScalarAsync<int>(
                    "SELECT pg_backend_pid()",
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
            Task<RowLockHolder> competingHolder = Task.Run(() =>
                RowLockHolder.TakeAsync(DataSource!, _applicationId)
            );
            try
            {
                await WaitUntilABackendIsBlockedBy(_lockingBackendPid, _blockedObservationTimeout);
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
    /// </summary>
    [TestFixture]
    public class Given_Rows_That_Do_Not_Count_Toward_The_Limit : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
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
        // inside the statement, and PostgreSQL stores microseconds, so a tick-wide margin would be
        // unrepresentable as well as unpinnable.
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
        /// un-approval exclude it. <c>pg_locks</c> must show the grant waiting on the holder, and
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

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, applicationId);
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
                probe = WaitUntilABackendIsBlockedBy(holder.BackendPid, GrantBlockedObservationTimeout);

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
    /// case-only variant pins the ordinal comparison, which a comparison under a case-insensitive
    /// collation would accept.
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
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
    /// DMS-1493 fixture C-3, §8.1 case (ii): a grant that arrives while a credential reset,
    /// deletion or un-approval transaction is still open. The transaction makes its change and
    /// revokes the client's tokens, then holds at a gate; <c>pg_locks</c> must show the grant
    /// waiting on that transaction's backend, and once it commits the grant must refuse without
    /// storing. The grant authenticated against <see cref="RegisteredSecret"/>, and the client
    /// starts with an approved API client row that the un-approval sets to false inside the held
    /// transaction. A store that takes no lock finishes before the probe sees it waiting; one that
    /// re-validated inside its <c>FOR UPDATE</c> statement would read the snapshot taken before
    /// that statement blocked, still see the client approved, and store (A-3).
    /// </summary>
    [TestFixture(LifecycleChange.CredentialReset, EnforcementDisabled)]
    [TestFixture(LifecycleChange.CredentialReset, 15)]
    [TestFixture(LifecycleChange.Deletion, EnforcementDisabled)]
    [TestFixture(LifecycleChange.Deletion, 15)]
    [TestFixture(LifecycleChange.UnApproval, EnforcementDisabled)]
    [TestFixture(LifecycleChange.UnApproval, 15)]
    public class Given_A_Grant_Blocked_By_A_Lifecycle_Transaction(LifecycleChange change, int maxActiveTokens)
        : OpenIddictDataRepositoryTests
    {
        private readonly Guid _grantTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private string _clientId = null!;
        private Guid _earlierTokenId;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            // ApiClient.ClientId is 36 characters wide.
            _clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(_repository, _clientId);
            await AddApiClientRowAsync(_repository, _clientId);
            _earlierTokenId = await StoreValidTokenAsync(_repository, _applicationId, FarFuture);

            TaskCompletionSource<int> changeHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource commitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? lifecycle = null;
            Task<TokenStoreOutcome>? grant = null;
            Task? probe = null;
            try
            {
                lifecycle = Task.Run(() =>
                    _repository.ExecuteInTransactionAsync(
                        async (connection, transaction) =>
                        {
                            await ApplyChangeAsync(connection, transaction);
                            changeHeld.SetResult(
                                await connection.ExecuteScalarAsync<int>(
                                    "SELECT pg_backend_pid()",
                                    transaction: transaction
                                )
                            );
                            await commitGate.Task;
                        }
                    )
                );
                if (await Task.WhenAny(changeHeld.Task, lifecycle) == lifecycle)
                {
                    await lifecycle;
                    Assert.Fail("The lifecycle transaction ended before it held its change open.");
                }
                int lifecycleBackendPid = await changeHeld.Task;

                grant = Task.Run(() =>
                    _repository.StoreTokenAsync(
                        _grantTokenId,
                        _applicationId,
                        "subject-blocked-grant",
                        FarFuture,
                        maxActiveTokens,
                        RegisteredSecret
                    )
                );
                probe = WaitUntilABackendIsBlockedBy(lifecycleBackendPid, GrantBlockedObservationTimeout);

                if (await Task.WhenAny(probe, grant) == grant)
                {
                    Assert.Fail(
                        $"The grant completed with {await grant} before it was observed waiting on the {change} transaction."
                    );
                }

                await probe;
                grant
                    .IsCompleted.Should()
                    .BeFalse("the grant is waiting on the lifecycle transaction's lock");

                commitGate.SetResult();
                await lifecycle;
                _outcome = await grant;
            }
            finally
            {
                // Lets the lifecycle transaction commit (a no-op after the release), so a grant still
                // waiting on it can finish before it is awaited.
                commitGate.TrySetResult();
                await AwaitWithoutObservingAsync(lifecycle, grant, probe);
            }
        }

        /// <summary>
        /// The statements of the lifecycle operation for this change, in its order: a reset or a
        /// deletion writes the application row first, an un-approval locks it first and writes
        /// <c>ApiClient</c> last. Each one revokes the client's tokens before the gate.
        /// </summary>
        private async Task ApplyChangeAsync(IDbConnection connection, IDbTransaction transaction)
        {
            switch (change)
            {
                case LifecycleChange.CredentialReset:
                    (
                        await _repository.UpdateClientSecretAsync(
                            _applicationId,
                            "rotated-secret",
                            connection,
                            transaction
                        )
                    )
                        .Should()
                        .Be(1);
                    await RevokeAsync();
                    break;
                case LifecycleChange.Deletion:
                    (await _repository.DeleteApplicationByIdAsync(_applicationId, connection, transaction))
                        .Should()
                        .Be(1);
                    await RevokeAsync();
                    break;
                case LifecycleChange.UnApproval:
                    (await _repository.LockApplicationAsync(_applicationId, connection, transaction))
                        .Should()
                        .BeTrue();
                    await RevokeAsync();
                    (
                        await connection.ExecuteAsync(
                            @"UPDATE ""dmscs"".""ApiClient"" SET ""IsApproved"" = false WHERE ""ClientId"" = @ClientId",
                            new { ClientId = _clientId },
                            transaction
                        )
                    )
                        .Should()
                        .Be(1);
                    break;
            }

            async Task RevokeAsync() =>
                (await _repository.RevokeTokensForApplicationAsync(_applicationId, connection, transaction))
                    .Should()
                    .Be(1);
        }

        [Test]
        public void It_refuses_the_grant() =>
            _outcome
                .Should()
                .Be(
                    change == LifecycleChange.Deletion
                        ? TokenStoreOutcome.ClientNotFound
                        : TokenStoreOutcome.ClientChanged
                );

        [Test]
        public async Task It_stores_no_token_for_the_grant() =>
            (await _repository.GetTokenStatusAsync(_grantTokenId)).Should().BeNull();

        [Test]
        public async Task It_leaves_the_client_only_the_earlier_token() =>
            (await TokenRowCountAsync(_applicationId)).Should().Be(1);

        [Test]
        public async Task It_commits_the_revocation_of_the_earlier_token() =>
            (await _repository.GetTokenStatusAsync(_earlierTokenId)).Should().Be("revoked");
    }

    // DMS-1493 Step 3.4: credential reset through its production caller,
    // OpenIddictClientRepository.ResetCredentialsAsync, against the real engine repository.

    /// <summary>
    /// AC-1 through the engine: the reset stores the new hash and revokes every token of the client
    /// in its own transaction, and leaves another client's secret and token alone.
    /// </summary>
    [TestFixture]
    public class Given_ResetCredentialsAsync_Through_The_Engine : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private string _clientId = null!;
        private string _bystanderClientId = null!;
        private Guid[] _tokenIds = null!;
        private Guid _bystanderTokenId;
        private ClientResetResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            _clientId = Guid.NewGuid().ToString();
            _bystanderClientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, _clientId);
            Guid bystanderApplicationId = await RegisterApplicationAsync(_repository, _bystanderClientId);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture);

            _result = await CreateClientRepository(_repository)
                .ResetCredentialsAsync(applicationId.ToString());
        }

        [Test]
        public void It_resets_the_credentials() => _result.Should().BeOfType<ClientResetResult.Success>();

        [Test]
        public async Task It_stores_the_new_secret_hash() =>
            (await StoredSecretAsync(_repository, _clientId)).Should().Be(ResetSecretHash);

        [Test]
        public async Task It_revokes_every_token_of_the_client() =>
            (await TokenStatusesAsync(_repository, _tokenIds))
                .Should()
                .Equal("revoked", "revoked", "revoked");

        [Test]
        public async Task It_leaves_the_other_client_unchanged()
        {
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
            (await StoredSecretAsync(_repository, _bystanderClientId)).Should().Be(RegisteredSecret);
        }
    }

    /// <summary>
    /// DMS-1493 fixture C-1 for a reset, §8.1 case (i): a grant that already holds the client's
    /// application row lock when the reset starts. <c>pg_locks</c> must show the reset waiting on
    /// the holder's backend; the holder then inserts a token and commits, so the reset has to
    /// revoke it. A reset that does not write the application row first finishes before the probe
    /// sees it waiting, and one that revokes before that write never sees the holder's token.
    /// </summary>
    [TestFixture]
    public class Given_A_Grant_Committed_While_The_Reset_Waits : OpenIddictDataRepositoryTests
    {
        private readonly Guid _committedWhileWaitingTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private string _clientId = null!;
        private Guid[] _earlierTokenIds = null!;
        private Guid _bystanderTokenId;
        private ClientResetResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            _clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, _clientId);
            Guid bystanderApplicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString()
            );
            _earlierTokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture);
            OpenIddictClientRepository clientRepository = CreateClientRepository(_repository);

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, applicationId);
            Task<ClientResetResult>? reset = null;
            Task? probe = null;
            try
            {
                reset = Task.Run(() => clientRepository.ResetCredentialsAsync(applicationId.ToString()));
                probe = WaitUntilABackendIsBlockedBy(holder.BackendPid, GrantBlockedObservationTimeout);

                if (await Task.WhenAny(probe, reset) == reset)
                {
                    Assert.Fail(
                        $"The reset completed with {(await reset).GetType().Name} before it was observed waiting on the held row lock."
                    );
                }

                await probe;
                reset.IsCompleted.Should().BeFalse("the reset is waiting on the row lock the holder owns");

                await holder.InsertTokenAsync(_committedWhileWaitingTokenId);
                await holder.CommitAsync();

                _result = await reset;
            }
            finally
            {
                // Ends the holder's transaction first (a no-op after the commit), so a reset still
                // waiting on it can finish before it is awaited.
                await holder.DisposeAsync();
                await AwaitWithoutObservingAsync(reset, probe);
            }
        }

        [Test]
        public void It_resets_the_credentials() => _result.Should().BeOfType<ClientResetResult.Success>();

        [Test]
        public async Task It_revokes_the_token_committed_while_it_waited() =>
            (await _repository.GetTokenStatusAsync(_committedWhileWaitingTokenId)).Should().Be("revoked");

        [Test]
        public async Task It_revokes_the_earlier_tokens() =>
            (await TokenStatusesAsync(_repository, _earlierTokenIds)).Should().Equal("revoked", "revoked");

        [Test]
        public async Task It_stores_the_new_secret_hash() =>
            (await StoredSecretAsync(_repository, _clientId)).Should().Be(ResetSecretHash);

        [Test]
        public async Task It_leaves_the_other_clients_token_valid() =>
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
    }

    /// <summary>
    /// DMS-1493 fixture C-2 for a reset, §8.1 case (ii): a grant that authenticated against the old
    /// secret and reaches the store after the reset committed is refused under either limit
    /// setting. A grant carrying the new hash is stored, so the refusal comes from the changed
    /// secret alone and not from the client being otherwise unusable.
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Grant_With_The_Old_Secret_After_The_Reset(int maxActiveTokens)
        : OpenIddictDataRepositoryTests
    {
        private readonly Guid _oldSecretTokenId = Guid.NewGuid();
        private readonly Guid _newSecretTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private ClientResetResult _resetResult = null!;
        private TokenStoreOutcome _oldSecretOutcome;
        private TokenStoreOutcome _newSecretOutcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            // ApiClient.ClientId is 36 characters wide.
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            await AddApiClientRowAsync(_repository, clientId);

            _resetResult = await CreateClientRepository(_repository)
                .ResetCredentialsAsync(applicationId.ToString());

            _oldSecretOutcome = await _repository.StoreTokenAsync(
                _oldSecretTokenId,
                applicationId,
                "subject-old-secret",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
            _newSecretOutcome = await _repository.StoreTokenAsync(
                _newSecretTokenId,
                applicationId,
                "subject-new-secret",
                FarFuture,
                maxActiveTokens,
                ResetSecretHash
            );
        }

        [Test]
        public void It_resets_the_credentials() =>
            _resetResult.Should().BeOfType<ClientResetResult.Success>();

        [Test]
        public void It_refuses_the_grant_with_the_old_secret() =>
            _oldSecretOutcome.Should().Be(TokenStoreOutcome.ClientChanged);

        [Test]
        public async Task It_stores_no_token_for_that_grant() =>
            (await _repository.GetTokenStatusAsync(_oldSecretTokenId)).Should().BeNull();

        [Test]
        public async Task It_stores_a_grant_with_the_new_secret()
        {
            _newSecretOutcome.Should().Be(TokenStoreOutcome.Stored);
            (await _repository.GetTokenStatusAsync(_newSecretTokenId)).Should().Be("valid");
        }
    }

    /// <summary>
    /// DMS-1493 whole-lifecycle rollback for a reset (D-7): the revocation fails after the secret
    /// write succeeded in the same transaction. The reset reports an unknown failure, and the
    /// engine keeps neither change: the stored hash is still the registered one and every token
    /// is still valid.
    /// </summary>
    [TestFixture]
    public class Given_A_Reset_Whose_Revocation_Fails : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private IOpenIddictDataRepository _failingRepository = null!;
        private string _clientId = null!;
        private Guid _applicationId;
        private Guid[] _tokenIds = null!;
        private ClientResetResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            _clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(_repository, _clientId);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
            ];
            _failingRepository = A.Fake<IOpenIddictDataRepository>(configuration =>
                configuration.Wrapping(_repository)
            );
            A.CallTo(() =>
                    _failingRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction?>._
                    )
                )
                .ThrowsAsync(new InvalidOperationException("Injected revocation failure"));

            _result = await CreateClientRepository(_failingRepository)
                .ResetCredentialsAsync(_applicationId.ToString());
        }

        [Test]
        public void It_reports_an_unknown_failure() =>
            _result.Should().BeOfType<ClientResetResult.FailureUnknown>();

        [Test]
        public void It_wrote_the_new_secret_in_a_transaction_before_the_revocation_failed() =>
            A.CallTo(() =>
                    _failingRepository.UpdateClientSecretAsync(
                        _applicationId,
                        ResetSecretHash,
                        A<IDbConnection>._,
                        A<IDbTransaction>.That.IsNotNull()
                    )
                )
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() =>
                            _failingRepository.RevokeTokensForApplicationAsync(
                                _applicationId,
                                A<IDbConnection>._,
                                A<IDbTransaction?>._
                            )
                        )
                        .MustHaveHappenedOnceExactly()
                );

        [Test]
        public async Task It_keeps_the_registered_secret_hash() =>
            (await StoredSecretAsync(_repository, _clientId)).Should().Be(RegisteredSecret);

        [Test]
        public async Task It_leaves_every_token_valid() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("valid", "valid");
    }

    // DMS-1493 Step 3.5: client deletion through its production caller,
    // OpenIddictClientRepository.DeleteClientAsync, against the real engine repository.

    /// <summary>
    /// AC-2 through the engine: the deletion removes the application row with its scope and
    /// client-role rows and revokes every token of the client in one transaction. Another client
    /// sharing a scope and the role keeps its row, its associations and its token, and the scope
    /// and role themselves remain. A repeated deletion answers not found and changes nothing: the
    /// revocation dates written over the first deletion's are still there afterwards.
    /// </summary>
    [TestFixture]
    public class Given_DeleteClientAsync_Through_The_Engine : OpenIddictDataRepositoryTests
    {
        private readonly string _sharedScope = $"delete-shared-{Guid.NewGuid():N}";
        private readonly string _ownScope = $"delete-own-{Guid.NewGuid():N}";
        private readonly string _roleName = $"delete-role-{Guid.NewGuid():N}";
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private Guid _bystanderApplicationId;
        private string? _bystanderRowBefore;
        private Guid[] _tokenIds = null!;
        private Guid _bystanderTokenId;
        private ClientDeleteResult _result = null!;
        private List<string?> _statusesAfterDeletion = null!;
        private ClientDeleteResult _repeatResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            _applicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString(),
                _sharedScope,
                _ownScope
            );
            _bystanderApplicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString(),
                _sharedScope
            );
            await AddClientRoleAsync(_repository, _applicationId, _roleName);
            await AddClientRoleAsync(_repository, _bystanderApplicationId, _roleName);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, _bystanderApplicationId, FarFuture);
            _bystanderRowBefore = await ApplicationRowJsonAsync(_bystanderApplicationId);

            (await ApplicationScopeRowCountAsync(_applicationId))
                .Should()
                .Be(2, "the deletion must have scope rows to remove");
            (await ClientRoleRowCountAsync(_applicationId))
                .Should()
                .Be(1, "the deletion must have a client-role row to remove");

            OpenIddictClientRepository clientRepository = CreateClientRepository(_repository);
            _result = await clientRepository.DeleteClientAsync(_applicationId.ToString());
            _statusesAfterDeletion = await TokenStatusesAsync(_repository, _tokenIds);

            // Fixed dates a repeated revocation would overwrite with the current time.
            foreach (Guid tokenId in _tokenIds)
            {
                await SetRedemptionDateAsync(tokenId, SentinelRedemptionDate);
            }
            _repeatResult = await clientRepository.DeleteClientAsync(_applicationId.ToString());
        }

        [Test]
        public void It_deletes_the_client() => _result.Should().BeOfType<ClientDeleteResult.Success>();

        [Test]
        public async Task It_removes_the_application_row() =>
            (await ApplicationRowJsonAsync(_applicationId)).Should().BeNull();

        [Test]
        public async Task It_removes_the_clients_scope_rows() =>
            (await ApplicationScopeRowCountAsync(_applicationId)).Should().Be(0);

        [Test]
        public async Task It_removes_the_clients_role_row() =>
            (await ClientRoleRowCountAsync(_applicationId)).Should().Be(0);

        [Test]
        public async Task It_keeps_the_scopes_and_the_role_themselves()
        {
            (await ScopeRowCountsAsync(_sharedScope)).Should().Be((1, 1));
            (await ScopeRowCountsAsync(_ownScope)).Should().Be((1, 0));
            (await RoleRowCountsAsync(_roleName)).Should().Be((1, 1));
        }

        [Test]
        public void It_revokes_every_token_of_the_client() =>
            _statusesAfterDeletion.Should().Equal("revoked", "revoked", "revoked");

        [Test]
        public async Task It_leaves_the_other_client_unchanged()
        {
            (await ApplicationRowJsonAsync(_bystanderApplicationId))
                .Should()
                .NotBeNull()
                .And.Be(_bystanderRowBefore);
            (await ApplicationScopeRowCountAsync(_bystanderApplicationId)).Should().Be(1);
            (await ClientRoleRowCountAsync(_bystanderApplicationId)).Should().Be(1);
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
        }

        [Test]
        public void It_answers_a_repeated_deletion_with_client_not_found() =>
            _repeatResult.Should().BeOfType<ClientDeleteResult.FailureClientNotFound>();

        [Test]
        public async Task It_keeps_the_earlier_revocations_unchanged_on_the_repeat()
        {
            (await TokenStatusesAsync(_repository, _tokenIds))
                .Should()
                .Equal("revoked", "revoked", "revoked");
            (await RedemptionDatesAsync(_tokenIds))
                .Should()
                .Equal(SentinelRedemptionDate, SentinelRedemptionDate, SentinelRedemptionDate);
        }
    }

    /// <summary>
    /// DMS-1493 D-6: an application row already deleted without revoking its tokens, as every
    /// deletion did before this change, leaves valid orphan tokens. Deleting the client again
    /// answers not found and still commits the revocation, so the orphans converge to revoked.
    /// </summary>
    [TestFixture]
    public class Given_DeleteClientAsync_For_An_Application_Deleted_Without_Revocation
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid[] _orphanTokenIds = null!;
        private Guid _bystanderTokenId;
        private ClientDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            Guid applicationId = await RegisterApplicationAsync(_repository, Guid.NewGuid().ToString());
            Guid bystanderApplicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString()
            );
            _orphanTokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture);
            await DeleteApplicationRowAsync(applicationId);

            (await ApplicationRowJsonAsync(applicationId)).Should().BeNull("the application is already gone");
            (await TokenStatusesAsync(_repository, _orphanTokenIds))
                .Should()
                .Equal(["valid", "valid"], "its tokens outlived it");

            _result = await CreateClientRepository(_repository).DeleteClientAsync(applicationId.ToString());
        }

        [Test]
        public void It_answers_client_not_found() =>
            _result.Should().BeOfType<ClientDeleteResult.FailureClientNotFound>();

        [Test]
        public async Task It_revokes_the_orphaned_tokens() =>
            (await TokenStatusesAsync(_repository, _orphanTokenIds)).Should().Equal("revoked", "revoked");

        [Test]
        public async Task It_leaves_the_other_clients_token_valid() =>
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
    }

    /// <summary>
    /// DMS-1493 fixture C-1 for a deletion, §8.1 case (i): a grant that already holds the client's
    /// application row lock when the deletion starts. <c>pg_locks</c> must show the deletion
    /// waiting on the holder's backend; the holder then inserts a token and commits, so the
    /// deletion has to revoke it. A deletion that does not delete the application row first
    /// finishes before the probe sees it waiting, and one that revokes before that delete never
    /// sees the holder's token.
    /// </summary>
    [TestFixture]
    public class Given_A_Grant_Committed_While_The_Deletion_Waits : OpenIddictDataRepositoryTests
    {
        private readonly Guid _committedWhileWaitingTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private Guid[] _earlierTokenIds = null!;
        private Guid _bystanderTokenId;
        private ClientDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            _applicationId = await RegisterApplicationAsync(_repository, Guid.NewGuid().ToString());
            Guid bystanderApplicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString()
            );
            _earlierTokenIds =
            [
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture);
            OpenIddictClientRepository clientRepository = CreateClientRepository(_repository);

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, _applicationId);
            Task<ClientDeleteResult>? deletion = null;
            Task? probe = null;
            try
            {
                deletion = Task.Run(() => clientRepository.DeleteClientAsync(_applicationId.ToString()));
                probe = WaitUntilABackendIsBlockedBy(holder.BackendPid, GrantBlockedObservationTimeout);

                if (await Task.WhenAny(probe, deletion) == deletion)
                {
                    Assert.Fail(
                        $"The deletion completed with {(await deletion).GetType().Name} before it was observed waiting on the held row lock."
                    );
                }

                await probe;
                deletion
                    .IsCompleted.Should()
                    .BeFalse("the deletion is waiting on the row lock the holder owns");

                await holder.InsertTokenAsync(_committedWhileWaitingTokenId);
                await holder.CommitAsync();

                _result = await deletion;
            }
            finally
            {
                // Ends the holder's transaction first (a no-op after the commit), so a deletion
                // still waiting on it can finish before it is awaited.
                await holder.DisposeAsync();
                await AwaitWithoutObservingAsync(deletion, probe);
            }
        }

        [Test]
        public void It_deletes_the_client() => _result.Should().BeOfType<ClientDeleteResult.Success>();

        [Test]
        public async Task It_revokes_the_token_committed_while_it_waited() =>
            (await _repository.GetTokenStatusAsync(_committedWhileWaitingTokenId)).Should().Be("revoked");

        [Test]
        public async Task It_revokes_the_earlier_tokens() =>
            (await TokenStatusesAsync(_repository, _earlierTokenIds)).Should().Equal("revoked", "revoked");

        [Test]
        public async Task It_removes_the_application_row() =>
            (await ApplicationRowJsonAsync(_applicationId)).Should().BeNull();

        [Test]
        public async Task It_leaves_the_other_clients_token_valid() =>
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
    }

    /// <summary>
    /// DMS-1493 fixture C-2 for a deletion, §8.1 case (ii): a grant that reaches the store after
    /// the deletion committed is refused as not found under either limit setting and writes no
    /// row. The same client's grant before the deletion was stored, so the refusal comes from the
    /// deletion alone, and the deletion revoked that earlier token.
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Grant_After_The_Deletion(int maxActiveTokens) : OpenIddictDataRepositoryTests
    {
        private readonly Guid _beforeDeletionTokenId = Guid.NewGuid();
        private readonly Guid _afterDeletionTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private TokenStoreOutcome _beforeDeletionOutcome;
        private ClientDeleteResult _deleteResult = null!;
        private TokenStoreOutcome _afterDeletionOutcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            // ApiClient.ClientId is 36 characters wide.
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            await AddApiClientRowAsync(_repository, clientId);

            _beforeDeletionOutcome = await _repository.StoreTokenAsync(
                _beforeDeletionTokenId,
                applicationId,
                "subject-before-deletion",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
            _deleteResult = await CreateClientRepository(_repository)
                .DeleteClientAsync(applicationId.ToString());
            _afterDeletionOutcome = await _repository.StoreTokenAsync(
                _afterDeletionTokenId,
                applicationId,
                "subject-after-deletion",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
        }

        [Test]
        public void It_deletes_the_client() => _deleteResult.Should().BeOfType<ClientDeleteResult.Success>();

        [Test]
        public async Task It_stored_the_grant_before_the_deletion_and_revoked_it()
        {
            _beforeDeletionOutcome.Should().Be(TokenStoreOutcome.Stored);
            (await _repository.GetTokenStatusAsync(_beforeDeletionTokenId)).Should().Be("revoked");
        }

        [Test]
        public void It_refuses_the_grant_after_the_deletion() =>
            _afterDeletionOutcome.Should().Be(TokenStoreOutcome.ClientNotFound);

        [Test]
        public async Task It_stores_no_token_for_that_grant() =>
            (await _repository.GetTokenStatusAsync(_afterDeletionTokenId)).Should().BeNull();
    }

    /// <summary>
    /// DMS-1493 whole-lifecycle rollback for a deletion (D-7): the revocation fails after the
    /// application row, and with it its scope and client-role rows, was deleted in the same
    /// transaction. The deletion reports an unknown failure, and the engine keeps neither change:
    /// the application row, its scope and client-role rows and every token are as they were.
    /// </summary>
    [TestFixture]
    public class Given_A_Deletion_Whose_Revocation_Fails : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private IOpenIddictDataRepository _failingRepository = null!;
        private Guid _applicationId;
        private string? _applicationRowBefore;
        private Guid[] _tokenIds = null!;
        private ClientDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            _applicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString(),
                $"rollback-scope-{Guid.NewGuid():N}"
            );
            await AddClientRoleAsync(_repository, _applicationId, $"rollback-role-{Guid.NewGuid():N}");
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
            ];
            _applicationRowBefore = await ApplicationRowJsonAsync(_applicationId);

            (await ApplicationScopeRowCountAsync(_applicationId))
                .Should()
                .Be(1, "the rollback must have a scope row to restore");
            (await ClientRoleRowCountAsync(_applicationId))
                .Should()
                .Be(1, "the rollback must have a client-role row to restore");

            _failingRepository = A.Fake<IOpenIddictDataRepository>(configuration =>
                configuration.Wrapping(_repository)
            );
            A.CallTo(() =>
                    _failingRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction?>._
                    )
                )
                .ThrowsAsync(new InvalidOperationException("Injected revocation failure"));

            _result = await CreateClientRepository(_failingRepository)
                .DeleteClientAsync(_applicationId.ToString());
        }

        [Test]
        public void It_reports_an_unknown_failure() =>
            _result.Should().BeOfType<ClientDeleteResult.FailureUnknown>();

        [Test]
        public void It_deleted_the_application_in_a_transaction_before_the_revocation_failed() =>
            A.CallTo(() =>
                    _failingRepository.DeleteApplicationByIdAsync(
                        _applicationId,
                        A<IDbConnection>._,
                        A<IDbTransaction>.That.IsNotNull()
                    )
                )
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() =>
                            _failingRepository.RevokeTokensForApplicationAsync(
                                _applicationId,
                                A<IDbConnection>._,
                                A<IDbTransaction?>._
                            )
                        )
                        .MustHaveHappenedOnceExactly()
                );

        [Test]
        public async Task It_keeps_the_application_row() =>
            (await ApplicationRowJsonAsync(_applicationId))
                .Should()
                .NotBeNull()
                .And.Be(_applicationRowBefore);

        [Test]
        public async Task It_keeps_the_scope_row() =>
            (await ApplicationScopeRowCountAsync(_applicationId)).Should().Be(1);

        [Test]
        public async Task It_keeps_the_client_role_row() =>
            (await ClientRoleRowCountAsync(_applicationId)).Should().Be(1);

        [Test]
        public async Task It_leaves_every_token_valid() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("valid", "valid");
    }

    // DMS-1493 Step 3.6: un-approval through its production caller, the CMS
    // ApiClientRepository.UpdateApiClient, with the real OpenIddict participant against the real
    // engine repository. Each API client row carries its OpenIddict application id as ClientUuid.

    /// <summary>
    /// AC-3 through the engine: the CMS update that un-approves an API client revokes every token
    /// of its identity-provider client in the update's transaction and commits that together with
    /// the un-approval and the new name. Another client keeps its approval and its token.
    /// </summary>
    [TestFixture]
    public class Given_UpdateApiClient_Un_Approving_Through_The_Engine : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private ApiClientRow _apiClient = null!;
        private ApiClientRow _bystanderApiClient = null!;
        private Guid[] _tokenIds = null!;
        private Guid _bystanderTokenId;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            // ApiClient.ClientId is 36 characters wide.
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: applicationId);
            string bystanderClientId = Guid.NewGuid().ToString();
            Guid bystanderApplicationId = await RegisterApplicationAsync(_repository, bystanderClientId);
            _bystanderApiClient = await AddApiClientRowAsync(
                _repository,
                bystanderClientId,
                clientUuid: bystanderApplicationId
            );
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture);

            _result = await CreateApiClientRepository(CreateUnapprovalParticipant(_repository))
                .UpdateApiClient(UpdateCommandFor(_apiClient, isApproved: false, RenamedApiClientName));
        }

        [Test]
        public void It_updates_the_api_client() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_commits_the_un_approval_and_the_new_name() =>
            (await ApiClientStateAsync(_apiClient.ApiClientId))
                .Should()
                .Be((false, RenamedApiClientName, _apiClient.ApplicationId));

        [Test]
        public async Task It_revokes_every_token_of_the_client() =>
            (await TokenStatusesAsync(_repository, _tokenIds))
                .Should()
                .Equal("revoked", "revoked", "revoked");

        [Test]
        public async Task It_leaves_the_other_client_approved_with_its_token_valid()
        {
            (await ApiClientStateAsync(_bystanderApiClient.ApiClientId))
                .Should()
                .Be((true, ApiClientRowName, _bystanderApiClient.ApplicationId));
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
        }
    }

    /// <summary>
    /// The regression control (R2-F3): the same update with the client kept approved, renamed only,
    /// commits the new name and leaves every token valid with no redemption date. Fails if the
    /// revocation runs on every update instead of on un-approval.
    /// </summary>
    [TestFixture]
    public class Given_UpdateApiClient_Keeping_The_Client_Approved : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private ApiClientRow _apiClient = null!;
        private Guid[] _tokenIds = null!;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: applicationId);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];

            _result = await CreateApiClientRepository(CreateUnapprovalParticipant(_repository))
                .UpdateApiClient(UpdateCommandFor(_apiClient, isApproved: true, RenamedApiClientName));
        }

        [Test]
        public void It_updates_the_api_client() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_commits_the_new_name_with_the_client_still_approved() =>
            (await ApiClientStateAsync(_apiClient.ApiClientId))
                .Should()
                .Be((true, RenamedApiClientName, _apiClient.ApplicationId));

        [Test]
        public async Task It_leaves_every_token_valid() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("valid", "valid");

        [Test]
        public async Task It_writes_no_redemption_date() =>
            (await RedemptionDatesAsync(_tokenIds))
                .Should()
                .HaveCount(2)
                .And.OnlyContain(date => date == null);
    }

    /// <summary>
    /// A repeated un-approval is idempotent: it succeeds, the client stays unapproved, and the
    /// participant's second revocation changes no token, so the revocation dates written over the
    /// first one's are still there afterwards.
    /// </summary>
    [TestFixture]
    public class Given_UpdateApiClient_Un_Approving_An_Already_Unapproved_Client
        : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private ApiClientRow _apiClient = null!;
        private Guid[] _tokenIds = null!;
        private ApiClientUpdateResult _firstResult = null!;
        private List<string?> _statusesAfterFirst = null!;
        private ApiClientUpdateResult _repeatResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: applicationId);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            ApiClientRepository apiClientRepository = CreateApiClientRepository(
                CreateUnapprovalParticipant(_repository)
            );

            _firstResult = await apiClientRepository.UpdateApiClient(
                UpdateCommandFor(_apiClient, isApproved: false)
            );
            _statusesAfterFirst = await TokenStatusesAsync(_repository, _tokenIds);

            // Fixed dates a repeated revocation would overwrite with the current time.
            foreach (Guid tokenId in _tokenIds)
            {
                await SetRedemptionDateAsync(tokenId, SentinelRedemptionDate);
            }
            _repeatResult = await apiClientRepository.UpdateApiClient(
                UpdateCommandFor(_apiClient, isApproved: false)
            );
        }

        [Test]
        public void It_un_approves_the_client() =>
            _firstResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public void It_revokes_the_tokens_on_the_first_un_approval() =>
            _statusesAfterFirst.Should().Equal("revoked", "revoked");

        [Test]
        public void It_accepts_the_repeat() =>
            _repeatResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_keeps_the_client_unapproved() =>
            (await ApiClientStateAsync(_apiClient.ApiClientId)).IsApproved.Should().BeFalse();

        [Test]
        public async Task It_keeps_the_earlier_revocations_unchanged_on_the_repeat()
        {
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("revoked", "revoked");
            (await RedemptionDatesAsync(_tokenIds))
                .Should()
                .Equal(SentinelRedemptionDate, SentinelRedemptionDate);
        }
    }

    /// <summary>
    /// Re-approving a client after an un-approval does not bring its old tokens back: they stay
    /// revoked. A new grant through the real token manager succeeds, and its token is the client's
    /// only valid one.
    /// </summary>
    [TestFixture]
    public class Given_Re_Approval_After_Un_Approval : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private ApiClientRow _apiClient = null!;
        private Guid[] _oldTokenIds = null!;
        private ApiClientUpdateResult _unApprovalResult = null!;
        private ApiClientUpdateResult _reApprovalResult = null!;
        private TokenResult _grant = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: _applicationId);
            _oldTokenIds =
            [
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
            ];
            ApiClientRepository apiClientRepository = CreateApiClientRepository(
                CreateUnapprovalParticipant(_repository)
            );

            _unApprovalResult = await apiClientRepository.UpdateApiClient(
                UpdateCommandFor(_apiClient, isApproved: false)
            );
            _reApprovalResult = await apiClientRepository.UpdateApiClient(
                UpdateCommandFor(_apiClient, isApproved: true)
            );
            _grant = await RequestGrantAsync(
                CreateTokenManager(_repository, new IdentityOptions()),
                clientId
            );
        }

        [Test]
        public async Task It_un_approves_then_re_approves_the_client()
        {
            _unApprovalResult.Should().BeOfType<ApiClientUpdateResult.Success>();
            _reApprovalResult.Should().BeOfType<ApiClientUpdateResult.Success>();
            (await ApiClientStateAsync(_apiClient.ApiClientId)).IsApproved.Should().BeTrue();
        }

        [Test]
        public async Task It_keeps_the_old_tokens_revoked() =>
            (await TokenStatusesAsync(_repository, _oldTokenIds)).Should().Equal("revoked", "revoked");

        [Test]
        public void It_issues_a_new_token() => _grant.Should().BeOfType<TokenResult.Success>();

        [Test]
        public async Task It_stores_the_new_token_as_the_only_valid_one()
        {
            (await TokenRowCountAsync(_applicationId)).Should().Be(3);
            (await ActiveTokenCountAsync(_applicationId)).Should().Be(1);
        }
    }

    /// <summary>
    /// DMS-1493 fixture C-1 for an un-approval, §8.1 case (i): a grant that already holds the
    /// client's application row lock when the CMS update starts. <c>pg_locks</c> must show the
    /// update waiting on the holder's backend, at its participant's lock; the holder then inserts a
    /// token and commits, so the update has to revoke it. An update whose participant takes no lock
    /// finishes before the probe sees it waiting, and one that revokes before locking never sees
    /// the holder's token.
    /// </summary>
    [TestFixture]
    public class Given_A_Grant_Committed_While_The_Un_Approval_Waits : OpenIddictDataRepositoryTests
    {
        private readonly Guid _committedWhileWaitingTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private ApiClientRow _apiClient = null!;
        private Guid[] _earlierTokenIds = null!;
        private Guid _bystanderTokenId;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: applicationId);
            Guid bystanderApplicationId = await RegisterApplicationAsync(
                _repository,
                Guid.NewGuid().ToString()
            );
            _earlierTokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];
            _bystanderTokenId = await StoreValidTokenAsync(_repository, bystanderApplicationId, FarFuture);
            ApiClientRepository apiClientRepository = CreateApiClientRepository(
                CreateUnapprovalParticipant(_repository)
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, applicationId);
            Task<ApiClientUpdateResult>? update = null;
            Task? probe = null;
            try
            {
                update = Task.Run(() =>
                    apiClientRepository.UpdateApiClient(UpdateCommandFor(_apiClient, isApproved: false))
                );
                probe = WaitUntilABackendIsBlockedBy(holder.BackendPid, GrantBlockedObservationTimeout);

                if (await Task.WhenAny(probe, update) == update)
                {
                    Assert.Fail(
                        $"The un-approval completed with {(await update).GetType().Name} before it was observed waiting on the held row lock."
                    );
                }

                await probe;
                update
                    .IsCompleted.Should()
                    .BeFalse("the un-approval is waiting on the row lock the holder owns");

                await holder.InsertTokenAsync(_committedWhileWaitingTokenId);
                await holder.CommitAsync();

                _result = await update;
            }
            finally
            {
                // Ends the holder's transaction first (a no-op after the commit), so an update still
                // waiting on it can finish before it is awaited.
                await holder.DisposeAsync();
                await AwaitWithoutObservingAsync(update, probe);
            }
        }

        [Test]
        public async Task It_un_approves_the_client()
        {
            _result.Should().BeOfType<ApiClientUpdateResult.Success>();
            (await ApiClientStateAsync(_apiClient.ApiClientId)).IsApproved.Should().BeFalse();
        }

        [Test]
        public async Task It_revokes_the_token_committed_while_it_waited() =>
            (await _repository.GetTokenStatusAsync(_committedWhileWaitingTokenId)).Should().Be("revoked");

        [Test]
        public async Task It_revokes_the_earlier_tokens() =>
            (await TokenStatusesAsync(_repository, _earlierTokenIds)).Should().Equal("revoked", "revoked");

        [Test]
        public async Task It_leaves_the_other_clients_token_valid() =>
            (await _repository.GetTokenStatusAsync(_bystanderTokenId)).Should().Be("valid");
    }

    /// <summary>
    /// DMS-1493 fixture C-2 for an un-approval, §8.1 case (ii): a grant that authenticated while
    /// the client was approved and reaches the store after the un-approval committed is refused
    /// under either limit setting and writes no row. The same client's grant before the
    /// un-approval was stored, so the refusal comes from the un-approval alone, and the
    /// un-approval revoked that earlier token.
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Grant_After_The_Un_Approval(int maxActiveTokens) : OpenIddictDataRepositoryTests
    {
        private readonly Guid _beforeUnApprovalTokenId = Guid.NewGuid();
        private readonly Guid _afterUnApprovalTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private TokenStoreOutcome _beforeUnApprovalOutcome;
        private ApiClientUpdateResult _updateResult = null!;
        private TokenStoreOutcome _afterUnApprovalOutcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            ApiClientRow apiClient = await AddApiClientRowAsync(
                _repository,
                clientId,
                clientUuid: applicationId
            );

            _beforeUnApprovalOutcome = await _repository.StoreTokenAsync(
                _beforeUnApprovalTokenId,
                applicationId,
                "subject-before-un-approval",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
            _updateResult = await CreateApiClientRepository(CreateUnapprovalParticipant(_repository))
                .UpdateApiClient(UpdateCommandFor(apiClient, isApproved: false));
            _afterUnApprovalOutcome = await _repository.StoreTokenAsync(
                _afterUnApprovalTokenId,
                applicationId,
                "subject-after-un-approval",
                FarFuture,
                maxActiveTokens,
                RegisteredSecret
            );
        }

        [Test]
        public void It_un_approves_the_client() =>
            _updateResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_stored_the_grant_before_the_un_approval_and_revoked_it()
        {
            _beforeUnApprovalOutcome.Should().Be(TokenStoreOutcome.Stored);
            (await _repository.GetTokenStatusAsync(_beforeUnApprovalTokenId)).Should().Be("revoked");
        }

        [Test]
        public void It_refuses_the_grant_after_the_un_approval() =>
            _afterUnApprovalOutcome.Should().Be(TokenStoreOutcome.ClientChanged);

        [Test]
        public async Task It_stores_no_token_for_that_grant() =>
            (await _repository.GetTokenStatusAsync(_afterUnApprovalTokenId)).Should().BeNull();
    }

    /// <summary>
    /// DMS-1493 fixture C-3 for an un-approval, §8.1 case (ii), with the CMS update's own
    /// transaction as the blocker. A participant wrapping the real one lets it lock and revoke,
    /// records the transaction's backend and holds at a gate before returning, so the update has
    /// not yet written <c>IsApproved</c>. <c>pg_locks</c> must show a grant waiting on that backend;
    /// once the gate opens the update writes the un-approval and commits, and the grant must refuse
    /// without storing. A store that takes no lock finishes before the probe sees it waiting; one
    /// that re-validated from the snapshot its blocked lock statement took would still see the
    /// client approved and store (A-3).
    /// </summary>
    [TestFixture(EnforcementDisabled)]
    [TestFixture(15)]
    public class Given_A_Grant_Blocked_By_The_Un_Approval_Transaction(int maxActiveTokens)
        : OpenIddictDataRepositoryTests
    {
        private readonly Guid _grantTokenId = Guid.NewGuid();
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private ApiClientRow _apiClient = null!;
        private Guid _earlierTokenId;
        private ApiClientUpdateResult _updateResult = null!;
        private TokenStoreOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: _applicationId);
            _earlierTokenId = await StoreValidTokenAsync(_repository, _applicationId, FarFuture);

            OpenIddictUnapprovalTokenRevocation participant = CreateUnapprovalParticipant(_repository);
            TaskCompletionSource<int> revocationHeld = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            TaskCompletionSource commitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IUnapprovalTokenRevocation gatedParticipant = A.Fake<IUnapprovalTokenRevocation>(configuration =>
                configuration.Wrapping(participant)
            );
            A.CallTo(() =>
                    gatedParticipant.RevokeOutstandingTokensAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .ReturnsLazily(
                    async Task<int> (Guid clientUuid, IDbConnection connection, IDbTransaction transaction) =>
                    {
                        int revokedCount = await participant.RevokeOutstandingTokensAsync(
                            clientUuid,
                            connection,
                            transaction
                        );
                        revocationHeld.SetResult(
                            await connection.ExecuteScalarAsync<int>(
                                "SELECT pg_backend_pid()",
                                transaction: transaction
                            )
                        );
                        await commitGate.Task;
                        return revokedCount;
                    }
                );
            ApiClientRepository apiClientRepository = CreateApiClientRepository(gatedParticipant);

            Task<ApiClientUpdateResult>? update = null;
            Task<TokenStoreOutcome>? grant = null;
            Task? probe = null;
            try
            {
                update = Task.Run(() =>
                    apiClientRepository.UpdateApiClient(UpdateCommandFor(_apiClient, isApproved: false))
                );
                if (await Task.WhenAny(revocationHeld.Task, update) == update)
                {
                    Assert.Fail(
                        $"The un-approval completed with {(await update).GetType().Name} before its participant held the transaction open."
                    );
                }
                int updateBackendPid = await revocationHeld.Task;

                grant = Task.Run(() =>
                    _repository.StoreTokenAsync(
                        _grantTokenId,
                        _applicationId,
                        "subject-blocked-grant",
                        FarFuture,
                        maxActiveTokens,
                        RegisteredSecret
                    )
                );
                probe = WaitUntilABackendIsBlockedBy(updateBackendPid, GrantBlockedObservationTimeout);

                if (await Task.WhenAny(probe, grant) == grant)
                {
                    Assert.Fail(
                        $"The grant completed with {await grant} before it was observed waiting on the un-approval transaction."
                    );
                }

                await probe;
                grant
                    .IsCompleted.Should()
                    .BeFalse("the grant is waiting on the un-approval transaction's lock");

                commitGate.SetResult();
                _updateResult = await update;
                _outcome = await grant;
            }
            finally
            {
                // Lets the update commit (a no-op after the release), so a grant still waiting on it
                // can finish before it is awaited.
                commitGate.TrySetResult();
                await AwaitWithoutObservingAsync(update, grant, probe);
            }
        }

        [Test]
        public async Task It_un_approves_the_client()
        {
            _updateResult.Should().BeOfType<ApiClientUpdateResult.Success>();
            (await ApiClientStateAsync(_apiClient.ApiClientId)).IsApproved.Should().BeFalse();
        }

        [Test]
        public void It_refuses_the_grant() => _outcome.Should().Be(TokenStoreOutcome.ClientChanged);

        [Test]
        public async Task It_stores_no_token_for_the_grant() =>
            (await _repository.GetTokenStatusAsync(_grantTokenId)).Should().BeNull();

        [Test]
        public async Task It_commits_the_revocation_of_the_earlier_token() =>
            (await _repository.GetTokenStatusAsync(_earlierTokenId)).Should().Be("revoked");
    }

    /// <summary>
    /// DMS-1493 whole-lifecycle rollback for an un-approval (D-7), participant side: a participant
    /// wrapping the real one lets it lock and revoke inside the CMS update's transaction, then
    /// fails. The update reports an unknown failure and rolls back both changes: the client keeps
    /// its approval and name, and the tokens the real participant had revoked are valid again.
    /// </summary>
    [TestFixture]
    public class Given_An_Un_Approval_Whose_Participant_Fails_After_Revoking : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private ApiClientRow _apiClient = null!;
        private Guid[] _tokenIds = null!;
        private int _revokedInsideTheTransaction;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            Guid applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: applicationId);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, applicationId, FarFuture),
            ];

            OpenIddictUnapprovalTokenRevocation participant = CreateUnapprovalParticipant(_repository);
            IUnapprovalTokenRevocation failingParticipant = A.Fake<IUnapprovalTokenRevocation>(
                configuration => configuration.Wrapping(participant)
            );
            A.CallTo(() =>
                    failingParticipant.RevokeOutstandingTokensAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .ReturnsLazily(
                    async Task<int> (Guid clientUuid, IDbConnection connection, IDbTransaction transaction) =>
                    {
                        _revokedInsideTheTransaction = await participant.RevokeOutstandingTokensAsync(
                            clientUuid,
                            connection,
                            transaction
                        );
                        throw new InvalidOperationException("Injected failure after the revocation");
                    }
                );

            _result = await CreateApiClientRepository(failingParticipant)
                .UpdateApiClient(UpdateCommandFor(_apiClient, isApproved: false, RenamedApiClientName));
        }

        [Test]
        public void It_reports_an_unknown_failure() =>
            _result.Should().BeOfType<ApiClientUpdateResult.FailureUnknown>();

        [Test]
        public void It_revoked_the_tokens_inside_the_transaction_before_failing() =>
            _revokedInsideTheTransaction.Should().Be(2);

        [Test]
        public async Task It_keeps_the_client_approved_with_its_name() =>
            (await ApiClientStateAsync(_apiClient.ApiClientId))
                .Should()
                .Be((true, ApiClientRowName, _apiClient.ApplicationId));

        [Test]
        public async Task It_leaves_every_token_valid() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("valid", "valid");
    }

    /// <summary>
    /// DMS-1493 whole-lifecycle rollback for an un-approval (D-7), CMS side: the real participant
    /// locks and revokes, then the <c>ApiClient</c> UPDATE matches no row because the target
    /// application does not exist. The update reports the missing application and rolls the
    /// revocation back with it: the client keeps its approval, name and application, and every
    /// token is valid. The participant logged the revocation as pending in that transaction, not
    /// as done (S3.3-F1).
    /// </summary>
    [TestFixture]
    public class Given_An_Un_Approval_Whose_Client_Update_Fails : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _applicationId;
        private ApiClientRow _apiClient = null!;
        private Guid[] _tokenIds = null!;
        private ILogger<OpenIddictUnapprovalTokenRevocation> _participantLogger = null!;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _repository = new(Configuration.DatabaseOptions, NullLogger<OpenIddictDataRepository>.Instance);
            string clientId = Guid.NewGuid().ToString();
            _applicationId = await RegisterApplicationAsync(_repository, clientId);
            _apiClient = await AddApiClientRowAsync(_repository, clientId, clientUuid: _applicationId);
            _tokenIds =
            [
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
                await StoreValidTokenAsync(_repository, _applicationId, FarFuture),
            ];
            _participantLogger = A.Fake<ILogger<OpenIddictUnapprovalTokenRevocation>>();

            _result = await CreateApiClientRepository(
                    CreateUnapprovalParticipant(_repository, _participantLogger)
                )
                .UpdateApiClient(
                    UpdateCommandFor(
                        _apiClient,
                        isApproved: false,
                        RenamedApiClientName,
                        applicationId: int.MaxValue
                    )
                );
        }

        [Test]
        public void It_reports_the_application_as_not_found() =>
            _result.Should().BeOfType<ApiClientUpdateResult.FailureApplicationNotFound>();

        [Test]
        public async Task It_keeps_the_client_approved_on_its_application() =>
            (await ApiClientStateAsync(_apiClient.ApiClientId))
                .Should()
                .Be((true, ApiClientRowName, _apiClient.ApplicationId));

        [Test]
        public async Task It_leaves_every_token_valid() =>
            (await TokenStatusesAsync(_repository, _tokenIds)).Should().Equal("valid", "valid");

        [Test]
        public void It_logged_the_revocation_as_pending_in_the_rolled_back_transaction() =>
            Fake.GetCalls(_participantLogger)
                .Where(call =>
                    call.Method.Name == "Log" && call.Arguments.Get<LogLevel>(0) == LogLevel.Information
                )
                .Select(call => call.Arguments.Get<object>(2)?.ToString())
                .Should()
                .Equal(
                    $"Marked 2 outstanding tokens for client {_applicationId} revoked in the pending un-approval transaction; they stay valid if it rolls back"
                );
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
                Configuration.DatabaseOptions,
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
    /// application row, and the grant is shown to be waiting on that specific lock in
    /// <c>pg_locks</c> before the holder is released.
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
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );

        [Test]
        public async Task It_blocks_a_grant_for_that_client_until_the_lock_is_released()
        {
            Guid applicationId = await RegisterApplicationAsync(
                _repository,
                $"serialized-{Guid.NewGuid():N}"
            );

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, applicationId);
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

                await WaitUntilABackendIsBlockedBy(holder.BackendPid, _blockedObservationTimeout);

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

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, blockedApplicationId);
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

                await WaitUntilABackendIsBlockedBy(holder.BackendPid, _blockedObservationTimeout);

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
        /// Contention is not a limit rejection. Held past the repository's lock_timeout, the grant
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

            RowLockHolder holder = await RowLockHolder.TakeAsync(DataSource!, applicationId);
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
    /// be reported as gone, never as over its limit, and must not receive a stored token.
    /// </summary>
    [TestFixture]
    public class Given_The_Application_Row_Was_Deleted : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;

        [SetUp]
        public void Setup() =>
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
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

        /// <summary>
        /// The concurrent form, which is where the PostgreSQL bypass lived: a <c>FOR UPDATE</c>
        /// matching no row takes no lock at all, so without the guard these grants would run an
        /// unserialized count-and-insert and could exceed the cap.
        /// </summary>
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
            _repository = new OpenIddictDataRepository(
                DstSessionDatabaseOptions(),
                NullLogger<OpenIddictDataRepository>.Instance
            );
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
                EarlierExpiration,
                EnforcementDisabled,
                RegisteredSecret
            );
            await _repository.StoreTokenAsync(
                _laterTokenId,
                applicationId,
                "subject-later",
                LaterExpiration,
                EnforcementDisabled,
                RegisteredSecret
            );
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

    /// <summary>
    /// The whole <c>OpenIddictApplication</c> row rendered as one JSON document, so a before/after
    /// comparison covers every column rather than only the ones a fixture happens to read.
    /// </summary>
    protected async Task<string?> ApplicationRowJsonAsync(Guid applicationId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            @"SELECT row_to_json(a)::text FROM ""dmscs"".""OpenIddictApplication"" a WHERE ""Id"" = @Id",
            new { Id = applicationId }
        );
    }

    protected async Task<int> ApplicationRowCountForClientIdAsync(string clientId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""dmscs"".""OpenIddictApplication"" WHERE ""ClientId"" = @ClientId",
            new { ClientId = clientId }
        );
    }

    protected async Task<int> ApplicationScopeRowCountAsync(Guid applicationId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""dmscs"".""OpenIddictApplicationScope"" WHERE ""ApplicationId"" = @ApplicationId",
            new { ApplicationId = applicationId }
        );
    }

    protected async Task<int> ClientRoleRowCountAsync(Guid applicationId)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""dmscs"".""OpenIddictClientRole"" WHERE ""ClientId"" = @ClientId",
            new { ClientId = applicationId }
        );
    }

    protected async Task<(int Roles, int ClientRoles)> RoleRowCountsAsync(string roleName)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        int roles = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""dmscs"".""OpenIddictRole"" WHERE ""Name"" = @Name",
            new { Name = roleName }
        );
        int clientRoles = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM ""dmscs"".""OpenIddictClientRole"" cr
              JOIN ""dmscs"".""OpenIddictRole"" r ON cr.""RoleId"" = r.""Id""
              WHERE r.""Name"" = @Name",
            new { Name = roleName }
        );
        return (roles, clientRoles);
    }

    protected async Task<(int Scopes, int ApplicationScopes)> ScopeRowCountsAsync(string scopeName)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        int scopes = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""dmscs"".""OpenIddictScope"" WHERE ""Name"" = @Name",
            new { Name = scopeName }
        );
        int applicationScopes = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM ""dmscs"".""OpenIddictApplicationScope"" aps
              JOIN ""dmscs"".""OpenIddictScope"" s ON aps.""ScopeId"" = s.""Id""
              WHERE s.""Name"" = @Name",
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
                Configuration.DatabaseOptions,
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
    /// a role-name collision propagate as the engine exception, which the provider repository
    /// reports as an unknown failure.
    /// </summary>
    [TestFixture]
    public class Given_OpenIddict_Inserts_That_Violate_Other_Constraints : OpenIddictDataRepositoryTests
    {
        private OpenIddictDataRepository _repository = null!;
        private Guid _seededApplicationId;

        [SetUp]
        public async Task Setup()
        {
            _repository = new OpenIddictDataRepository(
                Configuration.DatabaseOptions,
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

            (await act.Should().ThrowAsync<PostgresException>())
                .Which.ConstraintName.Should()
                .Be("PK_OpenIddictApplication");
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

            (await act.Should().ThrowAsync<PostgresException>())
                .Which.ConstraintName.Should()
                .Be("UX_OpenIddictRole_Name");
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
                Configuration.DatabaseOptions,
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
                    """INSERT INTO "dmscs"."Vendor" ("Company") VALUES (@Company) RETURNING "Id" """,
                    new { Company = $"credential-{Guid.NewGuid():N}" },
                    transaction
                );
                int applicationId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO "dmscs"."Application" ("ApplicationName", "VendorId", "ClaimSetName")
                    VALUES ('Credential test', @VendorId, 'Test claim set') RETURNING "Id"
                    """,
                    new { VendorId = vendorId },
                    transaction
                );
                int apiClientId = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "ClientId", "ClientUuid", "Name", "IsApproved")
                    VALUES (@ApplicationId, @ClientId, @ClientUuid, 'Credential test', @IsApproved) RETURNING "Id"
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
                        """INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name") VALUES ('Test', @Name) RETURNING "Id" """,
                        new { Name = $"credential-{Guid.NewGuid():N}" },
                        transaction
                    );
                    await connection.ExecuteAsync(
                        """INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId") VALUES (@ApiClientId, @DataStoreId)""",
                        new { ApiClientId = apiClientId, DataStoreId = dataStoreId },
                        transaction
                    );
                }
            }
        );

    // The credential query must report what the full lookup reports for every registration the
    // schema allows: no API client (approved by default, not absent), an approved or unapproved
    // one, and either of those linked to several data stores, which fans out the full lookup's
    // joins but must not change the approval.
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            string clientId = Guid.NewGuid().ToString();
            await RegisterApplicationAsync(repository, clientId);
            await Connection!.ExecuteAsync(
                """UPDATE "dmscs"."OpenIddictApplication" SET "ClientSecret" = NULL WHERE "ClientId" = @ClientId""",
                new { ClientId = clientId }
            );
            _storedSecretIsNull = await Connection!.ExecuteScalarAsync<bool>(
                """SELECT "ClientSecret" IS NULL FROM "dmscs"."OpenIddictApplication" WHERE "ClientId" = @ClientId""",
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

    // Matching is exact on PostgreSQL, as the full lookup's is: the stored spelling is returned,
    // and a different casing finds nothing.
    [TestFixture]
    public class Given_A_Client_Credential_Lookup_With_Different_Casing : OpenIddictDataRepositoryTests
    {
        private string _registeredClientId = null!;
        private ClientCredential? _exact;
        private ClientCredential? _differentCasing;
        private ApplicationInfo? _fullLookupWithDifferentCasing;

        [SetUp]
        public async Task Setup()
        {
            OpenIddictDataRepository repository = new(
                Configuration.DatabaseOptions,
                NullLogger<OpenIddictDataRepository>.Instance
            );
            _registeredClientId = $"Credential-Casing-{Guid.NewGuid():N}";
            await RegisterApplicationAsync(repository, _registeredClientId);
            string lowered = _registeredClientId.ToLowerInvariant();

            _exact = await repository.GetClientCredentialAsync(_registeredClientId);
            _differentCasing = await repository.GetClientCredentialAsync(lowered);
            _fullLookupWithDifferentCasing = await repository.GetApplicationByClientIdAsync(lowered);
        }

        [Test]
        public void It_returns_the_stored_spelling() =>
            _exact!.StoredClientId.Should().Be(_registeredClientId);

        [Test]
        public void It_does_not_resolve_a_different_casing() => _differentCasing.Should().BeNull();

        [Test]
        public void It_agrees_with_the_full_lookup() => _fullLookupWithDifferentCasing.Should().BeNull();
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
                Configuration.DatabaseOptions,
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
