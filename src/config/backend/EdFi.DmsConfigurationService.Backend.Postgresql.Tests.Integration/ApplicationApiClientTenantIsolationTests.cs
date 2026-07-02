// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
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

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class Given_application_and_api_client_data_in_two_tenants : DatabaseTest
{
    private static ApplicationRepository CreateApplicationRepository(
        TenantContextProvider tenantContextProvider
    ) =>
        new(
            Configuration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );

    private static ApiClientRepository CreateApiClientRepository(
        TenantContextProvider tenantContextProvider
    ) =>
        new(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );

    private static VendorRepository CreateVendorRepository(TenantContextProvider tenantContextProvider) =>
        new(
            Configuration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );

    private static DataStoreRepository CreateDataStoreRepository(TenantContextProvider tenantContextProvider)
    {
        var contextRepository = new DataStoreContextRepository(
            Configuration.DatabaseOptions,
            NullLogger<DataStoreContextRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );
        var derivativeRepository = new DataStoreDerivativeRepository(
            Configuration.DatabaseOptions,
            NullLogger<DataStoreDerivativeRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
            new TestAuditContext()
        );

        return new(
            Configuration.DatabaseOptions,
            NullLogger<DataStoreRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
            contextRepository,
            derivativeRepository,
            new TestAuditContext(),
            tenantContextProvider
        );
    }

    private static async Task<TenantContextProvider> CreateTenantProvider(string namePrefix)
    {
        var tenantRepository = new TenantRepository(
            Configuration.DatabaseOptions,
            NullLogger<TenantRepository>.Instance,
            new TestAuditContext()
        );

        string tenantName = $"{namePrefix}_{Guid.NewGuid():N}";
        var result = await tenantRepository.InsertTenant(new TenantInsertCommand { Name = tenantName });
        result.Should().BeOfType<TenantInsertResult.Success>();
        long tenantId = ((TenantInsertResult.Success)result).Id;

        return new TenantContextProvider { Context = new TenantContext.Multitenant(tenantId, tenantName) };
    }

    private static async Task<long> CreateVendor(VendorRepository vendorRepository, string companyName)
    {
        var result = await vendorRepository.InsertVendor(
            new VendorInsertCommand
            {
                Company = companyName,
                ContactEmailAddress = $"{companyName}@example.com",
                ContactName = companyName,
                NamespacePrefixes = $"uri://{companyName}.example",
            }
        );

        result.Should().BeOfType<VendorInsertResult.Success>();
        return ((VendorInsertResult.Success)result).Id;
    }

    private static async Task<long> CreateDataStore(
        DataStoreRepository dataStoreRepository,
        string dataStoreName
    )
    {
        var result = await dataStoreRepository.InsertDataStore(
            new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = dataStoreName,
                ConnectionString = $"Server={dataStoreName};Database={dataStoreName};",
            }
        );

        result.Should().BeOfType<DataStoreInsertResult.Success>();
        return ((DataStoreInsertResult.Success)result).Id;
    }

    private static async Task<long> CreateApplication(
        ApplicationRepository applicationRepository,
        long vendorId,
        string applicationName,
        long[]? dataStoreIds = null
    )
    {
        var result = await applicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = applicationName,
                VendorId = vendorId,
                ClaimSetName = $"{applicationName}ClaimSet",
                EducationOrganizationIds = [1, 2],
                DataStoreIds = dataStoreIds ?? [],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );

        result.Should().BeOfType<ApplicationInsertResult.Success>();
        return ((ApplicationInsertResult.Success)result).Id;
    }

    private static async Task<long> CreateApiClient(
        ApiClientRepository apiClientRepository,
        long applicationId,
        string name,
        long[] dataStoreIds
    )
    {
        var result = await apiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = applicationId,
                Name = name,
                IsApproved = true,
                DataStoreIds = dataStoreIds,
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );

        result.Should().BeOfType<ApiClientInsertResult.Success>();
        return ((ApiClientInsertResult.Success)result).Id;
    }

    private ApplicationRepository _tenantAApplicationRepository = null!;
    private ApplicationRepository _tenantBApplicationRepository = null!;
    private ApiClientRepository _tenantAApiClientRepository = null!;
    private ApiClientRepository _tenantBApiClientRepository = null!;
    private long _tenantAVendorId;
    private long _tenantBVendorId;
    private long _tenantADataStoreId;
    private long _tenantBDataStoreId;
    private long _tenantAApplicationId;
    private long _tenantBApplicationId;
    private long _tenantAApiClientId;
    private long _tenantBApiClientId;

    [SetUp]
    public async Task Setup()
    {
        TenantContextProvider tenantAProvider = await CreateTenantProvider("DMS1244_TenantA");
        TenantContextProvider tenantBProvider = await CreateTenantProvider("DMS1244_TenantB");

        var tenantAVendorRepository = CreateVendorRepository(tenantAProvider);
        var tenantBVendorRepository = CreateVendorRepository(tenantBProvider);
        var tenantADataStoreRepository = CreateDataStoreRepository(tenantAProvider);
        var tenantBDataStoreRepository = CreateDataStoreRepository(tenantBProvider);

        _tenantAApplicationRepository = CreateApplicationRepository(tenantAProvider);
        _tenantBApplicationRepository = CreateApplicationRepository(tenantBProvider);
        _tenantAApiClientRepository = CreateApiClientRepository(tenantAProvider);
        _tenantBApiClientRepository = CreateApiClientRepository(tenantBProvider);

        _tenantAVendorId = await CreateVendor(tenantAVendorRepository, "DMS1244TenantACompany");
        _tenantBVendorId = await CreateVendor(tenantBVendorRepository, "DMS1244TenantBCompany");
        _tenantADataStoreId = await CreateDataStore(tenantADataStoreRepository, "DMS1244TenantADataStore");
        _tenantBDataStoreId = await CreateDataStore(tenantBDataStoreRepository, "DMS1244TenantBDataStore");

        _tenantAApplicationId = await CreateApplication(
            _tenantAApplicationRepository,
            _tenantAVendorId,
            "DMS1244TenantAApplication",
            [_tenantADataStoreId]
        );
        _tenantBApplicationId = await CreateApplication(
            _tenantBApplicationRepository,
            _tenantBVendorId,
            "DMS1244TenantBApplication",
            [_tenantBDataStoreId]
        );
        _tenantAApiClientId = await CreateApiClient(
            _tenantAApiClientRepository,
            _tenantAApplicationId,
            "DMS1244TenantAApiClient",
            [_tenantADataStoreId]
        );
        _tenantBApiClientId = await CreateApiClient(
            _tenantBApiClientRepository,
            _tenantBApplicationId,
            "DMS1244TenantBApiClient",
            [_tenantBDataStoreId]
        );
    }

    [Test]
    public async Task It_should_not_return_application_rows_owned_by_another_tenant()
    {
        var queryResult = await _tenantAApplicationRepository.QueryApplication(new ApplicationQuery());
        queryResult.Should().BeOfType<ApplicationQueryResult.Success>();
        ((ApplicationQueryResult.Success)queryResult)
            .ApplicationResponses.Select(application => application.Id)
            .Should()
            .NotContain(_tenantBApplicationId);

        var getResult = await _tenantAApplicationRepository.GetApplication(_tenantBApplicationId);
        getResult.Should().BeOfType<ApplicationGetResult.FailureNotFound>();

        var clientsResult = await _tenantAApplicationRepository.GetApplicationApiClients(
            _tenantBApplicationId
        );
        clientsResult.Should().BeOfType<ApplicationApiClientsResult.Success>();
        ((ApplicationApiClientsResult.Success)clientsResult).Clients.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_not_mutate_application_rows_owned_by_another_tenant()
    {
        var updateResult = await _tenantAApplicationRepository.UpdateApplication(
            new ApplicationUpdateCommand
            {
                Id = _tenantBApplicationId,
                ApplicationName = "DMS1244TenantBApplicationUpdatedByTenantA",
                VendorId = _tenantBVendorId,
                ClaimSetName = "DMS1244TenantBClaimSet",
                EducationOrganizationIds = [],
                DataStoreIds = [_tenantBDataStoreId],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        updateResult.Should().BeOfType<ApplicationUpdateResult.FailureNotExists>();

        var deleteResult = await _tenantAApplicationRepository.DeleteApplication(_tenantBApplicationId);
        deleteResult.Should().BeOfType<ApplicationDeleteResult.FailureNotExists>();

        var tenantBGetResult = await _tenantBApplicationRepository.GetApplication(_tenantBApplicationId);
        tenantBGetResult.Should().BeOfType<ApplicationGetResult.Success>();
    }

    [Test]
    public async Task It_should_reject_cross_tenant_application_vendor_and_datastore_references()
    {
        var moveToOtherTenantVendorResult = await _tenantAApplicationRepository.UpdateApplication(
            new ApplicationUpdateCommand
            {
                Id = _tenantAApplicationId,
                ApplicationName = "DMS1244TenantAApplicationMoved",
                VendorId = _tenantBVendorId,
                ClaimSetName = "DMS1244TenantAClaimSet",
                EducationOrganizationIds = [],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        moveToOtherTenantVendorResult.Should().BeOfType<ApplicationUpdateResult.FailureVendorNotFound>();

        var insertWithOtherTenantDataStoreResult = await _tenantAApplicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = "DMS1244TenantAApplicationWithTenantBDataStore",
                VendorId = _tenantAVendorId,
                ClaimSetName = "DMS1244TenantAClaimSet",
                EducationOrganizationIds = [],
                DataStoreIds = [_tenantBDataStoreId],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        insertWithOtherTenantDataStoreResult
            .Should()
            .BeOfType<ApplicationInsertResult.FailureDataStoreNotFound>();
    }

    [Test]
    public async Task It_should_not_return_or_mutate_api_client_rows_owned_by_another_tenant()
    {
        var queryResult = await _tenantAApiClientRepository.QueryApiClient(new ApiClientQuery());
        queryResult.Should().BeOfType<ApiClientQueryResult.Success>();
        ((ApiClientQueryResult.Success)queryResult)
            .ApiClientResponses.Select(apiClient => apiClient.Id)
            .Should()
            .NotContain(_tenantBApiClientId);

        var getByIdResult = await _tenantAApiClientRepository.GetApiClientById(_tenantBApiClientId);
        getByIdResult.Should().BeOfType<ApiClientGetResult.FailureNotFound>();

        var updateResult = await _tenantAApiClientRepository.UpdateApiClient(
            new ApiClientUpdateCommand
            {
                Id = _tenantBApiClientId,
                ApplicationId = _tenantBApplicationId,
                Name = "DMS1244TenantBApiClientUpdatedByTenantA",
                IsApproved = false,
                DataStoreIds = [_tenantBDataStoreId],
            }
        );
        updateResult.Should().BeOfType<ApiClientUpdateResult.FailureNotFound>();

        var deleteResult = await _tenantAApiClientRepository.DeleteApiClient(_tenantBApiClientId);
        deleteResult.Should().BeOfType<ApiClientDeleteResult.FailureNotFound>();

        var tenantBGetResult = await _tenantBApiClientRepository.GetApiClientById(_tenantBApiClientId);
        tenantBGetResult.Should().BeOfType<ApiClientGetResult.Success>();
    }

    [Test]
    public async Task It_should_reject_cross_tenant_api_client_application_and_datastore_references()
    {
        var insertForOtherTenantApplicationResult = await _tenantAApiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = _tenantBApplicationId,
                Name = "DMS1244TenantAClientForTenantBApplication",
                IsApproved = true,
                DataStoreIds = [_tenantADataStoreId],
            },
            new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
        );
        insertForOtherTenantApplicationResult
            .Should()
            .BeOfType<ApiClientInsertResult.FailureApplicationNotFound>();

        var updateToOtherTenantApplicationResult = await _tenantAApiClientRepository.UpdateApiClient(
            new ApiClientUpdateCommand
            {
                Id = _tenantAApiClientId,
                ApplicationId = _tenantBApplicationId,
                Name = "DMS1244TenantAClientMovedToTenantBApplication",
                IsApproved = true,
                DataStoreIds = [_tenantADataStoreId],
            }
        );
        updateToOtherTenantApplicationResult
            .Should()
            .BeOfType<ApiClientUpdateResult.FailureApplicationNotFound>();

        var updateToOtherTenantDataStoreResult = await _tenantAApiClientRepository.UpdateApiClient(
            new ApiClientUpdateCommand
            {
                Id = _tenantAApiClientId,
                ApplicationId = _tenantAApplicationId,
                Name = "DMS1244TenantAClientWithTenantBDataStore",
                IsApproved = true,
                DataStoreIds = [_tenantBDataStoreId],
            }
        );
        updateToOtherTenantDataStoreResult
            .Should()
            .BeOfType<ApiClientUpdateResult.FailureDataStoreNotFound>();
    }
}
