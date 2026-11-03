// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The API client education organization storage of the repositories (DMS-1639). One Application
/// holds 1001, 1002 and 1003 with an inheriting client (its default client, all three) and two
/// clients narrowed to {1002} and {1001, 1003}; an unrelated Application of the same vendor also
/// holds 1001 with its default client. Client ids are created in that order: inheriting, unrelated,
/// then the two narrowed clients. Every seeded row is then stamped with a fixed CreatedAt and
/// CreatedBy, so a row that was deleted and reinserted is told apart from one that survived.
/// </summary>
public abstract class ApiClientEducationOrganizationRepositoryTests : DatabaseTest
{
    protected sealed record ClientRow(
        int ApiClientId,
        int ApplicationId,
        long EducationOrganizationId,
        DateTime CreatedAt,
        string? CreatedBy
    );

    protected sealed record ApplicationRow(
        int ApplicationId,
        long EducationOrganizationId,
        DateTime CreatedAt,
        string? CreatedBy
    );

    protected static readonly DateTime SeededAt = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
    protected const string SeededBy = "seeded-user";

    protected int VendorId { get; private set; }
    protected int ScopedApplicationId { get; private set; }
    protected int UnrelatedApplicationId { get; private set; }
    protected int InheritingClientId { get; private set; }
    protected int NarrowedClientId { get; private set; }
    protected int SecondNarrowedClientId { get; private set; }
    protected int UnrelatedClientId { get; private set; }
    protected Guid UnrelatedClientUuid { get; } = Guid.NewGuid();
    protected ClientRow[] ClientRowsBefore { get; private set; } = [];
    protected ApplicationRow[] ApplicationRowsBefore { get; private set; } = [];

    protected ApplicationRepository ApplicationRepository { get; private set; } = null!;
    protected ApiClientRepository ApiClientRepository { get; private set; } = null!;

    private readonly string _inheritingClientKey = Guid.NewGuid().ToString();
    private readonly Guid _inheritingClientUuid = Guid.NewGuid();

    protected async Task SeedAsync(TenantContextProvider tenantContextProvider)
    {
        ApplicationRepository = new(
            Configuration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );
        ApiClientRepository = new(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider,
            new NoOpUnapprovalTokenRevocation()
        );

        var vendorResult = await new VendorRepository(
            Configuration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        ).InsertVendor(
            new VendorInsertCommand
            {
                Company = $"Vendor {Guid.NewGuid():N}",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "",
            }
        );
        VendorId = vendorResult.Should().BeOfType<VendorInsertResult.Success>().Subject.Id;

        ScopedApplicationId = await InsertApplicationAsync(
            "Scoped Application",
            [1001, 1002, 1003],
            _inheritingClientKey,
            _inheritingClientUuid
        );
        UnrelatedApplicationId = await InsertApplicationAsync(
            "Unrelated Application",
            [1001],
            Guid.NewGuid().ToString(),
            UnrelatedClientUuid
        );

        InheritingClientId = await ClientIdAsync(_inheritingClientUuid);
        UnrelatedClientId = await ClientIdAsync(UnrelatedClientUuid);
        NarrowedClientId = await InsertNarrowedApiClientAsync([1002]);
        SecondNarrowedClientId = await InsertNarrowedApiClientAsync([1001, 1003]);

        await Connection!.ExecuteAsync(
            """
            UPDATE "dmscs"."ApplicationEducationOrganization" SET "CreatedAt" = @SeededAt, "CreatedBy" = @SeededBy;
            UPDATE "dmscs"."ApiClientEducationOrganization" SET "CreatedAt" = @SeededAt, "CreatedBy" = @SeededBy;
            """,
            new { SeededAt, SeededBy }
        );

        ClientRowsBefore = await ClientRowsAsync();
        ApplicationRowsBefore = await ApplicationRowsAsync();
    }

    /// <summary>Saves the scoped Application under a new name with the given education organizations.</summary>
    protected Task<ApplicationSaveResult> SaveRenamedScopedApplicationAsync(
        long[] educationOrganizationIds,
        int[]? profileIds = null,
        IApplicationRepository? applicationRepository = null
    ) =>
        (applicationRepository ?? ApplicationRepository).SaveApplication(
            new ApplicationUpdateCommand
            {
                Id = ScopedApplicationId,
                ApplicationName = "Renamed Application",
                VendorId = VendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = educationOrganizationIds,
                ProfileIds = profileIds ?? [],
            }
        );

    /// <summary>Saves the scoped Application, asserting success.</summary>
    protected async Task SaveRenamedScopedApplicationSucceedsAsync(long[] educationOrganizationIds) =>
        (await SaveRenamedScopedApplicationAsync(educationOrganizationIds))
            .Should()
            .BeOfType<ApplicationSaveResult.Success>();

    /// <summary>
    /// Saves the scoped Application, asserting success, then clears the identity provider sync
    /// markers the save set, for fixtures about the education organization rows alone.
    /// </summary>
    protected async Task SaveRenamedScopedApplicationUnmarkedAsync(long[] educationOrganizationIds)
    {
        await SaveRenamedScopedApplicationSucceedsAsync(educationOrganizationIds);
        await Connection!.ExecuteAsync(
            """UPDATE "dmscs"."ApiClient" SET "IdentityProviderSyncPendingSince" = NULL;"""
        );
    }

    protected async Task<string> ScopedApplicationNameAsync() =>
        await Connection!.ExecuteScalarAsync<string>(
            """SELECT "ApplicationName" FROM "dmscs"."Application" WHERE "Id" = @Id;""",
            new { Id = ScopedApplicationId }
        ) ?? "";

    protected async Task<ClientRow[]> ClientRowsAsync() =>
        (
            await Connection!.QueryAsync<ClientRow>(
                """
                SELECT "ApiClientId", "ApplicationId", "EducationOrganizationId", "CreatedAt", "CreatedBy"
                FROM "dmscs"."ApiClientEducationOrganization"
                ORDER BY "ApiClientId", "EducationOrganizationId";
                """
            )
        ).ToArray();

    protected async Task<ApplicationRow[]> ApplicationRowsAsync() =>
        (
            await Connection!.QueryAsync<ApplicationRow>(
                """
                SELECT "ApplicationId", "EducationOrganizationId", "CreatedAt", "CreatedBy"
                FROM "dmscs"."ApplicationEducationOrganization"
                ORDER BY "ApplicationId", "EducationOrganizationId";
                """
            )
        ).ToArray();

    protected async Task<int[]> ApiClientIdsAsync() =>
        (
            await Connection!.QueryAsync<int>("""SELECT "Id" FROM "dmscs"."ApiClient" ORDER BY "Id";""")
        ).ToArray();

