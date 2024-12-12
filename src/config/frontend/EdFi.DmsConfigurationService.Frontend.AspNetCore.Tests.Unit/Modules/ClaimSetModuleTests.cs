// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class ClaimSetModuleTests
{
    private readonly IClaimSetRepository _claimSetRepository = A.Fake<IClaimSetRepository>();
    private readonly HttpContext _httpContext = A.Fake<HttpContext>();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection
                        .AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            AuthenticationConstants.AuthenticationSchema,
                            _ => { }
                        );

                    collection.AddAuthorization(options =>
                        options.AddPolicy(
                            SecurityConstants.ServicePolicy,
                            policy => policy.RequireClaim(ClaimTypes.Role, AuthenticationConstants.Role)
                        )
                    );
                    collection.AddTransient((_) => _httpContext).AddTransient((_) => _claimSetRepository);
                }
            );
        });
        return factory.CreateClient();
    }

    [TestFixture]
    public class SuccessTests : ClaimSetModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _claimSetRepository.InsertClaimSet(A<ClaimSetInsertCommand>.Ignored))
                .Returns(new ClaimSetInsertResult.Success(1));

            A.CallTo(() => _claimSetRepository.QueryClaimSet(A<PagingQuery>.Ignored, false))
                .Returns(
                    new ClaimSetQueryResult.Success(
                        [
                            new ClaimSetResponseReduced()
                            {
                                ClaimSetName = "Test ClaimSet",
                                IsSystemReserved = false,
                            },
                        ]
                    )
                );

            A.CallTo(() => _claimSetRepository.GetClaimSet(A<long>.Ignored, false))
                .Returns(
                    new ClaimSetGetResult.Success(
                        new ClaimSetResponse()
                        {
                            Id = 1,
                            ClaimSetName = "ClaimSet with ResourceClaims",
                            IsSystemReserved = true,
                            ResourceClaims = JsonDocument
                                .Parse(
                                    """
                                        {
                                            "Resource": "Value"
                                        }
                                    """
                                )
                                .RootElement,
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
                            ClaimSetName = "ClaimSet with ResourceClaims",
                            _IsSystemReserved = true,
                            _Applications = [],
                            ResourceClaims = JsonDocument
                                .Parse(
                                    """
                                        {
                                            "Resource": "Value"
                                        }
                                    """
                                )
                                .RootElement,
                        }
                    )
                );

            A.CallTo(() => _claimSetRepository.Import(A<ClaimSetImportCommand>.Ignored))
                .Returns(new ClaimSetImportResult.Success(2));
        }

        [Test]
        public async Task Should_return_proper_success_responses()
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
                        "claimSetName":"Testing POST for ClaimSet",
                        "resourceClaims": {"resource":"Value"}
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
                        "claimSetName": "Test 11",
                        "isSystemReserved" : true,
                        "resourceClaims": {"resource":"Value"}
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
                        "name": "Test Copy" 
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
                        "claimSetName" : "Testing Import for ClaimSet",
                        "isSystemReserved": true,
                        "resourceClaims" : {"resource":"Value"}
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
    public class FailureValidationTests : ClaimSetModuleTests { }

    [TestFixture]
    public class FailureNotFoundTests : ClaimSetModuleTests { }

    [TestFixture]
    public class FailureUnknownTests : ClaimSetModuleTests { }
}
