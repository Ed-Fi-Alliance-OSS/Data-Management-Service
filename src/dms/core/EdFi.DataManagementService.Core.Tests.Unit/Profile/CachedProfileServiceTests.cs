// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.ApiSchema;
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

    private const string WarningProfileXml = """
        <Profile name="WarningProfile">
            <Resource name="School">
                <ReadContentType memberSelection="ExcludeOnly">
                    <Property name="schoolId"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    private const string ErrorProfileXml = """
        <Profile name="ErrorProfile">
            <Resource name="School">
                <ReadContentType memberSelection="ExcludeOnly">
                    <Property name="schoolId"/>
                </ReadContentType>
                <WriteContentType memberSelection="IncludeAll"/>
            </Resource>
        </Profile>
        """;

    protected static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<HybridCache>();
    }

    private static CachedProfileService CreateService(
        IProfileCmsProvider cmsProvider,
        HybridCache? cache = null,
        IProfileDataValidator? profileDataValidator = null,
        IEffectiveApiSchemaProvider? effectiveApiSchemaProvider = null
    )
    {
        // Default validator returns success for all profiles
        var validator = profileDataValidator ?? A.Fake<IProfileDataValidator>();
        if (profileDataValidator is null)
        {
            A.CallTo(() => validator.Validate(A<ProfileDefinition>._, A<IEffectiveApiSchemaProvider>._))
                .Returns(ProfileValidationResult.Success);
        }

        // The service canonicalizes extension names against the schema after validation,
        // so the provider must expose a non-null ApiSchemaDocuments. These profiles have
        // no extensions, so canonicalization is a no-op.
        var schemaProvider = effectiveApiSchemaProvider ?? A.Fake<IEffectiveApiSchemaProvider>();
        if (effectiveApiSchemaProvider is null)
        {
            A.CallTo(() => schemaProvider.Documents).Returns(CreateMinimalApiSchemaDocuments());
        }

        return new CachedProfileService(
            cmsProvider,
            validator,
            schemaProvider,
            cache ?? CreateHybridCache(),
            new CacheSettings { ProfileCacheExpirationSeconds = 1800 },
            NullLogger<CachedProfileService>.Instance
        );
    }

    private static ApiSchemaDocuments CreateMinimalApiSchemaDocuments() =>
        new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("Student")
            .WithEndResource()
            .WithStartResource("School")
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

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
        public async Task It_returns_profile_context_when_profile_header_specified_for_GET()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "student",
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
        public async Task It_returns_profile_context_when_profile_header_specified_for_POST()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "student",
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
            result.ProfileContext.Should().NotBeNull();
            result.ProfileContext!.ProfileName.Should().Be("StudentProfile");
            result.ProfileContext.ContentType.Should().Be(ProfileContentType.Write);
            result.ProfileContext.WasExplicitlySpecified.Should().BeTrue();
        }

        [Test]
        public async Task It_returns_profile_context_when_profile_header_specified_for_PUT()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(Task.FromResult<ApplicationProfileInfo?>(null));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                    ])
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "student",
                "StudentProfile",
                ProfileUsageType.Writable
            );

            var result = await service.ResolveProfileAsync(
                parsedHeader: parsedHeader,
                method: RequestMethod.PUT,
                resourceName: "Student",
                applicationId: 1,
                tenantId: null
            );

            result.IsSuccess.Should().BeTrue();
            result.ProfileContext.Should().NotBeNull();
            result.ProfileContext!.ProfileName.Should().Be("StudentProfile");
            result.ProfileContext.ContentType.Should().Be(ProfileContentType.Write);
            result.ProfileContext.WasExplicitlySpecified.Should().BeTrue();
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "WriteOnlyProfile", WriteOnlyProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "ReadOnlyProfile", ReadOnlyProfileXml)
                    )
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
        public async Task It_returns_implicit_profile_when_single_applicable_profile_is_not_explicitly_selected()
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            result.ProfileContext!.ProfileName.Should().Be("StudentProfile");
            result.ProfileContext.ContentType.Should().Be(ProfileContentType.Read);
            result.ProfileContext.WasExplicitlySpecified.Should().BeFalse();
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(101, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(101, "ReadOnlyProfile", ReadOnlyProfileXml)
                    )
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

        [Test]
        public async Task It_returns_implicit_profile_when_multiple_profiles_are_assigned_but_only_one_applies()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100, 101]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "SchoolProfile", SchoolProfileXml),
                        new CmsProfileResponse(101, "StudentProfile", StudentProfileXml),
                    ])
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "SchoolProfile", SchoolProfileXml)
                    )
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(101, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(101, "StudentProfile", StudentProfileXml)
                    )
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
            result.ProfileContext.Should().NotBeNull();
            result.ProfileContext!.ProfileName.Should().Be("SchoolProfile");
            result.ProfileContext.ContentType.Should().Be(ProfileContentType.Read);
            result.ProfileContext.WasExplicitlySpecified.Should().BeFalse();
        }

        [Test]
        public async Task It_returns_no_profile_when_assigned_profiles_do_not_support_the_method()
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "WriteOnlyProfile", WriteOnlyProfileXml)
                    )
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
            result.ProfileContext.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Resource_Not_In_Profile : CachedProfileServiceTests
    {
        [Test]
        public async Task It_allows_explicit_profile_when_assigned_profiles_do_not_apply_to_the_resource()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [101]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                        new CmsProfileResponse(101, "SchoolProfile", SchoolProfileXml),
                    ])
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(101, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(101, "SchoolProfile", SchoolProfileXml)
                    )
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
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

        [Test]
        public async Task It_returns_invalid_profile_usage_when_assigned_profiles_do_not_apply()
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(101, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(101, "ReadOnlyProfile", ReadOnlyProfileXml)
                    )
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
            result.Error.ErrorType.Should().Be("urn:ed-fi:api:profile:invalid-profile-usage");
        }

        [Test]
        public async Task It_recommends_only_profiles_that_support_the_requested_resource_and_method()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [100, 101, 102]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml),
                        new CmsProfileResponse(101, "SchoolProfile", SchoolProfileXml),
                        new CmsProfileResponse(102, "WriteOnlyProfile", WriteOnlyProfileXml),
                    ])
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(100, "StudentProfile", StudentProfileXml)
                    )
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(101, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(101, "SchoolProfile", SchoolProfileXml)
                    )
                );
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(102, A<string?>._))
                .Returns(
                    Task.FromResult<CmsProfileResponse?>(
                        new CmsProfileResponse(102, "WriteOnlyProfile", WriteOnlyProfileXml)
                    )
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
            result.Error!.StatusCode.Should().Be(403);
            result.Error.Errors.Should().ContainSingle();
            result
                .Error.Errors[0]
                .Should()
                .Contain("application/vnd.ed-fi.school.schoolprofile.readable+json");
            result
                .Error.Errors[0]
                .Should()
                .NotContain("application/vnd.ed-fi.school.studentprofile.readable+json");
            result
                .Error.Errors[0]
                .Should()
                .NotContain("application/vnd.ed-fi.school.writeonlyprofile.readable+json");
        }

        [Test]
        public async Task It_returns_profile_not_found_when_explicit_profile_does_not_exist()
        {
            var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
            A.CallTo(() => fakeCmsProvider.GetApplicationProfileInfoAsync(A<long>._, A<string?>._))
                .Returns(new ApplicationProfileInfo(1, [101]));
            A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                .Returns(
                    Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                        new CmsProfileResponse(101, "SchoolProfile", SchoolProfileXml),
                    ])
                );

            var service = CreateService(fakeCmsProvider);
            var parsedHeader = new ParsedProfileHeader(
                "Student",
                "MissingProfile",
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
            result.Error!.StatusCode.Should().Be(406);
            result.Error.ErrorType.Should().Be("urn:ed-fi:api:profile:invalid-profile-usage");
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
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(2, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(2, "SchoolProfile", SchoolProfileXml)
                        )
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
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
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
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(2, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(2, "InvalidProfile", InvalidProfileXml)
                        )
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(3, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(3, "SchoolProfile", SchoolProfileXml)
                        )
                    );

                var service = CreateService(fakeCmsProvider);

                var result = await service.GetProfileNamesAsync(null);

                result.Should().HaveCount(2);
                result.Should().Contain("StudentProfile");
                result.Should().Contain("SchoolProfile");
                result.Should().NotContain("InvalidProfile");
            }

            [Test]
            public async Task It_includes_profiles_with_warnings_only()
            {
                // Arrange - Set up fake CMS provider
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                            new CmsProfileResponse(2, "WarningProfile", WarningProfileXml),
                        ])
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(2, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(2, "WarningProfile", WarningProfileXml)
                        )
                    );

                // Arrange - Set up validator that returns warnings for WarningProfile
                var fakeValidator = A.Fake<IProfileDataValidator>();
                A.CallTo(() =>
                        fakeValidator.Validate(
                            A<ProfileDefinition>.That.Matches(p => p.ProfileName == "StudentProfile"),
                            A<IEffectiveApiSchemaProvider>._
                        )
                    )
                    .Returns(ProfileValidationResult.Success);

                A.CallTo(() =>
                        fakeValidator.Validate(
                            A<ProfileDefinition>.That.Matches(p => p.ProfileName == "WarningProfile"),
                            A<IEffectiveApiSchemaProvider>._
                        )
                    )
                    .Returns(
                        new ProfileValidationResult([
                            new ValidationFailure(
                                ValidationSeverity.Warning,
                                "WarningProfile",
                                "School",
                                "schoolId",
                                "ExcludeOnly excluding identity member"
                            ),
                        ])
                    );

                var service = CreateService(fakeCmsProvider, profileDataValidator: fakeValidator);

                // Act
                var profileNames = await service.GetProfileNamesAsync(null);
                var profileDefinition = await service.GetProfileDefinitionAsync("WarningProfile", null);

                // Assert - Profile with warnings should be included in catalog
                profileNames.Should().HaveCount(2);
                profileNames.Should().Contain("StudentProfile");
                profileNames.Should().Contain("WarningProfile");

                // Assert - Profile with warnings should be retrievable
                profileDefinition.Should().NotBeNull();
                profileDefinition!.ProfileName.Should().Be("WarningProfile");
                profileDefinition.Resources.Should().HaveCount(1);
                profileDefinition.Resources[0].ResourceName.Should().Be("School");
            }

            [Test]
            public async Task It_excludes_profiles_with_errors_even_if_warnings_present()
            {
                // Arrange - Set up fake CMS provider
                var fakeCmsProvider = A.Fake<IProfileCmsProvider>();
                A.CallTo(() => fakeCmsProvider.GetProfilesAsync(A<string?>._))
                    .Returns(
                        Task.FromResult<IReadOnlyList<CmsProfileResponse>>([
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml),
                            new CmsProfileResponse(2, "ErrorProfile", ErrorProfileXml),
                        ])
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(2, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(2, "ErrorProfile", ErrorProfileXml)
                        )
                    );

                // Arrange - Set up validator that returns both errors and warnings
                var fakeValidator = A.Fake<IProfileDataValidator>();
                A.CallTo(() =>
                        fakeValidator.Validate(
                            A<ProfileDefinition>.That.Matches(p => p.ProfileName == "StudentProfile"),
                            A<IEffectiveApiSchemaProvider>._
                        )
                    )
                    .Returns(ProfileValidationResult.Success);

                A.CallTo(() =>
                        fakeValidator.Validate(
                            A<ProfileDefinition>.That.Matches(p => p.ProfileName == "ErrorProfile"),
                            A<IEffectiveApiSchemaProvider>._
                        )
                    )
                    .Returns(
                        new ProfileValidationResult([
                            new ValidationFailure(
                                ValidationSeverity.Error,
                                "ErrorProfile",
                                "School",
                                "invalidProperty",
                                "Property does not exist"
                            ),
                            new ValidationFailure(
                                ValidationSeverity.Warning,
                                "ErrorProfile",
                                "School",
                                "schoolId",
                                "ExcludeOnly excluding identity member"
                            ),
                        ])
                    );

                var service = CreateService(fakeCmsProvider, profileDataValidator: fakeValidator);

                // Act
                var profileNames = await service.GetProfileNamesAsync(null);
                var profileDefinition = await service.GetProfileDefinitionAsync("ErrorProfile", null);

                // Assert - Profile with errors should be excluded from catalog
                profileNames.Should().HaveCount(1);
                profileNames.Should().Contain("StudentProfile");
                profileNames.Should().NotContain("ErrorProfile");

                // Assert - Profile with errors should not be retrievable
                profileDefinition.Should().BeNull();
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
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
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
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
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
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, A<string?>._))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
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
                            new CmsProfileResponse(1, "SchoolProfile", SchoolProfileXml),
                        ])
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, "tenant1"))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "StudentProfile", StudentProfileXml)
                        )
                    );
                A.CallTo(() => fakeCmsProvider.GetProfileAsync(1, "tenant2"))
                    .Returns(
                        Task.FromResult<CmsProfileResponse?>(
                            new CmsProfileResponse(1, "SchoolProfile", SchoolProfileXml)
                        )
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

    /// <summary>
    /// Regression fixtures for CMS failures (DMS-1557). They run through the real provider and the
    /// production response handler over a scripted CMS, because a faked provider that throws would
    /// pass on the pre-fix code: the old service never swallowed exceptions, the old provider did.
    /// CMS serves StudentProfile (id 5, IncludeOnly on Student) and SchoolProfile (id 6), and
    /// application 7 is assigned StudentProfile.
    /// </summary>
    public abstract class Given_A_Cms_Backed_Profile_Service : CachedProfileServiceTests
    {
        protected const string CatalogPath = "/v3/profiles";
        protected const string StudentProfilePath = "/v3/profiles/5";
        protected const string SchoolProfilePath = "/v3/profiles/6";
        protected const string ApplicationPath = "/v3/applications/7";
        protected const long ApplicationId = 7;

        private protected CmsProfileHttpDouble Cms { get; private set; } = null!;

        protected HybridCache Cache { get; private set; } = null!;

        private protected CachedProfileService Service { get; private set; } = null!;

        private protected static CmsReply InternalServerError =>
            CmsReply.Status(HttpStatusCode.InternalServerError);

        [SetUp]
        public async Task CreateCmsBackedService()
        {
            Cms = new CmsProfileHttpDouble();
            Cms.Serve(
                CatalogPath,
                CmsReply.Json("""[{"id":5,"name":"StudentProfile"},{"id":6,"name":"SchoolProfile"}]""")
            );
            Cms.Serve(StudentProfilePath, CmsReply.Json(ProfileJson(5, "StudentProfile", StudentProfileXml)));
            Cms.Serve(SchoolProfilePath, CmsReply.Json(ProfileJson(6, "SchoolProfile", SchoolProfileXml)));
            Cms.Serve(
                ApplicationPath,
                CmsReply.Json(
                    """{"id":7,"applicationName":"App","vendorId":1,"claimSetName":"SIS","educationOrganizationIds":[],"dataStoreIds":[],"profileIds":[5]}"""
                )
            );

            Cache = CreateHybridCache();
            Service = CreateService(Cms.CreateProvider(), Cache);

            await Act();
        }

        [TearDown]
        public void DisposeCms() => Cms.Dispose();

        protected abstract Task Act();

        protected Task<ProfileResolutionResult> ImplicitGet(string? tenantId = null) =>
            Service.ResolveProfileAsync(null, RequestMethod.GET, "Student", ApplicationId, tenantId);

        protected Task<ProfileResolutionResult> ExplicitGet(string? tenantId = null) =>
            Service.ResolveProfileAsync(
                new ParsedProfileHeader("Student", "StudentProfile", ProfileUsageType.Readable),
                RequestMethod.GET,
                "Student",
                ApplicationId,
                tenantId
            );

        protected Task<ProfileResolutionResult> ExplicitPost(string? tenantId = null) =>
            Service.ResolveProfileAsync(
                new ParsedProfileHeader("Student", "StudentProfile", ProfileUsageType.Writable),
                RequestMethod.POST,
                "Student",
                ApplicationId,
                tenantId
            );

        protected static async Task<Exception?> CaptureFailure(Func<Task> call)
        {
            try
            {
                await call();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        protected static void AssertExplicitStudentProfile(ProfileResolutionResult result)
        {
            result.IsSuccess.Should().BeTrue();
            result.ProfileContext!.ProfileName.Should().Be("StudentProfile");
            result.ProfileContext.WasExplicitlySpecified.Should().BeTrue();
        }

        protected static void AssertImplicitStudentProfile(ProfileResolutionResult result)
        {
            result.IsSuccess.Should().BeTrue();
            result.ProfileContext!.ProfileName.Should().Be("StudentProfile");
            result.ProfileContext.WasExplicitlySpecified.Should().BeFalse();
            result
                .ProfileContext.ResourceProfile.ReadContentType!.MemberSelection.Should()
                .Be(MemberSelection.IncludeOnly);
        }

        private static string ProfileJson(long id, string name, string definition) =>
            JsonSerializer.Serialize(
                new
                {
                    id,
                    name,
                    definition,
                }
            );
    }

    /// <summary>
    /// The ticket's failure: one CMS call fails once. The failure must not be cached as "absent"
    /// or "no profiles"; the very next request recovers without waiting for expiry, and the
    /// successful result is cached.
    /// </summary>
    [TestFixture(CatalogPath, HttpStatusCode.InternalServerError)]
    [TestFixture(StudentProfilePath, HttpStatusCode.InternalServerError)]
    [TestFixture(ApplicationPath, HttpStatusCode.InternalServerError)]
    // The application id comes from a resolved client, so CMS not finding it is a failure too,
    // never a cached "no profiles assigned".
    [TestFixture(ApplicationPath, HttpStatusCode.NotFound)]
    public class Given_A_Cms_Endpoint_That_Fails_Once(string failingPath, HttpStatusCode failureStatus)
        : Given_A_Cms_Backed_Profile_Service
    {
        private Exception? _firstFailure;
        private ProfileResolutionResult _explicitResult = null!;
        private ProfileResolutionResult _implicitResult = null!;
        private int _failingPathRequestsBeforeFourthCall;
        private int _requestsBeforeFourthCall;
        private int _requestsAfterFourthCall;

        protected override async Task Act()
        {
            Cms.FailTimes(failingPath, CmsReply.Status(failureStatus), times: 1);

            _firstFailure = await CaptureFailure(() => ImplicitGet());
            _explicitResult = await ExplicitGet();
            _implicitResult = await ImplicitGet();

            _failingPathRequestsBeforeFourthCall = Cms.RequestCount(failingPath);
            _requestsBeforeFourthCall = Cms.Requests.Count;
            await ImplicitGet();
            _requestsAfterFourthCall = Cms.Requests.Count;
        }

        [Test]
        public void It_throws_profile_data_unavailable_for_the_first_request()
        {
            _firstFailure.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_resolves_the_explicit_profile_on_the_next_request()
        {
            AssertExplicitStudentProfile(_explicitResult);
        }

        [Test]
        public void It_applies_the_assigned_profile_implicitly_after_recovery()
        {
            AssertImplicitStudentProfile(_implicitResult);
        }

        [Test]
        public void It_does_not_cache_the_failure()
        {
            _failingPathRequestsBeforeFourthCall.Should().Be(2);
        }

        [Test]
        public void It_caches_the_successful_result()
        {
            _requestsAfterFourthCall.Should().Be(_requestsBeforeFourthCall);
        }
    }

    /// <summary>
    /// The catalog is all-or-nothing, so its per-profile fetches are bounded: a catalog larger than the
    /// bound never has more than the bound in flight against CMS, and still loads completely.
    /// </summary>
    [TestFixture]
    public class Given_A_Catalog_Larger_Than_The_Fetch_Bound : Given_A_Cms_Backed_Profile_Service
    {
        private const int ProfileCount = CachedProfileService.MaxConcurrentProfileFetches * 3;

        private int _inFlightWhileHeld;
        private IReadOnlyList<string> _profileNames = [];
        private readonly List<string> _detailPaths = [];

        protected override async Task Act()
        {
            List<object> listed = [];
            List<(CmsGate Gate, CmsReply Reply)> gates = [];
            for (int index = 0; index < ProfileCount; index++)
            {
                long id = 100 + index;
                string name = $"Profile{index}";
                string path = $"/v3/profiles/{id}";
                listed.Add(new { id, name });
                _detailPaths.Add(path);
                CmsReply reply = CmsReply.Json(
                    JsonSerializer.Serialize(
                        new
                        {
                            id,
                            name,
                            definition = SchoolProfileXml.Replace("SchoolProfile", name),
                        }
                    )
                );
                Cms.Serve(path, reply);
                gates.Add((Cms.GateNext(path), reply));
            }
            Cms.Serve(CatalogPath, CmsReply.Json(JsonSerializer.Serialize(listed)));

            Task<IReadOnlyList<string>> load = Service.GetProfileNamesAsync(null);

            // Wait for the bound to fill, then give an unbounded fan-out time to exceed it.
            await WaitUntil(() => Cms.InFlight >= CachedProfileService.MaxConcurrentProfileFetches);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            _inFlightWhileHeld = Cms.InFlight;

            foreach ((CmsGate gate, CmsReply reply) in gates)
            {
                gate.Release(reply);
            }
            _profileNames = await load;
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The catalog fetch never reached the fetch bound.");
                }
                await Task.Delay(10);
            }
        }

        [Test]
        public void It_holds_no_more_than_the_bound_in_flight()
        {
            _inFlightWhileHeld.Should().Be(CachedProfileService.MaxConcurrentProfileFetches);
            Cms.MaxInFlight.Should().Be(CachedProfileService.MaxConcurrentProfileFetches);
        }

        [Test]
        public void It_fetches_every_listed_profile()
        {
            _detailPaths.Should().OnlyContain(path => Cms.RequestCount(path) == 1);
        }

        [Test]
        public void It_loads_the_complete_catalog()
        {
            _profileNames.Should().HaveCount(ProfileCount);
        }
    }

    /// <summary>
    /// Callers joined to one failing fetch all receive the failure, the fetch is still shared
    /// (stampede protection), and the failure is not left behind for the next caller.
    /// </summary>
    [TestFixture]
    public class Given_Concurrent_Requests_Joined_To_A_Failing_Catalog_Fetch
        : Given_A_Cms_Backed_Profile_Service
    {
        private Exception? _implicitFailure;
        private Exception? _explicitFailure;
        private int _catalogRequestsDuringFailure;
        private int _applicationRequestsDuringFailure;
        private ProfileResolutionResult _recoveredResult = null!;

        protected override async Task Act()
        {
            CmsGate gate = Cms.GateNext(StudentProfilePath);

            Task<Exception?> implicitCall = CaptureFailure(() => ImplicitGet());
            await gate.Observed;
            // Joins the application-profile fetch that is waiting on the gated catalog fetch.
            Task<Exception?> explicitCall = CaptureFailure(() => ExplicitGet());
            gate.Release(InternalServerError);

            _implicitFailure = await implicitCall;
            _explicitFailure = await explicitCall;
            _catalogRequestsDuringFailure = Cms.RequestCount(CatalogPath);
            _applicationRequestsDuringFailure = Cms.RequestCount(ApplicationPath);

            _recoveredResult = await ImplicitGet();
        }

        [Test]
        public void It_fails_the_first_caller()
        {
            _implicitFailure.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_fails_the_joined_caller()
        {
            _explicitFailure.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_shares_one_fetch_between_the_callers()
        {
            _catalogRequestsDuringFailure.Should().Be(1);
            _applicationRequestsDuringFailure.Should().Be(1);
        }

        [Test]
        public void It_fetches_again_after_the_failure()
        {
            AssertImplicitStudentProfile(_recoveredResult);
            Cms.RequestCount(CatalogPath).Should().Be(2);
        }
    }

    /// <summary>
    /// No staleness policy: when the catalog expires while the assignments are still cached and
    /// the refresh fails, nothing stale and nothing partial is served. The request fails closed
    /// until CMS is healthy again.
    /// </summary>
    [TestFixture]
    public class Given_A_Catalog_Refresh_That_Fails_After_Expiry : Given_A_Cms_Backed_Profile_Service
    {
        private Exception? _explicitFailure;
        private Exception? _implicitFailure;
        private ProfileResolutionResult _explicitAfterRecovery = null!;
        private ProfileResolutionResult _implicitAfterRecovery = null!;

        protected override async Task Act()
        {
            await ImplicitGet();
            await ExplicitGet();

            // Expire only the catalog, the way its own expiration would.
            await Cache.RemoveAsync(CachedProfileService.GetCatalogCacheKey(null));
            Cms.FailUntilHealthy(StudentProfilePath, InternalServerError);

            _explicitFailure = await CaptureFailure(() => ExplicitGet());
            _implicitFailure = await CaptureFailure(() => ImplicitGet());

            Cms.MakeHealthy(StudentProfilePath);
            _explicitAfterRecovery = await ExplicitGet();
            _implicitAfterRecovery = await ImplicitGet();
        }

        [Test]
        public void It_fails_an_explicit_request_closed()
        {
            _explicitFailure.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_fails_an_implicit_request_closed()
        {
            _implicitFailure.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_resolves_the_explicit_profile_after_recovery()
        {
            AssertExplicitStudentProfile(_explicitAfterRecovery);
        }

        [Test]
        public void It_applies_the_assigned_profile_implicitly_after_recovery()
        {
            AssertImplicitStudentProfile(_implicitAfterRecovery);
        }
    }

    /// <summary>
    /// A failure for one tenant neither leaks into nor evicts another tenant's cache entries.
    /// </summary>
    [TestFixture]
    public class Given_A_Failing_Tenant_Beside_A_Healthy_Tenant : Given_A_Cms_Backed_Profile_Service
    {
        private const string FailingTenant = "tenant-a";
        private const string HealthyTenant = "tenant-b";

        private ProfileResolutionResult _healthyFirst = null!;
        private Exception? _failingFirst;
        private ProfileResolutionResult _failingRecovered = null!;
        private ProfileResolutionResult _healthySecond = null!;

        protected override async Task Act()
        {
            Cms.FailTimes(StudentProfilePath, InternalServerError, times: 1, tenant: FailingTenant);

            _healthyFirst = await ImplicitGet(HealthyTenant);
            _failingFirst = await CaptureFailure(() => ImplicitGet(FailingTenant));
            _failingRecovered = await ImplicitGet(FailingTenant);
            _healthySecond = await ImplicitGet(HealthyTenant);
        }

        [Test]
        public void It_resolves_the_healthy_tenant()
        {
            AssertImplicitStudentProfile(_healthyFirst);
            AssertImplicitStudentProfile(_healthySecond);
        }

        [Test]
        public void It_keeps_the_healthy_tenant_cached()
        {
            Cms.RequestCount(CatalogPath, HealthyTenant).Should().Be(1);
        }

        [Test]
        public void It_fails_the_failing_tenant_closed()
        {
            _failingFirst.Should().BeOfType<ProfileDataUnavailableException>();
        }

        [Test]
        public void It_recovers_the_failing_tenant_on_its_next_request()
        {
            AssertImplicitStudentProfile(_failingRecovered);
            Cms.RequestCount(CatalogPath, FailingTenant).Should().Be(2);
        }
    }

    /// <summary>
    /// A genuine CMS 404: an unassigned profile deleted between the list and the detail fetch. (CMS
    /// refuses to delete an assigned profile, and leaves XSD-invalid profiles out of the list, so this is
    /// the state a detail 404 actually represents.) That profile is absent from a successfully fetched,
    /// cached catalog, explicit requests naming it get 406/415, and the assigned profile is unaffected.
    /// </summary>
    [TestFixture]
    public class Given_An_Unassigned_Profile_That_Cms_Reports_Not_Found : Given_A_Cms_Backed_Profile_Service
    {
        private IReadOnlyList<string> _profileNames = [];
        private ProfileResolutionResult _explicitGet = null!;
        private ProfileResolutionResult _explicitPost = null!;
        private ProfileResolutionResult _implicitGet = null!;

        protected override async Task Act()
        {
            Cms.Serve(SchoolProfilePath, CmsReply.Status(HttpStatusCode.NotFound));

            _profileNames = await Service.GetProfileNamesAsync(null);
            _explicitGet = await ExplicitSchoolRequest(RequestMethod.GET, ProfileUsageType.Readable);
            _explicitPost = await ExplicitSchoolRequest(RequestMethod.POST, ProfileUsageType.Writable);
            _implicitGet = await ImplicitGet();
        }

        private Task<ProfileResolutionResult> ExplicitSchoolRequest(
            RequestMethod method,
            ProfileUsageType usageType
        ) =>
            Service.ResolveProfileAsync(
                new ParsedProfileHeader("School", "SchoolProfile", usageType),
                method,
                "School",
                ApplicationId,
                null
            );

        [Test]
        public void It_caches_the_catalog_without_that_profile()
        {
            _profileNames.Should().Equal("StudentProfile");
            Cms.RequestCount(CatalogPath).Should().Be(1);
            Cms.RequestCount(SchoolProfilePath).Should().Be(1);
        }

        [Test]
        public void It_answers_an_explicit_get_naming_it_with_406()
        {
            _explicitGet.IsSuccess.Should().BeFalse();
            _explicitGet.Error!.StatusCode.Should().Be(406);
        }

        [Test]
        public void It_answers_an_explicit_post_naming_it_with_415()
        {
            _explicitPost.IsSuccess.Should().BeFalse();
            _explicitPost.Error!.StatusCode.Should().Be(415);
        }

        [Test]
        public void It_still_applies_the_assigned_profile_implicitly()
        {
            AssertImplicitStudentProfile(_implicitGet);
        }
    }
}
