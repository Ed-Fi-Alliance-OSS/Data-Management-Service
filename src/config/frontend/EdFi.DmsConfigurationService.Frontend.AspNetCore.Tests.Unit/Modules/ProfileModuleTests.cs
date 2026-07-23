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
                    collection.AddTransient((_) => _httpContext).AddTransient((_) => _profileRepository);
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
        var validProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.Success(1));
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(validProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith("/v3/profiles/1");
    }

    [Test]
    public async Task CreateProfile_MissingName_ShouldReturnBadRequest()
    {
        var invalidProfile = new
        {
            Name = "",
            definition = "<Profile name=\"\"><Resource name=\"Resource1\"></Resource></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(invalidProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.name"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile name is required.");
    }

    [Test]
    public async Task CreateProfile_DuplicateName_ShouldReturnConflict()
    {
        var duplicateProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.FailureDuplicateName("TestProfile"));
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(duplicateProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        actualResponse!["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict:non-unique-identity");
        actualResponse["validationErrors"]!.AsObject().Count.Should().Be(0);
        actualResponse["errors"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile 'TestProfile' already exists");
    }

    [Test]
    public async Task CreateProfile_MismatchedXmlName_ShouldReturnBadRequest()
    {
        var mismatchedProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"OtherName\"><Resource name=\"Resource1\"></Resource></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(mismatchedProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.definition"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Name must match the name attribute in the XML definition");
    }

    [Test]
    public async Task CreateProfile_InvalidXml_ShouldReturnBadRequest()
    {
        var invalidXmlProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(invalidXmlProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.definition"].Should().NotBeNull();
        actualResponse["validationErrors"]!["$.definition"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Name must match the name attribute in the XML definition.");
    }

    [Test]
    public async Task CreateProfile_NoResource_ShouldReturnBadRequest()
    {
        var noResourceProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"TestProfile\"></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(noResourceProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.definition"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile definition XML is invalid or does not match the XSD.");
    }

    [Test]
    public async Task CreateProfile_ResourceMissingName_ShouldReturnBadRequest()
    {
        var missingResourceNameProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"TestProfile\"><Resource></Resource></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(missingResourceNameProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.definition"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile definition XML is invalid or does not match the XSD.");
    }

    [Test]
    public async Task GetAllProfiles_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Returns(
                new[]
                {
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Name = "TestProfile",
                            Definition =
                                @"<Profile name=""TestProfile""><Resource name=""School""><ReadContentType memberSelection=""IncludeOnly""><Property name=""NameOfInstitution"" /></ReadContentType></Resource></Profile>",
                        }
                    ),
                }
            );
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=10&offset=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetProfileById_Valid_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(
                new ProfileGetResult.Success(
                    new ProfileResponse
                    {
                        Id = 1,
                        Name = "TestProfile",
                        Definition =
                            "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"></Resource></Profile>",
                    }
                )
            );
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles/1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetProfileById_NotFound_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(new ProfileGetResult.FailureNotFound());
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateProfile_Valid_ShouldReturnNoContent()
    {
        var updateProfile = new
        {
            id = 1,
            Name = "UpdatedProfile",
            definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.Success());
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(updateProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/1", content);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task UpdateProfile_Invalid_ShouldReturnBadRequest()
    {
        var invalidUpdate = new
        {
            id = 1,
            Name = "",
            definition = "<Profile name=\"\"><Resource name=\"Resource1\"></Resource></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(invalidUpdate),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.name"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile name is required.");
    }

    [Test]
    public async Task DeleteProfile_Valid_ShouldReturnNoContent()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.Success());
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/1");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task DeleteProfile_NotFound_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureNotExists(999));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteProfile_InUse_ShouldReturnConflict()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureInUse(1));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/1");

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        actualResponse!["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:conflict:dependent-item-exists");
        actualResponse["validationErrors"]!.AsObject().Count.Should().Be(0);
        actualResponse["errors"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile is assigned to applications and cannot be deleted");
    }

    [Test]
    public async Task UpdateProfile_IdMismatch_ShouldReturnBadRequest()
    {
        var updateProfile = new
        {
            id = 999,
            Name = "UpdatedProfile",
            definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(updateProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.id"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Request body id must match the id in the url");
    }

    [Test]
    public async Task UpdateProfile_DuplicateName_ShouldReturnConflict()
    {
        var updateProfile = new
        {
            id = 1,
            Name = "ExistingProfile",
            definition = "<Profile name=\"ExistingProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureDuplicateName("ExistingProfile"));
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(updateProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        actualResponse!["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:conflict:non-unique-identity");
        actualResponse["validationErrors"]!.AsObject().Count.Should().Be(0);
        actualResponse["errors"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("A profile with this name already exists");
    }

    [Test]
    public async Task UpdateProfile_NotFound_ShouldReturnNotFound()
    {
        var updateProfile = new
        {
            id = 999,
            Name = "UpdatedProfile",
            definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureNotExists(999));
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(updateProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/999", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateProfile_InvalidXml_ShouldReturnBadRequest()
    {
        var invalidUpdate = new
        {
            id = 1,
            Name = "UpdatedProfile",
            definition = "<Profile name=\"OtherName\"><Resource name=\"Resource1\"></Resource></Profile>",
        };
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(invalidUpdate),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/1", content);

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["$.definition"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Name must match the name attribute in the XML definition");
    }

    [Test]
    public async Task GetAllProfiles_EmptyResult_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Returns(new ProfileGetResult[] { });
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=10&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var profiles = JsonSerializer.Deserialize<ProfileListResponse[]>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        profiles.Should().BeEmpty();
    }

    [Test]
    public async Task GetAllProfiles_InvalidLimit_ShouldReturnParameterValidationFailure()
    {
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=0");
        await response.ShouldBeProblemDetailAsync(
            HttpStatusCode.BadRequest,
            "urn:ed-fi:api:bad-request:parameter",
            "Parameter Validation Failed",
            "One or more query parameters were invalid. See 'errors' for details.",
            errors: ["'limit' must be greater than 0."]
        );
    }

    [Test]
    public async Task GetAllProfiles_NonNumericOffset_ShouldReturnParameterValidationFailure()
    {
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?offset=abc");
        await response.ShouldBeProblemDetailAsync(
            HttpStatusCode.BadRequest,
            "urn:ed-fi:api:bad-request:parameter",
            "Parameter Validation Failed",
            "One or more query parameters were invalid. See 'errors' for details.",
            errors: ["'offset' must be an integer."]
        );
        (await response.Content.ReadAsStringAsync()).Should().NotContain("abc");
    }

    [Test]
    public async Task GetAllProfiles_MultipleProfiles_ShouldReturnOk()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Returns(
                new[]
                {
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 1,
                            Name = "Profile1",
                            Definition =
                                @"<Profile name=""Profile1""><Resource name=""School""><ReadContentType memberSelection=""IncludeOnly""><Property name=""NameOfInstitution"" /></ReadContentType></Resource></Profile>",
                        }
                    ),
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 2,
                            Name = "Profile2",
                            Definition =
                                @"<Profile name=""Profile2""><Resource name=""School""><ReadContentType memberSelection=""IncludeOnly""><Property name=""NameOfInstitution"" /></ReadContentType></Resource></Profile>",
                        }
                    ),
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 3,
                            Name = "Profile3",
                            Definition =
                                @"<Profile name=""Profile3""><Resource name=""School""><ReadContentType memberSelection=""IncludeOnly""><Property name=""NameOfInstitution"" /></ReadContentType></Resource></Profile>",
                        }
                    ),
                }
            );
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=10&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var profiles = JsonSerializer.Deserialize<ProfileListResponse[]>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        profiles.Should().HaveCount(3);
        profiles![0].Name.Should().Be("Profile1");
        profiles[1].Name.Should().Be("Profile2");
        profiles[2].Name.Should().Be("Profile3");
    }

    [Test]
    public async Task GetAllProfiles_FailureUnknown_ShouldReturnInternalServerError()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Returns(new[] { new ProfileGetResult.FailureUnknown("Database error") });
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=10&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task GetProfileById_FailureUnknown_ShouldReturnInternalServerError()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(new ProfileGetResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles/1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task CreateProfile_FailureUnknown_ShouldReturnInternalServerError()
    {
        var validProfile = new
        {
            Name = "TestProfile",
            definition = "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(validProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PostAsync("/v3/profiles", content);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task UpdateProfile_FailureUnknown_ShouldReturnInternalServerError()
    {
        var updateProfile = new
        {
            id = 1,
            Name = "UpdatedProfile",
            definition = "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
        };
        A.CallTo(() => _profileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(updateProfile),
            Encoding.UTF8,
            "application/json"
        );
        var response = await client.PutAsync("/v3/profiles/1", content);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task DeleteProfile_FailureUnknown_ShouldReturnInternalServerError()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureUnknown("Database error"));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task GetAllProfiles_ShouldReturnProfilesProvidedByRepository()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Returns(
                new[]
                {
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 1,
                            Name = "ValidProfile",
                            Definition =
                                @"<Profile name=""ValidProfile""><Resource name=""School""><ReadContentType memberSelection=""IncludeOnly""><Property name=""NameOfInstitution"" /></ReadContentType></Resource></Profile>",
                        }
                    ),
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 2,
                            Name = "AnotherValidProfile",
                            Definition =
                                @"<Profile name=""AnotherValidProfile""><Resource name=""Student""><ReadContentType memberSelection=""IncludeAll"" /></Resource></Profile>",
                        }
                    ),
                }
            );
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=10&offset=0");

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profiles = actualResponse!.AsArray();
        profiles.Should().HaveCount(2);
        profiles[0]!["id"]!.GetValue<int>().Should().Be(1);
        profiles[0]!["name"]!.GetValue<string>().Should().Be("ValidProfile");
        profiles[1]!["id"]!.GetValue<int>().Should().Be(2);
        profiles[1]!["name"]!.GetValue<string>().Should().Be("AnotherValidProfile");
    }

    [Test]
    public async Task GetAllProfiles_WhenRepositoryReturnsNoVisibleProfiles_ShouldReturnEmptyArray()
    {
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Returns(Array.Empty<ProfileGetResult>());
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles?limit=10&offset=0");

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profiles = actualResponse!.AsArray();
        profiles.Should().BeEmpty();
    }

    [Test]
    public async Task GetProfileById_InvalidProfile_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(
                new ProfileGetResult.Success(
                    new ProfileResponse
                    {
                        Id = 1,
                        Name = "InvalidProfile",
                        Definition = @"<Profile><Resource name=""School""></Resource></Profile>", // Missing required name attribute
                    }
                )
            );
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles/1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetProfileById_InvalidXmlProfile_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.GetProfile(A<long>.Ignored))
            .Returns(
                new ProfileGetResult.Success(
                    new ProfileResponse
                    {
                        Id = 2,
                        Name = "MalformedXmlProfile",
                        Definition =
                            @"<Profile name=""MalformedXmlProfile""><Resource name=""School""></Resource>", // Missing closing tag
                    }
                )
            );
        using var client = SetUpClient();
        var response = await client.GetAsync("/v3/profiles/2");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetAllProfiles_Should_bind_profile_filters_and_sorting()
    {
        ProfileQuery? capturedQuery = null;
        A.CallTo(() => _profileRepository.QueryProfiles(A<ProfileQuery>.Ignored))
            .Invokes(call => capturedQuery = call.GetArgument<ProfileQuery>(0))
            .Returns(
                new[]
                {
                    new ProfileGetResult.Success(
                        new ProfileResponse
                        {
                            Id = 42,
                            Name = "FilteredProfile",
                            Definition =
                                @"<Profile name=""FilteredProfile""><Resource name=""School""><ReadContentType memberSelection=""IncludeOnly""><Property name=""NameOfInstitution"" /></ReadContentType></Resource></Profile>",
                        }
                    ),
                }
            );

        using var client = SetUpClient();
        var response = await client.GetAsync(
            "/v3/profiles?id=42&name=FilteredProfile&orderBy=name&direction=DESC&limit=1&offset=0"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Id.Should().Be(42);
        capturedQuery.Name.Should().Be("FilteredProfile");
        capturedQuery.OrderBy.Should().Be("name");
        capturedQuery.Direction.Should().Be("DESC");
        capturedQuery.Limit.Should().Be(1);
        capturedQuery.Offset.Should().Be(0);
    }

    [Test]
    public async Task GetAllProfiles_InvalidOrderBy_ShouldReturnBadRequest()
    {
        using var client = SetUpClient();

        var response = await client.GetAsync("/v3/profiles?orderBy=invalidField");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

// Full-contract HTTP tests (asserting the application/problem+json media type via the shared helper) for
// the DMS-1218 Profile error branches. Added as new Given_/It_ fixtures alongside the legacy fixture.
public abstract class ProfileProblemDetailsTestBase
{
    protected readonly IProfileRepository ProfileRepository = A.Fake<IProfileRepository>();
    private WebApplicationFactory<Program> _factory = null!;
    protected HttpClient Client = null!;

    protected const string ValidInsertBody = """
        {
            "name": "TestProfile",
            "definition": "<Profile name=\"TestProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>"
        }
        """;

    protected const string ValidUpdateBody = """
        {
            "id": 1,
            "name": "UpdatedProfile",
            "definition": "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>"
        }
        """;

    [SetUp]
    public void BaseSetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (ctx, collection) =>
                {
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
                    collection.AddTransient(_ => ProfileRepository);
                }
            );
        });
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
    }

    [TearDown]
    public void BaseTearDown()
    {
        Client?.Dispose();
        _factory?.Dispose();
    }

    protected static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}

