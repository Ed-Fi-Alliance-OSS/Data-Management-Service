// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Numerics;
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
                    ContactName = "Fake Name"
                };

                _id = await _repository.AddAsync(vendor);
                _id.Should().BeGreaterThan(0);
            }

            [Test]
            public async Task Should_get_test_vendor_from_get_all()
            {
                var vendorFromDb = (await _repository.GetAllAsync()).First();
                vendorFromDb.Company.Should().Be("Test Company");
                vendorFromDb.ContactEmailAddress.Should().Be("test@test.com");
                vendorFromDb.ContactName.Should().Be("Fake Name");
            }

            [Test]
            public async Task Should_get_test_vendor_from_get_by_id()
            {
                var vendorFromDb = (await _repository.GetByIdAsync(_id));
                Debug.Assert(vendorFromDb != null);
                vendorFromDb.Company.Should().Be("Test Company");
                vendorFromDb.ContactEmailAddress.Should().Be("test@test.com");
                vendorFromDb.ContactName.Should().Be("Fake Name");
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
                    ContactName = "Fake Name"
                };

                vendor.Id = await _repository.AddAsync(vendor);

                vendor.Company = "Update Company";
                vendor.ContactEmailAddress = "update@update.com";
                vendor.ContactName = "Update Name";

                var success = await _repository.UpdateAsync(vendor);
                success.Should().BeTrue();
            }

            [Test]
            public async Task Should_get_update_vendor_from_get_all()
            {
                var vendorFromDb = (await _repository.GetAllAsync()).First();
                vendorFromDb.Company.Should().Be("Update Company");
                vendorFromDb.ContactEmailAddress.Should().Be("update@update.com");
                vendorFromDb.ContactName.Should().Be("Update Name");
            }

            [Test]
            public async Task Should_get_update_vendor_from_get_by_id()
            {
                var vendorFromDb = (await _repository.GetByIdAsync(vendor.Id));
                Debug.Assert(vendorFromDb != null);
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
                    ContactName = "Fake Name 1"
                };

                vendor1.Id = await _repository.AddAsync(vendor1);

                vendor2 = new()
                {
                    Company = "Test Company 2",
                    ContactEmailAddress = "test2@test.com",
                    ContactName = "Fake Name 2"
                };

                vendor2.Id = await _repository.AddAsync(vendor2);

                var success = await _repository.DeleteAsync(vendor1.Id);
                success.Should().BeTrue();
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_all()
            {
                var vendorsFromDb = (await _repository.GetAllAsync());
                vendorsFromDb.Count.Should().Be(1);
                vendorsFromDb.Count(v => v.Id == vendor1.Id).Should().Be(0);
                vendorsFromDb.Count(v => v.Company == "Test Company 1").Should().Be(0);
                vendorsFromDb.Count(v => v.Id == vendor2.Id).Should().Be(1);
                vendorsFromDb.Count(v => v.Company == "Test Company 2").Should().Be(1);
            }

            [Test]
            public async Task Should_not_get_test_vendor_from_get_by_id()
            {
                var vendorFromDb = (await _repository.GetByIdAsync(vendor1.Id));
                vendorFromDb.Should().Be(null);
            }
        }
    }
}
