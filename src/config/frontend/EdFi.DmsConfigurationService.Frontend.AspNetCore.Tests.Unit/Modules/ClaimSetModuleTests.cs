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
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

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

            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
                .Returns(
                    new ClaimSetQueryResult.Success(
                        [new ClaimSetResponse() { Name = "Test ClaimSet", IsSystemReserved = false }]
                    )
                );

            A.CallTo(() => _claimSetRepository.GetClaimSet(A<long>.Ignored))
                .Returns(
                    new ClaimSetGetResult.Success(
                        new ClaimSetResponse
                        {
                            Id = 1,
                            Name = "ClaimSet with ResourceClaims",
                            IsSystemReserved = true,
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
                        }
                    )
                );

            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.Success(2));

            A.CallTo(() => _dataProvider.GetActions()).Returns(["Create", "Read", "Update", "Delete"]);

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
            A.CallTo(() => _httpContext.Request.Path).Returns("/v2/claimSets");

            //Act
            var addResponse = await client.PostAsync(
                "/v2/claimSets",
                new StringContent(
                    """
                    {
                        "name":"Testing-POST-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v2/claimSets?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v2/claimSets/1");
            var updateResponse = await client.PutAsync(
                "/v2/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "name": "Test-11"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/claimSets/1");
            var copyResponse = await client.PostAsync(
                "/v2/claimSets/copy",
                new StringContent(
                    """
                    {
                        "originalId" : 1,
                        "name": "Test-Copy"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var exportResponse = await client.GetAsync("/v2/claimSets/1/export");
            var importResponse = await client.PostAsync(
                "/v2/claimSets/import",
                new StringContent(
                    """
                    {
                        "name" : "Testing-Import-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            addResponse.Headers.Location!.ToString().Should().EndWith("/v2/claimSets/1");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            copyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            importResponse.StatusCode.Should().Be(HttpStatusCode.Created);
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
                   "name" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;
            string claimSetNameWithWhiteSpace = """
                {
                   "name" : "ClaimSet name with white space"
                }
                """;

            string invalidPutBody = """
                {
                    "id": 1,
                    "name" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            string invalidImportBody = """
                {
                    "name" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789",
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
                """;

            string invalidCopyBody = """
                {
                    "originalId" : 1,
                    "name" : "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
                }
                """;

            //Act
            var addResponse = await client.PostAsync(
                "/v2/claimSets",
                new StringContent(invalidInsertBody, Encoding.UTF8, "application/json")
            );

            var addResponseWithInvalidName = await client.PostAsync(
                "/v2/claimSets",
                new StringContent(claimSetNameWithWhiteSpace, Encoding.UTF8, "application/json")
            );

            var updateResponse = await client.PutAsync(
                "/v2/claimSets/1",
                new StringContent(invalidPutBody, Encoding.UTF8, "application/json")
            );

            var importResponse = await client.PostAsync(
                "/v2/claimSets/import",
                new StringContent(invalidImportBody, Encoding.UTF8, "application/json")
            );

            var copyResponse = await client.PostAsync(
                "/v2/claimSets/copy",
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
                "/v2/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 2,
                        "name": "Test-11",
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
            var getByIdResponse = await client.GetAsync("/v2/claimSets/99");
            var updateResponse = await client.PutAsync(
                "/v2/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "name": "Test-11",
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
            var deleteResponse = await client.DeleteAsync("/v2/claimSets/1");
            var copyResponse = await client.PostAsync(
                "/v2/claimSets/copy",
                new StringContent(
                    """
                    {
                        "originalId" : 1,
                        "name": "Test-Copy"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var exportResponse = await client.GetAsync("/v2/claimSets/1/export");

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

            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored))
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
                "/v2/claimSets",
                new StringContent(
                    """
                    {
                        "name":"Testing-POST-for-ClaimSet"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v2/claimSets?offset=0&limit=25");
            var getByIdResponse = await client.GetAsync("/v2/claimSets/1");
            var updateResponse = await client.PutAsync(
                "/v2/claimSets/1",
                new StringContent(
                    """
                    {
                        "id": 1,
                        "name": "Test-11",
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
            var deleteResponse = await client.DeleteAsync("/v2/claimSets/1");
            var copyResponse = await client.PostAsync(
                "/v2/claimSets/copy",
                new StringContent(
                    """
                    {
                        "originalId" : 1,
                        "name": "Test-Copy"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var exportResponse = await client.GetAsync("/v2/claimSets/1/export");
            var importResponse = await client.PostAsync(
                "/v2/claimSets/import",
                new StringContent(
                    """
                    {
                        "name" : "Testing-Import-for-ClaimSet",
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
                "/v2/claimSets",
                new StringContent(
                    """
                    {
                        "name":"Testing-POST-for-ClaimSet"
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
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "A claim set with this name already exists in the database. Please enter a unique name."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualPostResponse!["correlationId"]!.GetValue<string>())
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                "/v2/claimSets/333",
                new StringContent(
                    """
                    {
                        "id": 333,
                        "name":"Testing-PUT-for-ClaimSet"
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
                  "detail": "Data validation failed. See 'validationErrors' for details.",
                  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                  "title": "Data Validation Failed",
                  "status": 400,
                  "correlationId": "{correlationId}",
                  "validationErrors": {
                    "Name": [
                      "A claim set with this name already exists in the database. Please enter a unique name."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualPostResponse!["correlationId"]!.GetValue<string>())
            );

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                    "name" : "Test-Duplicate"
                }
                """;

            var importResponse = await _client.PostAsync(
                "/v2/claimSets/import",
                new StringContent(importBody, Encoding.UTF8, "application/json")
            );

            string readAsStringAsync = await importResponse.Content.ReadAsStringAsync();
            var actualImportResponse = JsonNode.Parse(readAsStringAsync);
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
                      "A claim set with this name already exists in the database. Please enter a unique name."
                    ]
                  },
                  "errors": []
                }
                """.Replace("{correlationId}", actualImportResponse!["correlationId"]!.GetValue<string>())
            );

            importResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.DeepEquals(actualImportResponse, expectedImportResponse).Should().Be(true);
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
                    "name": "Updated-ClaimSet"
                }
                """;

            // Act
            var response = await _client.PutAsync(
                "/v2/claimSets/1",
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
                    "name": "Updated-ClaimSet"
                }
                """;

            // Act
            var response = await _client.PutAsync(
                "/v2/claimSets/1",
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
                    "name": "Updated-System-Reserved-ClaimSet"
                }
                """;

            // Act
            var response = await _client.PutAsync(
                "/v2/claimSets/1",
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
    }
}
