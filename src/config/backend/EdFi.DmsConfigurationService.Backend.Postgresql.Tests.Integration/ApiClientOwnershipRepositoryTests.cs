// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class ApiClientOwnershipRepositoryTests : DatabaseTest
{
    [TestFixture]
    public class Given_api_client_ownership_replacement : ApiClientOwnershipRepositoryTests
    {
        private TenantContextProvider _tenantContextProvider = null!;
        private IOwnershipTokenRepository _ownershipTokenRepository = null!;
        private int _apiClientId;

        [SetUp]
        public async Task Setup()
        {
            _tenantContextProvider = new TenantContextProvider();
            _ownershipTokenRepository = CreateOwnershipTokenRepository(_tenantContextProvider);
            _apiClientId = await InsertApiClient(_tenantContextProvider, "Ownership Client");
        }

        [Test]
        public async Task It_returns_null_creator_and_empty_read_modify_tokens_for_new_api_client()
        {
            var result = await _ownershipTokenRepository.GetApiClientOwnership(_apiClientId);

            result.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            var ownership = ((ApiClientOwnershipGetResult.Success)result).Ownership;
            ownership.ApiClientId.Should().Be(_apiClientId);
            ownership.CreatorOwnershipTokenId.Should().BeNull();
            ownership.OwnershipTokenIds.Should().BeEmpty();
        }

        [Test]
        public async Task It_replaces_creator_and_read_modify_tokens_and_returns_sorted_ids()
        {
            int readTokenId2 = await InsertToken(_ownershipTokenRepository, "Read 2");
            int creatorTokenId = await InsertToken(_ownershipTokenRepository, "Creator");
            int readTokenId1 = await InsertToken(_ownershipTokenRepository, "Read 1");

            var update = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _apiClientId,
                    CreatorOwnershipTokenId = creatorTokenId,
                    OwnershipTokenIds = [readTokenId2, readTokenId1],
                }
            );
            var get = await _ownershipTokenRepository.GetApiClientOwnership(_apiClientId);

            update.Should().BeOfType<ApiClientOwnershipUpdateResult.Success>();
            get.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            var ownership = ((ApiClientOwnershipGetResult.Success)get).Ownership;
            ownership.CreatorOwnershipTokenId.Should().Be(creatorTokenId);
            ownership.OwnershipTokenIds.Should().Equal(readTokenId1, readTokenId2);
        }

        [Test]
        public async Task It_leaves_existing_configuration_unchanged_when_a_referenced_token_is_missing()
        {
            int creatorTokenId = await InsertToken(_ownershipTokenRepository, "Creator");
            int readTokenId = await InsertToken(_ownershipTokenRepository, "Read");
            var initialUpdate = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _apiClientId,
                    CreatorOwnershipTokenId = creatorTokenId,
                    OwnershipTokenIds = [readTokenId],
                }
            );
            initialUpdate.Should().BeOfType<ApiClientOwnershipUpdateResult.Success>();

            var missingTokenUpdate = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _apiClientId,
                    CreatorOwnershipTokenId = creatorTokenId,
                    OwnershipTokenIds = [32767],
                }
            );
            var get = await _ownershipTokenRepository.GetApiClientOwnership(_apiClientId);

            missingTokenUpdate
                .Should()
                .BeOfType<ApiClientOwnershipUpdateResult.FailureOwnershipTokenNotFound>();
            get.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            var ownership = ((ApiClientOwnershipGetResult.Success)get).Ownership;
            ownership.CreatorOwnershipTokenId.Should().Be(creatorTokenId);
            ownership.OwnershipTokenIds.Should().Equal(readTokenId);
        }

        [Test]
        public async Task It_returns_api_client_not_found_for_missing_api_client()
        {
            var get = await _ownershipTokenRepository.GetApiClientOwnership(999999);
            var update = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = 999999,
                    CreatorOwnershipTokenId = null,
                    OwnershipTokenIds = [],
                }
            );

            get.Should().BeOfType<ApiClientOwnershipGetResult.FailureApiClientNotFound>();
            update.Should().BeOfType<ApiClientOwnershipUpdateResult.FailureApiClientNotFound>();
        }

        [Test]
        public async Task It_accepts_null_creator_and_empty_read_modify_tokens()
        {
            int creatorTokenId = await InsertToken(_ownershipTokenRepository, "Creator");
            int readTokenId = await InsertToken(_ownershipTokenRepository, "Read");
            var initialUpdate = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _apiClientId,
                    CreatorOwnershipTokenId = creatorTokenId,
                    OwnershipTokenIds = [readTokenId],
                }
            );
            initialUpdate.Should().BeOfType<ApiClientOwnershipUpdateResult.Success>();

            var clear = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _apiClientId,
                    CreatorOwnershipTokenId = null,
                    OwnershipTokenIds = [],
                }
            );
            var get = await _ownershipTokenRepository.GetApiClientOwnership(_apiClientId);

            clear.Should().BeOfType<ApiClientOwnershipUpdateResult.Success>();
            get.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            var ownership = ((ApiClientOwnershipGetResult.Success)get).Ownership;
            ownership.CreatorOwnershipTokenId.Should().BeNull();
            ownership.OwnershipTokenIds.Should().BeEmpty();
        }

        [Test]
        public async Task It_replaces_one_thousand_nine_hundred_ninety_nine_read_modify_tokens()
        {
            int[] tokenIds = await InsertTokensDirectly("Boundary", 1999);

            var update = await _ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _apiClientId,
                    CreatorOwnershipTokenId = null,
                    OwnershipTokenIds = [.. tokenIds.Reverse()],
                }
            );
            var get = await _ownershipTokenRepository.GetApiClientOwnership(_apiClientId);

            update.Should().BeOfType<ApiClientOwnershipUpdateResult.Success>();
            get.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            ((ApiClientOwnershipGetResult.Success)get).Ownership.OwnershipTokenIds.Should().Equal(tokenIds);
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_in_multiple_tenants : ApiClientOwnershipRepositoryTests
    {
        private IOwnershipTokenRepository _tenantARepository = null!;
        private IOwnershipTokenRepository _tenantBRepository = null!;
        private int _tenantAApiClientId;
        private int _tenantBApiClientId;
        private int _tenantATokenId;
        private int _tenantBTokenId;

        [SetUp]
        public async Task Setup()
        {
            var tenantAProvider = await CreateTenantProvider("A");
            var tenantBProvider = await CreateTenantProvider("B");
            _tenantARepository = CreateOwnershipTokenRepository(tenantAProvider);
            _tenantBRepository = CreateOwnershipTokenRepository(tenantBProvider);
            _tenantAApiClientId = await InsertApiClient(tenantAProvider, "Tenant A Client");
            _tenantBApiClientId = await InsertApiClient(tenantBProvider, "Tenant B Client");
            _tenantATokenId = await InsertToken(_tenantARepository, "Tenant A Token");
            _tenantBTokenId = await InsertToken(_tenantBRepository, "Tenant B Token");
        }

        [Test]
        public async Task It_does_not_read_another_tenants_api_client()
        {
            var result = await _tenantBRepository.GetApiClientOwnership(_tenantAApiClientId);

            result.Should().BeOfType<ApiClientOwnershipGetResult.FailureApiClientNotFound>();
        }

        [Test]
        public async Task It_does_not_use_another_tenants_token()
        {
            var result = await _tenantBRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _tenantBApiClientId,
                    CreatorOwnershipTokenId = _tenantATokenId,
                    OwnershipTokenIds = [],
                }
            );
            var get = await _tenantBRepository.GetApiClientOwnership(_tenantBApiClientId);

            result.Should().BeOfType<ApiClientOwnershipUpdateResult.FailureOwnershipTokenNotFound>();
            get.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            ((ApiClientOwnershipGetResult.Success)get).Ownership.CreatorOwnershipTokenId.Should().BeNull();
        }

        [Test]
        public async Task It_does_not_assign_another_tenants_read_modify_token()
        {
            var result = await _tenantARepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _tenantAApiClientId,
                    CreatorOwnershipTokenId = null,
                    OwnershipTokenIds = [_tenantBTokenId],
                }
            );
            var get = await _tenantARepository.GetApiClientOwnership(_tenantAApiClientId);

            result.Should().BeOfType<ApiClientOwnershipUpdateResult.FailureOwnershipTokenNotFound>();
            get.Should().BeOfType<ApiClientOwnershipGetResult.Success>();
            ((ApiClientOwnershipGetResult.Success)get).Ownership.OwnershipTokenIds.Should().BeEmpty();
        }

        private static async Task<TenantContextProvider> CreateTenantProvider(string suffix)
        {
            var tenantRepository = new TenantRepository(
                Configuration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );
            var tenantName = $"ApiClientOwnershipTenant{suffix}-{Guid.NewGuid()}";
            var tenantResult = await tenantRepository.InsertTenant(
                new TenantInsertCommand { Name = tenantName }
            );
            tenantResult.Should().BeOfType<TenantInsertResult.Success>();

            return new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(
                    ((TenantInsertResult.Success)tenantResult).Id,
                    tenantName
                ),
            };
        }
    }

    private static IOwnershipTokenRepository CreateOwnershipTokenRepository(
        TenantContextProvider tenantContextProvider
    ) =>
        new OwnershipTokenRepository(
            Configuration.DatabaseOptions,
            NullLogger<OwnershipTokenRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );

    private static async Task<int> InsertToken(IOwnershipTokenRepository repository, string description)
    {
        var insert = await repository.InsertOwnershipToken(
            new OwnershipTokenInsertCommand { Description = description }
        );
        insert.Should().BeOfType<OwnershipTokenInsertResult.Success>();
        return ((OwnershipTokenInsertResult.Success)insert).Id;
    }

    private static async Task<int> InsertApiClient(TenantContextProvider tenantContextProvider, string name)
    {
        int applicationId = await InsertVendorWithApplication(tenantContextProvider, name);
        var apiClientRepository = new ApiClientRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );
        var insert = await apiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = applicationId,
                Name = name,
                IsApproved = true,
                DataStoreIds = [],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        insert.Should().BeOfType<ApiClientInsertResult.Success>();
        return ((ApiClientInsertResult.Success)insert).Id;
    }

    private static async Task<int> InsertVendorWithApplication(
        TenantContextProvider tenantContextProvider,
        string name
    )
    {
        var vendorRepository = new VendorRepository(
            Configuration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );
        var vendorResult = await vendorRepository.InsertVendor(
            new VendorInsertCommand
            {
                Company = $"{name} Vendor",
                ContactEmailAddress = "ownership@test.com",
                ContactName = "Ownership Tester",
                NamespacePrefixes = $"uri://{Guid.NewGuid()}.example",
            }
        );
        vendorResult.Should().BeOfType<VendorInsertResult.Success>();

        var applicationRepository = new ApplicationRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );
        var applicationResult = await applicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = $"{name} Application",
                VendorId = ((VendorInsertResult.Success)vendorResult).Id,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();
        return ((ApplicationInsertResult.Success)applicationResult).Id;
    }

    private async Task<int[]> InsertTokensDirectly(string prefix, int count)
    {
        var ids = await Connection!.QueryAsync<int>(
            """
            INSERT INTO "dmscs"."OwnershipToken" ("Description", "CreatedBy", "TenantId")
            SELECT @Prefix || "Value"::text, @CreatedBy, @TenantId
            FROM generate_series(1, @Count) AS "Value"
            ORDER BY "Value"
            RETURNING "Id";
            """,
            new
            {
                Prefix = prefix,
                Count = count,
                CreatedBy = "test-user",
                TenantId = (long?)null,
            }
        );

        return [.. ids.Order()];
    }
}
