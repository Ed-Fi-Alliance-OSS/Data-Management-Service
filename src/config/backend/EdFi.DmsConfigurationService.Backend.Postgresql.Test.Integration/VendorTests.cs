// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration
{
    public class VendorTests : DatabaseTest
    {
        private readonly IRepository<Vendor>
            _repository = new VendorRepository(Configuration.DatabaseOptions);

        [TestFixture]
        public class InsertTests : VendorTests
        {
            private long _id = 0;

            [SetUp]
            public async Task Setup()
            {
                Vendor vendor = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = ["FakePrefix1", "FakePrefix2"]
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
            private Vendor vendor;

            [SetUp]
            public async Task Setup()
            {
                vendor = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = []
                };

                var insertResult = await _repository.AddAsync(vendor);
                insertResult.Should().BeOfType<InsertResult.InsertSuccess>();

                vendor.Id = (insertResult as InsertResult.InsertSuccess)!.Id;
                vendor.Company = "Update Company";
                vendor.ContactEmailAddress = "update@update.com";
                vendor.ContactName = "Update Name";

                var updateResult = await _repository.UpdateAsync(vendor);
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
                var getByIdResult = (await _repository.GetByIdAsync(vendor.Id));
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
            private Vendor vendor1;
            private Vendor vendor2;

            [SetUp]
            public async Task Setup()
            {
                vendor1 = new()
                {
                    Company = "Test Company 1",
                    ContactEmailAddress = "test1@test.com",
                    ContactName = "Fake Name 1",
                    NamespacePrefixes = []
                };

                var insertResult1 = await _repository.AddAsync(vendor1);
                insertResult1.Should().BeOfType<InsertResult.InsertSuccess>();

                vendor1.Id = ((InsertResult.InsertSuccess)insertResult1).Id;

                vendor2 = new()
                {
                    Company = "Test Company 2",
                    ContactEmailAddress = "test2@test.com",
                    ContactName = "Fake Name 2",
                    NamespacePrefixes = []
                };

                var insertResult2 = await _repository.AddAsync(vendor2);
                insertResult2.Should().BeOfType<InsertResult.InsertSuccess>();

                vendor2.Id = ((InsertResult.InsertSuccess)insertResult2).Id;

                var deleteResult = await _repository.DeleteAsync(vendor1.Id);
                deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_all()
            {
                var getResult = await _repository.GetAllAsync();
                getResult.Should().BeOfType<GetResult<Vendor>.GetSuccess>();

                ((GetResult<Vendor>.GetSuccess)getResult).Results.Count.Should().Be(1);
                ((GetResult<Vendor>.GetSuccess)getResult).Results.Count(v => v.Id == vendor1.Id).Should().Be(0);
                ((GetResult<Vendor>.GetSuccess)getResult).Results.Count(v => v.Company == "Test Company 1").Should().Be(0);
                ((GetResult<Vendor>.GetSuccess)getResult).Results.Count(v => v.Id == vendor2.Id).Should().Be(1);
                ((GetResult<Vendor>.GetSuccess)getResult).Results.Count(v => v.Company == "Test Company 2").Should().Be(1);
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_by_id()
            {
                var getByIdResult = (await _repository.GetByIdAsync(vendor1.Id));
                getByIdResult.Should().BeOfType<GetResult<Vendor>.GetByIdFailureNotExists>();

                getByIdResult = (await _repository.GetByIdAsync(vendor2.Id));
                getByIdResult.Should().BeOfType<GetResult<Vendor>.GetByIdSuccess>();
            }
        }
    }
}
