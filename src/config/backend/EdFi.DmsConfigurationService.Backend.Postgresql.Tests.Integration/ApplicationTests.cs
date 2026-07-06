// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class ApplicationTests : DatabaseTest
{
    private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance,
        new TestAuditContext()
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
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "FakePrefix1,FakePrefix2",
            };

            var vendorResult = await repository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand application = new()
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
                new ApplicationQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<ApplicationQueryResult.Success>();

            var application = ((ApplicationQueryResult.Success)getResult).ApplicationResponses.First();
            application.ApplicationName.Should().Be("Test Application");
            application.ClaimSetName.Should().Be("Test Claim set");
            application.VendorId.Should().Be(_vendorId);
            application.EducationOrganizationIds.Count.Should().Be(3);
            application.Enabled.Should().BeTrue();
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
            application.Enabled.Should().BeTrue();
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
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "",
            };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand application = new()
            {
                ApplicationName = "Test Application",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [],
            };

            var insertResult = await _applicationRepository.InsertApplication(
                application,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            insertResult.Should().BeOfType<ApplicationInsertResult.Success>();
            long appId = ((ApplicationInsertResult.Success)insertResult).Id;

            ApplicationUpdateCommand applicationUpdate = new()
            {
                Id = appId,
                ApplicationName = "Test Application",
                VendorId = 100,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [],
            };

            var applicationUpdateResult = await _applicationRepository.UpdateApplication(
                applicationUpdate,
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationUpdateResult.Should().BeOfType<ApplicationUpdateResult.FailureVendorNotFound>();
        }
    }

    [TestFixture]
    public class UpdateTests : ApplicationTests
    {
        private long _id;

        [SetUp]
        public async Task SetUp()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "",
            };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand command = new()
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

            var vendorUpdateResult = await _applicationRepository.UpdateApplication(
                new ApplicationUpdateCommand()
                {
                    Id = _id,
                    ApplicationName = command.ApplicationName,
                    ClaimSetName = command.ClaimSetName,
                    EducationOrganizationIds = command.EducationOrganizationIds,
                    VendorId = command.VendorId,
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            vendorUpdateResult.Should().BeOfType<ApplicationUpdateResult.Success>();
        }

        [Test]
        public async Task Should_get_update_application_from_get_all()
        {
            var getResult = await _applicationRepository.QueryApplication(
                new ApplicationQuery() { Limit = 25, Offset = 0 }
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
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "",
            };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            ApplicationInsertCommand application1 = new()
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

            ApplicationInsertCommand application2 = new()
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
                new ApplicationQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<ApplicationQueryResult.Success>();

            ((ApplicationQueryResult.Success)getResult).ApplicationResponses.Should().HaveCount(1);
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

    [TestFixture]
    public class DuplicateApplicationTests : ApplicationTests
    {
        private long _testVendorId;

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "FakePrefix1,FakePrefix2",
            };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _testVendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            // Insert first application
            ApplicationInsertCommand firstApplication = new()
            {
                ApplicationName = "Duplicate Test Application",
                VendorId = _testVendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [1, 2],
            };

            var firstResult = await _applicationRepository.InsertApplication(
                firstApplication,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            firstResult.Should().BeOfType<ApplicationInsertResult.Success>();
        }

        [Test]
        public async Task Should_fail_to_insert_duplicate_application_name_for_same_vendor()
        {
            // Attempt to insert a second application with the same name for the same vendor
            ApplicationInsertCommand duplicateApplication = new()
            {
                ApplicationName = "Duplicate Test Application",
                VendorId = _testVendorId,
                ClaimSetName = "Different Claim set",
                EducationOrganizationIds = [3, 4],
            };

            var insertResult = await _applicationRepository.InsertApplication(
                duplicateApplication,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );

            insertResult.Should().BeOfType<ApplicationInsertResult.FailureDuplicateApplication>();
            var failureResult = (ApplicationInsertResult.FailureDuplicateApplication)insertResult;
            failureResult.ApplicationName.Should().Be("Duplicate Test Application");
        }

        [Test]
        public async Task Should_allow_same_application_name_for_different_vendor()
        {
            // Create a second vendor
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand secondVendor = new()
            {
                Company = "Another Test Company",
                ContactEmailAddress = "test2@test.com",
                ContactName = "Another Fake Name",
                NamespacePrefixes = "AnotherPrefix1,AnotherPrefix2",
            };

            var secondVendorResult = await vendorRepository.InsertVendor(secondVendor);
            secondVendorResult.Should().BeOfType<VendorInsertResult.Success>();
            var secondVendorId = (secondVendorResult as VendorInsertResult.Success)!.Id;

            // Insert application with same name but different vendor
            ApplicationInsertCommand sameNameDifferentVendor = new()
            {
                ApplicationName = "Duplicate Test Application",
                VendorId = secondVendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [5, 6],
            };

            var insertResult = await _applicationRepository.InsertApplication(
                sameNameDifferentVendor,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );

            insertResult.Should().BeOfType<ApplicationInsertResult.Success>();
        }

        [Test]
        public async Task Should_fail_to_update_to_duplicate_application_name_for_same_vendor()
        {
            // Insert a second application with a different name
            ApplicationInsertCommand secondApplication = new()
            {
                ApplicationName = "Second Application",
                VendorId = _testVendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [7, 8],
            };

            var secondResult = await _applicationRepository.InsertApplication(
                secondApplication,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            secondResult.Should().BeOfType<ApplicationInsertResult.Success>();
            var secondApplicationId = ((ApplicationInsertResult.Success)secondResult).Id;

            // Try to update the second application to have the same name as the first
            ApplicationUpdateCommand updateCommand = new()
            {
                Id = secondApplicationId,
                ApplicationName = "Duplicate Test Application", // Same name as first application
                VendorId = _testVendorId,
                ClaimSetName = "Updated Claim set",
                EducationOrganizationIds = [9, 10],
            };

            var updateResult = await _applicationRepository.UpdateApplication(
                updateCommand,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );

            updateResult.Should().BeOfType<ApplicationUpdateResult.FailureDuplicateApplication>();
            var failureResult = (ApplicationUpdateResult.FailureDuplicateApplication)updateResult;
            failureResult.ApplicationName.Should().Be("Duplicate Test Application");
        }
    }

    [TestFixture]
    public class QueryPagingTests : ApplicationTests
    {
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
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            for (int i = 1; i <= 30; i++)
            {
                ApplicationInsertCommand app = new()
                {
                    ApplicationName = $"PagingApp-{i:D3}",
                    VendorId = _vendorId,
                    ClaimSetName = "TestClaimSet",
                    EducationOrganizationIds = [],
                };
                var insertResult = await _applicationRepository.InsertApplication(
                    app,
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                insertResult
                    .Should()
                    .BeOfType<ApplicationInsertResult.Success>(
                        $"application {i} (PagingApp-{i:D3}) should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _applicationRepository.QueryApplication(new ApplicationQuery());
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            ((ApplicationQueryResult.Success)result).ApplicationResponses.Should().HaveCount(30);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _applicationRepository.QueryApplication(new ApplicationQuery { Limit = 10 });
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            ((ApplicationQueryResult.Success)result).ApplicationResponses.Should().HaveCount(10);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _applicationRepository.QueryApplication(new ApplicationQuery { Offset = 25 });
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            ((ApplicationQueryResult.Success)result).ApplicationResponses.Should().HaveCount(5);
        }
    }

    [TestFixture]
    public class QuerySortTests : ApplicationTests
    {
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
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            foreach (var name in new[] { "Charlie-App", "Alice-App", "Bob-App" })
            {
                ApplicationInsertCommand app = new()
                {
                    ApplicationName = name,
                    VendorId = _vendorId,
                    ClaimSetName = "TestClaimSet",
                    EducationOrganizationIds = [],
                };
                var insertResult = await _applicationRepository.InsertApplication(
                    app,
                    new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
                );
                insertResult
                    .Should()
                    .BeOfType<ApplicationInsertResult.Success>(
                        $"application '{name}' should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_application_name()
        {
            var result = await _applicationRepository.QueryApplication(
                new ApplicationQuery { OrderBy = "applicationName", Direction = "ASC" }
            );
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            var names = ((ApplicationQueryResult.Success)result)
                .ApplicationResponses.Select(a => a.ApplicationName)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Alice-App", "Bob-App", "Charlie-App");
        }

        [Test]
        public async Task Should_return_descending_order_by_application_name()
        {
            var result = await _applicationRepository.QueryApplication(
                new ApplicationQuery { OrderBy = "applicationName", Direction = "DESC" }
            );
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            var names = ((ApplicationQueryResult.Success)result)
                .ApplicationResponses.Select(a => a.ApplicationName)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Charlie-App", "Bob-App", "Alice-App");
        }

        [Test]
        public async Task Should_default_to_ascending_order_by_application_name_when_orderby_is_omitted()
        {
            var result = await _applicationRepository.QueryApplication(new ApplicationQuery());
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            var names = ((ApplicationQueryResult.Success)result)
                .ApplicationResponses.Select(a => a.ApplicationName)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Alice-App", "Bob-App", "Charlie-App");
        }
    }

    [TestFixture]
    public class Given_ApplicationWithDisabledApiClient : ApplicationTests
    {
        private long _id;
        private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext()
        );

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            var vendorResult = await vendorRepository.InsertVendor(
                new VendorInsertCommand
                {
                    Company = "Disabled Test Company",
                    ContactEmailAddress = "disabled@test.com",
                    ContactName = "Disabled Name",
                    NamespacePrefixes = "DisabledPrefix",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            var appResult = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "Disabled Api Client App",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            _id = (appResult as ApplicationInsertResult.Success)!.Id;

            var clientResult = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = _id,
                    Name = "Unapproved Client",
                    IsApproved = false,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            clientResult.Should().BeOfType<ApiClientInsertResult.Success>();
        }

        [Test]
        public async Task It_should_return_enabled_false_from_query()
        {
            var result = await _applicationRepository.QueryApplication(
                new ApplicationQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<ApplicationQueryResult.Success>();
            var application = ((ApplicationQueryResult.Success)result).ApplicationResponses.Single(a =>
                a.Id == _id
            );
            application.Enabled.Should().BeFalse();
        }

        [Test]
        public async Task It_should_return_enabled_false_from_get_by_id()
        {
            var result = await _applicationRepository.GetApplication(_id);
            result.Should().BeOfType<ApplicationGetResult.Success>();
            var application = ((ApplicationGetResult.Success)result).ApplicationResponse;
            application.Enabled.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_DataStoreApplicationWithDisabledApiClient : ApplicationTests
    {
        private long _applicationId;
        private long _dataStoreId;

        private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext()
        );

        private readonly IDataStoreRepository _dataStoreRepository;

        public Given_DataStoreApplicationWithDisabledApiClient()
        {
            var routeContextRepository = new DataStoreContextRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreContextRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var derivativeRepository = new DataStoreDerivativeRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            _dataStoreRepository = new DataStoreRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                routeContextRepository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
        }

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var vendorResult = await vendorRepository.InsertVendor(
                new VendorInsertCommand
                {
                    Company = "DataStore Disabled Test Company",
                    ContactEmailAddress = "datastore@test.com",
                    ContactName = "DataStore Test",
                    NamespacePrefixes = "DataStorePrefix",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            var dmsResult = await _dataStoreRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Test",
                    Name = "Disabled Client Test Instance",
                    ConnectionString = "Server=test;Database=TestDb;",
                }
            );
            dmsResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = (dmsResult as DataStoreInsertResult.Success)!.Id;

            var appResult = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "DataStore Disabled App",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim Set",
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
                    Name = "Disabled DataStore Client",
                    IsApproved = false,
                    DataStoreIds = [_dataStoreId],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            clientResult.Should().BeOfType<ApiClientInsertResult.Success>();
        }

        [Test]
        public async Task It_should_return_enabled_false_from_QueryApplicationByDataStore()
        {
            var result = await _dataStoreRepository.QueryApplicationByDataStore(
                _dataStoreId,
                new PagingQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            var app = ((ApplicationByDataStoreQueryResult.Success)result).ApplicationResponse.Single(a =>
                a.Id == _applicationId
            );
            app.Enabled.Should().BeFalse();
        }

        [Test]
        public async Task It_should_return_enabled_true_when_all_api_clients_are_approved()
        {
            // Create a separate application with only approved clients
            var appResult = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "DataStore All Approved App",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim Set Approved",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            var approvedAppId = (appResult as ApplicationInsertResult.Success)!.Id;

            // Add an approved ApiClient linked to the DMS instance
            await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = approvedAppId,
                    Name = "Approved DataStore Client",
                    IsApproved = true,
                    DataStoreIds = [_dataStoreId],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );

            var result = await _dataStoreRepository.QueryApplicationByDataStore(
                _dataStoreId,
                new PagingQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            var app = ((ApplicationByDataStoreQueryResult.Success)result).ApplicationResponse.Single(a =>
                a.Id == approvedAppId
            );
            app.Enabled.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_DataStoreApplicationEnabled_CrossInstanceIsolation : ApplicationTests
    {
        private long _applicationId;
        private long _dataStore1Id;
        private long _dataStore2Id;

        private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext()
        );

        private readonly IDataStoreRepository _dataStoreRepository;

        public Given_DataStoreApplicationEnabled_CrossInstanceIsolation()
        {
            var routeContextRepository = new DataStoreContextRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreContextRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var derivativeRepository = new DataStoreDerivativeRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            _dataStoreRepository = new DataStoreRepository(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                routeContextRepository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
        }

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var vendorResult = await vendorRepository.InsertVendor(
                new VendorInsertCommand
                {
                    Company = "CrossInstance Test Company",
                    ContactEmailAddress = "crossinstance@test.com",
                    ContactName = "CrossInstance Test",
                    NamespacePrefixes = "CrossInstancePrefix",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            var dms1Result = await _dataStoreRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Test",
                    Name = "CrossInstance Instance 1",
                    ConnectionString = "Server=test1;Database=TestDb1;",
                }
            );
            dms1Result.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStore1Id = (dms1Result as DataStoreInsertResult.Success)!.Id;

            var dms2Result = await _dataStoreRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Test",
                    Name = "CrossInstance Instance 2",
                    ConnectionString = "Server=test2;Database=TestDb2;",
                }
            );
            dms2Result.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStore2Id = (dms2Result as DataStoreInsertResult.Success)!.Id;

            var appResult = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "CrossInstance App",
                    VendorId = _vendorId,
                    ClaimSetName = "CrossInstance Claim Set",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            _applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

            // Client 1: approved, linked to DataStore 1 only
            var client1Result = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = _applicationId,
                    Name = "Approved Client Instance1",
                    IsApproved = true,
                    DataStoreIds = [_dataStore1Id],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            client1Result.Should().BeOfType<ApiClientInsertResult.Success>();

            // Client 2: disabled, linked to DataStore 2 only
            var client2Result = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = _applicationId,
                    Name = "Disabled Client Instance2",
                    IsApproved = false,
                    DataStoreIds = [_dataStore2Id],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            client2Result.Should().BeOfType<ApiClientInsertResult.Success>();
        }

        [Test]
        public async Task It_should_return_enabled_true_for_instance_with_only_approved_clients()
        {
            var result = await _dataStoreRepository.QueryApplicationByDataStore(
                _dataStore1Id,
                new PagingQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            var app = ((ApplicationByDataStoreQueryResult.Success)result).ApplicationResponse.Single(a =>
                a.Id == _applicationId
            );
            app.Enabled.Should().BeTrue();
        }

        [Test]
        public async Task It_should_return_enabled_false_for_instance_with_disabled_client_without_bleeding_across_instances()
        {
            var result = await _dataStoreRepository.QueryApplicationByDataStore(
                _dataStore2Id,
                new PagingQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            var app = ((ApplicationByDataStoreQueryResult.Success)result).ApplicationResponse.Single(a =>
                a.Id == _applicationId
            );
            app.Enabled.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_GetApplicationByClientId_WithDisabledApiClient : ApplicationTests
    {
        private string _clientId = string.Empty;

        private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            new TestAuditContext()
        );

        private readonly OpenIddictDataRepository _openIddictDataRepository = new(
            Configuration.DatabaseOptions
        );

        [SetUp]
        public async Task Setup()
        {
            IVendorRepository vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var vendorResult = await vendorRepository.InsertVendor(
                new VendorInsertCommand
                {
                    Company = "OpenIddict Disabled Test Company",
                    ContactEmailAddress = "openiddict@test.com",
                    ContactName = "OpenIddict Test",
                    NamespacePrefixes = "OpenIddictPrefix",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            _clientId = Guid.NewGuid().ToString();

            var appResult = await _applicationRepository.InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "OpenIddict Disabled App",
                    VendorId = _vendorId,
                    ClaimSetName = "Test Claim Set",
                    EducationOrganizationIds = [],
                },
                new ApiClientCommand { ClientId = _clientId, ClientUuid = Guid.NewGuid() }
            );
            appResult.Should().BeOfType<ApplicationInsertResult.Success>();
            var applicationId = (appResult as ApplicationInsertResult.Success)!.Id;

            // Create OpenIddictApplication record to make the application visible to OpenIddict queries
            await _openIddictDataRepository.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    await _openIddictDataRepository.InsertApplicationAsync(
                        Guid.NewGuid(),
                        _clientId,
                        "secret",
                        "OpenIddict Disabled App",
                        [],
                        [],
                        "confidential",
                        "{}",
                        connection,
                        transaction
                    );
                }
            );

            // InsertApplication auto-creates an ApiClient with IsApproved = true using _clientId.
            // Adding a second client with IsApproved = false AND THE SAME ClientId makes BOOL_AND return false.
            var clientResult = await _apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = "Unapproved OpenIddict Client",
                    IsApproved = false,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = _clientId, ClientUuid = Guid.NewGuid() }
            );
            clientResult.Should().BeOfType<ApiClientInsertResult.Success>();
        }

        [Test]
        public async Task It_should_return_IsApproved_false_from_GetApplicationByClientIdAsync()
        {
            var result = await _openIddictDataRepository.GetApplicationByClientIdAsync(_clientId);
            result.Should().NotBeNull();
            result!.IsApproved.Should().BeFalse();
        }
    }
}
