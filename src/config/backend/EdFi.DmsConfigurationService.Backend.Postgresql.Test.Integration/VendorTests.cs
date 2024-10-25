// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repository;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration
{
    public class VendorTests : DatabaseTest
    {
        private readonly IVendorRepository _repository = new VendorRepository(Configuration.DatabaseOptions);

        [TestFixture]
        public class InsertTests : VendorTests
        {
            private long _id;

            [SetUp]
            public async Task Setup()
            {
                Vendor vendor =
                    new()
                    {
                        Company = "Test Company",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name",
                        NamespacePrefixes = ["FakePrefix1", "FakePrefix2"],
                    };

                var result = await _repository.AddAsync(vendor);
                result.Should().BeOfType<InsertResult.InsertSuccess>();
                _id = (result as InsertResult.InsertSuccess)!.Id;
                _id.Should().BeGreaterThan(0);
            }

            [Test]
            public async Task Should_get_test_vendor_from_get_all()
            {
                var getResult = await _repository.GetAllAsync();
                getResult.Should().BeOfType<GetResult<Vendor>.GetSuccess>();

                var vendorFromDb = ((GetResult<Vendor>.GetSuccess)getResult).Results.First();
                vendorFromDb.Company.Should().Be("Test Company");
                vendorFromDb.ContactEmailAddress.Should().Be("test@test.com");
                vendorFromDb.ContactName.Should().Be("Fake Name");
                vendorFromDb.NamespacePrefixes.Count.Should().Be(2);
            }

            [Test]
            public async Task Should_get_test_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetByIdAsync(_id));
                getByIdResult.Should().BeOfType<GetResult<Vendor>.GetByIdSuccess>();

                var vendorFromDb = ((GetResult<Vendor>.GetByIdSuccess)getByIdResult).Result;
                vendorFromDb.Company.Should().Be("Test Company");
                vendorFromDb.ContactEmailAddress.Should().Be("test@test.com");
                vendorFromDb.ContactName.Should().Be("Fake Name");
                vendorFromDb.NamespacePrefixes.Count.Should().Be(2);
            }
        }

        [TestFixture]
        public class UpdateTests : VendorTests
        {
            private Vendor _vendor = null!;

            [SetUp]
            public async Task Setup()
            {
                _vendor = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = [],
                };

                var insertResult = await _repository.AddAsync(_vendor);
                insertResult.Should().BeOfType<InsertResult.InsertSuccess>();

                _vendor.Id = (insertResult as InsertResult.InsertSuccess)!.Id;
                _vendor.Company = "Update Company";
                _vendor.ContactEmailAddress = "update@update.com";
                _vendor.ContactName = "Update Name";

                var updateResult = await _repository.UpdateAsync(_vendor);
                updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
                ((UpdateResult.UpdateSuccess)updateResult).RecordsUpdated.Should().Be(1);
            }

            [Test]
            public async Task Should_get_update_vendor_from_get_all()
            {
                var getResult = await _repository.GetAllAsync();
                getResult.Should().BeOfType<GetResult<Vendor>.GetSuccess>();

                var vendorFromDb = ((GetResult<Vendor>.GetSuccess)getResult).Results.First();
                vendorFromDb.Company.Should().Be("Update Company");
                vendorFromDb.ContactEmailAddress.Should().Be("update@update.com");
                vendorFromDb.ContactName.Should().Be("Update Name");
            }

            [Test]
            public async Task Should_get_update_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetByIdAsync(_vendor.Id.GetValueOrDefault()));
                getByIdResult.Should().BeOfType<GetResult<Vendor>.GetByIdSuccess>();

                var vendorFromDb = ((GetResult<Vendor>.GetByIdSuccess)getByIdResult).Result;
                vendorFromDb.Company.Should().Be("Update Company");
                vendorFromDb.ContactEmailAddress.Should().Be("update@update.com");
                vendorFromDb.ContactName.Should().Be("Update Name");
            }
        }

        [TestFixture]
        public class DeleteTests : VendorTests
        {
            private Vendor _vendor1 = null!;
            private Vendor _vendor2 = null!;

            [SetUp]
            public async Task Setup()
            {
                _vendor1 = new()
                {
                    Company = "Test Company 1",
                    ContactEmailAddress = "test1@test.com",
                    ContactName = "Fake Name 1",
                    NamespacePrefixes = [],
                };

                var insertResult1 = await _repository.AddAsync(_vendor1);
                insertResult1.Should().BeOfType<InsertResult.InsertSuccess>();

                _vendor1.Id = ((InsertResult.InsertSuccess)insertResult1).Id;

                _vendor2 = new()
                {
                    Company = "Test Company 2",
                    ContactEmailAddress = "test2@test.com",
                    ContactName = "Fake Name 2",
                    NamespacePrefixes = [],
                };

                var insertResult2 = await _repository.AddAsync(_vendor2);
                insertResult2.Should().BeOfType<InsertResult.InsertSuccess>();

                _vendor2.Id = ((InsertResult.InsertSuccess)insertResult2).Id;

                var deleteResult = await _repository.DeleteAsync(_vendor1.Id.GetValueOrDefault());
                deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_all()
            {
                var getResult = await _repository.GetAllAsync();
                getResult.Should().BeOfType<GetResult<Vendor>.GetSuccess>();

                ((GetResult<Vendor>.GetSuccess)getResult).Results.Count.Should().Be(1);
                ((GetResult<Vendor>.GetSuccess)getResult)
                    .Results.Count(v => v.Id == _vendor1.Id.GetValueOrDefault())
                    .Should()
                    .Be(0);
                ((GetResult<Vendor>.GetSuccess)getResult)
                    .Results.Count(v => v.Company == "Test Company 1")
                    .Should()
                    .Be(0);
                ((GetResult<Vendor>.GetSuccess)getResult)
                    .Results.Count(v => v.Id == _vendor2.Id.GetValueOrDefault())
                    .Should()
                    .Be(1);
                ((GetResult<Vendor>.GetSuccess)getResult)
                    .Results.Count(v => v.Company == "Test Company 2")
                    .Should()
                    .Be(1);
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetByIdAsync(_vendor1.Id.GetValueOrDefault()));
                getByIdResult.Should().BeOfType<GetResult<Vendor>.GetByIdFailureNotExists>();

                getByIdResult = (await _repository.GetByIdAsync(_vendor2.Id.GetValueOrDefault()));
                getByIdResult.Should().BeOfType<GetResult<Vendor>.GetByIdSuccess>();
            }
        }

        [TestFixture]
        public class GetApplicationsByVendorId : VendorTests
        {
            private long _vendorId1;
            private long _vendorId2;
            private long _vendorIdNotExist = 9999;
            private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions
            );

            [SetUp]
            public async Task Setup()
            {
                Vendor vendor1 =
                    new()
                    {
                        Company = "Test Company",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name",
                        NamespacePrefixes = ["FakePrefix1"],
                    };

                var result1 = await _repository.AddAsync(vendor1);
                result1.Should().BeOfType<InsertResult.InsertSuccess>();
                _vendorId1 = (result1 as InsertResult.InsertSuccess)!.Id;
                _vendorId1.Should().BeGreaterThan(0);

                Vendor vendor2 =
                    new()
                    {
                        Company = "Test Company 2",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name 2",
                        NamespacePrefixes = ["FakePrefix1"],
                    };

                var result2 = await _repository.AddAsync(vendor2);
                result2.Should().BeOfType<InsertResult.InsertSuccess>();
                _vendorId2 = (result2 as InsertResult.InsertSuccess)!.Id;
                _vendorId2.Should().BeGreaterThan(0);

                await _applicationRepository.InsertApplication(
                    new()
                    {
                        ApplicationName = "Test Application 1",
                        VendorId = _vendorId1,
                        ClaimSetName = "Test Claim set",
                        EducationOrganizationIds = [1, 255911001, 255911002],
                    },
                    Guid.Empty,
                    ""
                );

                await _applicationRepository.InsertApplication(
                    new()
                    {
                        ApplicationName = "Test Application 2",
                        VendorId = _vendorId1,
                        ClaimSetName = "Test Claim set 2",
                        EducationOrganizationIds = [1, 255911001, 255911002],
                    },
                    Guid.Empty,
                    ""
                );
            }

            [Test]
            public async Task Should_return_two_applications_for_vendor_one()
            {
                var getResult = await _repository.GetVendorByIdWithApplicationsAsync(_vendorId1);
                getResult.Should().BeOfType<GetResult<Vendor>.GetByIdSuccess>();
                var applicationsFromDb = ((GetResult<Vendor>.GetByIdSuccess)getResult).Result.Applications;
                applicationsFromDb.Count.Should().Be(2);
                applicationsFromDb[0].ApplicationName.Should().Be("Test Application 1");
                applicationsFromDb[1].ApplicationName.Should().Be("Test Application 2");
            }

            [Test]
            public async Task Should_return_empty_array_for_vendor_two_without_applications()
            {
                var getResult = await _repository.GetVendorByIdWithApplicationsAsync(_vendorId2);
                getResult.Should().BeOfType<GetResult<Vendor>.GetByIdSuccess>();
                var applicationsFromDb = ((GetResult<Vendor>.GetByIdSuccess)getResult).Result.Applications;
                applicationsFromDb.Count.Should().Be(0);
            }

            [Test]
            public async Task Should_return_not_found_for_non_existent_vendor()
            {
                var getResult = await _repository.GetVendorByIdWithApplicationsAsync(_vendorIdNotExist);
                getResult.Should().BeOfType<GetResult<Vendor>.GetByIdFailureNotExists>();
            }
        }
    }
}
