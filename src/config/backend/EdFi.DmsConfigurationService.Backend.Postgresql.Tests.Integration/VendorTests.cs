// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

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
            private int _id;

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
        public class Given_a_vendor_with_a_255_character_namespace_prefix : VendorTests
        {
            private static readonly string LongPrefix = BuildPrefix('a');
            private int _id;

            private static string BuildPrefix(char last) =>
                string.Concat("uri://vendor.example.org/".PadRight(254, 'x'), last);

            [SetUp]
            public async Task Setup()
            {
                LongPrefix.Length.Should().Be(255);

                VendorInsertCommand vendor = new()
                {
                    Company = "Long Prefix Company",
                    ContactEmailAddress = "test@test.com",
                    ContactName = "Fake Name",
                    NamespacePrefixes = $"FakePrefix1,{LongPrefix}",
                };

                var result = await _repository.InsertVendor(vendor);
                result.Should().BeOfType<VendorInsertResult.Success>();
                _id = (result as VendorInsertResult.Success)!.Id;
            }

            [Test]
            public async Task It_returns_both_prefixes_unchanged_from_get_by_id()
            {
                var getByIdResult = await _repository.GetVendor(_id);
                getByIdResult.Should().BeOfType<VendorGetResult.Success>();

                var vendorFromDb = ((VendorGetResult.Success)getByIdResult).VendorResponse;
                vendorFromDb.NamespacePrefixes.Split(',').Should().BeEquivalentTo("FakePrefix1", LongPrefix);
            }
        }

        [TestFixture]
        public class Given_a_vendor_whose_255_character_namespace_prefix_is_updated : VendorTests
        {
            private static readonly string LongPrefix = BuildPrefix('a');
            private static readonly string ChangedLongPrefix = BuildPrefix('b');
            private VendorUpdateResult _updateResult = null!;
            private VendorGetResult _getResult = null!;

            private static string BuildPrefix(char last) =>
                string.Concat("uri://vendor.example.org/".PadRight(254, 'x'), last);

            [SetUp]
            public async Task Setup()
            {
                LongPrefix.Length.Should().Be(255);
                ChangedLongPrefix.Length.Should().Be(255);
                ChangedLongPrefix.Should().NotBe(LongPrefix);

                var insertResult = await _repository.InsertVendor(
                    new VendorInsertCommand
                    {
                        Company = "Long Prefix Company",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name",
                        NamespacePrefixes = $"FakePrefix1,{LongPrefix}",
                    }
                );
                int id = ((VendorInsertResult.Success)insertResult).Id;

                _updateResult = await _repository.UpdateVendor(
                    new VendorUpdateCommand
                    {
                        Id = id,
                        Company = "Long Prefix Company",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name",
                        NamespacePrefixes = $"FakePrefix1,{ChangedLongPrefix}",
                    }
                );

                _getResult = await _repository.GetVendor(id);
            }

            [Test]
            public void It_updates_successfully() =>
                _updateResult.Should().BeOfType<VendorUpdateResult.Success>();

            [Test]
            public void It_returns_the_updated_prefix() =>
                ((VendorGetResult.Success)_getResult)
                    .VendorResponse.NamespacePrefixes.Split(',')
                    .Should()
                    .BeEquivalentTo("FakePrefix1", ChangedLongPrefix);
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
                vendorFromDb.NamespacePrefixes.Should().BeEmpty();
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
                vendorFromDb.NamespacePrefixes.Should().BeEmpty();
            }
        }

        [TestFixture]
        public class DeleteTests : VendorTests
        {
            private int _vendor1Id;
            private int _vendor2Id;

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

                var deleteResult = await _repository.DeleteVendor(_vendor1Id, []);
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
            private int _vendorId1;
            private int _vendorId2;
            private readonly int _vendorIdNotExist = 9999;
            private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
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
            public async Task Should_use_default_ascending_order_when_no_order_by_is_provided()
            {
                var result = await _repository.QueryVendor(new VendorQuery { Direction = "DESC", Limit = 5 });
                result.Should().BeOfType<VendorQueryResult.Success>();
                var companies = ((VendorQueryResult.Success)result)
                    .VendorResponses.Select(vendor => vendor.Company)
                    .ToList();

                companies
                    .Should()
                    .ContainInOrder("Vendor-01", "Vendor-02", "Vendor-03", "Vendor-04", "Vendor-05");
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
            private int _vendorId;
            private int _applicationId;

            private readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            private readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
                Configuration.DatabaseOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider(),
                new NoOpUnapprovalTokenRevocation()
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

        /// <summary>
        /// Creates a tenant with a unique name and a tenant context for it.
        /// </summary>
        private static async Task<TenantContextProvider> CreateTenantProvider(string suffix)
        {
            var tenantRepository = new TenantRepository(
                Configuration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );
            var tenantName = $"VendorTenant{suffix}-{Guid.NewGuid()}";
            var tenantResult = await tenantRepository.InsertTenant(
                new TenantInsertCommand { Name = tenantName }
            );
            tenantResult.Should().BeOfType<TenantInsertResult.Success>();
            return new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(
                    ((TenantInsertResult.Success)tenantResult).Id,
                    tenantName
                ),
            };
        }

        private static VendorRepository CreateVendorRepository(TenantContextProvider tenantContextProvider) =>
            new(
                Configuration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );

        private static VendorInsertCommand VendorCommand(
            string company,
            string contactName = "Tenant Tester",
            string contactEmailAddress = "tenant@test.com",
            string namespacePrefixes = "uri://tenant-test.example"
        ) =>
            new()
            {
                Company = company,
                ContactName = contactName,
                ContactEmailAddress = contactEmailAddress,
                NamespacePrefixes = namespacePrefixes,
            };

        private static async Task<VendorInsertResult.Success> InsertVendor(
            IVendorRepository repository,
            VendorInsertCommand command
        )
        {
            var result = await repository.InsertVendor(command);
            result.Should().BeOfType<VendorInsertResult.Success>();
            return (VendorInsertResult.Success)result;
        }

        private static async Task<VendorResponse> GetVendor(IVendorRepository repository, int id)
        {
            var result = await repository.GetVendor(id);
            result.Should().BeOfType<VendorGetResult.Success>();
            return ((VendorGetResult.Success)result).VendorResponse;
        }

        [TestFixture]
        public class Given_vendors_with_the_same_company_in_two_tenants : VendorTests
        {
            private const string SharedCompany = "DMS1530 Shared Company";

            private IVendorRepository _tenantARepository = null!;
            private IVendorRepository _tenantBRepository = null!;
            private VendorInsertResult.Success _tenantAInsert = null!;
            private VendorInsertResult.Success _tenantBInsert = null!;

            private static async Task<int[]> QueryVendorIds(IVendorRepository repository)
            {
                var result = await repository.QueryVendor(new VendorQuery());
                result.Should().BeOfType<VendorQueryResult.Success>();
                return [.. ((VendorQueryResult.Success)result).VendorResponses.Select(vendor => vendor.Id)];
            }

            /// <summary>
            /// A repeat of <paramref name="company"/> whose every other field differs from
            /// <see cref="VendorCommand"/>'s defaults, so an update of the existing row would be visible.
            /// </summary>
            private static VendorInsertCommand ChangedRepeatCommand(string company) =>
                VendorCommand(
                    company,
                    contactName: "Updated Contact",
                    contactEmailAddress: "updated@test.com",
                    namespacePrefixes: "uri://updated.example"
                );

            private static void ShouldBeUnchanged(VendorResponse vendor)
            {
                vendor.ContactName.Should().Be("Tenant Tester");
                vendor.ContactEmailAddress.Should().Be("tenant@test.com");
                vendor.NamespacePrefixes.Should().Be("uri://tenant-test.example");
            }

            [SetUp]
            public async Task Setup()
            {
                _tenantARepository = CreateVendorRepository(await CreateTenantProvider("A"));
                _tenantBRepository = CreateVendorRepository(await CreateTenantProvider("B"));

                _tenantAInsert = await InsertVendor(_tenantARepository, VendorCommand(SharedCompany));
                _tenantBInsert = await InsertVendor(_tenantBRepository, VendorCommand(SharedCompany));
            }

            [Test]
            public void It_should_create_a_new_vendor_in_each_tenant()
            {
                _tenantBInsert.Id.Should().NotBe(_tenantAInsert.Id);
            }

            [Test]
            public async Task It_should_list_only_its_own_tenants_vendor()
            {
                (await QueryVendorIds(_tenantARepository)).Should().Equal(_tenantAInsert.Id);
                (await QueryVendorIds(_tenantBRepository)).Should().Equal(_tenantBInsert.Id);
            }

            [Test]
            public async Task It_should_not_get_another_tenants_vendor()
            {
                (await _tenantBRepository.GetVendor(_tenantAInsert.Id))
                    .Should()
                    .BeOfType<VendorGetResult.FailureNotFound>();
                (await GetVendor(_tenantARepository, _tenantAInsert.Id)).Company.Should().Be(SharedCompany);
            }

            [Test]
            public async Task It_should_reject_a_repeat_within_a_tenant()
            {
                var repeat = await _tenantARepository.InsertVendor(ChangedRepeatCommand(SharedCompany));

                repeat.Should().BeOfType<VendorInsertResult.FailureDuplicateCompanyName>();
                ShouldBeUnchanged(await GetVendor(_tenantARepository, _tenantAInsert.Id));
                (await QueryVendorIds(_tenantARepository)).Should().Equal(_tenantAInsert.Id);
                ShouldBeUnchanged(await GetVendor(_tenantBRepository, _tenantBInsert.Id));
            }

            [Test]
            public async Task It_should_reject_a_repeat_in_single_tenant_mode()
            {
                const string Company = "DMS1341 Single Tenant Company";
                var first = await InsertVendor(_repository, VendorCommand(Company));

                var repeat = await _repository.InsertVendor(ChangedRepeatCommand(Company));

                repeat.Should().BeOfType<VendorInsertResult.FailureDuplicateCompanyName>();
                ShouldBeUnchanged(await GetVendor(_repository, first.Id));
                (await QueryVendorIds(_repository)).Should().Equal(first.Id);
            }

            [Test]
            public async Task It_should_let_a_single_tenant_vendor_share_a_tenants_company()
            {
                var singleTenant = await InsertVendor(_repository, VendorCommand(SharedCompany));

                singleTenant.Id.Should().NotBe(_tenantAInsert.Id).And.NotBe(_tenantBInsert.Id);
            }
        }

        [TestFixture]
        public class Given_a_vendor_renamed_onto_an_existing_company_in_its_tenant : VendorTests
        {
            private const string TakenCompany = "DMS1530 Taken Company";
            private const string OriginalCompany = "DMS1530 Original Company";
            private const string OtherTenantCompany = "DMS1530 Other Tenant Company";

            private IVendorRepository _tenantARepository = null!;
            private int _vendorId;

            [SetUp]
            public async Task Setup()
            {
                _tenantARepository = CreateVendorRepository(await CreateTenantProvider("A"));
                IVendorRepository tenantBRepository = CreateVendorRepository(await CreateTenantProvider("B"));

                await InsertVendor(_tenantARepository, VendorCommand(TakenCompany));
                _vendorId = (await InsertVendor(_tenantARepository, VendorCommand(OriginalCompany))).Id;
                await InsertVendor(tenantBRepository, VendorCommand(OtherTenantCompany));
            }

            private Task<VendorUpdateResult> RenameTo(string company) =>
                _tenantARepository.UpdateVendor(
                    new VendorUpdateCommand
                    {
                        Id = _vendorId,
                        Company = company,
                        ContactName = "Renamer",
                        ContactEmailAddress = "renamer@test.com",
                        NamespacePrefixes = "uri://renamed.example",
                    }
                );

            [Test]
            public async Task It_should_reject_a_company_taken_in_its_tenant()
            {
                (await RenameTo(TakenCompany))
                    .Should()
                    .BeOfType<VendorUpdateResult.FailureDuplicateCompanyName>();

                VendorResponse unchanged = await GetVendor(_tenantARepository, _vendorId);
                unchanged.Company.Should().Be(OriginalCompany);
                unchanged.ContactName.Should().Be("Tenant Tester");
                unchanged.NamespacePrefixes.Should().Be("uri://tenant-test.example");
            }

            [Test]
            public async Task It_should_allow_a_company_that_exists_only_in_another_tenant()
            {
                (await RenameTo(OtherTenantCompany)).Should().BeOfType<VendorUpdateResult.Success>();

                (await GetVendor(_tenantARepository, _vendorId)).Company.Should().Be(OtherTenantCompany);
            }
        }

        /// <summary>The client that appears under the vendor without the cleanup having seen it.</summary>
        public enum ConcurrentCreation
        {
            NewApplicationWithClient,
            ClientInClientlessApplication,
        }

        /// <summary>
        /// The vendor deletion guarded by the clients the caller cleaned up. The vendor has one
        /// application whose client was cleaned up and one application with no client.
        /// </summary>
        public abstract class GuardedDeleteTests : VendorTests
        {
            protected int _vendorId;
            protected int _clientlessApplicationId;
            protected VendorApiClient _cleanedClient = null!;
            protected Guid _concurrentClientUuid;

            [SetUp]
            public async Task SeedVendor()
            {
                _vendorId = (
                    (VendorInsertResult.Success)
                        await _repository.InsertVendor(
                            new VendorInsertCommand
                            {
                                Company = $"Guarded Vendor {Guid.NewGuid():N}",
                                ContactEmailAddress = "guarded@test.com",
                                ContactName = "Guarded",
                                NamespacePrefixes = "",
                            }
                        )
                ).Id;
                int cleanedApplicationId = await InsertApplicationAsync(
                    Connection!,
                    null,
                    "Cleaned Application"
                );
                _cleanedClient = await InsertClientAsync(
                    Connection!,
                    null,
                    cleanedApplicationId,
                    "Cleaned Client",
                    Guid.NewGuid()
                );
                _clientlessApplicationId = await InsertApplicationAsync(
                    Connection!,
                    null,
                    "Clientless Application"
                );
            }

            protected Task<VendorDeleteResult> DeleteAsync() =>
                _repository.DeleteVendor(_vendorId, [_cleanedClient]);

            protected async Task CreateConcurrentClientAsync(
                ConcurrentCreation creation,
                NpgsqlConnection connection,
                NpgsqlTransaction? transaction
            )
            {
                // Fixed before any insert, so a creation that fails still names the client it tried.
                _concurrentClientUuid = Guid.NewGuid();
                int applicationId =
                    creation == ConcurrentCreation.NewApplicationWithClient
                        ? await InsertApplicationAsync(connection, transaction, "Concurrent Application")
                        : _clientlessApplicationId;
                await InsertClientAsync(
                    connection,
                    transaction,
                    applicationId,
                    "Concurrent Client",
                    _concurrentClientUuid
                );
            }

            private Task<int> InsertApplicationAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction? transaction,
                string name
            ) =>
                connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO "dmscs"."Application" ("ApplicationName", "VendorId", "ClaimSetName")
                    VALUES (@Name, @VendorId, 'TestClaim') RETURNING "Id";
                    """,
                    new { Name = name, VendorId = _vendorId },
                    transaction
                );

            private async Task<VendorApiClient> InsertClientAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction? transaction,
                int applicationId,
                string name,
                Guid clientUuid
            )
            {
                int id = await connection.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "Name", "ClientId", "ClientUuid")
                    VALUES (@ApplicationId, @Name, @ClientId, @ClientUuid) RETURNING "Id";
                    """,
                    new
                    {
                        ApplicationId = applicationId,
                        Name = name,
                        ClientId = clientUuid.ToString(),
                        ClientUuid = clientUuid,
                    },
                    transaction
                );
                return new VendorApiClient(id, clientUuid.ToString(), clientUuid, applicationId);
            }

            protected Task<bool> VendorExistsAsync() =>
                Connection!.ExecuteScalarAsync<bool>(
                    """SELECT EXISTS (SELECT 1 FROM "dmscs"."Vendor" WHERE "Id" = @Id);""",
                    new { Id = _vendorId }
                );

            protected Task<int> ClientCountAsync(params Guid[] clientUuids) =>
                Connection!.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM "dmscs"."ApiClient" WHERE "ClientUuid" = ANY(@ClientUuids);""",
                    new { ClientUuids = clientUuids }
                );

            /// <summary>
            /// Returns once <paramref name="operation"/>'s session waits on a lock: on any session
            /// held by <paramref name="holderProcessId"/>, or, with none given, on any session at
            /// all for <paramref name="waiterProcessId"/>. The bound only turns an operation that
            /// never waits into a failure instead of a hang.
            /// </summary>
            protected async Task WaitUntilBlockedAsync(
                Task operation,
                int? holderProcessId,
                int? waiterProcessId
            )
            {
                await using NpgsqlConnection monitor = await DataSource!.OpenConnectionAsync();
                using CancellationTokenSource bound = new(TimeSpan.FromSeconds(60));
                while (
                    !await monitor.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS (
                            SELECT 1 FROM pg_stat_activity
                            WHERE cardinality(pg_blocking_pids(pid)) > 0
                              AND (@Holder::int IS NULL OR @Holder::int = ANY(pg_blocking_pids(pid)))
                              AND (@Waiter::int IS NULL OR pid = @Waiter::int)
                        );
                        """,
                        new { Holder = holderProcessId, Waiter = waiterProcessId }
                    )
                )
                {
                    operation.IsCompleted.Should().BeFalse("the operation must wait on the lock");
                    await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token);
                }
            }
        }

        [TestFixture]
        public class Given_a_guarded_vendor_delete_with_every_client_cleaned : GuardedDeleteTests
        {
            private VendorDeleteResult _result = null!;

            [SetUp]
            public async Task Act() => _result = await DeleteAsync();

            [Test]
            public void It_deletes_the_vendor() => _result.Should().BeOfType<VendorDeleteResult.Success>();

            [Test]
            public async Task It_removes_the_vendor_and_its_client()
            {
                (await VendorExistsAsync()).Should().BeFalse();
                (await ClientCountAsync(_cleanedClient.ClientUuid)).Should().Be(0);
            }
        }

        /// <summary>
        /// A client committed after the caller's cleanup read and before the guarded delete: the
        /// interleaving in which an unconditional cascade orphaned a live provider client.
        /// </summary>
        [TestFixture(ConcurrentCreation.NewApplicationWithClient)]
        [TestFixture(ConcurrentCreation.ClientInClientlessApplication)]
        public class Given_a_guarded_vendor_delete_after_a_client_was_created(ConcurrentCreation creation)
            : GuardedDeleteTests
        {
            private VendorDeleteResult _result = null!;

            [SetUp]
            public async Task Act()
            {
                await CreateConcurrentClientAsync(creation, Connection!, null);
                _result = await DeleteAsync();
            }

            [Test]
            public void It_refuses_the_cascade() =>
                _result.Should().BeOfType<VendorDeleteResult.FailureClientsChanged>();

            [Test]
            public async Task It_keeps_the_vendor_and_every_client()
            {
                (await VendorExistsAsync()).Should().BeTrue();
                (await ClientCountAsync(_cleanedClient.ClientUuid, _concurrentClientUuid)).Should().Be(2);
            }
        }

        /// <summary>
        /// The creation is still uncommitted when the guarded delete runs: the delete waits for it
        /// on its lock, and then sees the committed client.
        /// </summary>
        [TestFixture(ConcurrentCreation.NewApplicationWithClient)]
        [TestFixture(ConcurrentCreation.ClientInClientlessApplication)]
        public class Given_a_guarded_vendor_delete_waiting_on_an_uncommitted_client_creation(
            ConcurrentCreation creation
        ) : GuardedDeleteTests
        {
            private VendorDeleteResult _result = null!;

            [SetUp]
            public async Task Act()
            {
                await using NpgsqlConnection creator = await DataSource!.OpenConnectionAsync();
                await using NpgsqlTransaction creatorTransaction = await creator.BeginTransactionAsync();
                await CreateConcurrentClientAsync(creation, creator, creatorTransaction);

                Task<VendorDeleteResult> delete = DeleteAsync();
                await WaitUntilBlockedAsync(delete, creator.ProcessID, null);
                await creatorTransaction.CommitAsync();
                _result = await delete;
            }

            [Test]
            public void It_refuses_the_cascade() =>
                _result.Should().BeOfType<VendorDeleteResult.FailureClientsChanged>();

            [Test]
            public async Task It_keeps_the_vendor_and_every_client()
            {
                (await VendorExistsAsync()).Should().BeTrue();
                (await ClientCountAsync(_cleanedClient.ClientUuid, _concurrentClientUuid)).Should().Be(2);
            }
        }

        /// <summary>
        /// The creation arrives while the guarded delete holds its vendor and application locks:
        /// a held update of the cleaned client parks the delete at its client read, the creation
        /// is shown to wait on the delete, and once the update commits the delete removes the
        /// vendor and the creation fails on the vanished parent, leaving no client behind.
        /// </summary>
        [TestFixture(ConcurrentCreation.NewApplicationWithClient)]
        [TestFixture(ConcurrentCreation.ClientInClientlessApplication)]
        public class Given_a_client_creation_arriving_while_the_guarded_vendor_delete_holds_its_locks(
            ConcurrentCreation creation
        ) : GuardedDeleteTests
        {
            private VendorDeleteResult _result = null!;
            private string? _creationSqlState;

            [SetUp]
            public async Task Act()
            {
                await using NpgsqlConnection holder = await DataSource!.OpenConnectionAsync();
                await using NpgsqlTransaction holderTransaction = await holder.BeginTransactionAsync();
                await holder.ExecuteAsync(
                    """UPDATE "dmscs"."ApiClient" SET "Name" = 'Held Client' WHERE "Id" = @Id;""",
                    new { _cleanedClient.Id },
                    holderTransaction
                );

                Task<VendorDeleteResult> delete = DeleteAsync();
                await WaitUntilBlockedAsync(delete, holder.ProcessID, null);

                await using NpgsqlConnection creator = await DataSource!.OpenConnectionAsync();
                Task create = CreateConcurrentClientAsync(creation, creator, null);
                await WaitUntilBlockedAsync(create, null, creator.ProcessID);

                await holderTransaction.CommitAsync();
                _result = await delete;
                try
                {
                    await create;
                }
                catch (PostgresException ex)
                {
                    _creationSqlState = ex.SqlState;
                }
            }

            [Test]
            public void It_deletes_the_vendor() => _result.Should().BeOfType<VendorDeleteResult.Success>();

            [Test]
            public void It_rejects_the_creation_on_the_vanished_parent() =>
                _creationSqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

            [Test]
            public async Task It_leaves_no_client_behind()
            {
                (await VendorExistsAsync()).Should().BeFalse();
                (await ClientCountAsync(_cleanedClient.ClientUuid, _concurrentClientUuid)).Should().Be(0);
            }
        }
    }
}
