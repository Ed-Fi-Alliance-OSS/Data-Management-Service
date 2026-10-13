// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class ApiClientTests : DatabaseTest
{
    private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApiClientRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    [TestFixture]
    public class QueryPagingTests : ApiClientTests
    {
        private int _applicationId;

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            VendorInsertCommand vendorCommand = new()
            {
                Company = "Paging Test Vendor",
                ContactEmailAddress = "paging@test.com",
                ContactName = "Paging Tester",
                NamespacePrefixes = "uri://paging-test.example",
            };
            var vendorResult = await vendorRepository.InsertVendor(vendorCommand);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            int vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand app = new()
            {
                ApplicationName = "PagingTestApp",
                VendorId = vendorId,
                ClaimSetName = "TestClaimSet",
                EducationOrganizationIds = [],
            };
            var appResult = await _applicationRepository.InsertApplication(
                app,
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            _applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

            for (int i = 1; i <= 29; i++)
            {
                ApiClientInsertCommand apiClientCommand = new()
                {
                    ApplicationId = _applicationId,
                    Name = $"ApiClient-{i:D3}",
                    IsApproved = true,
                    DataStoreIds = [],
                };
                var insertResult = await _apiClientRepository.InsertApiClient(
                    apiClientCommand,
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                insertResult
                    .Should()
                    .BeOfType<ApiClientInsertResult.Success>(
                        $"api client {i} (ApiClient-{i:D3}) should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _apiClientRepository.QueryApiClient(new ApiClientQuery());
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            ((ApiClientQueryResult.Success)result).ApiClientResponses.Should().HaveCount(30);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _apiClientRepository.QueryApiClient(new ApiClientQuery { Limit = 10 });
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            ((ApiClientQueryResult.Success)result).ApiClientResponses.Should().HaveCount(10);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _apiClientRepository.QueryApiClient(new ApiClientQuery { Offset = 25 });
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            ((ApiClientQueryResult.Success)result).ApiClientResponses.Should().HaveCount(5);
        }
    }

    [TestFixture]
    public class QuerySortTests : ApiClientTests
    {
        private int _applicationId;

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            VendorInsertCommand vendorCommand = new()
            {
                Company = "Sort Test Vendor",
                ContactEmailAddress = "sort@test.com",
                ContactName = "Sort Tester",
                NamespacePrefixes = "uri://sort-test.example",
            };
            var vendorResult = await vendorRepository.InsertVendor(vendorCommand);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            int vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand app = new()
            {
                ApplicationName = "Bravo-App",
                VendorId = vendorId,
                ClaimSetName = "TestClaimSet",
                EducationOrganizationIds = [],
            };
            var appResult = await _applicationRepository.InsertApplication(
                app,
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            _applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

            foreach (var name in new[] { "Alpha-Client", "Charlie-Client" })
            {
                ApiClientInsertCommand apiClientCommand = new()
                {
                    ApplicationId = _applicationId,
                    Name = name,
                    IsApproved = true,
                    DataStoreIds = [],
                };
                var insertResult = await _apiClientRepository.InsertApiClient(
                    apiClientCommand,
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                insertResult
                    .Should()
                    .BeOfType<ApiClientInsertResult.Success>(
                        $"api client '{name}' should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_name()
        {
            var result = await _apiClientRepository.QueryApiClient(
                new ApiClientQuery
                {
                    ApplicationId = _applicationId,
                    OrderBy = "name",
                    Direction = "ASC",
                }
            );
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            var names = ((ApiClientQueryResult.Success)result)
                .ApiClientResponses.Select(c => c.Name)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Alpha-Client", "Bravo-App", "Charlie-Client");
        }

        [Test]
        public async Task Should_return_descending_order_by_name()
        {
            var result = await _apiClientRepository.QueryApiClient(
                new ApiClientQuery
                {
                    ApplicationId = _applicationId,
                    OrderBy = "name",
                    Direction = "DESC",
                }
            );
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            var names = ((ApiClientQueryResult.Success)result)
                .ApiClientResponses.Select(c => c.Name)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Charlie-Client", "Bravo-App", "Alpha-Client");
        }
    }

    [TestFixture]
    public class Given_api_client_ownership_projection : ApiClientTests
    {
        private int _applicationId;
        private int _configuredApiClientId;
        private string _configuredClientId = string.Empty;
        private int _unconfiguredApiClientId;
        private int _creatorTokenId;
        private int _readTokenId1;
        private int _readTokenId2;

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var vendorResult = await vendorRepository.InsertVendor(
                new VendorInsertCommand
                {
                    Company = "Projection Test Vendor",
                    ContactEmailAddress = "projection@test.com",
                    ContactName = "Projection Tester",
                    NamespacePrefixes = "uri://projection-test.example",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();

            var applicationResult = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "ProjectionTestApp",
                    VendorId = ((VendorInsertResult.Success)vendorResult).Id,
                    ClaimSetName = "TestClaimSet",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();
            _applicationId = ((ApplicationInsertResult.Success)applicationResult).Id;

            (_configuredApiClientId, _configuredClientId) = await InsertApiClient("Configured Client");
            (_unconfiguredApiClientId, _) = await InsertApiClient("Unconfigured Client");

            IOwnershipTokenRepository ownershipTokenRepository = new OwnershipTokenRepository(
                Configuration.DatabaseOptions,
                NullLogger<OwnershipTokenRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            _readTokenId2 = await InsertToken(ownershipTokenRepository, "Read 2");
            _creatorTokenId = await InsertToken(ownershipTokenRepository, "Creator");
            _readTokenId1 = await InsertToken(ownershipTokenRepository, "Read 1");

            var update = await ownershipTokenRepository.UpdateApiClientOwnership(
                new ApiClientOwnershipUpdateCommand
                {
                    ApiClientId = _configuredApiClientId,
                    CreatorOwnershipTokenId = _creatorTokenId,
                    OwnershipTokenIds = [_readTokenId2, _readTokenId1],
                }
            );
            update.Should().BeOfType<ApiClientOwnershipUpdateResult.Success>();
        }

        [Test]
        public async Task It_returns_creator_and_sorted_read_modify_tokens_on_get_by_client_id()
        {
            var result = await _apiClientRepository.GetApiClientByClientId(_configuredClientId);

            result.Should().BeOfType<ApiClientGetResult.Success>();
            var response = ((ApiClientGetResult.Success)result).ApiClientResponse;
            response.CreatorOwnershipTokenId.Should().Be(_creatorTokenId);
            response.OwnershipTokenIds.Should().Equal(_readTokenId2, _readTokenId1);
        }

        [Test]
        public async Task It_returns_creator_and_sorted_read_modify_tokens_on_query()
        {
            var result = await _apiClientRepository.QueryApiClient(
                new ApiClientQuery { ApplicationId = _applicationId }
            );

            result.Should().BeOfType<ApiClientQueryResult.Success>();
            var response = ((ApiClientQueryResult.Success)result).ApiClientResponses.Single(apiClient =>
                apiClient.Id == _configuredApiClientId
            );
            response.CreatorOwnershipTokenId.Should().Be(_creatorTokenId);
            response.OwnershipTokenIds.Should().Equal(_readTokenId2, _readTokenId1);
        }

        [Test]
        public async Task It_returns_empty_ownership_for_unconfigured_api_client()
        {
            var result = await _apiClientRepository.GetApiClientById(_unconfiguredApiClientId);

            result.Should().BeOfType<ApiClientGetResult.Success>();
            var response = ((ApiClientGetResult.Success)result).ApiClientResponse;
            response.CreatorOwnershipTokenId.Should().BeNull();
            response.OwnershipTokenIds.Should().BeEmpty();
        }

        private async Task<(int Id, string ClientId)> InsertApiClient(string name)
        {
            string clientId = Guid.NewGuid().ToString();
            var insertResult = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = _applicationId,
                    Name = name,
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = clientId, ClientUuid = Guid.NewGuid() }
            );
            insertResult.Should().BeOfType<ApiClientInsertResult.Success>();
            return (((ApiClientInsertResult.Success)insertResult).Id, clientId);
        }

        private static async Task<int> InsertToken(
            IOwnershipTokenRepository ownershipTokenRepository,
            string description
        )
        {
            var insert = await ownershipTokenRepository.InsertOwnershipToken(
                new OwnershipTokenInsertCommand { Description = description }
            );
            insert.Should().BeOfType<OwnershipTokenInsertResult.Success>();
            return ((OwnershipTokenInsertResult.Success)insert).Id;
        }
    }

    [TestFixture]
    public class Given_api_client_operations_from_another_tenant : ApiClientTests
    {
        private IApiClientRepository _tenantAApiClientRepository = null!;
        private IApiClientRepository _tenantBApiClientRepository = null!;
        private int _tenantAApplicationId;
        private int _tenantBApplicationId;
        private int _tenantAApiClientId;
        private string _tenantAClientId = string.Empty;
        private int _tenantBApiClientId;
        private int _tenantADataStoreId;

        [SetUp]
        public async Task Setup()
        {
            var tenantRepository = new TenantRepository(
                Configuration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );

            var tenantAProvider = await CreateTenantProvider(tenantRepository, "A");
            var tenantBProvider = await CreateTenantProvider(tenantRepository, "B");

            _tenantAApiClientRepository = CreateApiClientRepository(tenantAProvider);
            _tenantBApiClientRepository = CreateApiClientRepository(tenantBProvider);

            _tenantAApplicationId = await InsertVendorWithApplication(tenantAProvider, "Tenant A Company");
            _tenantBApplicationId = await InsertVendorWithApplication(tenantBProvider, "Tenant B Company");

            (_tenantAApiClientId, _tenantAClientId) = await InsertApiClient(
                _tenantAApiClientRepository,
                _tenantAApplicationId,
                "Tenant A Client"
            );
            (_tenantBApiClientId, _) = await InsertApiClient(
                _tenantBApiClientRepository,
                _tenantBApplicationId,
                "Tenant B Client"
            );

            _tenantADataStoreId = await InsertDataStore(tenantAProvider, "Tenant A Data Store");
        }

        private static async Task<TenantContextProvider> CreateTenantProvider(
            TenantRepository tenantRepository,
            string suffix
        )
        {
            var tenantName = $"ApiClientTenant{suffix}-{Guid.NewGuid()}";
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

        private static ApiClientRepository CreateApiClientRepository(
            TenantContextProvider tenantContextProvider
        ) =>
            new(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );

        private static async Task<int> InsertVendorWithApplication(
            TenantContextProvider tenantContextProvider,
            string company
        )
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );
            var vendorResult = await vendorRepository.InsertVendor(
                new VendorInsertCommand
                {
                    Company = company,
                    ContactEmailAddress = "tenant@test.com",
                    ContactName = "Tenant Tester",
                    NamespacePrefixes = "uri://tenant-test.example",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            int vendorId = ((VendorInsertResult.Success)vendorResult).Id;

            IApplicationRepository applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );
            var applicationResult = await applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = $"{company} Application",
                    VendorId = vendorId,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();
            return ((ApplicationInsertResult.Success)applicationResult).Id;
        }

        private static async Task<(int Id, string ClientId)> InsertApiClient(
            IApiClientRepository apiClientRepository,
            int applicationId,
            string name
        )
        {
            string clientId = Guid.NewGuid().ToString();
            var result = await apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = name,
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = clientId, ClientUuid = Guid.NewGuid() }
            );
            result.Should().BeOfType<ApiClientInsertResult.Success>();
            return (((ApiClientInsertResult.Success)result).Id, clientId);
        }

        private static async Task<int> InsertDataStore(
            TenantContextProvider tenantContextProvider,
            string name
        )
        {
            var dataStoreRepository = new DataStoreRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                TestConnectionStringReader.Create(
                    new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                    tenantContextProvider
                ),
                new DataStoreContextRepository(
                    Configuration.DatabaseOptions,
                    NullLogger<DataStoreContextRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                new DataStoreDerivativeRepository(
                    Configuration.DatabaseOptions,
                    NullLogger<DataStoreDerivativeRepository>.Instance,
                    new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                    TestConnectionStringReader.Create(
                        new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                        tenantContextProvider
                    ),
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                new TestAuditContext(),
                tenantContextProvider
            );
            var result = await dataStoreRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = name,
                    ConnectionString = "Server=tenant;Database=TenantDb;",
                }
            );
            result.Should().BeOfType<DataStoreInsertResult.Success>();
            return ((DataStoreInsertResult.Success)result).Id;
        }

        [Test]
        public async Task It_should_not_get_another_tenants_api_client_by_id()
        {
            var result = await _tenantBApiClientRepository.GetApiClientById(_tenantAApiClientId);
            result.Should().BeOfType<ApiClientGetResult.FailureNotFound>();
        }

        [Test]
        public async Task It_should_get_and_delete_its_own_tenant_api_client()
        {
            var getResult = await _tenantBApiClientRepository.GetApiClientById(_tenantBApiClientId);
            getResult.Should().BeOfType<ApiClientGetResult.Success>();

            var deleteResult = await _tenantBApiClientRepository.DeleteApiClient(_tenantBApiClientId);
            deleteResult.Should().BeOfType<ApiClientDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_not_get_another_tenants_api_client_by_client_id()
        {
            var result = await _tenantBApiClientRepository.GetApiClientByClientId(_tenantAClientId);
            result.Should().BeOfType<ApiClientGetResult.FailureNotFound>();
        }

        [Test]
        public async Task It_should_not_list_another_tenants_api_clients_in_query()
        {
            var result = await _tenantBApiClientRepository.QueryApiClient(
                new ApiClientQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            var responses = ((ApiClientQueryResult.Success)result).ApiClientResponses;
            responses.Should().Contain(c => c.Id == _tenantBApiClientId);
            responses.Should().NotContain(c => c.Id == _tenantAApiClientId);
        }

        [Test]
        public async Task It_should_not_update_another_tenants_api_client()
        {
            var result = await _tenantBApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = _tenantAApiClientId,
                    ApplicationId = _tenantAApplicationId,
                    Name = "Hijacked Client",
                    IsApproved = false,
                    DataStoreIds = [],
                }
            );
            result.Should().BeOfType<ApiClientUpdateResult.FailureNotFound>();

            var unchanged = await _tenantAApiClientRepository.GetApiClientById(_tenantAApiClientId);
            unchanged.Should().BeOfType<ApiClientGetResult.Success>();
            ((ApiClientGetResult.Success)unchanged).ApiClientResponse.Name.Should().Be("Tenant A Client");
        }

        [Test]
        public async Task It_should_not_move_an_api_client_to_another_tenants_application()
        {
            var result = await _tenantBApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = _tenantBApiClientId,
                    ApplicationId = _tenantAApplicationId,
                    Name = "Tenant B Client",
                    IsApproved = true,
                    DataStoreIds = [],
                }
            );
            result.Should().BeOfType<ApiClientUpdateResult.FailureApplicationNotFound>();
        }

        [Test]
        public async Task It_should_not_update_an_api_client_with_another_tenants_data_store()
        {
            var result = await _tenantBApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = _tenantBApiClientId,
                    ApplicationId = _tenantBApplicationId,
                    Name = "Tenant B Client",
                    IsApproved = true,
                    DataStoreIds = [_tenantADataStoreId],
                }
            );
            result.Should().BeOfType<ApiClientUpdateResult.FailureDataStoreNotFound>();
        }

        [Test]
        public async Task It_should_not_delete_another_tenants_api_client()
        {
            var result = await _tenantBApiClientRepository.DeleteApiClient(_tenantAApiClientId);
            result.Should().BeOfType<ApiClientDeleteResult.FailureNotFound>();

            var stillThere = await _tenantAApiClientRepository.GetApiClientById(_tenantAApiClientId);
            stillThere.Should().BeOfType<ApiClientGetResult.Success>();
        }

        [Test]
        public async Task It_should_not_insert_an_api_client_under_another_tenants_application()
        {
            var result = await _tenantBApiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = _tenantAApplicationId,
                    Name = "Cross Tenant Client",
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            result.Should().BeOfType<ApiClientInsertResult.FailureApplicationNotFound>();
        }

        [Test]
        public async Task It_should_not_insert_an_api_client_with_another_tenants_data_store()
        {
            var result = await _tenantBApiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = _tenantBApplicationId,
                    Name = "Cross Tenant Data Store Client",
                    IsApproved = true,
                    DataStoreIds = [_tenantADataStoreId],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            result.Should().BeOfType<ApiClientInsertResult.FailureDataStoreNotFound>();
        }

        [Test]
        public async Task It_should_not_expose_tenant_scoped_api_clients_in_single_tenant_context()
        {
            var singleTenantRepository = CreateApiClientRepository(new TenantContextProvider());
            var result = await singleTenantRepository.GetApiClientById(_tenantAApiClientId);
            result.Should().BeOfType<ApiClientGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_api_client_names_within_an_application : ApiClientTests
    {
        private const string ApplicationAName = "DMS1341 Application A";
        private const string SharedName = "DMS1341 Shared Client";
        private const string OtherName = "DMS1341 Other Client";

        private int _applicationAId;
        private int _applicationBId;
        private int _otherClientId;
        private int _duplicateAttemptDataStoreId;
        private ApiClientInsertResult _duplicateInsert = null!;
        private ApiClientInsertResult _otherApplicationInsert = null!;
        private ApiClientInsertResult _firstClientNameInsert = null!;
        private ApiClientUpdateResult _renameOntoTakenName = null!;
        private ApiClientUpdateResult _moveOntoTakenName = null!;
        private ApiClientResponse _otherClientAfterRejectedUpdates = null!;
        private ApiClientUpdateResult _updateKeepingOwnName = null!;

        private async Task<int> InsertApplication(int vendorId, string name)
        {
            var result = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = name,
                    VendorId = vendorId,
                    ClaimSetName = "TestClaimSet",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            result.Should().BeOfType<ApplicationInsertResult.Success>();
            return ((ApplicationInsertResult.Success)result).Id;
        }

        private static async Task<int> InsertDataStore()
        {
            var tenantContextProvider = new TenantContextProvider();
            var dataStoreRepository = new DataStoreRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                TestConnectionStringReader.Create(
                    new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                    tenantContextProvider
                ),
                new DataStoreContextRepository(
                    Configuration.DatabaseOptions,
                    NullLogger<DataStoreContextRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                new DataStoreDerivativeRepository(
                    Configuration.DatabaseOptions,
                    NullLogger<DataStoreDerivativeRepository>.Instance,
                    new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                    TestConnectionStringReader.Create(
                        new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                        tenantContextProvider
                    ),
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                new TestAuditContext(),
                tenantContextProvider
            );
            var result = await dataStoreRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = $"DMS1341 Data Store {Guid.NewGuid()}",
                    ConnectionString = "Server=localhost;Database=TestDb;",
                }
            );
            result.Should().BeOfType<DataStoreInsertResult.Success>();
            return ((DataStoreInsertResult.Success)result).Id;
        }

        private Task<ApiClientInsertResult> InsertApiClient(
            int applicationId,
            string name,
            int[] dataStoreIds
        ) =>
            _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = name,
                    IsApproved = true,
                    DataStoreIds = dataStoreIds,
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );

        private Task<ApiClientUpdateResult> UpdateOtherClient(
            int applicationId,
            string name,
            bool isApproved
        ) =>
            _apiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = _otherClientId,
                    ApplicationId = applicationId,
                    Name = name,
                    IsApproved = isApproved,
                    DataStoreIds = [],
                }
            );

        private async Task<List<ApiClientResponse>> ApiClients(int? applicationId)
        {
            var result = await _apiClientRepository.QueryApiClient(
                new ApiClientQuery { ApplicationId = applicationId }
            );
            result.Should().BeOfType<ApiClientQueryResult.Success>();
            return ((ApiClientQueryResult.Success)result).ApiClientResponses;
        }

        [SetUp]
        public async Task Setup()
        {
            var vendorResult = await new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            ).InsertVendor(
                new VendorInsertCommand
                {
                    Company = $"DMS1341 Vendor {Guid.NewGuid()}",
                    ContactEmailAddress = "names@test.com",
                    ContactName = "Names Tester",
                    NamespacePrefixes = "uri://names-test.example",
                }
            );
            int vendorId = ((VendorInsertResult.Success)vendorResult).Id;

            // Each new application already holds one API client named after the application.
            _applicationAId = await InsertApplication(vendorId, ApplicationAName);
            _applicationBId = await InsertApplication(vendorId, "DMS1341 Application B");
            int originalDataStoreId = await InsertDataStore();
            _duplicateAttemptDataStoreId = await InsertDataStore();

            (await InsertApiClient(_applicationAId, SharedName, [originalDataStoreId]))
                .Should()
                .BeOfType<ApiClientInsertResult.Success>();
            var otherInsert = await InsertApiClient(_applicationAId, OtherName, []);
            _otherClientId = ((ApiClientInsertResult.Success)otherInsert).Id;

            _duplicateInsert = await InsertApiClient(
                _applicationAId,
                SharedName,
                [_duplicateAttemptDataStoreId]
            );
            _otherApplicationInsert = await InsertApiClient(_applicationBId, SharedName, []);
            _firstClientNameInsert = await InsertApiClient(_applicationAId, ApplicationAName, []);

            _renameOntoTakenName = await UpdateOtherClient(_applicationAId, SharedName, isApproved: true);
            _moveOntoTakenName = await UpdateOtherClient(_applicationBId, SharedName, isApproved: true);
            var reread = await _apiClientRepository.GetApiClientById(_otherClientId);
            _otherClientAfterRejectedUpdates = ((ApiClientGetResult.Success)reread).ApiClientResponse;

            _updateKeepingOwnName = await UpdateOtherClient(_applicationAId, OtherName, isApproved: false);
        }

        [Test]
        public void It_should_reject_a_duplicate_name_within_an_application() =>
            _duplicateInsert.Should().BeOfType<ApiClientInsertResult.FailureDuplicateName>();

        [Test]
        public async Task It_should_write_no_row_for_a_rejected_insert() =>
            (await ApiClients(_applicationAId))
                .Select(apiClient => apiClient.Name)
                .Should()
                .BeEquivalentTo(ApplicationAName, SharedName, OtherName);

        [Test]
        public async Task It_should_write_no_data_store_assignment_for_a_rejected_insert() =>
            (await ApiClients(null))
                .Should()
                .NotContain(apiClient => apiClient.DataStoreIds.Contains(_duplicateAttemptDataStoreId));

        [Test]
        public void It_should_accept_the_same_name_under_another_application() =>
            _otherApplicationInsert.Should().BeOfType<ApiClientInsertResult.Success>();

        [Test]
        public void It_should_reject_the_name_of_the_applications_first_client() =>
            _firstClientNameInsert.Should().BeOfType<ApiClientInsertResult.FailureDuplicateName>();

        [Test]
        public void It_should_reject_a_rename_onto_a_taken_name() =>
            _renameOntoTakenName.Should().BeOfType<ApiClientUpdateResult.FailureDuplicateName>();

        [Test]
        public void It_should_reject_a_move_to_an_application_that_has_the_name() =>
            _moveOntoTakenName.Should().BeOfType<ApiClientUpdateResult.FailureDuplicateName>();

        [Test]
        public void It_should_leave_the_client_unchanged_after_rejected_updates()
        {
            _otherClientAfterRejectedUpdates.Name.Should().Be(OtherName);
            _otherClientAfterRejectedUpdates.ApplicationId.Should().Be(_applicationAId);
        }

        [Test]
        public void It_should_accept_an_update_that_keeps_its_own_name() =>
            _updateKeepingOwnName.Should().BeOfType<ApiClientUpdateResult.Success>();
    }

    /// <summary>
    /// DMS-1529: a client key and an identity-provider UUID are unique across every application and
    /// tenant, so a repeat through the repository is a classified failure that writes nothing.
    /// </summary>
    [TestFixture]
    public class Given_api_client_inserts_that_repeat_an_identifier : ApiClientTests
    {
        private readonly string _takenClientId = Guid.NewGuid().ToString();
        private readonly Guid _takenClientUuid = Guid.NewGuid();
        private int _applicationAId;
        private int _applicationBId;
        private int _otherClientRowId;
        private Guid _otherClientUuid;
        private int _duplicateAttemptDataStoreId;
        private ApiClientInsertResult _repeatKeySameApplication = null!;
        private ApiClientInsertResult _repeatUuidSameApplication = null!;
        private ApiClientInsertResult _repeatKeyOtherApplication = null!;
        private ApiClientInsertResult _repeatBothOtherApplication = null!;
        private ApiClientInsertResult _repeatKeyOtherTenant = null!;
        private ApiClientInsertResult _losingConcurrentInsert = null!;
        private ApiClientUpdateResult _updateOntoTakenUuid = null!;
        private Guid _otherClientUuidAfterRejectedUpdate;

        [SetUp]
        public async Task Setup()
        {
            int vendorId = await InsertVendor(new TenantContextProvider());
            _applicationAId = await InsertApplication(
                _applicationRepository,
                vendorId,
                "DMS1529 Application A"
            );
            _applicationBId = await InsertApplication(
                _applicationRepository,
                vendorId,
                "DMS1529 Application B"
            );
            _duplicateAttemptDataStoreId = await InsertDataStore();

            (await InsertApiClient(_applicationAId, "Taken Client", _takenClientId, _takenClientUuid, []))
                .Should()
                .BeOfType<ApiClientInsertResult.Success>();

            _otherClientUuid = Guid.NewGuid();
            var otherInsert = await InsertApiClient(
                _applicationAId,
                "Other Client",
                Guid.NewGuid().ToString(),
                _otherClientUuid,
                []
            );
            _otherClientRowId = ((ApiClientInsertResult.Success)otherInsert).Id;

            _repeatKeySameApplication = await InsertApiClient(
                _applicationAId,
                "Repeat Key",
                _takenClientId,
                Guid.NewGuid(),
                [_duplicateAttemptDataStoreId]
            );
            _repeatUuidSameApplication = await InsertApiClient(
                _applicationAId,
                "Repeat Uuid",
                Guid.NewGuid().ToString(),
                _takenClientUuid,
                [_duplicateAttemptDataStoreId]
            );
            _repeatKeyOtherApplication = await InsertApiClient(
                _applicationBId,
                "Repeat Key Elsewhere",
                _takenClientId,
                Guid.NewGuid(),
                []
            );
            _repeatBothOtherApplication = await InsertApiClient(
                _applicationBId,
                "Repeat Both",
                _takenClientId,
                _takenClientUuid,
                []
            );

            _repeatKeyOtherTenant = await InsertApiClientInAnotherTenant(_takenClientId);
            _losingConcurrentInsert = await RunLosingConcurrentInsert();

            _updateOntoTakenUuid = await _apiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = _otherClientRowId,
                    ApplicationId = _applicationAId,
                    Name = "Other Client",
                    IsApproved = true,
                    DataStoreIds = [],
                    ClientUuid = _takenClientUuid,
                }
            );
            _otherClientUuidAfterRejectedUpdate = await Connection!.ExecuteScalarAsync<Guid>(
                """SELECT "ClientUuid" FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""",
                new { Id = _otherClientRowId }
            );
        }

        private static async Task<int> InsertVendor(TenantContextProvider tenantContextProvider)
        {
            var vendorResult = await new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            ).InsertVendor(
                new VendorInsertCommand
                {
                    Company = $"DMS1529 Vendor {Guid.NewGuid()}",
                    ContactEmailAddress = "identifiers@test.com",
                    ContactName = "Identifiers Tester",
                    NamespacePrefixes = "uri://identifiers-test.example",
                }
            );
            return ((VendorInsertResult.Success)vendorResult).Id;
        }

        private static async Task<int> InsertApplication(
            IApplicationRepository applicationRepository,
            int vendorId,
            string name
        )
        {
            var result = await applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = name,
                    VendorId = vendorId,
                    ClaimSetName = "TestClaimSet",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            result.Should().BeOfType<ApplicationInsertResult.Success>();
            return ((ApplicationInsertResult.Success)result).Id;
        }

        private async Task<int> InsertDataStore() =>
            await Connection!.ExecuteScalarAsync<int>(
                """
                INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name")
                VALUES ('Production', @Name) RETURNING "Id";
                """,
                new { Name = $"DMS1529 Data Store {Guid.NewGuid()}" }
            );

        private Task<ApiClientInsertResult> InsertApiClient(
            int applicationId,
            string name,
            string clientId,
            Guid clientUuid,
            int[] dataStoreIds
        ) =>
            _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = name,
                    IsApproved = true,
                    DataStoreIds = dataStoreIds,
                },
                new ApiClientCommand { ClientId = clientId, ClientUuid = clientUuid }
            );

        /// <summary>
        /// A second tenant with its own vendor and application. The taken key lives under an
        /// application of no tenant, so the rejection shows the constraint ignores tenancy.
        /// </summary>
        private async Task<ApiClientInsertResult> InsertApiClientInAnotherTenant(string clientId)
        {
            long tenantId = await Connection!.ExecuteScalarAsync<long>(
                """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
                new { Name = $"dms1529-tenant-{Guid.NewGuid():N}" }
            );
            var tenantProvider = new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(tenantId, "dms1529-tenant"),
            };
            int vendorId = await InsertVendor(tenantProvider);
            int applicationId = await InsertApplication(
                new ApplicationRepository(
                    Configuration.DatabaseOptions,
                    NullLogger<ApplicationRepository>.Instance,
                    new TestAuditContext(),
                    tenantProvider
                ),
                vendorId,
                "DMS1529 Tenant Application"
            );

            return await new ApiClientRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                tenantProvider
            ).InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = "Tenant Client",
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = clientId, ClientUuid = Guid.NewGuid() }
            );
        }

        /// <summary>
        /// Another session inserts a fresh key and holds its transaction open; the repository insert
        /// of the same key is started, then the session commits. Whether the repository insert waited
        /// on the uncommitted row or ran after the commit, its outcome is the same classified
        /// failure, so the assertion holds for every interleaving without a fixed delay.
        /// </summary>
        private async Task<ApiClientInsertResult> RunLosingConcurrentInsert()
        {
            string contestedClientId = Guid.NewGuid().ToString();

            await using var winner = await DataSource!.OpenConnectionAsync();
            await using var winnerTransaction = await winner.BeginTransactionAsync();
            await winner.ExecuteAsync(
                """
                INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "Name", "ClientId", "ClientUuid")
                VALUES (@ApplicationId, 'Winner', @ClientId, @ClientUuid);
                """,
                new
                {
                    ApplicationId = _applicationBId,
                    ClientId = contestedClientId,
                    ClientUuid = Guid.NewGuid(),
                },
                winnerTransaction
            );

            Task<ApiClientInsertResult> loser = InsertApiClient(
                _applicationBId,
                "Loser",
                contestedClientId,
                Guid.NewGuid(),
                []
            );

            await winnerTransaction.CommitAsync();
            return await loser;
        }

        [Test]
        public void It_rejects_a_repeat_key_within_the_application() =>
            _repeatKeySameApplication.Should().BeOfType<ApiClientInsertResult.FailureDuplicateClientId>();

        [Test]
        public void It_rejects_a_repeat_uuid_within_the_application() =>
            _repeatUuidSameApplication.Should().BeOfType<ApiClientInsertResult.FailureDuplicateClientUuid>();

        [Test]
        public void It_rejects_a_repeat_key_under_another_application() =>
            _repeatKeyOtherApplication.Should().BeOfType<ApiClientInsertResult.FailureDuplicateClientId>();

        [Test]
        public void It_rejects_a_row_repeating_both_identifiers_as_one_of_the_two_conflicts() =>
            _repeatBothOtherApplication
                .Should()
                .Match<ApiClientInsertResult>(result =>
                    result is ApiClientInsertResult.FailureDuplicateClientId
                    || result is ApiClientInsertResult.FailureDuplicateClientUuid
                );

        [Test]
        public void It_rejects_a_repeat_key_under_another_tenant() =>
            _repeatKeyOtherTenant.Should().BeOfType<ApiClientInsertResult.FailureDuplicateClientId>();

        [Test]
        public void It_classifies_the_losing_write_of_a_concurrent_insert() =>
            _losingConcurrentInsert.Should().BeOfType<ApiClientInsertResult.FailureDuplicateClientId>();

        [Test]
        public async Task It_writes_no_row_for_a_rejected_insert() =>
            (
                await Connection!.QueryAsync<string>(
                    """SELECT "Name" FROM "dmscs"."ApiClient" WHERE "ApplicationId" = ANY(@ApplicationIds) ORDER BY "Name";""",
                    new { ApplicationIds = new[] { _applicationAId, _applicationBId } }
                )
            )
                .Should()
                .BeEquivalentTo(
                    "DMS1529 Application A",
                    "DMS1529 Application B",
                    "Other Client",
                    "Taken Client",
                    "Winner"
                );

        [Test]
        public async Task It_writes_no_data_store_assignment_for_a_rejected_insert() =>
            (
                await Connection!.ExecuteScalarAsync<int>(
                    """SELECT count(*) FROM "dmscs"."ApiClientDataStore" WHERE "DataStoreId" = @DataStoreId;""",
                    new { DataStoreId = _duplicateAttemptDataStoreId }
                )
            )
                .Should()
                .Be(0);

        [Test]
        public void It_rejects_an_update_that_stores_a_uuid_another_client_holds() =>
            _updateOntoTakenUuid.Should().BeOfType<ApiClientUpdateResult.FailureDuplicateClientUuid>();

        [Test]
        public void It_leaves_the_client_unchanged_after_the_rejected_update() =>
            _otherClientUuidAfterRejectedUpdate.Should().Be(_otherClientUuid);
    }
}
