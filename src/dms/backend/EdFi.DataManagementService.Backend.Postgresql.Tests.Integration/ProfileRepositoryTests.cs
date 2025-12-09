// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class ProfileRepositoryTests : DatabaseTest
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

    private IProfileRepository? _repository;

    [SetUp]
    public void Setup()
    {
        _repository = new PostgresqlProfileRepository(
            CreateDataSourceProvider(),
            NullLogger<PostgresqlProfileRepository>.Instance
        );
    }

    [Test]
    public async Task CreateProfile_ShouldCreateSuccessfully()
    {
        // Act
        var id = await _repository!.CreateProfileAsync(
            "Test-Profile",
            "A test profile",
            TestProfileXml
        );

        // Assert
        id.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task GetProfileByName_ShouldReturnProfile_WhenExists()
    {
        // Arrange
        await _repository!.CreateProfileAsync("Test-Profile", "A test profile", TestProfileXml);

        // Act
        var profile = await _repository.GetProfileByNameAsync("Test-Profile");

        // Assert
        profile.Should().NotBeNull();
        profile!.ProfileName.Should().Be("Test-Profile");
        profile.Description.Should().Be("A test profile");
        profile.ProfileDefinition.Should().Contain("Test_Profile");
    }

    [Test]
    public async Task GetProfileByName_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var profile = await _repository!.GetProfileByNameAsync("NonExistent");

        // Assert
        profile.Should().BeNull();
    }

    [Test]
    public async Task GetAllProfiles_ShouldReturnAllProfiles()
    {
        // Arrange
        await _repository!.CreateProfileAsync("Profile1", "First profile", TestProfileXml);
        await _repository.CreateProfileAsync("Profile2", "Second profile", TestProfileXml);

        // Act
        var profiles = await _repository.GetAllProfilesAsync();

        // Assert
        profiles.Should().HaveCount(2);
        profiles.Should().Contain(p => p.ProfileName == "Profile1");
        profiles.Should().Contain(p => p.ProfileName == "Profile2");
    }

    [Test]
    public async Task UpdateProfile_ShouldUpdateSuccessfully()
    {
        // Arrange
        await _repository!.CreateProfileAsync("Test-Profile", "Original description", TestProfileXml);
        var updatedXml = TestProfileXml.Replace("Test_Profile", "Updated_Profile");

        // Act
        var result = await _repository.UpdateProfileAsync(
            "Test-Profile",
            "Updated description",
            updatedXml
        );

        // Assert
        result.Should().BeTrue();
        var profile = await _repository.GetProfileByNameAsync("Test-Profile");
        profile!.Description.Should().Be("Updated description");
        profile.ProfileDefinition.Should().Contain("Updated_Profile");
    }

    [Test]
    public async Task UpdateProfile_ShouldReturnFalse_WhenNotExists()
    {
        // Act
        var result = await _repository!.UpdateProfileAsync(
            "NonExistent",
            "Description",
            TestProfileXml
        );

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task DeleteProfile_ShouldDeleteSuccessfully()
    {
        // Arrange
        await _repository!.CreateProfileAsync("Test-Profile", "A test profile", TestProfileXml);

        // Act
        var result = await _repository.DeleteProfileAsync("Test-Profile");

        // Assert
        result.Should().BeTrue();
        var profile = await _repository.GetProfileByNameAsync("Test-Profile");
        profile.Should().BeNull();
    }

    [Test]
    public async Task DeleteProfile_ShouldReturnFalse_WhenNotExists()
    {
        // Act
        var result = await _repository!.DeleteProfileAsync("NonExistent");

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task GetLatestUpdateTimestamp_ShouldReturnNull_WhenNoProfiles()
    {
        // Act
        var timestamp = await _repository!.GetLatestUpdateTimestampAsync();

        // Assert
        timestamp.Should().BeNull();
    }

    [Test]
    public async Task GetLatestUpdateTimestamp_ShouldReturnLatest_WhenProfilesExist()
    {
        // Arrange
        await _repository!.CreateProfileAsync("Profile1", null, TestProfileXml);
        await Task.Delay(100); // Ensure different timestamps
        await _repository.CreateProfileAsync("Profile2", null, TestProfileXml);

        // Act
        var timestamp = await _repository.GetLatestUpdateTimestampAsync();

        // Assert
        timestamp.Should().NotBeNull();
        timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
