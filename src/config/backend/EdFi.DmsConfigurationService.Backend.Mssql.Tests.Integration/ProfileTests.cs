// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class ProfileTests : DatabaseTest
{
    private readonly IProfileRepository _repository = new ProfileRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<ProfileRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    private async Task ResetProfiles(params string[] names)
    {
        foreach (var name in names)
        {
            await Connection!.ExecuteAsync(
                @"DELETE FROM dmscs.Profile WHERE ProfileName = @Name;",
                new { Name = name }
            );
        }
    }

    private static readonly string[] EarlySortedProfileNames =
    [
        "000-DMS1074-Alpha",
        "000-DMS1074-Bravo",
        "000-DMS1074-Charlie",
    ];

    private static readonly string[] LateSortedProfileNames =
    [
        "~~~DMS1074-Charlie",
        "~~~DMS1074-Alpha",
        "~~~DMS1074-Bravo",
    ];

    private static string CreateDefinition(string name) =>
        $"""
            <Profile name="{name}">
              <Resource name="School">
                <ReadContentType memberSelection="IncludeOnly">
                  <Property name="NameOfInstitution" />
                </ReadContentType>
              </Resource>
            </Profile>
            """;

    [TestFixture]
    public class QueryPagingTests : ProfileTests
    {
        [SetUp]
        public async Task Setup()
        {
            await ResetProfiles(EarlySortedProfileNames);
            foreach (var name in EarlySortedProfileNames)
            {
                var result = await _repository.InsertProfile(
                    new ProfileInsertCommand { Name = name, Definition = CreateDefinition(name) }
                );
                result.Should().BeOfType<ProfileInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { OrderBy = "name", Direction = "ASC" }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(EarlySortedProfileNames);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 2,
                }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(EarlySortedProfileNames[..2]);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 2,
                    Offset = 1,
                }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(EarlySortedProfileNames[1..]);
        }

        [Test]
        public async Task Should_apply_offset_after_excluding_invalid_profiles()
        {
            await ResetProfiles([
                .. EarlySortedProfileNames,
                "000-DMS1074-Invalid",
                "001-DMS1074-Valid",
                "002-DMS1074-Valid",
            ]);

            var invalidInsert = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "000-DMS1074-Invalid",
                    Definition = @"<Profile><Resource name=""School""></Resource></Profile>",
                }
            );
            invalidInsert.Should().BeOfType<ProfileInsertResult.Success>();

            var validAInsert = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "001-DMS1074-Valid",
                    Definition = CreateDefinition("001-DMS1074-Valid"),
                }
            );
            validAInsert.Should().BeOfType<ProfileInsertResult.Success>();

            var validBInsert = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "002-DMS1074-Valid",
                    Definition = CreateDefinition("002-DMS1074-Valid"),
                }
            );
            validBInsert.Should().BeOfType<ProfileInsertResult.Success>();

            var result = await _repository.QueryProfiles(
                new ProfileQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 1,
                    Offset = 1,
                }
            );

            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("00"))
                .ToList();

            names.Should().ContainSingle().Which.Should().Be("002-DMS1074-Valid");
        }
    }

    [TestFixture]
    public class QuerySortTests : ProfileTests
    {
        [SetUp]
        public async Task Setup()
        {
            await ResetProfiles(LateSortedProfileNames);
            foreach (var name in LateSortedProfileNames)
            {
                var result = await _repository.InsertProfile(
                    new ProfileInsertCommand { Name = name, Definition = CreateDefinition(name) }
                );
                result.Should().BeOfType<ProfileInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { OrderBy = "name", Direction = "ASC" }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("~~~DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(LateSortedProfileNames.OrderBy(name => name).ToArray());
        }

        [Test]
        public async Task Should_return_descending_order_by_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { OrderBy = "name", Direction = "DESC" }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("~~~DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(LateSortedProfileNames.OrderByDescending(name => name).ToArray());
        }
    }

    [TestFixture]
    public class QueryFilterTests : ProfileTests
    {
        private int _profileId;

        [SetUp]
        public async Task Setup()
        {
            await ResetProfiles("DMS1074-FilteredProfile", "DMS1074-OtherProfile");
            var profileResult = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "DMS1074-FilteredProfile",
                    Definition = CreateDefinition("DMS1074-FilteredProfile"),
                }
            );
            _profileId = ((ProfileInsertResult.Success)profileResult).Id;

            var otherResult = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "DMS1074-OtherProfile",
                    Definition = CreateDefinition("DMS1074-OtherProfile"),
                }
            );
            otherResult.Should().BeOfType<ProfileInsertResult.Success>();
        }

        [Test]
        public async Task Should_filter_by_id()
        {
            var result = await _repository.QueryProfiles(new ProfileQuery { Id = _profileId });
            var profiles = result.OfType<ProfileGetResult.Success>().ToList();
            profiles.Should().ContainSingle();
            profiles[0].Profile.Id.Should().Be(_profileId);
        }

        [Test]
        public async Task Should_filter_by_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { Name = "DMS1074-FilteredProfile" }
            );
            var profiles = result.OfType<ProfileGetResult.Success>().ToList();
            profiles.Should().ContainSingle();
            profiles[0].Profile.Name.Should().Be("DMS1074-FilteredProfile");
        }

        [Test]
        public async Task Should_filter_by_id_and_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { Id = _profileId, Name = "DMS1074-FilteredProfile" }
            );
            var profiles = result.OfType<ProfileGetResult.Success>().ToList();
            profiles.Should().ContainSingle();
            profiles[0].Profile.Id.Should().Be(_profileId);
            profiles[0].Profile.Name.Should().Be("DMS1074-FilteredProfile");
        }
    }

    [TestFixture]
    public class Given_profiles_in_two_tenants : ProfileTests
    {
        private const string SharedName = "DMS1530-Shared-Profile";
        private const string SingleTenantName = "DMS1530-Single-Tenant-Profile";

        private TenantContextProvider _tenantAProvider = null!;
        private IProfileRepository _tenantARepository = null!;
        private IProfileRepository _tenantBRepository = null!;
        private int _tenantAProfileId;
        private int _tenantBProfileId;
        private int _singleTenantProfileId;

        [SetUp]
        public async Task Setup()
        {
            var tenantRepository = new TenantRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );

            _tenantAProvider = await CreateTenantProvider(tenantRepository, "A");
            TenantContextProvider tenantBProvider = await CreateTenantProvider(tenantRepository, "B");

            _tenantARepository = CreateProfileRepository(_tenantAProvider);
            _tenantBRepository = CreateProfileRepository(tenantBProvider);

            _tenantAProfileId = await InsertProfile(_tenantARepository, SharedName);
            _tenantBProfileId = await InsertProfile(_tenantBRepository, SharedName);
            _singleTenantProfileId = await InsertProfile(_repository, SingleTenantName);
        }

        private static async Task<TenantContextProvider> CreateTenantProvider(
            TenantRepository tenantRepository,
            string suffix
        )
        {
            var tenantName = $"ProfileTenant{suffix}-{Guid.NewGuid()}";
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

        private static ProfileRepository CreateProfileRepository(
            TenantContextProvider tenantContextProvider
        ) =>
            new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ProfileRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );

        private static async Task<int> InsertProfile(IProfileRepository repository, string name)
        {
            var result = await repository.InsertProfile(
                new ProfileInsertCommand { Name = name, Definition = CreateDefinition(name) }
            );
            result.Should().BeOfType<ProfileInsertResult.Success>();
            return ((ProfileInsertResult.Success)result).Id;
        }

        private static async Task<int[]> QueryIds(IProfileRepository repository, ProfileQuery query)
        {
            var results = (await repository.QueryProfiles(query)).ToList();
            results.Should().AllBeOfType<ProfileGetResult.Success>();
            return [.. results.OfType<ProfileGetResult.Success>().Select(result => result.Profile.Id)];
        }

        private async Task AssertTenantAProfileUnchanged()
        {
            var result = await _tenantARepository.GetProfile(_tenantAProfileId);
            result.Should().BeOfType<ProfileGetResult.Success>();
            var profile = ((ProfileGetResult.Success)result).Profile;
            profile.Name.Should().Be(SharedName);
            profile.Definition.Should().Be(CreateDefinition(SharedName));
        }

        [Test]
        public void It_should_insert_the_same_name_in_both_tenants() =>
            _tenantBProfileId.Should().NotBe(_tenantAProfileId);

        [Test]
        public async Task It_should_not_get_another_tenants_profile()
        {
            var result = await _tenantBRepository.GetProfile(_tenantAProfileId);
            result.Should().BeOfType<ProfileGetResult.FailureNotFound>();
        }

        [Test]
        public async Task It_should_not_update_another_tenants_profile()
        {
            var result = await _tenantBRepository.UpdateProfile(
                new ProfileUpdateCommand
                {
                    Id = _tenantAProfileId,
                    Name = "DMS1530-Hijacked",
                    Definition = CreateDefinition("DMS1530-Hijacked"),
                }
            );
            result.Should().BeOfType<ProfileUpdateResult.FailureNotExists>();
            await AssertTenantAProfileUnchanged();
        }

        [Test]
        public async Task It_should_not_delete_another_tenants_profile()
        {
            var result = await _tenantBRepository.DeleteProfile(_tenantAProfileId);
            result.Should().BeOfType<ProfileDeleteResult.FailureNotExists>();
            await AssertTenantAProfileUnchanged();
        }

        [Test]
        public async Task It_should_list_only_its_own_tenants_profiles()
        {
            int[] ids = await QueryIds(_tenantBRepository, new ProfileQuery());
            ids.Should().Equal(_tenantBProfileId);
        }

        [Test]
        public async Task It_should_not_find_another_tenants_profile_by_name_or_id()
        {
            (await QueryIds(_tenantBRepository, new ProfileQuery { Name = SharedName }))
                .Should()
                .Equal(_tenantBProfileId);
            (await QueryIds(_tenantBRepository, new ProfileQuery { Id = _tenantAProfileId }))
                .Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_should_reject_a_duplicate_name_within_a_tenant_on_insert()
        {
            var result = await _tenantARepository.InsertProfile(
                new ProfileInsertCommand { Name = SharedName, Definition = CreateDefinition(SharedName) }
            );
            result.Should().BeOfType<ProfileInsertResult.FailureDuplicateName>();
        }

        [Test]
        public async Task It_should_reject_a_duplicate_name_within_a_tenant_on_rename()
        {
            int otherId = await InsertProfile(_tenantARepository, "DMS1530-Other-Profile");

            var result = await _tenantARepository.UpdateProfile(
                new ProfileUpdateCommand
                {
                    Id = otherId,
                    Name = SharedName,
                    Definition = CreateDefinition(SharedName),
                }
            );
            result.Should().BeOfType<ProfileUpdateResult.FailureDuplicateName>();
        }

        [Test]
        public async Task It_should_reject_a_duplicate_name_in_single_tenant_mode_on_insert()
        {
            var result = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = SingleTenantName,
                    Definition = CreateDefinition(SingleTenantName),
                }
            );
            result.Should().BeOfType<ProfileInsertResult.FailureDuplicateName>();
        }

        [Test]
        public async Task It_should_reject_a_duplicate_name_in_single_tenant_mode_on_rename()
        {
            int otherId = await InsertProfile(_repository, "DMS1530-Other-Single-Tenant-Profile");

            var result = await _repository.UpdateProfile(
                new ProfileUpdateCommand
                {
                    Id = otherId,
                    Name = SingleTenantName,
                    Definition = CreateDefinition(SingleTenantName),
                }
            );
            result.Should().BeOfType<ProfileUpdateResult.FailureDuplicateName>();
        }

        [Test]
        public async Task It_should_keep_single_tenant_and_tenant_profiles_apart()
        {
            (await _repository.GetProfile(_tenantAProfileId))
                .Should()
                .BeOfType<ProfileGetResult.FailureNotFound>();
            (await QueryIds(_repository, new ProfileQuery())).Should().Equal(_singleTenantProfileId);

            (await _tenantARepository.GetProfile(_singleTenantProfileId))
                .Should()
                .BeOfType<ProfileGetResult.FailureNotFound>();
            (await QueryIds(_tenantARepository, new ProfileQuery())).Should().Equal(_tenantAProfileId);
        }

        [Test]
        public async Task It_should_report_an_in_use_own_tenant_profile_on_delete()
        {
            var vendorResult = await new VendorRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                _tenantAProvider
            ).InsertVendor(
                new VendorInsertCommand
                {
                    Company = "DMS1530 Profile Tenant A Company",
                    ContactEmailAddress = "tenant@test.com",
                    ContactName = "Tenant Tester",
                    NamespacePrefixes = "uri://tenant-test.example",
                }
            );
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();

            var applicationResult = await new ApplicationRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                _tenantAProvider
            ).InsertApplication(
                new ApplicationInsertCommand
                {
                    ApplicationName = "DMS1530 Profile Tenant A Application",
                    VendorId = ((VendorInsertResult.Success)vendorResult).Id,
                    ClaimSetName = "Test Claim set",
                    EducationOrganizationIds = [],
                    ProfileIds = [_tenantAProfileId],
                },
                new ApiClientCommand { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();

            var result = await _tenantARepository.DeleteProfile(_tenantAProfileId);
            result.Should().BeOfType<ProfileDeleteResult.FailureInUse>();
        }
    }
}
