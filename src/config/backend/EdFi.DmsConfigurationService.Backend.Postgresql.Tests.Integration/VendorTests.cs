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

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration
{
    public class VendorTests : DatabaseTest
    {
        private readonly IVendorRepository _repository = new VendorRepository(
            Configuration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            new TenantContextProvider()
        );

        [TestFixture]
        public class InsertTests : VendorTests
        {
            private long _id;

            [SetUp]
            public async Task Setup()
            {
                VendorInsertCommand vendor = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "FakePrefix1,FakePrefix2",
                };

                var result = await _repository.InsertVendor(vendor);
                result.Should().BeOfType<VendorInsertResult.Success>();
                _id = (result as VendorInsertResult.Success)!.Id;
                _id.Should().BeGreaterThan(0);
            }

            [Test]
            public async Task Should_get_test_vendor_from_get_all()
            {
                var getResult = await _repository.QueryVendor(new VendorQuery() { Limit = 25, Offset = 0 });
                getResult.Should().BeOfType<VendorQueryResult.Success>();

                var vendorFromDb = ((VendorQueryResult.Success)getResult).VendorResponses.First();
                vendorFromDb.Company.Should().Be("Test Company");
                vendorFromDb.ContactEmailAddress.Should().Be("test@test.com");
                vendorFromDb.ContactName.Should().Be("Fake Name");
                vendorFromDb.NamespacePrefixes.Split(',').Length.Should().Be(2);
            }

            [Test]
            public async Task Should_get_test_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetVendor(_id));
                getByIdResult.Should().BeOfType<VendorGetResult.Success>();

                var vendorFromDb = ((VendorGetResult.Success)getByIdResult).VendorResponse;
                vendorFromDb.Company.Should().Be("Test Company");
                vendorFromDb.ContactEmailAddress.Should().Be("test@test.com");
                vendorFromDb.ContactName.Should().Be("Fake Name");
                vendorFromDb.NamespacePrefixes.Split(',').Length.Should().Be(2);
            }
        }

        [TestFixture]
        public class UpdateTests : VendorTests
        {
            private VendorInsertCommand _vendorInsert = null!;
            private VendorUpdateCommand _vendorUpdate = null!;

            [SetUp]
            public async Task Setup()
            {
                _vendorInsert = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "",
                };

                _vendorUpdate = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "",
                };

                var insertResult = await _repository.InsertVendor(_vendorInsert);
                insertResult.Should().BeOfType<VendorInsertResult.Success>();

                _vendorUpdate.Id = (insertResult as VendorInsertResult.Success)!.Id;
                _vendorUpdate.Company = "Update Company";
                _vendorUpdate.ContactEmailAddress = "update@update.com";
                _vendorUpdate.ContactName = "Update Name";

                var vendorUpdateResult = await _repository.UpdateVendor(_vendorUpdate);
                vendorUpdateResult.Should().BeOfType<VendorUpdateResult.Success>();
            }

            [Test]
            public async Task Should_get_update_vendor_from_get_all()
            {
                var getResult = await _repository.QueryVendor(new VendorQuery() { Limit = 25, Offset = 0 });
                getResult.Should().BeOfType<VendorQueryResult.Success>();

                var vendorFromDb = ((VendorQueryResult.Success)getResult).VendorResponses.First();
                vendorFromDb.Company.Should().Be("Update Company");
                vendorFromDb.ContactEmailAddress.Should().Be("update@update.com");
                vendorFromDb.ContactName.Should().Be("Update Name");
            }

            [Test]
            public async Task Should_get_update_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetVendor(_vendorUpdate.Id));
                getByIdResult.Should().BeOfType<VendorGetResult.Success>();

                var vendorFromDb = ((VendorGetResult.Success)getByIdResult).VendorResponse;
                vendorFromDb.Company.Should().Be("Update Company");
                vendorFromDb.ContactEmailAddress.Should().Be("update@update.com");
                vendorFromDb.ContactName.Should().Be("Update Name");
            }
        }

        [TestFixture]
        public class DeleteTests : VendorTests
        {
            private long _vendor1Id;
            private long _vendor2Id;

            [SetUp]
            public async Task Setup()
            {
                var insertResult1 = await _repository.InsertVendor(
                    new VendorInsertCommand()
                    {
                        Company = "Test Company 1",
                        ContactEmailAddress = "test1@test.com",
                        ContactName = "Fake Name 1",
                        NamespacePrefixes = "",
                    }
                );

                _vendor1Id = ((VendorInsertResult.Success)insertResult1).Id;

                var insertResult2 = await _repository.InsertVendor(
                    new VendorInsertCommand()
                    {
                        Company = "Test Company 2",
                        ContactEmailAddress = "test2@test.com",
                        ContactName = "Fake Name 2",
                        NamespacePrefixes = "",
                    }
                );

                _vendor2Id = ((VendorInsertResult.Success)insertResult2).Id;

                var deleteResult = await _repository.DeleteVendor(_vendor1Id);
                deleteResult.Should().BeOfType<VendorDeleteResult.Success>();
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_all()
            {
                var getResult = await _repository.QueryVendor(new VendorQuery() { Limit = 25, Offset = 0 });
                getResult.Should().BeOfType<VendorQueryResult.Success>();

                ((VendorQueryResult.Success)getResult).VendorResponses.Count().Should().Be(1);
                ((VendorQueryResult.Success)getResult)
                    .VendorResponses.Count(v => v.Id == _vendor1Id)
                    .Should()
                    .Be(0);
                ((VendorQueryResult.Success)getResult)
                    .VendorResponses.Count(v => v.Company == "Test Company 1")
                    .Should()
                    .Be(0);
                ((VendorQueryResult.Success)getResult)
                    .VendorResponses.Count(v => v.Id == _vendor2Id)
                    .Should()
                    .Be(1);
                ((VendorQueryResult.Success)getResult)
                    .VendorResponses.Count(v => v.Company == "Test Company 2")
                    .Should()
                    .Be(1);
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetVendor(_vendor1Id));
                getByIdResult.Should().BeOfType<VendorGetResult.FailureNotFound>();

                getByIdResult = (await _repository.GetVendor(_vendor2Id));
                getByIdResult.Should().BeOfType<VendorGetResult.Success>();
            }
        }

        [TestFixture]
        public class GetApplicationsByVendorId : VendorTests
        {
            private long _vendorId1;
            private long _vendorId2;
            private readonly long _vendorIdNotExist = 9999;
            private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext()
            );

            [SetUp]
            public async Task Setup()
            {
                var result1 = await _repository.InsertVendor(
                    new()
                    {
                        Company = "Test Company",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name",
                        NamespacePrefixes = "FakePrefix1",
                    }
                );
                _vendorId1 = (result1 as VendorInsertResult.Success)!.Id;
                _vendorId1.Should().BeGreaterThan(0);

                var result2 = await _repository.InsertVendor(
                    new()
                    {
                        Company = "Test Company 2",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name 2",
                        NamespacePrefixes = "FakePrefix1",
                    }
                );
                _vendorId2 = (result2 as VendorInsertResult.Success)!.Id;
                _vendorId2.Should().BeGreaterThan(0);

                await _applicationRepository.InsertApplication(
                    new()
                    {
                        ApplicationName = "Test Application 1",
                        VendorId = _vendorId1,
                        ClaimSetName = "Test Claim set",
                        EducationOrganizationIds = [1, 255911001, 255911002],
                    },
                    new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );

                await _applicationRepository.InsertApplication(
                    new()
                    {
                        ApplicationName = "Test Application 2",
                        VendorId = _vendorId1,
                        ClaimSetName = "Test Claim set 2",
                        EducationOrganizationIds = [1, 255911001, 255911002],
                    },
                    new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
            }

            [Test]
            public async Task Should_return_two_applications_for_vendor_one()
            {
                var getResult = await _repository.GetVendorApplications(_vendorId1);
                getResult.Should().BeOfType<VendorApplicationsResult.Success>();
                var applicationsFromDb = (
                    (VendorApplicationsResult.Success)getResult
                ).ApplicationResponses.ToArray();
                applicationsFromDb.Length.Should().Be(2);
                applicationsFromDb[0].ApplicationName.Should().Be("Test Application 1");
                applicationsFromDb[1].ApplicationName.Should().Be("Test Application 2");
            }

            [Test]
            public async Task Should_return_empty_array_for_vendor_two_without_applications()
            {
                var getResult = await _repository.GetVendorApplications(_vendorId2);
                getResult.Should().BeOfType<VendorApplicationsResult.Success>();
                var applicationsFromDb = (
                    (VendorApplicationsResult.Success)getResult
                ).ApplicationResponses.ToArray();
                applicationsFromDb.Length.Should().Be(0);
            }

            [Test]
            public async Task Should_return_not_found_for_non_existent_vendor()
            {
                var getResult = await _repository.GetVendorApplications(_vendorIdNotExist);
                getResult.Should().BeOfType<VendorApplicationsResult.FailureNotExists>();
            }
        }

        [TestFixture]
        public class QueryPagingTests : VendorTests
        {
            [SetUp]
            public async Task Setup()
            {
                for (int i = 1; i <= 15; i++)
                {
                    VendorInsertCommand vendorCommand = new()
                    {
                        Company = $"Vendor-{i:D2}",
                        ContactEmailAddress = $"vendor{i}@test.com",
                        ContactName = "Paging Tester",
                        NamespacePrefixes = $"uri://vendor-{i}.example",
                    };
                    var insertResult = await _repository.InsertVendor(vendorCommand);
                    insertResult
                        .Should()
                        .BeOfType<VendorInsertResult.Success>(
                            $"vendor {i} (Vendor-{i:D2}) should insert successfully"
                        );
                }
            }

            [Test]
            public async Task Should_return_all_results_when_no_paging_params_provided()
            {
                var result = await _repository.QueryVendor(new VendorQuery());
                result.Should().BeOfType<VendorQueryResult.Success>();
                ((VendorQueryResult.Success)result).VendorResponses.Should().HaveCount(15);
            }

            [Test]
            public async Task Should_apply_limit_when_limit_is_provided()
            {
                var result = await _repository.QueryVendor(new VendorQuery { Limit = 5 });
                result.Should().BeOfType<VendorQueryResult.Success>();
                ((VendorQueryResult.Success)result).VendorResponses.Should().HaveCount(5);
            }

            [Test]
            public async Task Should_apply_offset_when_offset_is_provided()
            {
                var result = await _repository.QueryVendor(new VendorQuery { Offset = 10 });
                result.Should().BeOfType<VendorQueryResult.Success>();
                ((VendorQueryResult.Success)result).VendorResponses.Should().HaveCount(5);
            }

            [Test]
            public async Task Should_apply_descending_default_order_before_paging()
            {
                var result = await _repository.QueryVendor(new VendorQuery { Direction = "DESC", Limit = 5 });
                result.Should().BeOfType<VendorQueryResult.Success>();
                var companies = ((VendorQueryResult.Success)result)
                    .VendorResponses.Select(vendor => vendor.Company)
                    .ToList();

                companies
                    .Should()
                    .ContainInOrder("Vendor-15", "Vendor-14", "Vendor-13", "Vendor-12", "Vendor-11");
            }
        }

        [TestFixture]
        public class QuerySortTests : VendorTests
        {
            [SetUp]
            public async Task Setup()
            {
                foreach (var company in new[] { "Acme Corp", "Beta Inc", "Alpha Systems" })
                {
                    VendorInsertCommand vendorCommand = new()
                    {
                        Company = company,
                        ContactEmailAddress = "sort@test.com",
                        ContactName = "Sort Tester",
                        NamespacePrefixes = $"uri://{company.ToLower().Replace(" ", "-")}.example",
                    };
                    var insertResult = await _repository.InsertVendor(vendorCommand);
                    insertResult
                        .Should()
                        .BeOfType<VendorInsertResult.Success>(
                            $"vendor '{company}' should insert successfully"
                        );
                }
            }

            [Test]
            public async Task Should_return_ascending_order_by_company()
            {
                var result = await _repository.QueryVendor(
                    new VendorQuery { OrderBy = "company", Direction = "ASC" }
                );
                result.Should().BeOfType<VendorQueryResult.Success>();
                var companies = ((VendorQueryResult.Success)result)
                    .VendorResponses.Select(v => v.Company)
                    .ToList();
                companies.Should().HaveCount(3);
                companies.Should().ContainInOrder("Acme Corp", "Alpha Systems", "Beta Inc");
            }

            [Test]
            public async Task Should_return_descending_order_by_company()
            {
                var result = await _repository.QueryVendor(
                    new VendorQuery { OrderBy = "company", Direction = "DESC" }
                );
                result.Should().BeOfType<VendorQueryResult.Success>();
                var companies = ((VendorQueryResult.Success)result)
                    .VendorResponses.Select(v => v.Company)
                    .ToList();
                companies.Should().HaveCount(3);
                companies.Should().ContainInOrder("Beta Inc", "Alpha Systems", "Acme Corp");
            }
        }

        [TestFixture]
        public class Given_VendorApplicationWithDisabledApiClient : VendorTests
        {
            private long _vendorId;
            private long _applicationId;

            private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext()
            );

            private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext()
            );

            [SetUp]
            public async Task Setup()
            {
                var vendorResult = await _repository.InsertVendor(
                    new VendorInsertCommand
                    {
                        Company = "Vendor Disabled Test Company",
                        ContactEmailAddress = "vendordisabled@test.com",
                        ContactName = "Vendor Disabled Test",
                        NamespacePrefixes = "VendorDisabledPrefix",
                    }
                );
                vendorResult.Should().BeOfType<VendorInsertResult.Success>();
                _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

                var appResult = await _applicationRepository.InsertApplication(
                    new ApplicationInsertCommand
                    {
                        ApplicationName = "Vendor Disabled App",
                        VendorId = _vendorId,
                        ClaimSetName = "Vendor Claim Set",
                        EducationOrganizationIds = [],
                    },
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                appResult.Should().BeOfType<ApplicationInsertResult.Success>();
                _applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

                // InsertApplication auto-creates an ApiClient with IsApproved = true.
                // Adding a second client with IsApproved = false makes BOOL_AND return false.
                var clientResult = await _apiClientRepository.InsertApiClient(
                    new ApiClientInsertCommand
                    {
                        ApplicationId = _applicationId,
                        Name = "Disabled Vendor Client",
                        IsApproved = false,
                        DataStoreIds = [],
                    },
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                clientResult.Should().BeOfType<ApiClientInsertResult.Success>();
            }

            [Test]
            public async Task It_should_return_enabled_false_from_GetVendorApplications()
            {
                var result = await _repository.GetVendorApplications(_vendorId);
                result.Should().BeOfType<VendorApplicationsResult.Success>();
                var app = ((VendorApplicationsResult.Success)result).ApplicationResponses.Single(a =>
                    a.Id == _applicationId
                );
                app.Enabled.Should().BeFalse();
            }

            [Test]
            public async Task It_should_return_enabled_true_when_all_api_clients_are_approved()
            {
                // Create a separate application with only the auto-approved client from InsertApplication
                var appResult = await _applicationRepository.InsertApplication(
                    new ApplicationInsertCommand
                    {
                        ApplicationName = "Vendor All Approved App",
                        VendorId = _vendorId,
                        ClaimSetName = "Vendor Claim Set Approved",
                        EducationOrganizationIds = [],
                    },
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                appResult.Should().BeOfType<ApplicationInsertResult.Success>();
                var approvedAppId = (appResult as ApplicationInsertResult.Success)!.Id;

                var result = await _repository.GetVendorApplications(_vendorId);
                result.Should().BeOfType<VendorApplicationsResult.Success>();
                var app = ((VendorApplicationsResult.Success)result).ApplicationResponses.Single(a =>
                    a.Id == approvedAppId
                );
                app.Enabled.Should().BeTrue();
            }
        }
    }
}
