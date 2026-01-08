// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class ProfileModuleTests
{
    private readonly IProfileRepository _profileRepository = A.Fake<IProfileRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();
    private WebApplicationFactory<Program>? _factory;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
                    // Use the new test authentication extension that mimics production setup
                    collection.AddTestAuthentication();

                    var identitySettings = ctx
                        .Configuration.GetSection("IdentitySettings")
                        .Get<IdentitySettings>()!;
                    collection.AddAuthorization(options =>
                    {
                        options.AddPolicy(
                            SecurityConstants.ServicePolicy,
                            policy =>
                                policy.RequireClaim(
                                    identitySettings.RoleClaimType,
                                    identitySettings.ConfigServiceRole
                                )
                        );
                        AuthorizationScopePolicies.Add(options);
                    });
                    collection
                        .AddTransient((_) => _httpContext)
                        .AddTransient((_) => _profileRepository);
                }
            );
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    private HttpClient SetUpClient()
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [Test]
    public async Task CreateProfile_Valid_ShouldReturnCreated()
    {
        var validProfile = new { Name = "TestProfile", definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        A.CallTo(() => _profileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.Success(1));
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(validProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith("/v2/profiles/1");
    }

    [Test]
    public async Task CreateProfile_MissingName_ShouldReturnBadRequest()
    {
        var invalidProfile = new { Name = "", definition = "<Profile name=\"\"><Resource name=\"Resource1\"></Resource></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(invalidProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Name"]![0]!.GetValue<string>()
            .Should().Contain("Profile name is required.");
    }

    [Test]
    public async Task CreateProfile_DuplicateName_ShouldReturnBadRequest()
    {
        var duplicateProfile = new { Name = "TestProfile", definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource></Profile>" };
        A.CallTo(() => _profileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.FailureDuplicateName("TestProfile"));
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(duplicateProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Name"]![0]!.GetValue<string>()
            .Should().Contain("Profile 'TestProfile' already exists");
    }

    [Test]
    public async Task CreateProfile_MismatchedXmlName_ShouldReturnBadRequest()
    {
        var mismatchedProfile = new { Name = "TestProfile", definition = "<Profile name=\"OtherName\"><Resource name=\"Resource1\"></Resource></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(mismatchedProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Definition"]![0]!.GetValue<string>()
            .Should().Contain("Name must match the name attribute in the XML definition");
    }

    [Test]
    public async Task CreateProfile_InvalidXml_ShouldReturnBadRequest()
    {
        var invalidXmlProfile = new { Name = "TestProfile", definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(invalidXmlProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Definition"].Should().NotBeNull();
        actualResponse["validationErrors"]!["Definition"]![0]!.GetValue<string>().Should().Contain("Name must match the name attribute in the XML definition.");
    }

    [Test]
    public async Task CreateProfile_NoResource_ShouldReturnBadRequest()
    {
        var noResourceProfile = new { Name = "TestProfile", definition = "<Profile name=\"TestProfile\"></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(noResourceProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Definition"]![0]!.GetValue<string>()
            .Should().Contain("Profile definition XML is invalid or does not match the XSD.");
    }

    [Test]
    public async Task CreateProfile_ResourceMissingName_ShouldReturnBadRequest()
    {
        var missingResourceNameProfile = new { Name = "TestProfile", definition = "<Profile name=\"TestProfile\"><Resource></Resource></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(missingResourceNameProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Definition"]![0]!.GetValue<string>()
            .Should().Contain("Profile definition XML is invalid or does not match the XSD.");
    }

    [Test]
    public async Task GetAllProfiles_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<PagingQuery>.Ignored))
            .Returns(new[] { new ProfileGetResult.Success(new ProfileResponse { Name = "TestProfile" }) });
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles?limit=10&offset=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetProfileById_Valid_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(new ProfileGetResult.Success(new ProfileResponse { Id = 1, Name = "TestProfile" }));
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetProfileById_NotFound_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(new ProfileGetResult.FailureNotFound());
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateProfile_Valid_ShouldReturnNoContent()
    {
        var updateProfile = new { id = 1, Name = "UpdatedProfile", definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.Success());
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(updateProfile), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/1", content);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task UpdateProfile_Invalid_ShouldReturnBadRequest()
    {
        var invalidUpdate = new { id = 1, Name = "", definition = "<Profile name=\"\"><Resource name=\"Resource1\"></Resource></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(invalidUpdate), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Name"]![0]!.GetValue<string>()
            .Should().Contain("Profile name is required.");
    }

    [Test]
    public async Task DeleteProfile_Valid_ShouldReturnNoContent()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.Success());
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v2/profiles/1");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task DeleteProfile_NotFound_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureNotExists(999));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v2/profiles/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteProfile_InUse_ShouldReturnBadRequest()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureInUse(1));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v2/profiles/1");

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["detail"]!.GetValue<string>()
            .Should().Contain("Profile is assigned to applications and cannot be deleted");
    }

    [Test]
    public async Task UpdateProfile_IdMismatch_ShouldReturnBadRequest()
    {
        var updateProfile = new { id = 999, Name = "UpdatedProfile", definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(updateProfile), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Id"]![0]!.GetValue<string>()
            .Should().Contain("Request body id must match the id in the url");
    }

    [Test]
    public async Task UpdateProfile_DuplicateName_ShouldReturnBadRequest()
    {
        var updateProfile = new { id = 1, Name = "ExistingProfile", definition = "<Profile name=\"ExistingProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureDuplicateName("ExistingProfile"));
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(updateProfile), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Name"]![0]!.GetValue<string>()
            .Should().Contain("A profile with this name already exists");
    }

    [Test]
    public async Task UpdateProfile_NotFound_ShouldReturnNotFound()
    {
        var updateProfile = new { id = 999, Name = "UpdatedProfile", definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureNotExists(999));
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(updateProfile), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/999", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateProfile_InvalidXml_ShouldReturnBadRequest()
    {
        var invalidUpdate = new { id = 1, Name = "UpdatedProfile", definition = "<Profile name=\"OtherName\"><Resource name=\"Resource1\"></Resource></Profile>" };
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(invalidUpdate), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Definition"]![0]!.GetValue<string>()
            .Should().Contain("Name must match the name attribute in the XML definition");
    }

    [Test]
    public async Task GetAllProfiles_EmptyResult_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<PagingQuery>.Ignored))
            .Returns(new ProfileGetResult[] { });
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles?limit=10&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var profiles = JsonSerializer.Deserialize<ProfileListResponse[]>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }
        );

        profiles.Should().BeEmpty();
    }

    [Test]
    public async Task GetAllProfiles_MultipleProfiles_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<PagingQuery>.Ignored))
            .Returns(new[]
            {
                new ProfileGetResult.Success(new ProfileResponse { Id = 1, Name = "Profile1" }),
                new ProfileGetResult.Success(new ProfileResponse { Id = 2, Name = "Profile2" }),
                new ProfileGetResult.Success(new ProfileResponse { Id = 3, Name = "Profile3" })
            });
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles?limit=10&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var profiles = JsonSerializer.Deserialize<ProfileListResponse[]>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }
        );

        profiles.Should().HaveCount(3);
        profiles![0].Name.Should().Be("Profile1");
        profiles[1].Name.Should().Be("Profile2");
        profiles[2].Name.Should().Be("Profile3");
    }

    [Test]
    public async Task GetAllProfiles_FailureUnknown_ShouldReturnInternalServerError()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<PagingQuery>.Ignored))
            .Returns(new[] { new ProfileGetResult.FailureUnknown("Database error") });
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles?limit=10&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task GetProfileById_FailureUnknown_ShouldReturnInternalServerError()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(new ProfileGetResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        var response = await client.GetAsync("/v2/profiles/1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task CreateProfile_FailureUnknown_ShouldReturnInternalServerError()
    {
        var validProfile = new { Name = "TestProfile", definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        A.CallTo(() => _profileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(validProfile), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v2/profiles", content);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task UpdateProfile_FailureUnknown_ShouldReturnInternalServerError()
    {
        var updateProfile = new { id = 1, Name = "UpdatedProfile", definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>" };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        using var content = new StringContent(JsonSerializer.Serialize(updateProfile), Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/v2/profiles/1", content);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task DeleteProfile_FailureUnknown_ShouldReturnInternalServerError()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v2/profiles/1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