    protected static ClientRow Seeded(int apiClientId, int applicationId, long educationOrganizationId) =>
        new(apiClientId, applicationId, educationOrganizationId, SeededAt, SeededBy);

    protected static ApplicationRow Seeded(int applicationId, long educationOrganizationId) =>
        new(applicationId, educationOrganizationId, SeededAt, SeededBy);

    protected static async Task<TenantContextProvider> CreateTenantProviderAsync(string suffix)
    {
        var tenantName = $"ClientEducationOrganizationTenant{suffix}-{Guid.NewGuid()}";
        var tenantResult = await new TenantRepository(
            Configuration.DatabaseOptions,
            NullLogger<TenantRepository>.Instance,
            new TestAuditContext()
        ).InsertTenant(new TenantInsertCommand { Name = tenantName });

        return new TenantContextProvider
        {
            Context = new TenantContext.Multitenant(
                tenantResult.Should().BeOfType<TenantInsertResult.Success>().Subject.Id,
                tenantName
            ),
        };
    }

    protected async Task<int> InsertApplicationAsync(
        string name,
        long[] educationOrganizationIds,
        string clientKey,
        Guid clientUuid
    )
    {
        var result = await ApplicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = name,
                VendorId = VendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = educationOrganizationIds,
            },
            new ApiClientCommand { ClientId = clientKey, ClientUuid = clientUuid }
        );
        return result.Should().BeOfType<ApplicationInsertResult.Success>().Subject.Id;
    }

    private async Task<int> ClientIdAsync(Guid clientUuid) =>
        await Connection!.ExecuteScalarAsync<int>(
            """SELECT "Id" FROM "dmscs"."ApiClient" WHERE "ClientUuid" = @ClientUuid;""",
            new { ClientUuid = clientUuid }
        );

    protected async Task<int> InsertApiClientAsync(int applicationId, string? name = null)
    {
        var result = await ApiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = applicationId,
                Name = name ?? $"Client {Guid.NewGuid():N}",
                IsApproved = true,
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        return result.Should().BeOfType<ApiClientInsertResult.Success>().Subject.Id;
    }

    protected async Task<ApiClientEducationOrganizationState> StateAsync(int apiClientId) =>
        (await ApiClientRepository.GetApiClientEducationOrganizationState(apiClientId))
            .Should()
            .BeOfType<ApiClientEducationOrganizationStateResult.Success>()
            .Subject.State;

    protected Task<ApiClientInsertResult> InsertApiClientWithScopeAsync(
        int applicationId,
        ApiClientEducationOrganizationScope? scope,
        int[]? dataStoreIds = null
    ) =>
        ApiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = applicationId,
                Name = $"Client {Guid.NewGuid():N}",
                IsApproved = true,
                DataStoreIds = dataStoreIds ?? [],
                EducationOrganizationScope = scope,
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );

    private async Task<int> InsertNarrowedApiClientAsync(long[] educationOrganizationIds) =>
        (
            await InsertApiClientWithScopeAsync(
                ScopedApplicationId,
                new ApiClientEducationOrganizationScope.Narrow(educationOrganizationIds)
            )
        )
            .Should()
            .BeOfType<ApiClientInsertResult.Success>()
            .Subject.Id;

    protected Task<ApiClientUpdateResult> UpdateApiClientAsync(
        int id,
        int applicationId,
        string name,
        ApiClientEducationOrganizationScope? scope = null,
        int[]? dataStoreIds = null
    ) =>
        ApiClientRepository.UpdateApiClient(
            new ApiClientUpdateCommand
            {
                Id = id,
                ApplicationId = applicationId,
                Name = name,
                IsApproved = true,
                DataStoreIds = dataStoreIds ?? [],
                EducationOrganizationScope = scope,
            }
        );

    /// <summary>A data store id no tenant has, so a write that assigns it fails after its other changes.</summary>
    protected const int MissingDataStoreId = int.MaxValue;

    /// <summary>Inserts an Application holding 1001, 1003 and 1005, which lacks the narrowed client's 1002.</summary>
    protected Task<int> InsertDestinationApplicationAsync() =>
        InsertApplicationAsync(
            "Destination Application",
            [1001, 1003, 1005],
            Guid.NewGuid().ToString(),
            Guid.NewGuid()
        );

    protected async Task<ClientRow[]> ClientRowsOfAsync(int apiClientId) =>
        (await ClientRowsAsync()).Where(row => row.ApiClientId == apiClientId).ToArray();

    protected ClientRow[] ClientRowsBeforeOf(int apiClientId) =>
        ClientRowsBefore.Where(row => row.ApiClientId == apiClientId).ToArray();

    protected async Task<(int ApplicationId, string Name)> ClientApplicationAndNameAsync(int apiClientId) =>
        await Connection!.QuerySingleAsync<(int, string)>(
            """SELECT "ApplicationId", "Name" FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""",
            new { Id = apiClientId }
        );

    protected static ApiClientEducationOrganizationState State(
        int applicationId,
        bool inherits,
        long[] storedIds,
        long[] effectiveIds,
        DateTime? pendingSince = null
    ) => new(applicationId, inherits, storedIds, effectiveIds, pendingSince);

    protected static void ShouldMatch(
        ApiClientEducationOrganizationState actual,
        ApiClientEducationOrganizationState expected
    ) =>
        actual
            .Should()
            .BeEquivalentTo(
                expected,
                options =>
                    options.ComparingByMembers<ApiClientEducationOrganizationState>().WithStrictOrdering()
            );

    protected static readonly DateTime PendingSince = new(2030, 4, 5, 6, 7, 8, DateTimeKind.Utc);

    /// <summary>Marks a client sync-pending, as an Application save would, at the given time.</summary>
    protected async Task MarkPendingAsync(int apiClientId, DateTime? pendingSince = null) =>
        (
            await Connection!.ExecuteAsync(
                """UPDATE "dmscs"."ApiClient" SET "IdentityProviderSyncPendingSince" = @PendingSince WHERE "Id" = @Id;""",
                new { Id = apiClientId, PendingSince = pendingSince ?? PendingSince }
            )
        )
            .Should()
            .Be(1);

    /// <summary>Audit values for tests, running a callback on one numbered user lookup.</summary>
    protected sealed class CallbackAuditContext(int userLookupCallingBack, Action callback) : IAuditContext
    {
        private int _userLookups;

        public string GetCurrentUser()
        {
            if (Interlocked.Increment(ref _userLookups) == userLookupCallingBack)
            {
                callback();
            }

            return "test-user";
        }

        public DateTime GetCurrentTimestamp() => DateTime.UtcNow;
    }

    [TestFixture]
    public class Given_an_Application_saved_with_its_education_organizations_unchanged
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveRenamedScopedApplicationAsync([1001, 1002, 1003]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationSaveResult.Success>();

        [Test]
        public async Task It_saves_the_other_Application_changes() =>
            (await ScopedApplicationNameAsync()).Should().Be("Renamed Application");

        [Test]
        public async Task It_leaves_every_client_row_untouched() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);

        [Test]
        public async Task It_keeps_every_Application_row_with_its_audit_values() =>
            (await ApplicationRowsAsync()).Should().Equal(ApplicationRowsBefore);
    }

    [TestFixture]
    public class Given_an_Application_saved_with_an_added_education_organization
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveRenamedScopedApplicationAsync([1001, 1002, 1003, 1004, 1004]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationSaveResult.Success>();

        [Test]
        public async Task It_changes_no_client_rows_not_even_an_inheriting_client() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);

        [Test]
        public async Task It_keeps_the_surviving_Application_rows_and_inserts_the_added_one_once()
        {
            ApplicationRow[] rows = await ApplicationRowsAsync();

            rows.Where(row => row.EducationOrganizationId != 1004).Should().Equal(ApplicationRowsBefore);
            rows.Should()
                .ContainSingle(row => row.EducationOrganizationId == 1004)
                .Which.Should()
                .Match<ApplicationRow>(row =>
                    row.ApplicationId == ScopedApplicationId && row.CreatedBy == "test-user"
                );
        }
    }

    [TestFixture]
    public class Given_an_Application_saved_with_removed_education_organizations
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveRenamedScopedApplicationAsync([1002, 1003]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationSaveResult.Success>();

        [Test]
        public async Task It_removes_the_education_organization_from_every_client_of_the_Application_only() =>
            (await ClientRowsAsync())
                .Should()
                .Equal(
                    Seeded(InheritingClientId, ScopedApplicationId, 1002),
                    Seeded(InheritingClientId, ScopedApplicationId, 1003),
                    Seeded(UnrelatedClientId, UnrelatedApplicationId, 1001),
                    Seeded(NarrowedClientId, ScopedApplicationId, 1002),
                    Seeded(SecondNarrowedClientId, ScopedApplicationId, 1003)
                );

        [Test]
        public async Task It_keeps_the_surviving_Application_rows_with_their_audit_values() =>
            (await ApplicationRowsAsync())
                .Should()
                .Equal(
                    Seeded(ScopedApplicationId, 1002),
                    Seeded(ScopedApplicationId, 1003),
                    Seeded(UnrelatedApplicationId, 1001)
                );
    }

    [TestFixture]
    public class Given_an_Application_saved_without_education_organizations
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveRenamedScopedApplicationAsync([]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationSaveResult.Success>();

        [Test]
        public async Task It_leaves_rows_only_for_the_unrelated_Application_client() =>
            (await ClientRowsAsync()).Should().Equal(Seeded(UnrelatedClientId, UnrelatedApplicationId, 1001));

        [Test]
        public async Task It_leaves_rows_only_for_the_unrelated_Application() =>
            (await ApplicationRowsAsync()).Should().Equal(Seeded(UnrelatedApplicationId, 1001));

        [Test]
        public async Task It_keeps_every_client() =>
            (await ApiClientIdsAsync())
                .Should()
                .BeEquivalentTo([
                    InheritingClientId,
                    NarrowedClientId,
                    SecondNarrowedClientId,
                    UnrelatedClientId,
                ]);
    }

    /// <summary>
    /// The profile the save assigns is deleted after the save has checked it and changed the education
    /// organizations, so the profile insert that follows fails on its foreign key and the whole save,
    /// client marking included, rolls back.
    /// </summary>
    [TestFixture]
    public class Given_an_Application_save_that_fails_after_its_education_organizations_change
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationSaveResult _result = null!;
        private bool _removalInProgressAtCallback;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            int profileId = await Connection!.ExecuteScalarAsync<int>(
                """
                INSERT INTO "dmscs"."Profile" ("ProfileName", "Definition")
                VALUES (@ProfileName, '<Profile/>') RETURNING "Id";
                """,
                new { ProfileName = $"Deleted Profile {Guid.NewGuid()}" }
            );

            // The save's second audit user lookup comes after the education organization removal
            // and before the profile rows are written.
            ApplicationRepository failingRepository = new(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new CallbackAuditContext(
                    userLookupCallingBack: 2,
                    () =>
                    {
                        _removalInProgressAtCallback = RemovedRowIsLocked();
                        Connection!.Execute(
                            """DELETE FROM "dmscs"."Profile" WHERE "Id" = @Id;""",
                            new { Id = profileId }
                        );
                    }
                ),
                new TenantContextProvider()
            );
            _result = await SaveRenamedScopedApplicationAsync([1002, 1004], [profileId], failingRepository);
        }

        /// <summary>
        /// True when the save's transaction already holds the row of the removed education
        /// organization 1003, which proves the callback fired after the removal began.
        /// </summary>
        private bool RemovedRowIsLocked()
        {
            try
            {
                Connection!.Execute(
                    """
                    SELECT 1 FROM "dmscs"."ApplicationEducationOrganization"
                    WHERE "ApplicationId" = @ApplicationId AND "EducationOrganizationId" = 1003
                    FOR UPDATE NOWAIT;
                    """,
                    new { ApplicationId = ScopedApplicationId }
                );
                return false;
            }
            catch (PostgresException exception) when (exception.SqlState == "55P03")
            {
                return true;
            }
        }

        [Test]
        public void It_deletes_the_profile_after_the_removal_began() =>
            _removalInProgressAtCallback.Should().BeTrue();

        [Test]
        public void It_fails_on_the_deleted_profile() =>
            _result.Should().BeOfType<ApplicationSaveResult.FailureProfileNotFound>();

        [Test]
        public async Task It_rolls_back_the_client_marking() =>
            (
                await Connection!.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM "dmscs"."ApiClient" WHERE "IdentityProviderSyncPendingSince" IS NOT NULL;"""
                )
            )
                .Should()
                .Be(0);

        [Test]
        public async Task It_rolls_back_the_client_row_removals() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);

        [Test]
        public async Task It_rolls_back_the_Application_row_changes() =>
            (await ApplicationRowsAsync()).Should().Equal(ApplicationRowsBefore);

        [Test]
        public async Task It_keeps_the_Application_name() =>
            (await ScopedApplicationNameAsync()).Should().Be("Scoped Application");
    }

    [TestFixture]
    public class Given_a_client_with_education_organizations_deleted
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await new ApiClientRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider(),
                new NoOpUnapprovalTokenRevocation()
            ).DeleteApiClient(SecondNarrowedClientId);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientDeleteResult.Success>();

        [Test]
        public async Task It_deletes_only_that_client() =>
            (await ApiClientIdsAsync())
                .Should()
                .BeEquivalentTo([InheritingClientId, NarrowedClientId, UnrelatedClientId]);

        [Test]
        public async Task It_deletes_only_that_client_rows() =>
            (await ClientRowsAsync())
                .Should()
                .Equal(ClientRowsBefore.Where(row => row.ApiClientId != SecondNarrowedClientId));

        [Test]
        public async Task It_keeps_the_Application_rows() =>
            (await ApplicationRowsAsync()).Should().Equal(ApplicationRowsBefore);
    }

    [TestFixture]
    public class Given_a_client_with_education_organizations_deleted_from_another_tenant
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            TenantContextProvider owningTenant = await CreateTenantProviderAsync("Owner");
            TenantContextProvider otherTenant = await CreateTenantProviderAsync("Other");

            await SeedAsync(owningTenant);
            _result = await new ApiClientRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                otherTenant,
                new NoOpUnapprovalTokenRevocation()
            ).DeleteApiClient(SecondNarrowedClientId);
        }

        [Test]
        public void It_reports_the_client_not_found() =>
            _result.Should().BeOfType<ApiClientDeleteResult.FailureNotFound>();

        [Test]
        public async Task It_keeps_the_client() =>
            (await ApiClientIdsAsync()).Should().Contain(SecondNarrowedClientId);

        [Test]
        public async Task It_keeps_every_client_row() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
    }

    [TestFixture]
    public class Given_clients_created_by_the_repositories : ApiClientEducationOrganizationRepositoryTests
    {
        private int _insertedClientId;
        private int _emptyApplicationId;
        private readonly Guid _emptyApplicationClientUuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _insertedClientId = await InsertApiClientAsync(ScopedApplicationId);
            _emptyApplicationId = await InsertApplicationAsync(
                "Empty Application",
                [],
                Guid.NewGuid().ToString(),
                _emptyApplicationClientUuid
            );
        }

        [Test]
        public async Task It_makes_an_Application_default_client_inherit_with_the_Application_list() =>
            ShouldMatch(
                await StateAsync(InheritingClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
            );

        [Test]
        public async Task It_makes_an_inserted_client_inherit_with_the_Application_list() =>
            ShouldMatch(
                await StateAsync(_insertedClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
            );

        [Test]
        public async Task It_gives_the_default_client_of_an_Application_without_education_organizations_empty_lists()
        {
            int defaultClientId = await Connection!.ExecuteScalarAsync<int>(
                """SELECT "Id" FROM "dmscs"."ApiClient" WHERE "ApplicationId" = @ApplicationId;""",
                new { ApplicationId = _emptyApplicationId }
            );

            ShouldMatch(await StateAsync(defaultClientId), State(_emptyApplicationId, true, [], []));
        }
    }

    [TestFixture]
    public class Given_a_client_narrowed_to_repeated_education_organizations
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateApiClientAsync(
                InheritingClientId,
                ScopedApplicationId,
                "Narrowed client",
                new ApiClientEducationOrganizationScope.Narrow([1003, 1001, 1003])
            );
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_stores_the_narrowed_flag_and_each_education_organization_once() =>
            ShouldMatch(
                await StateAsync(InheritingClientId),
                State(ScopedApplicationId, false, [1001, 1003], [1001, 1003])
            );

        [Test]
        public async Task It_stores_rows_for_the_client_current_Application() =>
            (await ClientRowsAsync())
                .Where(row => row.ApiClientId == InheritingClientId)
                .Select(row => (row.ApplicationId, row.EducationOrganizationId))
                .Should()
                .Equal((ScopedApplicationId, 1001L), (ScopedApplicationId, 1003L));
    }

    [TestFixture]
    public class Given_a_narrowed_client_set_to_inherit : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await SaveRenamedScopedApplicationUnmarkedAsync([1001, 1002, 1003, 1004]);
            _result = await UpdateApiClientAsync(
                NarrowedClientId,
                ScopedApplicationId,
                "Inheriting client",
                new ApiClientEducationOrganizationScope.Inherit()
            );
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_stores_the_inheriting_flag_and_the_Application_current_list() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003, 1004], [1001, 1002, 1003, 1004])
            );
    }

    [TestFixture]
    public class Given_a_narrowed_scope_with_an_education_organization_not_on_the_Application
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateApiClientAsync(
                NarrowedClientId,
                ScopedApplicationId,
                "Renamed client",
                new ApiClientEducationOrganizationScope.Narrow([1001, 9999])
            );
        }

        [Test]
        public void It_reports_the_education_organization_not_on_the_Application() =>
            _result
                .Should()
                .BeOfType<ApiClientUpdateResult.FailureRequestedEducationOrganizationsNotOnApplication>()
                .Which.EducationOrganizationIds.Should()
                .Equal(9999);

        [Test]
        public async Task It_keeps_the_client_scope() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, false, [1002], [1002])
            );

        [Test]
        public async Task It_keeps_every_client_row() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
    }

    [TestFixture]
    public class Given_Application_education_organizations_added_removed_and_added_back
        : ApiClientEducationOrganizationRepositoryTests
    {
        private readonly Dictionary<int, ApiClientEducationOrganizationState> _afterAddition = [];
        private readonly Dictionary<int, ApiClientEducationOrganizationState> _afterRemoval = [];
        private ApiClientEducationOrganizationState _narrowedAfterReaddition = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            // The second narrowed client is narrowed to every current education organization
            (
                await UpdateApiClientAsync(
                    SecondNarrowedClientId,
                    ScopedApplicationId,
                    "Narrowed to every education organization",
                    new ApiClientEducationOrganizationScope.Narrow([1001, 1002, 1003])
                )
            )
                .Should()
                .BeOfType<ApiClientUpdateResult.Success>();
            int[] clientIds = [InheritingClientId, NarrowedClientId, SecondNarrowedClientId];

            await SaveRenamedScopedApplicationUnmarkedAsync([1001, 1002, 1003, 1004]);
            foreach (int clientId in clientIds)
            {
                _afterAddition[clientId] = await StateAsync(clientId);
            }

            await SaveRenamedScopedApplicationUnmarkedAsync([1001, 1003, 1004]);
            foreach (int clientId in clientIds)
            {
                _afterRemoval[clientId] = await StateAsync(clientId);
            }

            await SaveRenamedScopedApplicationUnmarkedAsync([1001, 1002, 1003, 1004]);
            _narrowedAfterReaddition = await StateAsync(NarrowedClientId);
        }

        [Test]
        public void It_gives_an_inheriting_client_an_addition_in_its_effective_list_only() =>
            ShouldMatch(
                _afterAddition[InheritingClientId],
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003, 1004])
            );

        [Test]
        public void It_does_not_grow_a_client_narrowed_to_the_whole_list() =>
            ShouldMatch(
                _afterAddition[SecondNarrowedClientId],
                State(ScopedApplicationId, false, [1001, 1002, 1003], [1001, 1002, 1003])
            );

        [Test]
        public void It_does_not_grow_a_narrowed_client() =>
            ShouldMatch(_afterAddition[NarrowedClientId], State(ScopedApplicationId, false, [1002], [1002]));

        [Test]
        public void It_removes_a_removal_from_an_inheriting_client_stored_and_effective_lists() =>
            ShouldMatch(
                _afterRemoval[InheritingClientId],
                State(ScopedApplicationId, true, [1001, 1003], [1001, 1003, 1004])
            );

        [Test]
        public void It_removes_a_removal_from_a_narrowed_client() =>
            ShouldMatch(
                _afterRemoval[SecondNarrowedClientId],
                State(ScopedApplicationId, false, [1001, 1003], [1001, 1003])
            );

        [Test]
        public void It_leaves_a_narrowed_client_that_lost_its_only_education_organization_narrowed_and_empty() =>
            ShouldMatch(_afterRemoval[NarrowedClientId], State(ScopedApplicationId, false, [], []));

        [Test]
        public void It_keeps_an_emptied_narrowed_client_empty_when_the_education_organization_returns() =>
            ShouldMatch(_narrowedAfterReaddition, State(ScopedApplicationId, false, [], []));
    }

    [TestFixture]
    public class Given_a_client_sync_pending_time : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientSyncPendingResult _clearResult = null!;
        private ApiClientEducationOrganizationState _afterSet = null!;
        private ApiClientEducationOrganizationState _afterUnrelatedChanges = null!;
        private ApiClientEducationOrganizationState _afterClear = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            await MarkPendingAsync(SecondNarrowedClientId);
            _afterSet = await StateAsync(SecondNarrowedClientId);

            (await UpdateApiClientAsync(SecondNarrowedClientId, ScopedApplicationId, "Renamed client"))
                .Should()
                .BeOfType<ApiClientUpdateResult.Success>();
            (
                await UpdateApiClientAsync(
                    SecondNarrowedClientId,
                    ScopedApplicationId,
                    "Renamed client",
                    new ApiClientEducationOrganizationScope.Narrow([1001])
                )
            )
                .Should()
                .BeOfType<ApiClientUpdateResult.Success>();
            await SaveRenamedScopedApplicationSucceedsAsync([1001, 1003]);
            _afterUnrelatedChanges = await StateAsync(SecondNarrowedClientId);

            _clearResult = await ApiClientRepository.ClearApiClientIdentityProviderSyncPending(
                SecondNarrowedClientId,
                "Test Claim set"
            );
            _afterClear = await StateAsync(SecondNarrowedClientId);
        }

        [Test]
        public void It_reads_the_time_back_as_UTC()
        {
            _afterSet.IdentityProviderSyncPendingSince.Should().Be(PendingSince);
            _afterSet.IdentityProviderSyncPendingSince!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Test]
        public void It_keeps_the_time_through_client_scope_and_Application_changes() =>
            ShouldMatch(
                _afterUnrelatedChanges,
                State(ScopedApplicationId, false, [1001], [1001], PendingSince)
            );

        [Test]
        public void It_clears_the_time() =>
            _clearResult.Should().BeOfType<ApiClientSyncPendingResult.Success>();

        [Test]
        public void It_reads_no_time_after_clearing() =>
            _afterClear.IdentityProviderSyncPendingSince.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_narrowed_client_updated_on_the_same_Application
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;
        private ClientRow[] _rowsBeforeUpdate = [];

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(NarrowedClientId);
            _rowsBeforeUpdate = await ClientRowsAsync();
            _result = await UpdateApiClientAsync(NarrowedClientId, ScopedApplicationId, "Renamed client");
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_saves_the_other_client_changes() =>
            (await ClientApplicationAndNameAsync(NarrowedClientId)).Name.Should().Be("Renamed client");

        [Test]
        public async Task It_keeps_the_scope_and_pending_time() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, false, [1002], [1002], PendingSince)
            );

        [Test]
        public async Task It_leaves_every_client_row_untouched() =>
            (await ClientRowsAsync()).Should().Equal(_rowsBeforeUpdate);
    }

    [TestFixture]
    public class Given_clients_moved_to_another_Application : ApiClientEducationOrganizationRepositoryTests
    {
        private int _destinationApplicationId;
        private ApiClientUpdateResult _inheritingMove = null!;
        private ApiClientUpdateResult _fittingNarrowedMove = null!;
        private ApiClientUpdateResult _unfittingNarrowedMove = null!;
        private (int ApplicationId, string Name) _unfittingClientBefore;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _destinationApplicationId = await InsertApplicationAsync(
                "Destination Application",
                [1001, 1003, 1005],
                Guid.NewGuid().ToString(),
                Guid.NewGuid()
            );
            await MarkPendingAsync(SecondNarrowedClientId);
            await MarkPendingAsync(NarrowedClientId);
            _unfittingClientBefore = await ClientApplicationAndNameAsync(NarrowedClientId);

            _inheritingMove = await UpdateApiClientAsync(
                InheritingClientId,
                _destinationApplicationId,
                "Moved inheriting client"
            );
            _fittingNarrowedMove = await UpdateApiClientAsync(
                SecondNarrowedClientId,
                _destinationApplicationId,
                "Moved narrowed client"
            );
            _unfittingNarrowedMove = await UpdateApiClientAsync(
                NarrowedClientId,
                _destinationApplicationId,
                "Unmoved narrowed client"
            );
        }

        [Test]
        public void It_moves_the_inheriting_client() =>
            _inheritingMove.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_gives_the_inheriting_client_the_destination_list() =>
            ShouldMatch(
                await StateAsync(InheritingClientId),
                State(_destinationApplicationId, true, [1001, 1003, 1005], [1001, 1003, 1005])
            );

        [Test]
        public void It_moves_the_narrowed_client_whose_list_is_on_the_destination() =>
            _fittingNarrowedMove.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_keeps_the_moved_narrowed_client_list_flag_and_pending_time() =>
            ShouldMatch(
                await StateAsync(SecondNarrowedClientId),
                State(_destinationApplicationId, false, [1001, 1003], [1001, 1003], PendingSince)
            );

        [Test]
        public async Task It_points_every_moved_client_row_at_the_destination() =>
            (await ClientRowsAsync())
                .Where(row =>
                    row.ApiClientId == InheritingClientId || row.ApiClientId == SecondNarrowedClientId
                )
                .Should()
                .OnlyContain(row => row.ApplicationId == _destinationApplicationId);

        [Test]
        public void It_rejects_moving_the_narrowed_client_whose_list_is_not_on_the_destination() =>
            _unfittingNarrowedMove
                .Should()
                .BeOfType<ApiClientUpdateResult.FailureEducationOrganizationsNotOnApplication>()
                .Which.EducationOrganizationIds.Should()
                .Equal(1002);

        [Test]
        public async Task It_leaves_the_rejected_client_scope_and_pending_time_on_its_Application() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, false, [1002], [1002], PendingSince)
            );

        [Test]
        public async Task It_leaves_the_rejected_client_Application_and_name() =>
            (await ClientApplicationAndNameAsync(NarrowedClientId)).Should().Be(_unfittingClientBefore);

        [Test]
        public async Task It_leaves_the_rejected_client_rows() =>
            (await ClientRowsAsync())
                .Where(row => row.ApiClientId == NarrowedClientId)
                .Should()
                .Equal(ClientRowsBefore.Where(row => row.ApiClientId == NarrowedClientId));
    }

    [TestFixture]
    public class Given_a_client_move_that_fails_after_its_rows_are_replaced
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            // The destination's default client is named after the Application, so the client
            // update that follows the row changes violates UX_ApiClient_ApplicationId_Name.
            int destinationApplicationId = await InsertApplicationAsync(
                "Destination Application",
                [1001, 1002, 1003],
                Guid.NewGuid().ToString(),
                Guid.NewGuid()
            );
            _result = await UpdateApiClientAsync(
                SecondNarrowedClientId,
                destinationApplicationId,
                "Destination Application"
            );
        }

        [Test]
        public void It_fails_on_the_client_update() =>
            _result.Should().BeOfType<ApiClientUpdateResult.FailureDuplicateName>();

        [Test]
        public async Task It_keeps_the_client_on_its_Application_with_its_scope() =>
            ShouldMatch(
                await StateAsync(SecondNarrowedClientId),
                State(ScopedApplicationId, false, [1001, 1003], [1001, 1003])
            );

        [Test]
        public async Task It_rolls_back_the_row_changes() =>
            (await ClientRowsAsync())
                .Where(row => row.ApiClientId == SecondNarrowedClientId)
                .Should()
                .Equal(ClientRowsBefore.Where(row => row.ApiClientId == SecondNarrowedClientId));
    }

    [TestFixture]
    public class Given_education_organization_operations_on_a_missing_client
        : ApiClientEducationOrganizationRepositoryTests
    {
        private const int MissingClientId = int.MaxValue;

        [SetUp]
        public Task Setup() => SeedAsync(new TenantContextProvider());

        [Test]
        public async Task It_reports_the_state_not_found() =>
            (await ApiClientRepository.GetApiClientEducationOrganizationState(MissingClientId))
                .Should()
                .BeOfType<ApiClientEducationOrganizationStateResult.FailureNotFound>();

        [Test]
        public async Task It_reports_a_scope_write_not_found() =>
            (
                await UpdateApiClientAsync(
                    MissingClientId,
                    ScopedApplicationId,
                    "Missing client",
                    new ApiClientEducationOrganizationScope.Narrow([1001])
                )
            )
                .Should()
                .BeOfType<ApiClientUpdateResult.FailureNotFound>();

        [Test]
        public async Task It_reports_a_pending_time_clear_not_found() =>
            (
                await ApiClientRepository.ClearApiClientIdentityProviderSyncPending(
                    MissingClientId,
                    "Test Claim set"
                )
            )
                .Should()
                .BeOfType<ApiClientSyncPendingResult.FailureNotFound>();
    }

    [TestFixture]
    public class Given_education_organization_operations_from_another_tenant
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientRepository _otherTenantRepository = null!;
        private int _otherTenantApplicationId;
        private ApiClientEducationOrganizationStateResult _stateResult = null!;
        private ApiClientUpdateResult _scopeResult = null!;
        private ApiClientSyncPendingResult _pendingResult = null!;
        private ApiClientUpdateResult _moveResult = null!;

        [SetUp]
        public async Task Setup()
        {
            TenantContextProvider otherTenant = await CreateTenantProviderAsync("Other");
            _otherTenantRepository = new(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                otherTenant,
                new NoOpUnapprovalTokenRevocation()
            );

            await SeedAsync(await CreateTenantProviderAsync("Owner"));

            // The other tenant's Application holds the client's education organizations, so only
            // the tenant check can stop the move
            var otherVendorResult = await new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                otherTenant
            ).InsertVendor(
                new VendorInsertCommand
                {
                    Company = $"Vendor {Guid.NewGuid():N}",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "",
                }
            );
            var otherApplicationResult = await new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                otherTenant
            ).InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "Other Tenant Application",
                    VendorId = otherVendorResult.Should().BeOfType<VendorInsertResult.Success>().Subject.Id,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [1001, 1002, 1003],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            _otherTenantApplicationId = otherApplicationResult
                .Should()
                .BeOfType<ApplicationInsertResult.Success>()
                .Subject.Id;

            _stateResult = await _otherTenantRepository.GetApiClientEducationOrganizationState(
                SecondNarrowedClientId
            );
            _scopeResult = await _otherTenantRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = SecondNarrowedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Narrowed across tenants",
                    IsApproved = true,
                    EducationOrganizationScope = new ApiClientEducationOrganizationScope.Narrow([1001]),
                }
            );
            _pendingResult = await _otherTenantRepository.ClearApiClientIdentityProviderSyncPending(
                SecondNarrowedClientId,
                "Test Claim set"
            );
            _moveResult = await _otherTenantRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = SecondNarrowedClientId,
                    ApplicationId = _otherTenantApplicationId,
                    Name = "Moved across tenants",
                    IsApproved = true,
                }
            );
        }

        [Test]
        public void It_reports_the_state_not_found() =>
            _stateResult.Should().BeOfType<ApiClientEducationOrganizationStateResult.FailureNotFound>();

        [Test]
        public void It_reports_the_scope_write_not_found() =>
            _scopeResult.Should().BeOfType<ApiClientUpdateResult.FailureNotFound>();

        [Test]
        public void It_reports_the_pending_time_clear_not_found() =>
            _pendingResult.Should().BeOfType<ApiClientSyncPendingResult.FailureNotFound>();

        [Test]
        public void It_reports_the_move_not_found() =>
            _moveResult.Should().BeOfType<ApiClientUpdateResult.FailureNotFound>();

        [Test]
        public async Task It_changes_nothing() =>
            ShouldMatch(
                await StateAsync(SecondNarrowedClientId),
                State(ScopedApplicationId, false, [1001, 1003], [1001, 1003])
            );

        [Test]
        public async Task It_keeps_the_client_Application() =>
            (await ClientApplicationAndNameAsync(SecondNarrowedClientId))
                .ApplicationId.Should()
                .Be(ScopedApplicationId);
    }

    [TestFixture]
    public class Given_a_client_inserted_with_a_narrowed_scope : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientInsertResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await InsertApiClientWithScopeAsync(
                ScopedApplicationId,
                new ApiClientEducationOrganizationScope.Narrow([1003, 1001, 1003])
            );
        }

        private int InsertedClientId => _result.Should().BeOfType<ApiClientInsertResult.Success>().Subject.Id;

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientInsertResult.Success>();

        [Test]
        public async Task It_stores_the_client_narrowed_to_the_distinct_list() =>
            ShouldMatch(
                await StateAsync(InsertedClientId),
                State(ScopedApplicationId, false, [1001, 1003], [1001, 1003])
            );

        [Test]
        public async Task It_points_the_client_rows_at_its_Application() =>
            (await ClientRowsOfAsync(InsertedClientId))
                .Select(row => (row.ApplicationId, row.EducationOrganizationId))
                .Should()
                .Equal((ScopedApplicationId, 1001L), (ScopedApplicationId, 1003L));
    }

    [TestFixture]
    public class Given_clients_inserted_with_an_inheriting_and_an_empty_narrowed_scope
        : ApiClientEducationOrganizationRepositoryTests
    {
        private int _inheritingClientId;
        private int _emptyClientId;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _inheritingClientId = (
                await InsertApiClientWithScopeAsync(
                    ScopedApplicationId,
                    new ApiClientEducationOrganizationScope.Inherit()
                )
            )
                .Should()
                .BeOfType<ApiClientInsertResult.Success>()
                .Subject.Id;
            _emptyClientId = (
                await InsertApiClientWithScopeAsync(
                    ScopedApplicationId,
                    new ApiClientEducationOrganizationScope.Narrow([])
                )
            )
                .Should()
                .BeOfType<ApiClientInsertResult.Success>()
                .Subject.Id;
        }

        [Test]
        public async Task It_makes_the_inheriting_client_inherit_with_the_Application_list() =>
            ShouldMatch(
                await StateAsync(_inheritingClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
            );

        [Test]
        public async Task It_stores_the_empty_narrowed_client_without_rows() =>
            ShouldMatch(await StateAsync(_emptyClientId), State(ScopedApplicationId, false, [], []));
    }

    [TestFixture]
    public class Given_a_client_insert_with_a_narrowed_scope_not_on_the_Application
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientInsertResult _result = null!;
        private int[] _clientIdsBefore = [];

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _clientIdsBefore = await ApiClientIdsAsync();
            _result = await InsertApiClientWithScopeAsync(
                ScopedApplicationId,
                new ApiClientEducationOrganizationScope.Narrow([1001, 1004])
            );
        }

        [Test]
        public void It_reports_the_ids_not_on_the_Application() =>
            _result
                .Should()
                .BeOfType<ApiClientInsertResult.FailureRequestedEducationOrganizationsNotOnApplication>()
                .Which.EducationOrganizationIds.Should()
                .Equal(1004);

        [Test]
        public async Task It_inserts_no_client() =>
            (await ApiClientIdsAsync()).Should().Equal(_clientIdsBefore);

        [Test]
        public async Task It_inserts_no_client_rows() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
    }

    [TestFixture]
    public class Given_a_narrowed_client_insert_that_fails_on_its_data_stores
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientInsertResult _result = null!;
        private int[] _clientIdsBefore = [];

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _clientIdsBefore = await ApiClientIdsAsync();
            _result = await InsertApiClientWithScopeAsync(
                ScopedApplicationId,
                new ApiClientEducationOrganizationScope.Narrow([1001]),
                [MissingDataStoreId]
            );
        }

        [Test]
        public void It_fails_on_the_data_stores() =>
            _result.Should().BeOfType<ApiClientInsertResult.FailureDataStoreNotFound>();

        [Test]
        public async Task It_inserts_no_client() =>
            (await ApiClientIdsAsync()).Should().Equal(_clientIdsBefore);

        [Test]
        public async Task It_rolls_back_the_scope_rows() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
    }

    [TestFixture]
    public class Given_scopes_requested_by_updates_on_the_same_Application
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _narrowResult = null!;
        private ApiClientUpdateResult _inheritResult = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(InheritingClientId);
            await MarkPendingAsync(NarrowedClientId);
            _narrowResult = await UpdateApiClientAsync(
                InheritingClientId,
                ScopedApplicationId,
                "Narrowed by update",
                new ApiClientEducationOrganizationScope.Narrow([1002])
            );
            _inheritResult = await UpdateApiClientAsync(
                NarrowedClientId,
                ScopedApplicationId,
                "Inheriting by update",
                new ApiClientEducationOrganizationScope.Inherit()
            );
        }

        [Test]
        public void It_narrows_the_inheriting_client() =>
            _narrowResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_stores_the_narrowed_list_and_keeps_the_pending_time() =>
            ShouldMatch(
                await StateAsync(InheritingClientId),
                State(ScopedApplicationId, false, [1002], [1002], PendingSince)
            );

        [Test]
        public void It_makes_the_narrowed_client_inherit() =>
            _inheritResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_gives_the_inheriting_client_the_Application_list_and_keeps_the_pending_time() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003], PendingSince)
            );

        [Test]
        public async Task It_saves_the_other_client_changes() =>
            (await ClientApplicationAndNameAsync(NarrowedClientId)).Name.Should().Be("Inheriting by update");

        [Test]
        public async Task It_leaves_the_other_clients_rows_untouched() =>
            (await ClientRowsAsync())
                .Where(row => row.ApiClientId != InheritingClientId && row.ApiClientId != NarrowedClientId)
                .Should()
                .Equal(
                    ClientRowsBefore.Where(row =>
                        row.ApiClientId != InheritingClientId && row.ApiClientId != NarrowedClientId
                    )
                );
    }

    [TestFixture]
    public class Given_a_client_that_does_not_fit_moved_with_a_replacement_list_that_does
        : ApiClientEducationOrganizationRepositoryTests
    {
        private int _destinationApplicationId;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _destinationApplicationId = await InsertDestinationApplicationAsync();
            _result = await UpdateApiClientAsync(
                NarrowedClientId,
                _destinationApplicationId,
                "Moved narrowed client",
                new ApiClientEducationOrganizationScope.Narrow([1005, 1001])
            );
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_narrows_the_client_on_the_destination_to_the_replacement_list() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(_destinationApplicationId, false, [1001, 1005], [1001, 1005])
            );

        [Test]
        public async Task It_points_the_client_rows_at_the_destination() =>
            (await ClientRowsOfAsync(NarrowedClientId))
                .Select(row => (row.ApplicationId, row.EducationOrganizationId))
                .Should()
                .Equal((_destinationApplicationId, 1001L), (_destinationApplicationId, 1005L));
    }

    [TestFixture]
    public class Given_a_client_that_does_not_fit_moved_to_inherit
        : ApiClientEducationOrganizationRepositoryTests
    {
        private int _destinationApplicationId;
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _destinationApplicationId = await InsertDestinationApplicationAsync();
            _result = await UpdateApiClientAsync(
                NarrowedClientId,
                _destinationApplicationId,
                "Moved narrowed client",
                new ApiClientEducationOrganizationScope.Inherit()
            );
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_makes_the_client_inherit_the_destination_list() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(_destinationApplicationId, true, [1001, 1003, 1005], [1001, 1003, 1005])
            );

        [Test]
        public async Task It_points_the_client_rows_at_the_destination() =>
            (await ClientRowsOfAsync(NarrowedClientId))
                .Should()
                .OnlyContain(row => row.ApplicationId == _destinationApplicationId)
                .And.HaveCount(3);
    }

    [TestFixture]
    public class Given_a_client_moved_with_a_replacement_list_only_on_its_current_Application
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;
        private (int ApplicationId, string Name) _clientBefore;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            int destinationApplicationId = await InsertDestinationApplicationAsync();
            _clientBefore = await ClientApplicationAndNameAsync(SecondNarrowedClientId);

            // 1002 is on the current Application but not on the destination, which the list is
            // checked against; the omitted scope would have fit, since {1001, 1003} is on both
            _result = await UpdateApiClientAsync(
                SecondNarrowedClientId,
                destinationApplicationId,
                "Unmoved narrowed client",
                new ApiClientEducationOrganizationScope.Narrow([1001, 1002])
            );
        }

        [Test]
        public void It_reports_the_requested_ids_not_on_the_destination() =>
            _result
                .Should()
                .BeOfType<ApiClientUpdateResult.FailureRequestedEducationOrganizationsNotOnApplication>()
                .Which.EducationOrganizationIds.Should()
                .Equal(1002);

        [Test]
        public async Task It_leaves_the_client_Application_and_name() =>
            (await ClientApplicationAndNameAsync(SecondNarrowedClientId)).Should().Be(_clientBefore);

        [Test]
        public async Task It_leaves_the_client_scope() =>
            ShouldMatch(
                await StateAsync(SecondNarrowedClientId),
                State(ScopedApplicationId, false, [1001, 1003], [1001, 1003])
            );

        [Test]
        public async Task It_leaves_the_client_rows() =>
            (await ClientRowsOfAsync(SecondNarrowedClientId))
                .Should()
                .Equal(ClientRowsBeforeOf(SecondNarrowedClientId));
    }

    [TestFixture]
    public class Given_a_requested_scope_on_the_same_Application_not_on_it
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateApiClientAsync(
                InheritingClientId,
                ScopedApplicationId,
                "Renamed client",
                new ApiClientEducationOrganizationScope.Narrow([1001, 1005])
            );
        }

        [Test]
        public void It_reports_the_requested_ids_not_on_the_Application() =>
            _result
                .Should()
                .BeOfType<ApiClientUpdateResult.FailureRequestedEducationOrganizationsNotOnApplication>()
                .Which.EducationOrganizationIds.Should()
                .Equal(1005);

        [Test]
        public async Task It_leaves_the_client_inheriting() =>
            ShouldMatch(
                await StateAsync(InheritingClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
            );

        [Test]
        public async Task It_leaves_the_client_name() =>
            (await ClientApplicationAndNameAsync(InheritingClientId)).Name.Should().Be("Scoped Application");

        [Test]
        public async Task It_leaves_every_client_row_untouched() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
    }

    [TestFixture]
    public class Given_requested_scope_updates_that_fail_after_their_rows_are_replaced
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientUpdateResult _sameApplicationResult = null!;
        private ApiClientUpdateResult _moveResult = null!;
        private (int ApplicationId, string Name) _sameApplicationClientBefore;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _sameApplicationClientBefore = await ClientApplicationAndNameAsync(NarrowedClientId);

            // A missing data store fails the update after the scope rows and the flag are written
            _sameApplicationResult = await UpdateApiClientAsync(
                NarrowedClientId,
                ScopedApplicationId,
                "Renamed client",
                new ApiClientEducationOrganizationScope.Inherit(),
                [MissingDataStoreId]
            );

            // The destination's default client is named after the Application, so the move violates
            // UX_ApiClient_ApplicationId_Name after its rows are deleted
            int destinationApplicationId = await InsertDestinationApplicationAsync();
            _moveResult = await UpdateApiClientAsync(
                InheritingClientId,
                destinationApplicationId,
                "Destination Application",
                new ApiClientEducationOrganizationScope.Narrow([1005])
            );
        }

        [Test]
        public void It_fails_the_same_Application_update_on_its_data_stores() =>
            _sameApplicationResult.Should().BeOfType<ApiClientUpdateResult.FailureDataStoreNotFound>();

        [Test]
        public async Task It_keeps_the_same_Application_client_narrowed() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, false, [1002], [1002])
            );

        [Test]
        public async Task It_rolls_back_the_same_Application_client_rows_and_name()
        {
            (await ClientRowsOfAsync(NarrowedClientId)).Should().Equal(ClientRowsBeforeOf(NarrowedClientId));
            (await ClientApplicationAndNameAsync(NarrowedClientId)).Should().Be(_sameApplicationClientBefore);
        }

        [Test]
        public void It_fails_the_move_on_the_client_name() =>
            _moveResult.Should().BeOfType<ApiClientUpdateResult.FailureDuplicateName>();

        [Test]
        public async Task It_keeps_the_moved_client_inheriting_on_its_Application() =>
            ShouldMatch(
                await StateAsync(InheritingClientId),
                State(ScopedApplicationId, true, [1001, 1002, 1003], [1001, 1002, 1003])
            );

        [Test]
        public async Task It_rolls_back_the_moved_client_rows() =>
            (await ClientRowsOfAsync(InheritingClientId))
                .Should()
                .Equal(ClientRowsBeforeOf(InheritingClientId));
    }
}
