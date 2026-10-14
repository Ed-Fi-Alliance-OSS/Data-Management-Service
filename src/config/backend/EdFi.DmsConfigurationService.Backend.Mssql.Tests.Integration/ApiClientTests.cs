// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class ApiClientTests : DatabaseTest
{
    private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<ApiClientRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
    public class Given_insert_api_client_with_invalid_application_id : ApiClientTests
    {
        private ApiClientInsertResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _result = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = 999999,
                    Name = "Invalid Application Client",
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
        }

        [Test]
        public void It_should_return_failure_application_not_found()
        {
            _result.Should().BeOfType<ApiClientInsertResult.FailureApplicationNotFound>();
        }
    }

    [TestFixture]
    public class Given_update_api_client_with_invalid_application_id : ApiClientTests
    {
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            VendorInsertCommand vendorCommand = new()
            {
                Company = "Invalid Application Test Vendor",
                ContactEmailAddress = "invalidapp@test.com",
                ContactName = "Invalid Application Tester",
                NamespacePrefixes = "uri://invalid-application-test.example",
            };
            var vendorResult = await vendorRepository.InsertVendor(vendorCommand);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            int vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand app = new()
            {
                ApplicationName = "InvalidApplicationTestApp",
                VendorId = vendorId,
                ClaimSetName = "TestClaimSet",
                EducationOrganizationIds = [],
            };
            var appResult = await _applicationRepository.InsertApplication(
                app,
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            int applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

            var insertResult = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = "Client To Update",
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            insertResult.Should().BeOfType<ApiClientInsertResult.Success>();
            int apiClientId = (insertResult as ApiClientInsertResult.Success)!.Id;

            _result = await _apiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = apiClientId,
                    ApplicationId = 999999,
                    Name = "Client To Update",
                    IsApproved = true,
                    DataStoreIds = [],
                }
            );
        }

        [Test]
        public void It_should_return_failure_application_not_found()
        {
            _result.Should().BeOfType<ApiClientUpdateResult.FailureApplicationNotFound>();
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
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
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                TestConnectionStringReader.Create(
                    new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                    tenantContextProvider
                ),
                new DataStoreContextRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<DataStoreContextRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                new DataStoreDerivativeRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<DataStoreDerivativeRepository>.Instance,
                    new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                    TestConnectionStringReader.Create(
                        new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
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
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                TestConnectionStringReader.Create(
                    new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                    tenantContextProvider
                ),
                new DataStoreContextRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<DataStoreContextRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                new DataStoreDerivativeRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<DataStoreDerivativeRepository>.Instance,
                    new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                    TestConnectionStringReader.Create(
                        new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
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
                MssqlTestConfiguration.DatabaseOptions,
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
        private ApiClientResponse _otherClientBeforeRejectedUpdate = null!;
        private ApiClientResponse _otherClientAfterRejectedUpdate = null!;

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
            _duplicateAttemptDataStoreId = await InsertDataStore(Connection!);

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

            _repeatKeyOtherTenant = await InsertApiClientRepeatingAnotherTenantsKey();
            _losingConcurrentInsert = await RunLosingConcurrentInsert();

            // Every scalar and association of the client changes alongside the taken UUID, so a
            // partially persisted update would show in the full before/after comparison.
            _otherClientBeforeRejectedUpdate = await ReadApiClient(_otherClientRowId);
            _updateOntoTakenUuid = await _apiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = _otherClientRowId,
                    ApplicationId = _applicationBId,
                    Name = "Renamed Other Client",
                    IsApproved = false,
                    DataStoreIds = [_duplicateAttemptDataStoreId],
                    ClientUuid = _takenClientUuid,
                }
            );
            _otherClientAfterRejectedUpdate = await ReadApiClient(_otherClientRowId);
        }

        private async Task<ApiClientResponse> ReadApiClient(int id)
        {
            var result = await _apiClientRepository.GetApiClientById(id);
            return ((ApiClientGetResult.Success)result).ApiClientResponse;
        }

        internal static async Task<int> InsertVendor(TenantContextProvider tenantContextProvider)
        {
            var vendorResult = await new VendorRepository(
                MssqlTestConfiguration.DatabaseOptions,
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

        internal static async Task<int> InsertApplication(
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

        internal static async Task<int> InsertDataStore(SqlConnection connection) =>
            await connection.ExecuteScalarAsync<int>(
                """
                INSERT INTO dmscs.DataStore (DataStoreType, Name)
                OUTPUT INSERTED.Id VALUES ('Production', @Name);
                """,
                new { Name = $"DMS1529 Data Store {Guid.NewGuid()}" }
            );

        /// <summary>
        /// A tenant of its own with a vendor and an application, and the tenant-scoped repository
        /// that writes clients under it.
        /// </summary>
        internal static async Task<(
            IApiClientRepository ApiClientRepository,
            int ApplicationId
        )> CreateTenantAggregate(SqlConnection connection, string tenantName)
        {
            long tenantId = await connection.ExecuteScalarAsync<long>(
                """INSERT INTO dmscs.Tenant (Name) OUTPUT INSERTED.Id VALUES (@Name);""",
                new { Name = $"{tenantName}-{Guid.NewGuid():N}" }
            );
            var tenantProvider = new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(tenantId, tenantName),
            };
            int vendorId = await InsertVendor(tenantProvider);
            int applicationId = await InsertApplication(
                new ApplicationRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<ApplicationRepository>.Instance,
                    new TestAuditContext(),
                    tenantProvider
                ),
                vendorId,
                $"DMS1529 {tenantName} Application"
            );
            IApiClientRepository apiClientRepository = new ApiClientRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                tenantProvider
            );
            return (apiClientRepository, applicationId);
        }

        private Task<ApiClientInsertResult> InsertApiClient(
            int applicationId,
            string name,
            string clientId,
            Guid clientUuid,
            int[] dataStoreIds
        ) => InsertApiClient(_apiClientRepository, applicationId, name, clientId, clientUuid, dataStoreIds);

        private static Task<ApiClientInsertResult> InsertApiClient(
            IApiClientRepository apiClientRepository,
            int applicationId,
            string name,
            string clientId,
            Guid clientUuid,
            int[] dataStoreIds
        ) =>
            apiClientRepository.InsertApiClient(
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
        /// Two tenants, each with its own vendor, application and tenant-scoped repository. The key
        /// is taken by the first tenant's client; the second tenant's insert of the same key is
        /// rejected, so the constraint is global rather than tenant-scoped.
        /// </summary>
        private async Task<ApiClientInsertResult> InsertApiClientRepeatingAnotherTenantsKey()
        {
            string tenantOneClientId = Guid.NewGuid().ToString();
            var tenantOne = await CreateTenantAggregate(Connection!, "dms1529-tenant-one");
            (
                await InsertApiClient(
                    tenantOne.ApiClientRepository,
                    tenantOne.ApplicationId,
                    "Tenant One Client",
                    tenantOneClientId,
                    Guid.NewGuid(),
                    []
                )
            )
                .Should()
                .BeOfType<ApiClientInsertResult.Success>();

            var tenantTwo = await CreateTenantAggregate(Connection!, "dms1529-tenant-two");
            return await InsertApiClient(
                tenantTwo.ApiClientRepository,
                tenantTwo.ApplicationId,
                "Tenant Two Client",
                tenantOneClientId,
                Guid.NewGuid(),
                []
            );
        }

        /// <summary>
        /// Another session inserts a fresh key and holds its transaction open; the repository insert
        /// of the same key is started, then the session commits. Whether the repository insert waited
        /// on the uncommitted key or ran after the commit, its outcome is the same classified
        /// failure, so the assertion holds for every interleaving without a fixed delay.
        /// </summary>
        private async Task<ApiClientInsertResult> RunLosingConcurrentInsert()
        {
            string contestedClientId = Guid.NewGuid().ToString();

            await using var winner = await OpenConnectionAsync();
            await using var winnerTransaction = await winner.BeginTransactionAsync();
            await winner.ExecuteAsync(
                """
                INSERT INTO dmscs.ApiClient (ApplicationId, Name, ClientId, ClientUuid, IsApproved, CreatedBy)
                VALUES (@ApplicationId, 'Winner', @ClientId, @ClientUuid, 1, 'dms1529-winner');
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
                    """SELECT Name FROM dmscs.ApiClient WHERE ApplicationId IN @ApplicationIds ORDER BY Name;""",
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
                    """SELECT count(*) FROM dmscs.ApiClientDataStore WHERE DataStoreId = @DataStoreId;""",
                    new { DataStoreId = _duplicateAttemptDataStoreId }
                )
            )
                .Should()
                .Be(0);

        [Test]
        public void It_rejects_an_update_that_stores_a_uuid_another_client_holds() =>
            _updateOntoTakenUuid.Should().BeOfType<ApiClientUpdateResult.FailureDuplicateClientUuid>();

        [Test]
        public void It_leaves_the_whole_client_unchanged_after_the_rejected_update()
        {
            _otherClientBeforeRejectedUpdate.Name.Should().Be("Other Client");
            _otherClientBeforeRejectedUpdate.ClientUuid.Should().Be(_otherClientUuid);
            _otherClientAfterRejectedUpdate.Should().BeEquivalentTo(_otherClientBeforeRejectedUpdate);
        }
    }

    /// <summary>
    /// DMS-1529: the SQL Server unique-violation message always names the conflicting value ("The
    /// duplicate key value is ..."), so an expected conflict must be logged as fixed text with the
    /// error number and the matched constraint, and no attached exception. Every one of the eight
    /// conflict catches is driven here and its captured log line checked, after proving the value
    /// really is present on the exception.
    /// </summary>
    [TestFixture]
    public class Given_identifier_conflicts_logged_where_the_error_names_the_duplicate_value : ApiClientTests
    {
        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => Entries.Add((logLevel, formatter(state, exception), exception));
        }

        private readonly string _takenClientId = Guid.NewGuid().ToString();
        private readonly Guid _takenClientUuid = Guid.NewGuid();
        private readonly string _secondClientId = Guid.NewGuid().ToString();
        private readonly Guid _secondClientUuid = Guid.NewGuid();
        private CapturingLogger<ApiClientRepository> _apiClientLogger = null!;
        private CapturingLogger<ApplicationRepository> _applicationLogger = null!;
        private string _rawExceptionMessage = string.Empty;
        private int _vendorId;
        private int _secondApplicationId;
        private int _secondClientRowId;
        private List<object> _results = [];

        [SetUp]
        public async Task Setup()
        {
            // NUnit runs SetUp before each test on one fixture instance, so the captured state
            // is recreated here rather than accumulated across tests.
            _apiClientLogger = new CapturingLogger<ApiClientRepository>();
            _applicationLogger = new CapturingLogger<ApplicationRepository>();
            _results = [];

            IApiClientRepository apiClientRepository = new ApiClientRepository(
                MssqlTestConfiguration.DatabaseOptions,
                _apiClientLogger,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            IApplicationRepository applicationRepository = new ApplicationRepository(
                MssqlTestConfiguration.DatabaseOptions,
                _applicationLogger,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            _vendorId = await Given_api_client_inserts_that_repeat_an_identifier.InsertVendor(
                new TenantContextProvider()
            );
            (
                await _applicationRepository.InsertApplication(
                    new ApplicationInsertCommand
                    {
                        ApplicationName = "DMS1529 Logged Taken Application",
                        VendorId = _vendorId,
                        ClaimSetName = "TestClaimSet",
                        EducationOrganizationIds = [],
                    },
                    new ApiClientCommand { ClientId = _takenClientId, ClientUuid = _takenClientUuid }
                )
            ).Should().BeOfType<ApplicationInsertResult.Success>();
            var secondApplication = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "DMS1529 Logged Second Application",
                    VendorId = _vendorId,
                    ClaimSetName = "TestClaimSet",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = _secondClientId, ClientUuid = _secondClientUuid }
            );
            _secondApplicationId = ((ApplicationInsertResult.Success)secondApplication).Id;
            _secondClientRowId = await Connection!.ExecuteScalarAsync<int>(
                """SELECT Id FROM dmscs.ApiClient WHERE ClientId = @ClientId;""",
                new { ClientId = _secondClientId }
            );

            // Prerequisite: the raw exception message names the key, so a catch that attached the
            // exception or logged its message would leak the value the assertions below look for.
            try
            {
                await Connection!.ExecuteAsync(
                    """
                    INSERT INTO dmscs.ApiClient (ApplicationId, Name, ClientId, ClientUuid, IsApproved, CreatedBy)
                    VALUES (@ApplicationId, 'Raw Repeat', @ClientId, @ClientUuid, 1, 'dms1529-raw');
                    """,
                    new
                    {
                        ApplicationId = _secondApplicationId,
                        ClientId = _takenClientId,
                        ClientUuid = Guid.NewGuid(),
                    }
                );
            }
            catch (SqlException exception)
            {
                _rawExceptionMessage = exception.Message;
            }

            _results.Add(
                await apiClientRepository.InsertApiClient(
                    new ApiClientInsertCommand
                    {
                        ApplicationId = _secondApplicationId,
                        Name = "Logged Repeat Key",
                        IsApproved = true,
                        DataStoreIds = [],
                    },
                    new ApiClientCommand { ClientId = _takenClientId, ClientUuid = Guid.NewGuid() }
                )
            );
            _results.Add(
                await apiClientRepository.InsertApiClient(
                    new ApiClientInsertCommand
                    {
                        ApplicationId = _secondApplicationId,
                        Name = "Logged Repeat Uuid",
                        IsApproved = true,
                        DataStoreIds = [],
                    },
                    new ApiClientCommand
                    {
                        ClientId = Guid.NewGuid().ToString(),
                        ClientUuid = _takenClientUuid,
                    }
                )
            );
            _results.Add(
                await apiClientRepository.UpdateApiClient(
                    new ApiClientUpdateCommand
                    {
                        Id = _secondClientRowId,
                        ApplicationId = _secondApplicationId,
                        Name = "DMS1529 Logged Second Application",
                        IsApproved = true,
                        DataStoreIds = [],
                        ClientUuid = _takenClientUuid,
                    }
                )
            );
            _results.Add(
                await apiClientRepository.SyncApiClientUuid(
                    _secondClientRowId,
                    _secondClientUuid,
                    _takenClientUuid
                )
            );
            _results.Add(
                await applicationRepository.InsertApplication(
                    new ApplicationInsertCommand
                    {
                        ApplicationName = "DMS1529 Logged Repeat Key Application",
                        VendorId = _vendorId,
                        ClaimSetName = "TestClaimSet",
                        EducationOrganizationIds = [],
                    },
                    new ApiClientCommand { ClientId = _takenClientId, ClientUuid = Guid.NewGuid() }
                )
            );
            _results.Add(
                await applicationRepository.InsertApplication(
                    new ApplicationInsertCommand
                    {
                        ApplicationName = "DMS1529 Logged Repeat Uuid Application",
                        VendorId = _vendorId,
                        ClaimSetName = "TestClaimSet",
                        EducationOrganizationIds = [],
                    },
                    new ApiClientCommand
                    {
                        ClientId = Guid.NewGuid().ToString(),
                        ClientUuid = _takenClientUuid,
                    }
                )
            );
            _results.Add(
                await applicationRepository.UpdateApplication(
                    new ApplicationUpdateCommand
                    {
                        Id = _secondApplicationId,
                        ApplicationName = "DMS1529 Logged Second Application",
                        VendorId = _vendorId,
                        ClaimSetName = "TestClaimSet",
                        EducationOrganizationIds = [],
                        ProfileIds = [],
                    },
                    new ApiClientCommand { ClientId = _secondClientId, ClientUuid = _takenClientUuid }
                )
            );
            _results.Add(
                await applicationRepository.SyncApplicationApiClientUuid(
                    _secondApplicationId,
                    _secondClientId,
                    _secondClientUuid,
                    _takenClientUuid
                )
            );
        }

        private IEnumerable<(LogLevel Level, string Message, Exception? Exception)> AllEntries =>
            _apiClientLogger.Entries.Concat(_applicationLogger.Entries);

        [Test]
        public void It_runs_against_an_engine_whose_error_reports_the_conflicting_value() =>
            _rawExceptionMessage.Should().Contain(_takenClientId);

        [Test]
        public void It_classifies_every_conflict() =>
            _results
                .Select(result => result.GetType().Name)
                .Should()
                .Equal(
                    nameof(ApiClientInsertResult.FailureDuplicateClientId),
                    nameof(ApiClientInsertResult.FailureDuplicateClientUuid),
                    nameof(ApiClientUpdateResult.FailureDuplicateClientUuid),
                    nameof(ApiClientUuidSyncResult.FailureNewUuidInUse),
                    nameof(ApplicationInsertResult.FailureDuplicateClientId),
                    nameof(ApplicationInsertResult.FailureDuplicateClientUuid),
                    nameof(ApplicationUpdateResult.FailureDuplicateClientUuid),
                    nameof(ApiClientUuidSyncResult.FailureNewUuidInUse)
                );

        [Test]
        public void It_logs_one_warning_per_conflict_and_nothing_at_error()
        {
            _apiClientLogger.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().HaveCount(4);
            _applicationLogger.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().HaveCount(4);
            AllEntries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        }

        [Test]
        public void It_attaches_no_exception_to_any_log_entry() =>
            AllEntries.Should().AllSatisfy(entry => entry.Exception.Should().BeNull());

        [Test]
        public void It_keeps_every_identifier_out_of_the_rendered_messages() =>
            AllEntries
                .Select(entry => entry.Message)
                .Should()
                .OnlyContain(message =>
                    !message.Contains(_takenClientId)
                    && !message.Contains(_secondClientId)
                    && !message.Contains(_takenClientUuid.ToString())
                    && !message.Contains(_secondClientUuid.ToString())
                );

        [Test]
        public void It_names_the_violated_constraint_and_error_number_in_each_warning()
        {
            string[] messages = AllEntries
                .Where(entry => entry.Level == LogLevel.Warning)
                .Select(entry => entry.Message)
                .ToArray();
            messages.Count(message => message.Contains("UX_ApiClient_ClientId")).Should().Be(2);
            messages.Count(message => message.Contains("UX_ApiClient_ClientUuid")).Should().Be(6);
            messages.Should().OnlyContain(message => message.Contains("2627"));
        }
    }
}