[TestFixture]
public class Given_A_Profile_Insert_With_A_Duplicate_Name : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.InsertProfile(A<ProfileInsertCommand>.Ignored))
            .Returns(new ProfileInsertResult.FailureDuplicateName("TestProfile"));
        _response = await Client.PostAsync("/v3/profiles", Json(ValidInsertBody));
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_non_unique_identity_conflict_contract()
    {
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.Conflict,
            "urn:ed-fi:api:conflict:non-unique-identity",
            "Identifying Values Are Not Unique",
            "The identifying value(s) of the item are the same as another item that already exists.",
            errors: ["Profile 'TestProfile' already exists."]
        );
    }
}

[TestFixture]
public class Given_A_Profile_GetById_With_An_Invalid_Definition : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.GetProfile(A<long>.Ignored))
            .Returns(
                new ProfileGetResult.Success(
                    new ProfileResponse
                    {
                        Id = 5,
                        Name = "TestProfile",
                        Definition = "<Profile name=\"TestProfile\"></Profile>",
                    }
                )
            );
        _response = await Client.GetAsync("/v3/profiles/5");
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_not_found_contract() =>
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.NotFound,
            "urn:ed-fi:api:not-found",
            "Not Found",
            "Profile 5 not found."
        );
}

[TestFixture]
public class Given_A_Profile_GetById_That_Is_Not_Found : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.GetProfile(A<long>.Ignored))
            .Returns(new ProfileGetResult.FailureNotFound());
        _response = await Client.GetAsync("/v3/profiles/999");
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_not_found_contract() =>
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.NotFound,
            "urn:ed-fi:api:not-found",
            "Not Found",
            "Profile 999 not found."
        );
}

