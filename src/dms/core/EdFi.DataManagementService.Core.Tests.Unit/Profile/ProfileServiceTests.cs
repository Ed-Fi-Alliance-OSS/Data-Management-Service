// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
public class ProfileServiceTests
{
    private const string TestProfileXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Profile name=""Test_Profile"">
    <Resource name=""School"">
        <ReadContentType memberSelection=""IncludeOnly"">
            <Property name=""schoolId"" />
            <Property name=""nameOfInstitution"" />
        </ReadContentType>
    </Resource>
</Profile>";

    [Test]
    public async Task GetProfileAsync_WhenProfileExists_ShouldReturnProfile()
    {
        // Arrange
        var repository = A.Fake<IProfileRepository>();
        var profileInfo = new ProfileInfo(
            1,
            "Test-Profile",
            "A test profile",
            TestProfileXml,
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        A.CallTo(() => repository.GetAllProfilesAsync())
            .Returns(new[] { profileInfo });
        A.CallTo(() => repository.GetLatestUpdateTimestampAsync())
            .Returns(DateTime.UtcNow);

        var service = new ProfileService(repository, NullLogger<ProfileService>.Instance);

        // Act
        var result = await service.GetProfileAsync("Test-Profile");

        // Assert
        result.Should().NotBeNull();
        result!.ProfileName.Should().Be("Test_Profile");
        result.ResourcePolicies.Should().HaveCount(1);
        result.ResourcePolicies[0].ResourceName.Should().Be("School");
    }

    [Test]
    public async Task GetProfileAsync_WhenProfileDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var repository = A.Fake<IProfileRepository>();
        A.CallTo(() => repository.GetAllProfilesAsync())
            .Returns(Array.Empty<ProfileInfo>());
        A.CallTo(() => repository.GetLatestUpdateTimestampAsync())
            .Returns((DateTime?)null);

        var service = new ProfileService(repository, NullLogger<ProfileService>.Instance);

        // Act
        var result = await service.GetProfileAsync("NonExistent");

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task ReloadProfilesAsync_ShouldLoadAllProfiles()
    {
        // Arrange
        var repository = A.Fake<IProfileRepository>();
        var profileInfo1 = new ProfileInfo(
            1,
            "Profile1",
            "First profile",
            TestProfileXml,
            DateTime.UtcNow,
            DateTime.UtcNow
        );
        var profileInfo2 = new ProfileInfo(
            2,
            "Profile2",
            "Second profile",
            TestProfileXml.Replace("Test_Profile", "Profile2"),
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        A.CallTo(() => repository.GetAllProfilesAsync())
            .Returns(new[] { profileInfo1, profileInfo2 });

        var service = new ProfileService(repository, NullLogger<ProfileService>.Instance);

        // Act
        await service.ReloadProfilesAsync();

        // Assert
        var profile1 = await service.GetProfileAsync("Profile1");
        var profile2 = await service.GetProfileAsync("Profile2");

        profile1.Should().NotBeNull();
        profile2.Should().NotBeNull();
    }

    [Test]
    public async Task ProfileExistsAsync_WhenProfileExists_ShouldReturnTrue()
    {
        // Arrange
        var repository = A.Fake<IProfileRepository>();
        var profileInfo = new ProfileInfo(
            1,
            "Test-Profile",
            "A test profile",
            TestProfileXml,
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        A.CallTo(() => repository.GetAllProfilesAsync())
            .Returns(new[] { profileInfo });
        A.CallTo(() => repository.GetLatestUpdateTimestampAsync())
            .Returns(DateTime.UtcNow);

        var service = new ProfileService(repository, NullLogger<ProfileService>.Instance);

        // Act
        var exists = await service.ProfileExistsAsync("Test-Profile");

        // Assert
        exists.Should().BeTrue();
    }

    [Test]
    public async Task ProfileExistsAsync_WhenProfileDoesNotExist_ShouldReturnFalse()
    {
        // Arrange
        var repository = A.Fake<IProfileRepository>();
        A.CallTo(() => repository.GetAllProfilesAsync())
            .Returns(Array.Empty<ProfileInfo>());
        A.CallTo(() => repository.GetLatestUpdateTimestampAsync())
            .Returns((DateTime?)null);

        var service = new ProfileService(repository, NullLogger<ProfileService>.Instance);

        // Act
        var exists = await service.ProfileExistsAsync("NonExistent");

        // Assert
        exists.Should().BeFalse();
    }

    [Test]
    public async Task GetProfileAsync_WhenProfileIsInvalid_ShouldSkipAndContinue()
    {
        // Arrange
        var repository = A.Fake<IProfileRepository>();
        var validProfile = new ProfileInfo(
            1,
            "Valid-Profile",
            "A valid profile",
            TestProfileXml,
            DateTime.UtcNow,
            DateTime.UtcNow
        );
        var invalidProfile = new ProfileInfo(
            2,
            "Invalid-Profile",
            "An invalid profile",
            "<Invalid>XML</Invalid>",
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        A.CallTo(() => repository.GetAllProfilesAsync())
            .Returns(new[] { validProfile, invalidProfile });
        A.CallTo(() => repository.GetLatestUpdateTimestampAsync())
            .Returns(DateTime.UtcNow);

        var service = new ProfileService(repository, NullLogger<ProfileService>.Instance);

        // Act
        var validResult = await service.GetProfileAsync("Valid-Profile");
        var invalidResult = await service.GetProfileAsync("Invalid-Profile");

        // Assert
        validResult.Should().NotBeNull();
        invalidResult.Should().BeNull();
    }
}
