// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration
{
    public class VendorTests : DatabaseTest
    {
        private readonly IVendorRepository _repository = new VendorRepository(
            Configuration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance
        );

        [TestFixture]
        public class InsertTests : VendorTests
        {
            private long _id;

            [SetUp]
            public async Task Setup()
            {
                VendorInsertCommand vendor =
                    new()
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
                var getResult = await _repository.QueryVendor(new PagingQuery() { Limit = 25, Offset = 0 });
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
                var getResult = await _repository.QueryVendor(new PagingQuery() { Limit = 25, Offset = 0 });
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
                var getResult = await _repository.QueryVendor(new PagingQuery() { Limit = 25, Offset = 0 });
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
            private long _vendorIdNotExist = 9999;
            private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance
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
    }
}
