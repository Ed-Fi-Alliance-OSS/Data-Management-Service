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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

            var service = CreateService(fakeCmsProvider);
            // Header claims "School" but we're requesting "Student"
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "WriteOnlyProfile", WriteOnlyProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "ReadOnlyProfile", ReadOnlyProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

            var service = CreateService(fakeCmsProvider);

            // StudentProfile only covers Student, not School
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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(101, A<string?>._))
                .Returns(new CmsProfileResponse(101, "ReadOnlyProfile", ReadOnlyProfileXml));

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
            A.CallTo(() => fakeCmsProvider.GetProfileAsync(100, A<string?>._))
                .Returns(new CmsProfileResponse(100, "StudentProfile", StudentProfileXml));

            var service = CreateService(fakeCmsProvider);
            // StudentProfile only contains Student resource, not School
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
}
