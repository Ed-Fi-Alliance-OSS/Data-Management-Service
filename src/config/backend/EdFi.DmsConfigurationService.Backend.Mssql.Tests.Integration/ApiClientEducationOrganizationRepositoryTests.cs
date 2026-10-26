// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

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
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );
        ApiClientRepository = new(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );

        var vendorResult = await new VendorRepository(
            MssqlTestConfiguration.DatabaseOptions,
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
        NarrowedClientId = await InsertApiClientAsync(ScopedApplicationId);
        SecondNarrowedClientId = await InsertApiClientAsync(ScopedApplicationId);

        (await ApiClientRepository.SetApiClientEducationOrganizationScope(NarrowedClientId, [1002]))
            .Should()
            .BeOfType<ApiClientEducationOrganizationScopeResult.Success>();
        (
            await ApiClientRepository.SetApiClientEducationOrganizationScope(
                SecondNarrowedClientId,
                [1001, 1003]
            )
        )
            .Should()
            .BeOfType<ApiClientEducationOrganizationScopeResult.Success>();

        await Connection!.ExecuteAsync(
            """
            UPDATE dmscs.ApplicationEducationOrganization SET CreatedAt = @SeededAt, CreatedBy = @SeededBy;
            UPDATE dmscs.ApiClientEducationOrganization SET CreatedAt = @SeededAt, CreatedBy = @SeededBy;
            """,
            new { SeededAt, SeededBy }
        );

        ClientRowsBefore = await ClientRowsAsync();
        ApplicationRowsBefore = await ApplicationRowsAsync();
    }

    /// <summary>Saves the scoped Application under a new name with the given education organizations.</summary>
    protected Task<ApplicationUpdateResult> UpdateScopedApplicationAsync(
        long[] educationOrganizationIds,
        Guid? clientUuid = null
    ) =>
        ApplicationRepository.UpdateApplication(
            new ApplicationUpdateCommand
            {
                Id = ScopedApplicationId,
                ApplicationName = "Renamed Application",
                VendorId = VendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = educationOrganizationIds,
            },
            new ApiClientCommand
            {
                ClientId = _inheritingClientKey,
                ClientUuid = clientUuid ?? _inheritingClientUuid,
            }
        );

    protected async Task<string> ScopedApplicationNameAsync() =>
        await Connection!.ExecuteScalarAsync<string>(
            """SELECT ApplicationName FROM dmscs.Application WHERE Id = @Id;""",
            new { Id = ScopedApplicationId }
        ) ?? "";

    protected async Task<ClientRow[]> ClientRowsAsync() =>
        (
            await Connection!.QueryAsync<ClientRow>(
                """
                SELECT ApiClientId, ApplicationId, EducationOrganizationId, CreatedAt, CreatedBy
                FROM dmscs.ApiClientEducationOrganization
                ORDER BY ApiClientId, EducationOrganizationId;
                """
            )
        ).ToArray();

    protected async Task<ApplicationRow[]> ApplicationRowsAsync() =>
        (
            await Connection!.QueryAsync<ApplicationRow>(
                """
                SELECT ApplicationId, EducationOrganizationId, CreatedAt, CreatedBy
                FROM dmscs.ApplicationEducationOrganization
                ORDER BY ApplicationId, EducationOrganizationId;
                """
            )
        ).ToArray();

    protected async Task<int[]> ApiClientIdsAsync() =>
        (await Connection!.QueryAsync<int>("""SELECT Id FROM dmscs.ApiClient ORDER BY Id;""")).ToArray();

    protected static ClientRow Seeded(int apiClientId, int applicationId, long educationOrganizationId) =>
        new(apiClientId, applicationId, educationOrganizationId, SeededAt, SeededBy);

    protected static ApplicationRow Seeded(int applicationId, long educationOrganizationId) =>
        new(applicationId, educationOrganizationId, SeededAt, SeededBy);

    protected static async Task<TenantContextProvider> CreateTenantProviderAsync(string suffix)
    {
        var tenantName = $"ClientEducationOrganizationTenant{suffix}-{Guid.NewGuid()}";
        var tenantResult = await new TenantRepository(
            MssqlTestConfiguration.DatabaseOptions,
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
            """SELECT Id FROM dmscs.ApiClient WHERE ClientUuid = @ClientUuid;""",
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

    protected Task<ApiClientUpdateResult> UpdateApiClientAsync(int id, int applicationId, string name) =>
        ApiClientRepository.UpdateApiClient(
            new ApiClientUpdateCommand
            {
                Id = id,
                ApplicationId = applicationId,
                Name = name,
                IsApproved = true,
            }
        );

    protected async Task<(int ApplicationId, string Name)> ClientApplicationAndNameAsync(int apiClientId) =>
        await Connection!.QuerySingleAsync<(int, string)>(
            """SELECT ApplicationId, Name FROM dmscs.ApiClient WHERE Id = @Id;""",
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

    [TestFixture]
    public class Given_an_Application_saved_with_its_education_organizations_unchanged
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateScopedApplicationAsync([1001, 1002, 1003]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationUpdateResult.Success>();

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
    public class Given_an_Application_saved_with_its_education_organizations_reordered_and_repeated
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateScopedApplicationAsync([1003, 1001, 1002, 1003, 1001]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationUpdateResult.Success>();

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
        private ApplicationUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateScopedApplicationAsync([1001, 1002, 1003, 1004, 1004]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationUpdateResult.Success>();

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
        private ApplicationUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateScopedApplicationAsync([1002, 1003]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationUpdateResult.Success>();

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
        private ApplicationUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await UpdateScopedApplicationAsync([]);
        }

        [Test]
        public void It_succeeds() => _result.Should().BeOfType<ApplicationUpdateResult.Success>();

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

    [TestFixture]
    public class Given_an_Application_save_that_fails_after_its_education_organizations_change
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApplicationUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            // The client update after the education organization changes takes the unrelated
            // client's identity-provider UUID and violates UX_ApiClient_ClientUuid.
            _result = await UpdateScopedApplicationAsync([1002, 1004], UnrelatedClientUuid);
        }

        [Test]
        public void It_fails_on_the_client_update() =>
            _result.Should().BeOfType<ApplicationUpdateResult.FailureDuplicateClientUuid>();

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
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
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
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                otherTenant
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
                """SELECT Id FROM dmscs.ApiClient WHERE ApplicationId = @ApplicationId;""",
                new { ApplicationId = _emptyApplicationId }
            );

            ShouldMatch(await StateAsync(defaultClientId), State(_emptyApplicationId, true, [], []));
        }
    }

    [TestFixture]
    public class Given_a_client_narrowed_to_repeated_education_organizations
        : ApiClientEducationOrganizationRepositoryTests
    {
        private ApiClientEducationOrganizationScopeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await ApiClientRepository.SetApiClientEducationOrganizationScope(
                InheritingClientId,
                [1003, 1001, 1003]
            );
        }

        [Test]
        public void It_succeeds() =>
            _result.Should().BeOfType<ApiClientEducationOrganizationScopeResult.Success>();

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
        private ApiClientEducationOrganizationScopeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await UpdateScopedApplicationAsync([1001, 1002, 1003, 1004]);
            _result = await ApiClientRepository.SetApiClientEducationOrganizationScope(
                NarrowedClientId,
                null
            );
        }

        [Test]
        public void It_succeeds() =>
            _result.Should().BeOfType<ApiClientEducationOrganizationScopeResult.Success>();

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
        private ApiClientEducationOrganizationScopeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await ApiClientRepository.SetApiClientEducationOrganizationScope(
                NarrowedClientId,
                [1001, 9999]
            );
        }

        [Test]
        public void It_reports_the_education_organization_not_on_the_Application() =>
            _result
                .Should()
                .BeOfType<ApiClientEducationOrganizationScopeResult.FailureNotOnApplication>()
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
            await ApiClientRepository.SetApiClientEducationOrganizationScope(
                SecondNarrowedClientId,
                [1001, 1002, 1003]
            );
            int[] clientIds = [InheritingClientId, NarrowedClientId, SecondNarrowedClientId];

            await UpdateScopedApplicationAsync([1001, 1002, 1003, 1004]);
            foreach (int clientId in clientIds)
            {
                _afterAddition[clientId] = await StateAsync(clientId);
            }

            await UpdateScopedApplicationAsync([1001, 1003, 1004]);
            foreach (int clientId in clientIds)
            {
                _afterRemoval[clientId] = await StateAsync(clientId);
            }

            await UpdateScopedApplicationAsync([1001, 1002, 1003, 1004]);
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
        private ApiClientSyncPendingResult _setResult = null!;
        private ApiClientSyncPendingResult _clearResult = null!;
        private ApiClientEducationOrganizationState _afterSet = null!;
        private ApiClientEducationOrganizationState _afterUnrelatedChanges = null!;
        private ApiClientEducationOrganizationState _afterClear = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            _setResult = await ApiClientRepository.SetApiClientIdentityProviderSyncPending(
                SecondNarrowedClientId,
                PendingSince
            );
            _afterSet = await StateAsync(SecondNarrowedClientId);

            (await UpdateApiClientAsync(SecondNarrowedClientId, ScopedApplicationId, "Renamed client"))
                .Should()
                .BeOfType<ApiClientUpdateResult.Success>();
            await ApiClientRepository.SetApiClientEducationOrganizationScope(SecondNarrowedClientId, [1001]);
            await UpdateScopedApplicationAsync([1001, 1003]);
            _afterUnrelatedChanges = await StateAsync(SecondNarrowedClientId);

            _clearResult = await ApiClientRepository.SetApiClientIdentityProviderSyncPending(
                SecondNarrowedClientId,
                null
            );
            _afterClear = await StateAsync(SecondNarrowedClientId);
        }

        [Test]
        public void It_sets_the_time() => _setResult.Should().BeOfType<ApiClientSyncPendingResult.Success>();

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

        [Test]
        public void It_rejects_a_time_that_is_not_UTC() =>
            Assert.ThrowsAsync<ArgumentException>(() =>
                ApiClientRepository.SetApiClientIdentityProviderSyncPending(
                    SecondNarrowedClientId,
                    DateTime.SpecifyKind(PendingSince, DateTimeKind.Unspecified)
                )
            );
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
            await ApiClientRepository.SetApiClientIdentityProviderSyncPending(NarrowedClientId, PendingSince);
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
            await ApiClientRepository.SetApiClientIdentityProviderSyncPending(
                SecondNarrowedClientId,
                PendingSince
            );
            await ApiClientRepository.SetApiClientIdentityProviderSyncPending(NarrowedClientId, PendingSince);
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
            (await ApiClientRepository.SetApiClientEducationOrganizationScope(MissingClientId, [1001]))
                .Should()
                .BeOfType<ApiClientEducationOrganizationScopeResult.FailureNotFound>();

        [Test]
        public async Task It_reports_a_pending_time_write_not_found() =>
            (await ApiClientRepository.SetApiClientIdentityProviderSyncPending(MissingClientId, PendingSince))
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
        private ApiClientEducationOrganizationScopeResult _scopeResult = null!;
        private ApiClientSyncPendingResult _pendingResult = null!;
        private ApiClientUpdateResult _moveResult = null!;

        [SetUp]
        public async Task Setup()
        {
            TenantContextProvider otherTenant = await CreateTenantProviderAsync("Other");
            _otherTenantRepository = new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                otherTenant
            );

            await SeedAsync(await CreateTenantProviderAsync("Owner"));

            // The other tenant's Application holds the client's education organizations, so only
            // the tenant check can stop the move
            var otherVendorResult = await new VendorRepository(
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
            _scopeResult = await _otherTenantRepository.SetApiClientEducationOrganizationScope(
                SecondNarrowedClientId,
                [1001]
            );
            _pendingResult = await _otherTenantRepository.SetApiClientIdentityProviderSyncPending(
                SecondNarrowedClientId,
                PendingSince
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
            _scopeResult.Should().BeOfType<ApiClientEducationOrganizationScopeResult.FailureNotFound>();

        [Test]
        public void It_reports_the_pending_time_write_not_found() =>
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
}
