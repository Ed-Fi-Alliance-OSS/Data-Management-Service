// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Application;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public class ApplicationTests : DatabaseTest
{
    private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance
    );

    private long _vendorId;

    [TestFixture]
    public class InsertTest : ApplicationTests
    {
        private long _id;
        private readonly string _clientId = Guid.NewGuid().ToString();
        private readonly Guid _clientUuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository repository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance
            );

            VendorInsertCommand vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "FakePrefix1,FakePrefix2",
                };

            var vendorResult = await repository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand application =
                new()
                {
                    ApplicationName = "Test Application",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [1, 255911001, 255911002],
                };

            var result = await _applicationRepository.InsertApplication(
                application,
                new() { ClientId = _clientId, ClientUuid = _clientUuid }
            );
            result.Should().BeOfType<ApplicationInsertResult.Success>();
            _id = (result as ApplicationInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_test_application_from_get_all()
        {
            var getResult = await _applicationRepository.QueryApplication(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<ApplicationQueryResult.Success>();

            var application = ((ApplicationQueryResult.Success)getResult).ApplicationResponses.First();
            application.ApplicationName.Should().Be("Test Application");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.EducationOrganizationIds.Count.Should().Be(3);
        }

        [Test]
        public async Task Should_get_test_application_from_get_by_id()
        {
            var getByIdResult = (await _applicationRepository.GetApplication(_id));
            getByIdResult.Should().BeOfType<ApplicationGetResult.Success>();

            var application = ((ApplicationGetResult.Success)getByIdResult).ApplicationResponse;
            application.ApplicationName.Should().Be("Test Application");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.EducationOrganizationIds.Count.Should().Be(3);
        }

        [Test]
        public async Task Should_get_api_clients()
        {
            var getApiClientsResult = await _applicationRepository.GetApplicationApiClients(_id);
            getApiClientsResult.Should().BeOfType<ApplicationApiClientsResult.Success>();

            var apiClients = ((ApplicationApiClientsResult.Success)getApiClientsResult).Clients;
            apiClients.Length.Should().Be(1);
            apiClients[0].ClientId.Should().Be(_clientId);
            apiClients[0].ClientUuid.Should().Be(_clientUuid);
        }
    }

    [TestFixture]
    public class InsertFailureTests : ApplicationTests
    {
        private ApplicationInsertCommand _application = null!;

        [Test]
        public async Task Should_get_and_failure_reference_not_found_and_invalid_vendor_id()
        {
            _application = new()
            {
                ApplicationName = "Test Application",
                VendorId = 15,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [],
            };

            var insertResult = await _applicationRepository.InsertApplication(
                _application,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            insertResult.Should().BeOfType<ApplicationInsertResult.FailureVendorNotFound>();
        }
    }

    [TestFixture]
    public class UpdateFailureTests : ApplicationTests
    {
        [Test]
        public async Task Should_get_and_failure_reference_not_found_and_invalid_vendor_id()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance
            );

            VendorInsertCommand vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "",
                };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand _application =
                new()
                {
                    ApplicationName = "Test Application",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [],
                };

            var insertResult = await _applicationRepository.InsertApplication(
                _application,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            insertResult.Should().BeOfType<ApplicationInsertResult.Success>();
            long appId = ((ApplicationInsertResult.Success)insertResult).Id;

            ApplicationUpdateCommand applicationUpdate =
                new()
                {
                    Id = appId,
                    ApplicationName = "Test Application",
                    VendorId = 100,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [],
                };

            var applicationUpdateResult = await _applicationRepository.UpdateApplication(applicationUpdate);
            applicationUpdateResult.Should().BeOfType<ApplicationUpdateResult.FailureVendorNotFound>();
        }
    }

    [TestFixture]
    public class UpdateTests : ApplicationTests
    {
        private long _id = 0;

        [SetUp]
        public async Task SetUp()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance
            );

            VendorInsertCommand vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "",
                };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand command =
                new()
                {
                    ApplicationName = "Test Application",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [],
                };

            var insertResult = await _applicationRepository.InsertApplication(
                command,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            insertResult.Should().BeOfType<ApplicationInsertResult.Success>();

            _id = ((ApplicationInsertResult.Success)insertResult).Id;
            command.ApplicationName = "Update Application Name";
            command.EducationOrganizationIds = [1, 2];

            var VendorUpdateResult = await _applicationRepository.UpdateApplication(
                new ApplicationUpdateCommand()
                {
                    Id = _id,
                    ApplicationName = command.ApplicationName,
                    ClaimSetName = command.ClaimSetName,
                    EducationOrganizationIds = command.EducationOrganizationIds,
                    VendorId = command.VendorId,
                }
            );
            VendorUpdateResult.Should().BeOfType<ApplicationUpdateResult.Success>();
        }

        [Test]
        public async Task Should_get_update_application_from_get_all()
        {
            var getResult = await _applicationRepository.QueryApplication(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<ApplicationQueryResult.Success>();

            var applicationFromDb = ((ApplicationQueryResult.Success)getResult).ApplicationResponses.First();
            applicationFromDb.ApplicationName.Should().NotBe("Test Application");
            applicationFromDb.EducationOrganizationIds.Count.Should().Be(2);
        }

        [Test]
        public async Task Should_get_update_application_from_get_by_id()
        {
            var getByIdResult = (await _applicationRepository.GetApplication(_id));
            getByIdResult.Should().BeOfType<ApplicationGetResult.Success>();

            var applicationFromDb = ((ApplicationGetResult.Success)getByIdResult).ApplicationResponse;
            applicationFromDb.ApplicationName.Should().Be("Update Application Name");
            applicationFromDb.ClaimSetName.Should().Be("Test Claim set");
        }
    }

    [TestFixture]
    public class DeleteTests : ApplicationTests
    {
        private long _application1Id;
        private long _application2Id;

        [SetUp]
        public async Task SetUp()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance
            );

            VendorInsertCommand vendor =
                new()
                {
                    Company = "Test Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = "",
                };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand application1 =
                new()
                {
                    ApplicationName = "Application One",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [1, 2],
                };

            var insertResult = await _applicationRepository.InsertApplication(
                application1,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            _application1Id = ((ApplicationInsertResult.Success)insertResult).Id;

            ApplicationInsertCommand application2 =
                new()
                {
                    ApplicationName = "Application Two",
                    VendorId = _vendorId,
                    ClaimSetName = "Another ClaimSet",
                    EducationOrganizationIds = [3, 4],
                };

            insertResult = await _applicationRepository.InsertApplication(
                application2,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            _application2Id = ((ApplicationInsertResult.Success)insertResult).Id;

            var deleteResult = await _applicationRepository.DeleteApplication(_application2Id);
            deleteResult.Should().BeOfType<ApplicationDeleteResult.Success>();
        }

        [Test]
        public async Task Should_not_get_application_two_from_get_all()
        {
            var getResult = await _applicationRepository.QueryApplication(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<ApplicationQueryResult.Success>();

            int count = ((ApplicationQueryResult.Success)getResult).ApplicationResponses.ToList().Count();

            count.Should().Be(1);
            ((ApplicationQueryResult.Success)getResult)
                .ApplicationResponses.Count(v => v.Id == _application2Id)
                .Should()
                .Be(0);
            ((ApplicationQueryResult.Success)getResult)
                .ApplicationResponses.Count(v => v.ApplicationName == "Application Two")
                .Should()
                .Be(0);
        }

        [Test]
        public async Task Should_get_test_application_from_get_by_id()
        {
            var getByIdResult = (await _applicationRepository.GetApplication(_application1Id));
            getByIdResult.Should().BeOfType<ApplicationGetResult.Success>();

            var application = ((ApplicationGetResult.Success)getByIdResult).ApplicationResponse;
            application.ApplicationName.Should().Be("Application One");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.EducationOrganizationIds.Count.Should().Be(2);
        }

        [Test]
        public async Task Should_not_get_api_clients()
        {
            var getApiClientsResult = await _applicationRepository.GetApplicationApiClients(_application2Id);
            getApiClientsResult.Should().BeOfType<ApplicationApiClientsResult.Success>();

            var apiClients = ((ApplicationApiClientsResult.Success)getApiClientsResult).Clients;
            apiClients.Length.Should().Be(0);
        }
    }
}
