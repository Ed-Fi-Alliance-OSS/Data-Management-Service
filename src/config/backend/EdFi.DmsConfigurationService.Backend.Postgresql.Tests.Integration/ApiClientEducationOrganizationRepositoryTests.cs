// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// How Application saves and client deletes treat the API client education organization rows
/// (DMS-1639). One Application holds 1001, 1002 and 1003 with an inheriting client (all three) and
/// two narrowed clients ({1002} and {1001, 1003}); an unrelated Application of the same vendor also
/// holds 1001 with one client. Client ids are created in that order: inheriting, unrelated, then
/// the two narrowed clients. No repository writes client rows yet, so they are seeded with SQL.
/// Every seeded row carries a fixed CreatedAt and CreatedBy, so a row that was deleted and
/// reinserted is told apart from one that survived.
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

    private readonly string _inheritingClientKey = Guid.NewGuid().ToString();
    private readonly Guid _inheritingClientUuid = Guid.NewGuid();
    private ApplicationRepository _applicationRepository = null!;

    protected async Task SeedAsync(TenantContextProvider tenantContextProvider)
    {
        _applicationRepository = new(
            Configuration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
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
        NarrowedClientId = await InsertNarrowedClientAsync(ScopedApplicationId);
        SecondNarrowedClientId = await InsertNarrowedClientAsync(ScopedApplicationId);

        await InsertClientRowsAsync(InheritingClientId, ScopedApplicationId, 1001, 1002, 1003);
        await InsertClientRowsAsync(NarrowedClientId, ScopedApplicationId, 1002);
        await InsertClientRowsAsync(SecondNarrowedClientId, ScopedApplicationId, 1001, 1003);
        await InsertClientRowsAsync(UnrelatedClientId, UnrelatedApplicationId, 1001);

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
    protected Task<ApplicationUpdateResult> UpdateScopedApplicationAsync(
        long[] educationOrganizationIds,
        Guid? clientUuid = null
    ) =>
        _applicationRepository.UpdateApplication(
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

    private async Task<int> InsertApplicationAsync(
        string name,
        long[] educationOrganizationIds,
        string clientKey,
        Guid clientUuid
    )
    {
        var result = await _applicationRepository.InsertApplication(
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

    private async Task<int> InsertNarrowedClientAsync(int applicationId) =>
        await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."ApiClient"
                ("ApplicationId", "Name", "ClientId", "ClientUuid", "InheritsApplicationEducationOrganizations")
            VALUES (@ApplicationId, @Name, @ClientId, @ClientUuid, false)
            RETURNING "Id";
            """,
            new
            {
                ApplicationId = applicationId,
                Name = $"Client {Guid.NewGuid():N}",
                ClientId = Guid.NewGuid().ToString(),
                ClientUuid = Guid.NewGuid(),
            }
        );

    private Task InsertClientRowsAsync(
        int apiClientId,
        int applicationId,
        params long[] educationOrganizationIds
    ) =>
        Connection!.ExecuteAsync(
            """
            INSERT INTO "dmscs"."ApiClientEducationOrganization" ("ApiClientId", "ApplicationId", "EducationOrganizationId")
            VALUES (@ApiClientId, @ApplicationId, @EducationOrganizationId);
            """,
            educationOrganizationIds.Select(educationOrganizationId => new
            {
                ApiClientId = apiClientId,
                ApplicationId = applicationId,
                EducationOrganizationId = educationOrganizationId,
            })
        );

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
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
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
}
