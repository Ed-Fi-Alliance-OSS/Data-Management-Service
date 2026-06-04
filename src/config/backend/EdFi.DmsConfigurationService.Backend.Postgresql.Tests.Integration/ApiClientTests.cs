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
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class ApiClientTests : DatabaseTest
{
    private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApiClientRepository>.Instance,
        new TestAuditContext()
    );

    private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance,
        new TestAuditContext()
    );

    [TestFixture]
    public class QueryPagingTests : ApiClientTests
    {
        private long _applicationId;

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
}
