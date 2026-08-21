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
        actualResponse!["validationErrors"]!["Name"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile name is required.");
    }

    [Test]
    public async Task CreateProfile_DuplicateName_ShouldReturnBadRequest()
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

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Name"]![0]!
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
        actualResponse!["validationErrors"]!["Definition"]![0]!
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
        actualResponse!["validationErrors"]!["Definition"].Should().NotBeNull();
        actualResponse["validationErrors"]!["Definition"]![0]!
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
        actualResponse!["validationErrors"]!["Definition"]![0]!
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
        actualResponse!["validationErrors"]!["Definition"]![0]!
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
        A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
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
        A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
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
        actualResponse!["validationErrors"]!["Name"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Profile name is required.");
    }

    [Test]
    public async Task DeleteProfile_Valid_ShouldReturnNoContent()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<int>.Ignored))
            .Returns(new ProfileDeleteResult.Success());
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/1");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task DeleteProfile_NotFound_ShouldReturnNotFound()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<int>.Ignored))
            .Returns(new ProfileDeleteResult.FailureNotExists(999));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteProfile_InUse_ShouldReturnConflict()
    {
        A.CallTo(() => _profileRepository.DeleteProfile(A<int>.Ignored))
            .Returns(new ProfileDeleteResult.FailureInUse(1));
        using var client = SetUpClient();
        var response = await client.DeleteAsync("/v3/profiles/1");

        var actualResponse = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        actualResponse!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Profile is assigned to applications and cannot be deleted.");
        actualResponse["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:conflict:dependent-item-exists");
        actualResponse["title"]!.GetValue<string>().Should().Be("Dependent Item Exists");
        actualResponse["status"]!.GetValue<int>().Should().Be(409);
        actualResponse["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        actualResponse["validationErrors"]!.AsObject().Count.Should().Be(0);
        actualResponse["errors"]!.AsArray().Count.Should().Be(0);
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
        actualResponse!["validationErrors"]!["Id"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Request body id must match the id in the url");
    }

    [Test]
    public async Task Should_return_bad_request_when_profile_body_id_is_omitted()
    {
        // Arrange
        using var client = SetUpClient();

        // Act: PUT with route id=1, body omits "id" (defaults to 0)
        var response = await client.PutAsync(
            "/v3/profiles/1",
            new StringContent(
                """
                {
                    "name": "UpdatedProfile",
                    "definition": "<Profile name=\"UpdatedProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>"
                }
                """,
                Encoding.UTF8,
                "application/json"
            )
        );

        // Assert
        string responseContent = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        responseContent.Should().Contain("Request body id must match the id in the url.");
    }

    [Test]
    public async Task UpdateProfile_DuplicateName_ShouldReturnBadRequest()
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

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        actualResponse!["validationErrors"]!["Name"]![0]!
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
        actualResponse!["validationErrors"]!["Definition"]![0]!
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
        A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
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
        A.CallTo(() => _profileRepository.DeleteProfile(A<int>.Ignored))
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
        A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
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
        A.CallTo(() => _profileRepository.GetProfile(A<int>.Ignored))
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

public class ProfileMissingBodyTests
{
    private static void AssertGenericMissingBodyContract(HttpResponseMessage response, JsonObject body)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The request could not be processed. See 'errors' for details.");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!
            .AsArray()
            .Select(e => e!.GetValue<string>())
            .Should()
            .Equal("A non-empty request body is required.");
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var profileRepository = A.Fake<IProfileRepository>();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    collection.AddTransient(_ => profileRepository);
                }
            );
        });
    }

    [TestFixture]
    public class Given_a_post_with_no_body
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory();
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            _response = await _client.PostAsync("/v3/profiles/", null);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_generic_missing_body_contract() =>
            AssertGenericMissingBodyContract(_response, _body);
    }

    [TestFixture]
    public class Given_a_post_with_a_json_null_body
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory();
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            using var content = new StringContent("null", Encoding.UTF8, "application/json");
            _response = await _client.PostAsync("/v3/profiles/", content);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_generic_missing_body_contract() =>
            AssertGenericMissingBodyContract(_response, _body);
    }

    [TestFixture]
    public class Given_a_put_with_a_bad_route_id_and_a_valid_body
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _factory = CreateFactory();
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);

            var validBody = new
            {
                id = 1,
                name = "ValidProfile",
                definition = "<Profile name=\"ValidProfile\"><Resource name=\"Resource1\"><ReadContentType memberSelection=\"IncludeAll\" /></Resource></Profile>",
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(validBody),
                Encoding.UTF8,
                "application/json"
            );
            _response = await _client.PutAsync("/v3/profiles/abc", content);
            _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_returns_the_parameter_validation_contract()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter");
            _body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
            _body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("Parameter validation failed. See 'errors' for details.");
            _body["status"]!.GetValue<int>().Should().Be(400);
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(e => e!.GetValue<string>())
                .Should()
                .Equal("The request contains one or more invalid parameters.");
        }
    }
}
