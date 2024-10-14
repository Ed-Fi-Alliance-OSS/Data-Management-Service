// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Numerics;
using EdFi.DmsConfigurationService.DataModel;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public class ApplicationTests : DatabaseTest
{
    private readonly IRepository<Application> _repository = new ApplicationRepository(
        Configuration.DatabaseOptions
    );
    private long _vendorId;

    [TestFixture]
    public class InsertTest : ApplicationTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            IRepository<Vendor> vendorRepository = new VendorRepository(Configuration.DatabaseOptions);

            Vendor vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = ["FakePrefix1", "FakePrefix2"]
                };

            var vendorResult = await vendorRepository.AddAsync(vendor);
            vendorResult.Should().BeOfType<InsertResult.InsertSuccess>();
            _vendorId = (vendorResult as InsertResult.InsertSuccess)!.Id;

            Application application =
                new()
                {
                    ApplicationName = "Test Application",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim set",
                    ApplicationEducationOrganizations = [1, 255911001, 255911002]
                };

            var result = await _repository.AddAsync(application);
            result.Should().BeOfType<InsertResult.InsertSuccess>();
            _id = (result as InsertResult.InsertSuccess)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_test_application_from_get_all()
        {
            var getResult = await _repository.GetAllAsync();
            getResult.Should().BeOfType<GetResult<Application>.GetSuccess>();

            var application = ((GetResult<Application>.GetSuccess)getResult).Results.First();
            application.ApplicationName.Should().Be("Test Application");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.ApplicationEducationOrganizations.Count.Should().Be(3);
        }

        [Test]
        public async Task Should_get_test_application_from_get_by_id()
        {
            var getByIdResult = (await _repository.GetByIdAsync(_id));
            getByIdResult.Should().BeOfType<GetResult<Application>.GetByIdSuccess>();

            var application = ((GetResult<Application>.GetByIdSuccess)getByIdResult).Result;
            application.ApplicationName.Should().Be("Test Application");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.ApplicationEducationOrganizations.Count.Should().Be(3);
        }
    }

    [TestFixture]
    public class UpdateTests : ApplicationTests
    {
        private Application _application = null!;

        [SetUp]
        public async Task SetUp()
        {
            IRepository<Vendor> vendorRepository = new VendorRepository(Configuration.DatabaseOptions);

            Vendor vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = []
                };

            var vendorResult = await vendorRepository.AddAsync(vendor);
            vendorResult.Should().BeOfType<InsertResult.InsertSuccess>();
            _vendorId = (vendorResult as InsertResult.InsertSuccess)!.Id;

            _application = new()
            {
                ApplicationName = "Test Application",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set",
                ApplicationEducationOrganizations = []
            };

            var insertResult = await _repository.AddAsync(_application);
            insertResult.Should().BeOfType<InsertResult.InsertSuccess>();

            _application.Id = (insertResult as InsertResult.InsertSuccess)!.Id;
            _application.ApplicationName = "Update Application Name";
            _application.ApplicationEducationOrganizations = [1, 2];

            var updateResult = await _repository.UpdateAsync(_application);
            updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
            ((UpdateResult.UpdateSuccess)updateResult).RecordsUpdated.Should().Be(1);
        }

        [Test]
        public async Task Should_get_update_application_from_get_all()
        {
            var getResult = await _repository.GetAllAsync();
            getResult.Should().BeOfType<GetResult<Application>.GetSuccess>();

            var applicationFromDb = ((GetResult<Application>.GetSuccess)getResult).Results.First();
            applicationFromDb.ApplicationName.Should().NotBe("Test Application");
            applicationFromDb.ApplicationEducationOrganizations.Count.Should().Be(2);
        }

        [Test]
        public async Task Should_get_update_application_from_get_by_id()
        {
            var getByIdResult = (await _repository.GetByIdAsync(_application.Id));
            getByIdResult.Should().BeOfType<GetResult<Application>.GetByIdSuccess>();

            var applicationFromDb = ((GetResult<Application>.GetByIdSuccess)getByIdResult).Result;
            applicationFromDb.ApplicationName.Should().Be("Update Application Name");
            applicationFromDb.ClaimSetName.Should().Be("Test Claim set");
        }
    }

    [TestFixture]
    public class DeleteTests : ApplicationTests
    {
        private Application _application1 = null!;
        private Application _application2 = null!;

        [SetUp]
        public async Task SetUp()
        {
            IRepository<Vendor> vendorRepository = new VendorRepository(Configuration.DatabaseOptions);

            Vendor vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = []
                };

            var vendorResult = await vendorRepository.AddAsync(vendor);
            vendorResult.Should().BeOfType<InsertResult.InsertSuccess>();
            _vendorId = (vendorResult as InsertResult.InsertSuccess)!.Id;

            _application1 = new()
            {
                ApplicationName = "Application One",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set",
                ApplicationEducationOrganizations = [1, 2]
            };

            var insertResult = await _repository.AddAsync(_application1);
            insertResult.Should().BeOfType<InsertResult.InsertSuccess>();
            _application1.Id = ((InsertResult.InsertSuccess)insertResult).Id;

            _application2 = new()
            {
                ApplicationName = "Application Two",
                VendorId = _vendorId,
                ClaimSetName = "Another ClaimSet",
                ApplicationEducationOrganizations = [3, 4]
            };

            var insertResult2 = await _repository.AddAsync(_application2);
            insertResult2.Should().BeOfType<InsertResult.InsertSuccess>();
            _application2.Id = ((InsertResult.InsertSuccess)insertResult2).Id;

            var deleteResult = await _repository.DeleteAsync(_application2.Id);
            deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public async Task Should_not_get_application_two_from_get_all()
        {
            var getResult = await _repository.GetAllAsync();
            getResult.Should().BeOfType<GetResult<Application>.GetSuccess>();

            ((GetResult<Application>.GetSuccess)getResult).Results.Count.Should().Be(1);
            ((GetResult<Application>.GetSuccess)getResult)
                .Results.Count(v => v.Id == _application2.Id)
                .Should()
                .Be(0);
            ((GetResult<Application>.GetSuccess)getResult)
                .Results.Count(v => v.ApplicationName == "Application Two")
                .Should()
                .Be(0);
        }

        [Test]
        public async Task Should_get_test_application_from_get_by_id()
        {
            var getByIdResult = (await _repository.GetByIdAsync(_application1.Id));
            getByIdResult.Should().BeOfType<GetResult<Application>.GetByIdSuccess>();

            var application = ((GetResult<Application>.GetByIdSuccess)getByIdResult).Result;
            application.ApplicationName.Should().Be("Application One");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.ApplicationEducationOrganizations.Count.Should().Be(2);
        }
    }
}