[TestFixture]
public class Given_A_Profile_Update_With_A_Duplicate_Name : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureDuplicateName("UpdatedProfile"));
        _response = await Client.PutAsync("/v3/profiles/1", Json(ValidUpdateBody));
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_non_unique_identity_conflict_contract()
    {
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.Conflict,
            "urn:ed-fi:api:conflict:non-unique-identity",
            "Identifying Values Are Not Unique",
            "The identifying value(s) of the item are the same as another item that already exists.",
            errors: ["A profile with this name already exists."]
        );
    }
}

[TestFixture]
public class Given_A_Profile_Update_That_Is_Not_Found : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.UpdateProfile(A<ProfileUpdateCommand>.Ignored))
            .Returns(new ProfileUpdateResult.FailureNotExists(999));
        _response = await Client.PutAsync(
            "/v3/profiles/999",
            Json(
                """
                {
                    "id": 999,
                    "name": "UpdatedProfile",
                    "definition": "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>"
                }
                """
            )
        );
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_not_found_contract() =>
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.NotFound,
            "urn:ed-fi:api:not-found",
            "Not Found",
            "Profile 999 not found."
        );
}

[TestFixture]
public class Given_A_Profile_Delete_That_Is_In_Use : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureInUse(1));
        _response = await Client.DeleteAsync("/v3/profiles/1");
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_dependent_item_exists_contract() =>
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.Conflict,
            "urn:ed-fi:api:conflict:dependent-item-exists",
            "Dependent Item Exists",
            "The requested action cannot be performed because this item is referenced by existing item(s).",
            errors: ["Profile is assigned to applications and cannot be deleted."]
        );
}

[TestFixture]
public class Given_A_Profile_Delete_That_Is_Not_Found : ProfileProblemDetailsTestBase
{
    private HttpResponseMessage _response = null!;

    [SetUp]
    public async Task Setup()
    {
        A.CallTo(() => ProfileRepository.DeleteProfile(A<long>.Ignored))
            .Returns(new ProfileDeleteResult.FailureNotExists(999));
        _response = await Client.DeleteAsync("/v3/profiles/999");
    }

    [TearDown]
    public void TearDown() => _response.Dispose();

    [Test]
    public async Task It_returns_the_not_found_contract() =>
        await _response.ShouldBeProblemDetailAsync(
            HttpStatusCode.NotFound,
            "urn:ed-fi:api:not-found",
            "Not Found",
            "Profile 999 not found."
        );
}
