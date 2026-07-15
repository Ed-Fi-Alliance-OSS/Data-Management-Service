// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
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

public class ClaimSetModuleTests
{
    private readonly IClaimSetRepository _claimSetRepository = A.Fake<IClaimSetRepository>();
    private readonly IClaimsHierarchyRepository _claimsHierarchyRepository =
        A.Fake<IClaimsHierarchyRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();
    private readonly IClaimSetDataProvider _dataProvider = A.Fake<IClaimSetDataProvider>();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                        .AddTransient((_) => _claimSetRepository)
                        .AddTransient((_) => _dataProvider)
                        .AddTransient((_) => _claimsHierarchyRepository);
                }
            );
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [TestFixture]
    public class SuccessTests : ClaimSetModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _claimSetRepository.InsertClaimSet(A<ClaimSetInsertCommand>.Ignored))
                .Returns(new ClaimSetInsertResult.Success(1));

            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
                .Returns(
                    new ClaimSetQueryResult.Success([
                        new ClaimSetResponse() { Name = "Test ClaimSet", IsSystemReserved = false },
                    ])
                );

            A.CallTo(() => _claimSetRepository.GetClaimSet(A<long>.Ignored))
                .Returns(
                    new ClaimSetGetResult.Success(
                        new ClaimSetResponse
                        {
                            Id = 1,
                            Name = "ClaimSet with ResourceClaims",
                            IsSystemReserved = true,
                            ResourceClaims =
                            [
                                new ResourceClaim
                                {
                                    Name = "systemDescriptors",
                                    ClaimName = "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                                    ParentClaimName = null,
                                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                                },
                            ],
                        }
                    )
                );

            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.Success());

            A.CallTo(() => _claimSetRepository.DeleteClaimSet(A<long>.Ignored))
                .Returns(new ClaimSetDeleteResult.Success());

            A.CallTo(() => _claimSetRepository.Copy(A<ClaimSetCopyCommand>.Ignored))
                .Returns(new ClaimSetCopyResult.Success(1));

            A.CallTo(() => _claimSetRepository.Export(A<long>.Ignored))
                .Returns(
                    new ClaimSetExportResult.Success(
                        new ClaimSetExportResponse()
                        {
                            Id = 1,
                            Name = "ClaimSet with ResourceClaims",
                            IsSystemReserved = true,
                            ResourceClaims =
                            [
                                new ResourceClaim
                                {
                                    Name = "systemDescriptors",
                                    ClaimName = "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                                    ParentClaimName = null,
                                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                                },
                            ],
                        }
                    )
                );

            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(
                    new ClaimSetImportResult.Success(
                        2,
                        new[] { "Skipped: http://example.org/nonexistent/Claim" }
                    )
                );

            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);
            A.CallTo(() => _dataProvider.GetAuthorizationStrategies())
                .Returns(["NoFurtherAuthorizationRequired"]);

            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(
                    new ClaimsHierarchyGetResult.Success(
                        [new() { Name = "Testing-POST-for-ClaimSet" }],
                        DateTime.Now,
                        1
                    )
                );
        }

        [Test]
        public async Task Should_return_success_responses()
        {
            //Arrange
            using var client = SetUpClient();
            A.CallTo(() => _httpContext.Request.Path).Returns("/v3/claimSets");

            //Act
            var addResponse = await client.PostAsync(
                "/v3/claimSets",
                new StringContent(
                    """
                    {
                        "claimSetName":"Testing-POST-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v3/claimSets?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/claimSets/1");
            var updateResponse = await client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "claimSetName": "Test-11"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/claimSets/1");
            var copyResponse = await client.PostAsync(
                "/v3/claimSets/copy",
                new StringContent(
                    """
                    {
                        "originalId" : 1,
                        "claimSetName": "Test-Copy"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var exportResponse = await client.GetAsync("/v3/claimSets/1/export");
            var importResponse = await client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(
                    """
                    {
                        "claimSetName" : "Testing-Import-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            addResponse.Headers.Location!.ToString().Should().EndWith("/v3/claimSets/1");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            copyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            copyResponse.Headers.Location!.ToString().Should().EndWith("/v3/claimSets/1");
            exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            importResponse.StatusCode.Should().Be(HttpStatusCode.Created);

            // Verify import response body contains our combined warnings array (repository-provided plus any validator warnings)
            var importJson = JsonNode.Parse(await importResponse.Content.ReadAsStringAsync());
            importJson!["id"]!.GetValue<int>().Should().Be(2);
            importJson["warnings"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Contain("Skipped: http://example.org/nonexistent/Claim");

            var getByIdJson = JsonNode.Parse(await getByIdResponse.Content.ReadAsStringAsync());
            getByIdJson!["claimSetName"]!.GetValue<string>().Should().Be("ClaimSet with ResourceClaims");
            getByIdJson["resourceClaims"]!.AsArray().Should().HaveCount(1);

            var exportJson = JsonNode.Parse(await exportResponse.Content.ReadAsStringAsync());
            exportJson!["claimSetName"]!.GetValue<string>().Should().Be("ClaimSet with ResourceClaims");
            exportJson["resourceClaims"]!.AsArray().Should().HaveCount(1);
        }

        [Test]
        public async Task GetById_should_emit_v3_authorization_strategy_field_names()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.GetClaimSet(1))
                .Returns(
                    new ClaimSetGetResult.Success(
                        new ClaimSetResponse
                        {
                            Id = 1,
                            Name = "ClaimSetV3",
                            IsSystemReserved = false,
                            ResourceClaims =
                            [
                                new ResourceClaim
                                {
                                    Name = "school",
                                    ClaimName = "http://ed-fi.org/identity/claims/ed-fi/school",
                                    ParentClaimName =
                                        "http://ed-fi.org/identity/claims/domains/educationOrganizations",
                                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                                    DefaultAuthorizationStrategies =
                                    [
                                        new ClaimSetResourceClaimActionAuthStrategies
                                        {
                                            ActionName = "Read",
                                            AuthorizationStrategies =
                                            [
                                                new AuthorizationStrategy
                                                {
                                                    AuthorizationStrategyName =
                                                        "NoFurtherAuthorizationRequired",
                                                },
                                            ],
                                        },
                                    ],
                                    AuthorizationStrategyOverrides = [],
                                },
                            ],
                        }
                    )
                );

            using var client = SetUpClient();

            // Act
            var response = await client.GetAsync("/v3/claimSets/1");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var resourceClaim = json["resourceClaims"]!.AsArray()[0]!;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            resourceClaim["_defaultAuthorizationStrategies"].Should().NotBeNull();
            resourceClaim["authorizationStrategyOverrides"].Should().NotBeNull();
            resourceClaim["_defaultAuthorizationStrategiesForCrud"].Should().BeNull();
            resourceClaim["authorizationStrategyOverridesForCRUD"].Should().BeNull();
            resourceClaim["_defaultAuthorizationStrategies"]![0]!["authorizationStrategies"]![0]![
                "authStrategyName"
            ]!
                .GetValue<string>()
                .Should()
                .Be("NoFurtherAuthorizationRequired");
            resourceClaim["_defaultAuthorizationStrategies"]![0]!["authorizationStrategies"]![0]!
                ["name"]
                .Should()
                .BeNull();
        }

        [Test]
        public async Task Import_should_bind_v3_auth_strategy_name_and_include_parent_mismatch_warning()
        {
            // Arrange
            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(
                    new ClaimsHierarchyGetResult.Success(
                        [
                            new()
                            {
                                Name = "http://ed-fi.org/identity/claims/domains/educationOrganizations",
                                Claims = [new() { Name = "http://ed-fi.org/identity/claims/ed-fi/school" }],
                            },
                        ],
                        DateTime.Now,
                        1
                    )
                );

            ClaimSetImportCommand? capturedCommand = null;

            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Invokes(call => capturedCommand = call.GetArgument<ClaimSetImportCommand>(0))
                .Returns(new ClaimSetImportResult.Success(9));

            using var client = SetUpClient();

            // Act
            var response = await client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(
                    """
                    {
                        "claimSetName": "Imported-ClaimSet",
                        "resourceClaims": [
                            {
                                "name": "school",
                                "claimName": "http://ed-fi.org/identity/claims/ed-fi/school",
                                "parentClaimName": "http://ed-fi.org/identity/claims/domains/wrongParent",
                                "actions": [
                                    {
                                        "name": "Read",
                                        "enabled": true
                                    }
                                ],
                                "authorizationStrategyOverrides": [
                                    {
                                        "actionName": "Read",
                                        "authorizationStrategies": [
                                            {
                                                "authStrategyName": "NoFurtherAuthorizationRequired"
                                            }
                                        ]
                                    }
                                ],
                                "_defaultAuthorizationStrategies": [
                                    {
                                        "actionName": "Read",
                                        "authorizationStrategies": [
                                            {
                                                "authStrategyName": "UnknownDefaultShouldBeIgnored"
                                            }
                                        ]
                                    }
                                ]
                            }
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            capturedCommand.Should().NotBeNull();
            capturedCommand!.ResourceClaims.Should().ContainSingle();
            capturedCommand
                .ResourceClaims![0]
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(o => o.ActionName == "Read");
            capturedCommand
                .ResourceClaims![0]
                .AuthorizationStrategyOverrides[0]
                .AuthorizationStrategies.Should()
                .ContainSingle(s => s.AuthorizationStrategyName == "NoFurtherAuthorizationRequired");
            json["warnings"]!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .Should()
                .Contain(warning => warning.Contains("Correct parent resource is"));
        }

        [Test]
        public async Task Import_should_deduplicate_repeated_warning_text()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.Success(9, ["http://example.org/nonexistent/Claim"]));

            using var client = SetUpClient();

            // Act
            var response = await client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(
                    """
                    {
                        "claimSetName": "Imported-ClaimSet",
                        "resourceClaims": [
                            {
                                "claimName": "http://example.org/nonexistent/Claim",
                                "actions": [
                                    {
                                        "name": "Read",
                                        "enabled": true
                                    }
                                ]
                            }
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            json["warnings"]!.AsArray().Should().HaveCount(1);
            json["warnings"]![0]!.GetValue<string>().Should().Be("http://example.org/nonexistent/Claim");
        }

        [Test]
        public async Task Import_should_deduplicate_repeated_warning_text_ignoring_case()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.Success(9, ["HTTP://EXAMPLE.ORG/NONEXISTENT/CLAIM"]));

            using var client = SetUpClient();

            // Act
            var response = await client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(
                    """
                    {
                        "claimSetName": "Imported-ClaimSet",
                        "resourceClaims": [
                            {
                                "claimName": "http://example.org/nonexistent/claim",
                                "actions": [
                                    {
                                        "name": "Read",
                                        "enabled": true
                                    }
                                ]
                            }
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            json["warnings"]!.AsArray().Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class FailureValidationTests : ClaimSetModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(
                    new ClaimsHierarchyGetResult.Success(
                        [new() { Name = "Test ResourceClaim" }],
                        DateTime.Now,
                        1
                    )
                );
        }

        [Test]
        public async Task Should_return_bad_request()
        {
            //Arrange
            using var client = SetUpClient();

            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);

            string invalidInsertBody = """
                {
                   "claimSetName" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;
            string claimSetNameWithWhiteSpace = """
                {
                   "claimSetName" : "ClaimSet name with white space"
                }
                """;

            string invalidPutBody = """
                {
                    "id": 1,
                    "claimSetName" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            string invalidImportBody = """
                {
                    "claimSetName" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            string invalidCopyBody = """
                {
                    "originalId" : 1,
                    "claimSetName" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v3/claimSets",
                new StringContent(invalidInsertBody, Encoding.UTF8, "application/json")
            );

            var addResponseWithInvalidName = await client.PostAsync(
                "/v3/claimSets",
                new StringContent(claimSetNameWithWhiteSpace, Encoding.UTF8, "application/json")
            );

            var updateResponse = await client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(invalidPutBody, Encoding.UTF8, "application/json")
            );

            var importResponse = await client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(invalidImportBody, Encoding.UTF8, "application/json")
            );

            var copyResponse = await client.PostAsync(
                "/v3/claimSets/copy",
                new StringContent(invalidCopyBody, Encoding.UTF8, "application/json")
            );

            var actualPostResponse = JsonNode.Parse(await addResponse.Content.ReadAsStringAsync());
            var expectedPostResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "The claim set name must be less than 256 characters."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualPostResponse!["correlationId"]!.GetValue<string>())
            );

            var actualPostResponseForInvalidName = JsonNode.Parse(
                await addResponseWithInvalidName.Content.ReadAsStringAsync()
            );
            var expectedPostResponseForInvalidName = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "Claim set name must not contain white spaces."
                    ]
                  },
                  "errors": []
                }
                """.Replace(
                    "{correlationId}",
                    actualPostResponseForInvalidName!["correlationId"]!.GetValue<string>()
                )
            );

            var actualPutResponse = JsonNode.Parse(await updateResponse.Content.ReadAsStringAsync());
            var expectedPutResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "The claim set name must be less than 256 characters."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualPutResponse!["correlationId"]!.GetValue<string>())
            );

            var actualImportResponse = JsonNode.Parse(await importResponse.Content.ReadAsStringAsync());
            var expectedImportResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "The claim set name must be less than 256 characters."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualImportResponse!["correlationId"]!.GetValue<string>())
            );

            var actualCopyResponse = JsonNode.Parse(await copyResponse.Content.ReadAsStringAsync());
            var expectedCopyResponse = JsonNode.Parse(
                """
                {
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "The length of 'Name' must be 256 characters or fewer. You entered 300 characters."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualCopyResponse!["correlationId"]!.GetValue<string>())
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualPostResponse, expectedPostResponse).Should().Be(true);

            addResponseWithInvalidName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode
                .DeepEquals(actualPostResponseForInvalidName, expectedPostResponseForInvalidName)
                .Should()
                .Be(true);

            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualPutResponse, expectedPutResponse).Should().Be(true);

            importResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualImportResponse, expectedImportResponse).Should().Be(true);

            copyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualCopyResponse, expectedCopyResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_bad_request_mismatch_id()
        {
            //Arrange
            using var client = SetUpClient();
            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);

            //Act
            var updateResponse = await client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 2,
                        "claimSetName": "Test-11",
                        "resourceClaims": [
                            {
                                "name": "Test ResourceClaim",
                                "actions": [
                                  {
                                    "name": "Create",
                                    "enabled": true
                                  }
                                ]
                            }
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            string updateResponseContent = await updateResponse.Content.ReadAsStringAsync();
            updateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            updateResponseContent.Should().Contain("Request body id must match the id in the url.");
        }
    }

    [TestFixture]
    public class FailureNotFoundTests : ClaimSetModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _claimSetRepository.GetClaimSet(A<long>.Ignored))
                .Returns(new ClaimSetGetResult.FailureNotFound());

            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.FailureNotFound());

            A.CallTo(() => _claimSetRepository.DeleteClaimSet(A<long>.Ignored))
                .Returns(new ClaimSetDeleteResult.FailureNotFound());

            A.CallTo(() => _claimSetRepository.Export(A<long>.Ignored))
                .Returns(new ClaimSetExportResult.FailureNotFound());

            A.CallTo(() => _claimSetRepository.Copy(A<ClaimSetCopyCommand>.Ignored))
                .Returns(new ClaimSetCopyResult.FailureNotFound());

            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);
        }

        [Test]
        public async Task Should_return_not_found_responses()
        {
            //Arrange
            using var client = SetUpClient();

            //Act
            var getByIdResponse = await client.GetAsync("/v3/claimSets/99");
            var updateResponse = await client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "claimSetName": "Test-11",
                        "resourceClaims": [
                             {
                                "name": "Test ResourceClaim",
                                "actions": [
                                  {
                                    "name": "Create",
                                    "enabled": true
                                  }
                                ]
                                }
                            ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/claimSets/1");
            var copyResponse = await client.PostAsync(
                "/v3/claimSets/copy",
                new StringContent(
                    """
                    {
                        "originalId" : 1,
                        "claimSetName": "Test-Copy"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var exportResponse = await client.GetAsync("/v3/claimSets/1/export");

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            copyResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            exportResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class FailureUnknownTests : ClaimSetModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _claimSetRepository.InsertClaimSet(A<ClaimSetInsertCommand>.Ignored))
                .Returns(new ClaimSetInsertResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
                .Returns(new ClaimSetQueryResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.GetClaimSet(A<long>.Ignored))
                .Returns(new ClaimSetGetResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.DeleteClaimSet(A<long>.Ignored))
                .Returns(new ClaimSetDeleteResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.Copy(A<ClaimSetCopyCommand>.Ignored))
                .Returns(new ClaimSetCopyResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.Export(A<long>.Ignored))
                .Returns(new ClaimSetExportResult.FailureUnknown(""));

            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.FailureUnknown(""));

            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(
                    new ClaimsHierarchyGetResult.Success(
                        [
                            new() { Name = "Test ResourceClaim" },
                            new() { Name = "Testing-POST-for-ClaimSet" },
                            new() { Name = "Testing-Import-for-ClaimSet" },
                        ],
                        DateTime.Now,
                        1
                    )
                );

            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);
        }

        [Test]
        public async Task Should_return_internal_server_error_response()
        {
            //Arrange
            using var client = SetUpClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v3/claimSets",
                new StringContent(
                    """
                    {
                        "claimSetName":"Testing-POST-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v3/claimSets?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v3/claimSets/1");
            var updateResponse = await client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "claimSetName": "Test-11",
                        "resourceClaims": [
                         {
                            "name": "Test ResourceClaim",
                            "actions": [
                              {
                                "name": "Create",
                                "enabled": true
                              }
                            ]
                            }
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v3/claimSets/1");
            var copyResponse = await client.PostAsync(
                "/v3/claimSets/copy",
                new StringContent(
                    """
                    {
                        "originalId" : 1,
                        "claimSetName": "Test-Copy"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var exportResponse = await client.GetAsync("/v3/claimSets/1/export");
            var importResponse = await client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(
                    """
                    {
                        "claimSetName" : "Testing-Import-for-ClaimSet",
                        "resourceClaims" : [
                            {
                                "name": "Test ResourceClaim",
                                "actions": [
                                  {
                                    "name": "Create",
                                    "enabled": true
                                  }
                                ]
                            }
                        ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            copyResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            exportResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            importResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }

    [TestFixture]
    public class FailureDuplicateClaimSetNameTests : ClaimSetModuleTests
    {
        private HttpClient _client;

        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);

            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(
                    new ClaimsHierarchyGetResult.Success([new() { Name = "Test-Duplicate" }], DateTime.Now, 1)
                );

            _client = SetUpClient();
        }

        [TearDown]
        public void TearDown()
        {
            //Arrange
            _client.Dispose();
        }

        [Test]
        public async Task Should_return_duplicate_claimSetName_error_message_on_insert()
        {
            //Arrange
            A.CallTo(() => _claimSetRepository.InsertClaimSet(A<ClaimSetInsertCommand>.Ignored))
                .Returns(new ClaimSetInsertResult.FailureDuplicateClaimSetName());

            //Act
            var addResponse = await _client.PostAsync(
                "/v3/claimSets",
                new StringContent(
                    """
                    {
                        "claimSetName":"Testing-POST-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var actualPostResponse = JsonNode.Parse(await addResponse.Content.ReadAsStringAsync());
            var expectedPostResponse = JsonNode.Parse(
                """
                {
                  "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                  "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                  "title": "Identifying Values Are Not Unique",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": [
                    "A claim set with this name already exists."
                  ]
                }
                """.Replace("{correlationId}", actualPostResponse!["correlationId"]!.GetValue<string>())
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualPostResponse, expectedPostResponse).Should().BeTrue();
        }

        [Test]
        public async Task Should_return_duplicate_claimSetName_error_message_on_update()
        {
            //Arrange
            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.FailureDuplicateClaimSetName());

            //Act
            var addResponse = await _client.PutAsync(
                "/v3/claimSets/333",
                new StringContent(
                    """
                    {
                        "id": 333,
                        "claimSetName":"Testing-PUT-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var actualPostResponse = JsonNode.Parse(await addResponse.Content.ReadAsStringAsync());
            var expectedPostResponse = JsonNode.Parse(
                """
                {
                  "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                  "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                  "title": "Identifying Values Are Not Unique",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": [
                    "A claim set with this name already exists."
                  ]
                }
                """.Replace("{correlationId}", actualPostResponse!["correlationId"]!.GetValue<string>())
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualPostResponse, expectedPostResponse).Should().BeTrue();
        }

        [Test]
        public async Task Should_return_duplicate_claimSetName_error_message_on_import_claim_set()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.FailureDuplicateClaimSetName());

            string importBody = """
                {
                    "claimSetName" : "Test-Duplicate"
                }
                """;

            var importResponse = await _client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(importBody, Encoding.UTF8, "application/json")
            );

            string readAsStringAsync = await importResponse.Content.ReadAsStringAsync();
            var actualImportResponse = JsonNode.Parse(readAsStringAsync);
            var expectedImportResponse = JsonNode.Parse(
                """
                {
                  "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                  "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                  "title": "Identifying Values Are Not Unique",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": [
                    "A claim set with this name already exists."
                  ]
                }
                """.Replace("{correlationId}", actualImportResponse!["correlationId"]!.GetValue<string>())
            );

            importResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualImportResponse, expectedImportResponse).Should().Be(true);
        }

        [Test]
        public async Task Should_return_duplicate_claimSetName_error_message_on_copy()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.Copy(A<ClaimSetCopyCommand>.Ignored))
                .Returns(new ClaimSetCopyResult.FailureDuplicateClaimSetName());

            string copyBody = """
                {
                    "originalId" : 1,
                    "claimSetName" : "Test-Duplicate"
                }
                """;

            // Act
            var copyResponse = await _client.PostAsync(
                "/v3/claimSets/copy",
                new StringContent(copyBody, Encoding.UTF8, "application/json")
            );

            var actualCopyResponse = JsonNode.Parse(await copyResponse.Content.ReadAsStringAsync());
            var expectedCopyResponse = JsonNode.Parse(
                """
                {
                  "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                  "type": "urn:ed-fi:api:data-conflict:non-unique-identity",
                  "title": "Identifying Values Are Not Unique",
                  "status": 409,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": [
                    "A claim set with this name already exists."
                  ]
                }
                """.Replace("{correlationId}", actualCopyResponse!["correlationId"]!.GetValue<string>())
            );

            // Assert
            copyResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualCopyResponse, expectedCopyResponse).Should().BeTrue();
        }
    }

    [TestFixture]
    public class FailureMultipleHierarchiesFoundTests : ClaimSetModuleTests
    {
        private HttpClient _client;

        [SetUp]
        public void SetUp()
        {
            _client = SetUpClient();
        }

        [TearDown]
        public void TearDown()
        {
            //Arrange
            _client.Dispose();
        }

        [Test]
        public async Task Should_return_conflict_when_multiple_hierarchies_found_on_claim_set_update()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.FailureMultipleHierarchiesFound());

            var updateBody = """
                {
                    "id": 1,
                    "claimSetName": "Updated-ClaimSet"
                }
                """;

            // Act
            var response = await _client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(updateBody, Encoding.UTF8, "application/json")
            );

            var actualResponseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var expectedResponseJson = JsonNode.Parse(
                """
                {
                    "detail": "",
                    "type": "urn:ed-fi:api:internal-server-error",
                    "title": "Internal Server Error",
                    "status": 500,
                    "correlationId": "{correlationId}",
                    "validationErrors": {},
                    "errors": []
                }
                """.Replace("{correlationId}", actualResponseJson!["correlationId"]!.GetValue<string>())
            );

            //Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            JsonNode.DeepEquals(actualResponseJson, expectedResponseJson).Should().BeTrue();
        }
    }

    [TestFixture]
    public class FailureMultiUserConflictTests : ClaimSetModuleTests
    {
        private HttpClient _client;

        [SetUp]
        public void SetUp()
        {
            _client = SetUpClient();
        }

        [TearDown]
        public void TearDown()
        {
            //Arrange
            _client.Dispose();
        }

        [Test]
        public async Task Should_return_conflict_when_multi_user_conflict_occurs_on_hierarchy_during_claim_set_update()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.FailureMultiUserConflict());

            var updateBody = """
                {
                    "id": 1,
                    "claimSetName": "Updated-ClaimSet"
                }
                """;

            // Act
            var response = await _client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(updateBody, Encoding.UTF8, "application/json")
            );

            var actualResponseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var expectedResponseJson = JsonNode.Parse(
                """
                {
                    "detail": "Unable to update claim set due to multi-user conflicts. Retry the request.",
                    "type": "urn:ed-fi:api:conflict",
                    "title": "Conflict",
                    "status": 409,
                    "correlationId": "{correlationId}",
                    "validationErrors": {},
                    "errors": []
                }
                """.Replace("{correlationId}", actualResponseJson!["correlationId"]!.GetValue<string>())
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualResponseJson, expectedResponseJson).Should().BeTrue();
        }

        [Test]
        public async Task Should_return_conflict_when_multi_user_conflict_occurs_on_claim_set_copy()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.Copy(A<ClaimSetCopyCommand>.Ignored))
                .Returns(new ClaimSetCopyResult.FailureMultiUserConflict());

            var copyBody = """
                {
                    "originalId": 1,
                    "claimSetName": "Copy-Target"
                }
                """;

            // Act
            var response = await _client.PostAsync(
                "/v3/claimSets/copy",
                new StringContent(copyBody, Encoding.UTF8, "application/json")
            );

            var actualResponseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var expectedResponseJson = JsonNode.Parse(
                """
                {
                    "detail": "Unable to copy claim set due to multi-user conflicts. Retry the request.",
                    "type": "urn:ed-fi:api:conflict",
                    "title": "Conflict",
                    "status": 409,
                    "correlationId": "{correlationId}",
                    "validationErrors": {},
                    "errors": []
                }
                """.Replace("{correlationId}", actualResponseJson!["correlationId"]!.GetValue<string>())
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualResponseJson, expectedResponseJson).Should().BeTrue();
        }

        [Test]
        public async Task Should_return_conflict_when_multi_user_conflict_occurs_on_claim_set_delete()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.DeleteClaimSet(A<long>.Ignored))
                .Returns(new ClaimSetDeleteResult.FailureMultiUserConflict());

            // Act
            var response = await _client.DeleteAsync("/v3/claimSets/1");

            var actualResponseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var expectedResponseJson = JsonNode.Parse(
                """
                {
                    "detail": "Unable to delete claim set due to multi-user conflicts. Retry the request.",
                    "type": "urn:ed-fi:api:conflict",
                    "title": "Conflict",
                    "status": 409,
                    "correlationId": "{correlationId}",
                    "validationErrors": {},
                    "errors": []
                }
                """.Replace("{correlationId}", actualResponseJson!["correlationId"]!.GetValue<string>())
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            JsonNode.DeepEquals(actualResponseJson, expectedResponseJson).Should().BeTrue();
        }
    }

    [TestFixture]
    public class FailureSystemReservedTests : ClaimSetModuleTests
    {
        private HttpClient _client;

        [SetUp]
        public void SetUp()
        {
            _client = SetUpClient();
        }

        [TearDown]
        public void TearDown()
        {
            //Arrange
            _client.Dispose();
        }

        [Test]
        public async Task Should_return_bad_request_when_attempt_made_to_update_a_system_reserved_claim_set()
        {
            // Arrange
            A.CallTo(() => _claimSetRepository.UpdateClaimSet(A<ClaimSetUpdateCommand>.Ignored))
                .Returns(new ClaimSetUpdateResult.FailureSystemReserved());

            var updateBody = """
                {
                    "id": 1,
                    "claimSetName": "Updated-System-Reserved-ClaimSet"
                }
                """;

            // Act
            var response = await _client.PutAsync(
                "/v3/claimSets/1",
                new StringContent(updateBody, Encoding.UTF8, "application/json")
            );

            var actualResponseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var expectedResponseJson = JsonNode.Parse(
                """
                {
                    "detail": "The specified claim set is system-reserved and cannot be updated.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": "{correlationId}",
                    "validationErrors": {},
                    "errors": []
                }
                """.Replace("{correlationId}", actualResponseJson!["correlationId"]!.GetValue<string>())
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualResponseJson, expectedResponseJson).Should().BeTrue();
        }

        [Test]
        public async Task Should_return_bad_request_when_attempt_made_to_import_a_system_reserved_claim_set()
        {
            // Arrange
            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);
            A.CallTo(() => _dataProvider.GetAuthorizationStrategies())
                .Returns(["NoFurtherAuthorizationRequired"]);
            A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction>.Ignored))
                .Returns(new ClaimsHierarchyGetResult.Success([], DateTime.Now, 1));
            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.FailureSystemReserved());

            var importBody = """
                {
                    "claimSetName": "System-Reserved"
                }
                """;

            // Act
            var response = await _client.PostAsync(
                "/v3/claimSets/import",
                new StringContent(importBody, Encoding.UTF8, "application/json")
            );

            var actualResponseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var expectedResponseJson = JsonNode.Parse(
                """
                {
                    "detail": "The specified claim set is system-reserved and cannot be imported.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": "{correlationId}",
                    "validationErrors": {},
                    "errors": []
                }
                """.Replace("{correlationId}", actualResponseJson!["correlationId"]!.GetValue<string>())
            );

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualResponseJson, expectedResponseJson).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Invalid_PagingQuery : ClaimSetModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
                .Returns(new ClaimSetQueryResult.Success([]));
        }

        [Test]
        public async Task Should_return_400_when_orderBy_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?orderBy=invalidField");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_direction_is_invalid()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?orderBy=id&direction=SIDEWAYS");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_negative()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?offset=-1");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_zero()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?limit=0");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_200_with_valid_orderBy_and_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?orderBy=name&direction=DESC");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_200_with_claim_set_name_orderBy_alias()
        {
            ClaimSetQuery? capturedQuery = null;
            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
                .Invokes(call => capturedQuery = call.GetArgument<ClaimSetQuery>(0))
                .Returns(new ClaimSetQueryResult.Success([]));

            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?orderBy=claimSetName&direction=DESC");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capturedQuery.Should().NotBeNull();
            capturedQuery!.OrderBy.Should().Be("claimSetName");
        }

        [Test]
        public async Task Should_return_200_when_filter_name_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?name=MyClaimSet");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_bind_claim_set_name_filter_alias_to_repository_query()
        {
            ClaimSetQuery? capturedQuery = null;
            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
                .Invokes(call => capturedQuery = call.GetArgument<ClaimSetQuery>(0))
                .Returns(new ClaimSetQueryResult.Success([]));

            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?claimSetName=MyClaimSet");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capturedQuery.Should().NotBeNull();
            capturedQuery!.Name.Should().Be("MyClaimSet");
        }

        [Test]
        public async Task Should_prefer_claim_set_name_filter_when_both_aliases_are_provided()
        {
            ClaimSetQuery? capturedQuery = null;
            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
                .Invokes(call => capturedQuery = call.GetArgument<ClaimSetQuery>(0))
                .Returns(new ClaimSetQueryResult.Success([]));

            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?name=OldName&claimSetName=PreferredName");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capturedQuery.Should().NotBeNull();
            capturedQuery!.Name.Should().Be("PreferredName");
        }

        [Test]
        public async Task Should_return_200_when_filter_id_is_provided()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?id=1");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public async Task Should_return_400_when_offset_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?offset=abc");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_400_when_limit_is_non_numeric()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?limit=xyz");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task Should_return_200_when_orderBy_omitted_with_direction()
        {
            using var client = SetUpClient();
            var response = await client.GetAsync("/v3/claimSets?direction=asc");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
