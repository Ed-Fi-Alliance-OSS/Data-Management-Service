// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
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
        private long _applicationId;

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
            long vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

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
        private long _applicationId;

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
            long vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

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
            long vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

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
            long applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

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
            long apiClientId = (insertResult as ApiClientInsertResult.Success)!.Id;

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
        private long _tenantAApplicationId;
        private long _tenantBApplicationId;
        private long _tenantAApiClientId;
        private string _tenantAClientId = string.Empty;
        private long _tenantBApiClientId;
        private long _tenantADataStoreId;

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

        private static async Task<long> InsertVendorWithApplication(
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
            long vendorId = ((VendorInsertResult.Success)vendorResult).Id;

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

        private static async Task<(long Id, string ClientId)> InsertApiClient(
            IApiClientRepository apiClientRepository,
            long applicationId,
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

        private static async Task<long> InsertDataStore(
            TenantContextProvider tenantContextProvider,
            string name
        )
        {
            var dataStoreRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
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
}
