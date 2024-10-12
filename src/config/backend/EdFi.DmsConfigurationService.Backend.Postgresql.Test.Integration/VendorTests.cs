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
        private readonly IRepository<Vendor> _repository = new VendorRepository(Configuration.DatabaseOptions);

            [TestFixture]
        public class InsertTests : VendorTests
        {
            [SetUp]
            public async Task Setup()
            {
                Vendor vendor = new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name"
                };

                var success = await _repository.AddAsync(vendor);
                success.Should().BeTrue();
            }

            [Test]
            public async Task Should_get_test_company_from_get_all()
            {
                var vendor = (await _repository.GetAllAsync()).First();
                vendor.Company.Should().Be("Test Company");
                vendor.ContactEmailAddress.Should().Be("test@test.com");
                vendor.ContactName.Should().Be("Fake Name");
            }
        }
    }
}
