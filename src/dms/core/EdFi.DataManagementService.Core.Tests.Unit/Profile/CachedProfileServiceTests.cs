// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class CachedProfileServiceTests
{
    private const string StudentProfileXml = """
        <Profile name="StudentProfile">
            <Resource name="Student">
                <ReadContentType memberSelection="IncludeOnly">
                    <Property name="firstName"/>
                    <Property name="lastName"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeOnly">
                    <Property name="firstName"/>
                    <Property name="lastName"/>
                </WriteContentType>
            </Resource>
        </Profile>
        """;

    private const string SchoolProfileXml = """
        <Profile name="SchoolProfile">
            <Resource name="School">
                <ReadContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    private const string ReadOnlyProfileXml = """
        <Profile name="ReadOnlyProfile">
            <Resource name="Student">
                <ReadContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    private const string WriteOnlyProfileXml = """
        <Profile name="WriteOnlyProfile">
            <Resource name="Student">
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    private const string InvalidProfileXml = "<Invalid>Not a valid profile</Invalid>";

    protected static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<HybridCache>();
    }

    private static CachedProfileService CreateService(
        IProfileCmsProvider cmsProvider,
        HybridCache? cache = null
    )
    {
        return new CachedProfileService(
            cmsProvider,
            cache ?? CreateHybridCache(),
            new CacheSettings { ProfileCacheExpirationSeconds = 1800 },
            NullLogger<CachedProfileService>.Instance
        );
    }

    [TestFixture]
    public class Given_No_Profiles_Assigned : CachedProfileServiceTests
    {
        [Test]
        public async Task It_returns_no_profile_when_no_header_specified()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));

            var service = CreateService(fakeCmsProvider);

            var result = await service.ResolveProfileAsync(
                parsedHeader: null,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeTrue();
            result.ProfileContext.Should().BeNull();
        }

        [Test]
        public async Task It_returns_406_error_when_profile_header_specified_for_GET()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader("student", "TestProfile", ProfileUsageType.Readable);

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(406);
        }

        [Test]
        public async Task It_returns_415_error_when_profile_header_specified_for_POST()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader("student", "TestProfile", ProfileUsageType.Writable);

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.POST,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(415);
        }

        [Test]
        public async Task It_returns_415_error_when_profile_header_specified_for_PUT()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader("student", "TestProfile", ProfileUsageType.Writable);

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.PUT,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(415);
        }
    }

    [TestFixture]
    public class Given_Profile_Assigned : CachedProfileServiceTests
    {
        [Test]
        public async Task It_returns_profile_context_for_valid_readable_request()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "StudentProfile",
                ProfileUsageType.Readable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeTrue();
            result.ProfileContext.Should().NotBeNull();
            result.ProfileContext!.ProfileName.Should().Be("StudentProfile");
            result.ProfileContext.ContentType.Should().Be(ProfileContentType.Read);
            result.ProfileContext.WasExplicitlySpecified.Should().BeTrue();
        }

        [Test]
        public async Task It_returns_profile_context_for_valid_writable_request()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "StudentProfile",
                ProfileUsageType.Writable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.POST,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeTrue();
            result.ProfileContext!.ContentType.Should().Be(ProfileContentType.Write);
        }

        [Test]
        public async Task It_fails_when_profile_not_assigned_to_application()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader("Student", "OtherProfile", ProfileUsageType.Readable);

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(403);
        }

        [Test]
        public async Task It_fails_when_resource_name_mismatches()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader("School", "StudentProfile", ProfileUsageType.Readable);

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(400);
            result.Error.Errors.Should().Contain(e => e.Contains("does not match"));
        }

        [Test]
        public async Task It_fails_when_readable_header_used_with_POST()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "StudentProfile",
                ProfileUsageType.Readable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.POST,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(400);
            result.Error.Errors.Should().Contain(e => e.Contains("readable cannot be used with POST"));
        }

        [Test]
        public async Task It_fails_when_writable_header_used_with_GET()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "StudentProfile",
                ProfileUsageType.Writable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(400);
            result.Error.Errors.Should().Contain(e => e.Contains("writable cannot be used with GET"));
        }

        [Test]
        public async Task It_fails_when_profile_has_no_read_content_type_for_GET()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "WriteOnlyProfile", WriteOnlyProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "WriteOnlyProfile",
                ProfileUsageType.Readable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(405);
        }

        [Test]
        public async Task It_fails_when_profile_has_no_write_content_type_for_POST()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "ReadOnlyProfile", ReadOnlyProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "ReadOnlyProfile",
                ProfileUsageType.Writable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.POST,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(405);
        }
    }

    [TestFixture]
    public class Given_Implicit_Profile_Selection : CachedProfileServiceTests
    {
        [Test]
        public async Task It_selects_single_applicable_profile_implicitly()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);

            var result = await service.ResolveProfileAsync(
                parsedHeader: null,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeTrue();
            result.ProfileContext.Should().NotBeNull();
            result.ProfileContext!.WasExplicitlySpecified.Should().BeFalse();
        }

        [Test]
        public async Task It_returns_no_profile_when_resource_not_covered()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);

            var result = await service.ResolveProfileAsync(
                parsedHeader: null,
                method: RequestMethod.GET,
                resourceName: "School",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeTrue();
            result.ProfileContext.Should().BeNull();
        }

        [Test]
        public async Task It_returns_error_when_multiple_profiles_apply()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100, 101]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                        new CmsProfileResponse(101, "ReadOnlyProfile", ReadOnlyProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);

            var result = await service.ResolveProfileAsync(
                parsedHeader: null,
                method: RequestMethod.GET,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(403);
            result
                .Error.Errors.Should()
                .Contain(e => e.Contains("profile-specific content types is required"));
        }
    }

    [TestFixture]
    public class Given_Resource_Not_In_Profile : CachedProfileServiceTests
    {
        [Test]
        public async Task It_fails_when_requested_resource_not_in_profile()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader("School", "StudentProfile", ProfileUsageType.Readable);

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.GET,
                resourceName: "School",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeFalse();
            result.Error!.StatusCode.Should().Be(400);
            result.Error.Errors.Should().Contain(e => e.Contains("not accessible"));
        }
    }

    [TestFixture]
    public class Given_Profile_Catalog : CachedProfileServiceTests
    {
        [TestFixture]
        public class Given_No_Profiles_Exist : Given_Profile_Catalog
        {
            [Test]
            public async Task It_returns_empty_list_when_CMS_returns_no_profiles()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(Task.FromResult<IReadOnlyList<CmsProfileResponse>>([]));

                var service = CreateService(fakeCmsProvider);

                var result = await service.GetProfileNamesAsync(null);

                result.Should().NotBeNull();
                result.Should().BeEmpty();
            }

            [Test]
            public async Task It_returns_null_for_any_profile_definition_lookup()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(Task.FromResult<IReadOnlyList<CmsProfileResponse>>([]));

                var service = CreateService(fakeCmsProvider);

                var result = await service.GetProfileDefinitionAsync("NonExistent", null);

                result.Should().BeNull();
            }
        }

        [TestFixture]
        public class Given_Profiles_Exist : Given_Profile_Catalog
        {
            [Test]
            public async Task It_returns_list_of_profile_names()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                            new CmsProfileResponse(2, "SchoolProfile", SchoolProfileXml),
                        ])
                    );

                var service = CreateService(fakeCmsProvider);

                var result = await service.GetProfileNamesAsync(null);

                result.Should().HaveCount(2);
                result.Should().Contain("StudentProfile");
                result.Should().Contain("SchoolProfile");
            }

            [Test]
            public async Task It_provides_case_insensitive_profile_definition_lookup()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                        ])
                    );

                var service = CreateService(fakeCmsProvider);

                var definition1 = await service.GetProfileDefinitionAsync("studentprofile", null);
                var definition2 = await service.GetProfileDefinitionAsync("STUDENTPROFILE", null);
                var definition3 = await service.GetProfileDefinitionAsync("StudentProfile", null);

                definition1.Should().NotBeNull();
                definition2.Should().NotBeNull();
                definition3.Should().NotBeNull();
                definition1!.ProfileName.Should().Be("StudentProfile");
            }

            [Test]
            public async Task It_skips_invalid_profile_definitions()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                            new CmsProfileResponse(2, "InvalidProfile", InvalidProfileXml),
                            new CmsProfileResponse(3, "SchoolProfile", SchoolProfileXml),
                        ])
                    );

                var service = CreateService(fakeCmsProvider);

                var result = await service.GetProfileNamesAsync(null);

                result.Should().HaveCount(2);
                result.Should().Contain("StudentProfile");
                result.Should().Contain("SchoolProfile");
                result.Should().NotContain("InvalidProfile");
            }

            [Test]
            public async Task It_preserves_original_case_in_profile_names()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                        ])
                    );

                var service = CreateService(fakeCmsProvider);

                var result = await service.GetProfileNamesAsync(null);

                result.Should().Contain("StudentProfile");
                result.Should().NotContain("studentprofile");
            }

            [Test]
            public async Task It_returns_profile_definition_with_correct_resources()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                        ])
                    );

                var service = CreateService(fakeCmsProvider);

                var definition = await service.GetProfileDefinitionAsync("StudentProfile", null);

                definition.Should().NotBeNull();
                definition!.ProfileName.Should().Be("StudentProfile");
                definition.Resources.Should().HaveCount(1);
                definition.Resources[0].ResourceName.Should().Be("Student");
            }
        }

        [TestFixture]
        public class Given_Caching_Behavior : Given_Profile_Catalog
        {
            [Test]
            public async Task It_caches_catalog_and_returns_same_result()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                        ])
                    );

                var sharedCache = CreateHybridCache();
                var service = CreateService(fakeCmsProvider, sharedCache);

                var result1 = await service.GetProfileNamesAsync(null);
                var result2 = await service.GetProfileNamesAsync(null);

                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._)).MustHaveHappenedOnceExactly();

                result1.Should().BeEquivalentTo(result2);
            }

            [Test]
            public async Task It_uses_separate_cache_keys_per_tenant()
            {
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync("tenant1"))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                        ])
                    );
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync("tenant2"))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(2, "SchoolProfile", SchoolProfileXml),
                        ])
                    );

                var sharedCache = CreateHybridCache();
                var service = CreateService(fakeCmsProvider, sharedCache);

                var result1 = await service.GetProfileNamesAsync("tenant1");
                var result2 = await service.GetProfileNamesAsync("tenant2");

                result1.Should().Contain("StudentProfile");
                result1.Should().NotContain("SchoolProfile");
                result2.Should().Contain("SchoolProfile");
                result2.Should().NotContain("StudentProfile");
            }
        }

        [TestFixture]
        public class Given_CachedProfileStore_TryGetProfile : Given_Profile_Catalog
        {
            [Test]
            public void It_returns_false_when_profile_not_found()
            {
                var store = new CachedProfileStore(
                    new Dictionary<string, ProfileDefinition>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<long, string>()
                );

                var result = store.TryGetByName("NonExistent", out var definition);

                result.Should().BeFalse();
                definition.Should().BeNull();
            }

            [Test]
            public void It_returns_true_and_definition_when_profile_found()
            {
                var profileDef = ProfileDefinitionParser.Parse(StudentProfileXml).Definition!;
                var store = new CachedProfileStore(
                    new Dictionary<string, ProfileDefinition>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["StudentProfile"] = profileDef,
                    },
                    new Dictionary<long, string> { [1] = "StudentProfile" }
                );

                var result = store.TryGetByName("StudentProfile", out var definition);

                result.Should().BeTrue();
                definition.Should().NotBeNull();
                definition!.ProfileName.Should().Be("StudentProfile");
            }

            [Test]
            public void It_returns_true_for_id_lookup()
            {
                var profileDef = ProfileDefinitionParser.Parse(StudentProfileXml).Definition!;
                var store = new CachedProfileStore(
                    new Dictionary<string, ProfileDefinition>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["StudentProfile"] = profileDef,
                    },
                    new Dictionary<long, string> { [100] = "StudentProfile" }
                );

                var result = store.TryGetById(100, out var definition);

                result.Should().BeTrue();
                definition.Should().NotBeNull();
                definition!.ProfileName.Should().Be("StudentProfile");
            }
        }
    }
}
