// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class OwnershipTokenModuleTests
{
    private readonly IOwnershipTokenRepository _ownershipTokenRepository =
        A.Fake<IOwnershipTokenRepository>();
    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();

    [SetUp]
    public void ClearRepositoryCalls() => Fake.ClearRecordedCalls(_ownershipTokenRepository);

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

    private HttpClient SetUpClient()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTestAuthentication();
                collection.AddTransient(_ => _ownershipTokenRepository);
            });
        });
        _factoryTracker.Track(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    [Test]
    public async Task It_creates_an_ownership_token()
    {
        A.CallTo(() => _ownershipTokenRepository.InsertOwnershipToken(A<OwnershipTokenInsertCommand>.Ignored))
            .Returns(new OwnershipTokenInsertResult.Success(12));
        using var client = SetUpClient();

        var response = await client.PostAsync(
            "/v3/ownershipTokens",
            new StringContent("""{"description":"District"}""", Encoding.UTF8, "application/json")
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith("/v3/ownershipTokens/12");
    }

    [Test]
    public async Task It_validates_and_forwards_query_paging()
    {
        OwnershipTokenQuery? capturedQuery = null;
        A.CallTo(() => _ownershipTokenRepository.QueryOwnershipTokens(A<OwnershipTokenQuery>.Ignored))
            .Invokes(call => capturedQuery = call.GetArgument<OwnershipTokenQuery>(0))
            .Returns(
                new OwnershipTokenQueryResult.Success([
                    new OwnershipTokenResponse { Id = 12, Description = "District" },
                ])
            );
        using var client = SetUpClient();

        var response = await client.GetAsync(
            "/v3/ownershipTokens?orderBy=description&direction=DESC&limit=10&offset=5"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.OrderBy.Should().Be("description");
        capturedQuery.Direction.Should().Be("DESC");
        capturedQuery.Limit.Should().Be(10);
        capturedQuery.Offset.Should().Be(5);
    }

    [Test]
    public void It_does_not_expose_unsupported_query_filters()
    {
        typeof(FrontendOwnershipTokenQuery).GetProperty("Id").Should().BeNull();
        typeof(FrontendOwnershipTokenQuery).GetProperty("Description").Should().BeNull();
        typeof(OwnershipTokenQuery).GetProperty("Id").Should().BeNull();
        typeof(OwnershipTokenQuery).GetProperty("Description").Should().BeNull();
    }

    [Test]
    public async Task It_returns_bad_request_for_invalid_query_orderBy()
    {
        A.CallTo(() => _ownershipTokenRepository.QueryOwnershipTokens(A<OwnershipTokenQuery>.Ignored))
            .Returns(new OwnershipTokenQueryResult.Success([]));
        using var client = SetUpClient();

        var response = await client.GetAsync("/v3/ownershipTokens?orderBy=invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(32768)]
    public async Task It_returns_bad_request_for_invalid_ownership_token_route_id(int id)
    {
        A.CallTo(() => _ownershipTokenRepository.GetOwnershipToken(A<int>.Ignored))
            .Returns(new OwnershipTokenGetResult.FailureNotFound());
        using var client = SetUpClient();

        var response = await client.GetAsync($"/v3/ownershipTokens/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => _ownershipTokenRepository.GetOwnershipToken(A<int>.Ignored)).MustNotHaveHappened();
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(32768)]
    public async Task It_returns_bad_request_for_invalid_ownership_token_update_route_id(int id)
    {
        using var client = SetUpClient();

        var response = await client.PutAsync(
            $"/v3/ownershipTokens/{id}",
            new StringContent(
                $$"""{"id":{{id}},"description":"District"}""",
                Encoding.UTF8,
                "application/json"
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => _ownershipTokenRepository.UpdateOwnershipToken(A<OwnershipTokenUpdateCommand>.Ignored))
            .MustNotHaveHappened();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task It_returns_bad_request_for_invalid_api_client_ownership_route_id(int id)
    {
        A.CallTo(() => _ownershipTokenRepository.GetApiClientOwnership(A<int>.Ignored))
            .Returns(new ApiClientOwnershipGetResult.FailureApiClientNotFound());
        using var client = SetUpClient();

        var response = await client.GetAsync($"/v3/apiClients/{id}/ownership");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => _ownershipTokenRepository.GetApiClientOwnership(A<int>.Ignored)).MustNotHaveHappened();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task It_returns_bad_request_for_invalid_api_client_ownership_update_route_id(int id)
    {
        using var client = SetUpClient();

        var response = await client.PutAsync(
            $"/v3/apiClients/{id}/ownership",
            new StringContent(
                $$"""{"apiClientId":{{id}},"creatorOwnershipTokenId":null,"ownershipTokenIds":[]}""",
                Encoding.UTF8,
                "application/json"
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() =>
                _ownershipTokenRepository.UpdateApiClientOwnership(A<ApiClientOwnershipUpdateCommand>.Ignored)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_missing_ownership_token_to_not_found()
    {
        A.CallTo(() => _ownershipTokenRepository.GetOwnershipToken(A<int>.Ignored))
            .Returns(new OwnershipTokenGetResult.FailureNotFound());
        using var client = SetUpClient();

        var response = await client.GetAsync("/v3/ownershipTokens/12");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_rejects_catalog_update_when_body_id_does_not_match_route_id()
    {
        using var client = SetUpClient();

        var response = await client.PutAsync(
            "/v3/ownershipTokens/1",
            new StringContent("""{"id":2,"description":"District"}""", Encoding.UTF8, "application/json")
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => _ownershipTokenRepository.UpdateOwnershipToken(A<OwnershipTokenUpdateCommand>.Ignored))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_missing_api_client_ownership_to_not_found()
    {
        A.CallTo(() => _ownershipTokenRepository.GetApiClientOwnership(A<int>.Ignored))
            .Returns(new ApiClientOwnershipGetResult.FailureApiClientNotFound());
        using var client = SetUpClient();

        var response = await client.GetAsync("/v3/apiClients/1/ownership");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_returns_api_client_ownership_without_the_route_id_in_the_response_body()
    {
        A.CallTo(() => _ownershipTokenRepository.GetApiClientOwnership(A<int>.Ignored))
            .Returns(
                new ApiClientOwnershipGetResult.Success(
                    new ApiClientOwnershipResponse { CreatorOwnershipTokenId = 2, OwnershipTokenIds = [3] }
                )
            );
        using var client = SetUpClient();

        var response = await client.GetAsync("/v3/apiClients/1/ownership");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body.AsObject().Should().NotContainKey("apiClientId");
        body["creatorOwnershipTokenId"]!.GetValue<int>().Should().Be(2);
        body["ownershipTokenIds"]!.AsArray().Count.Should().Be(1);
        body["ownershipTokenIds"]![0]!.GetValue<int>().Should().Be(3);
    }

    [Test]
    public async Task It_maps_missing_referenced_token_on_ownership_update_to_conflict()
    {
        A.CallTo(() =>
                _ownershipTokenRepository.UpdateApiClientOwnership(A<ApiClientOwnershipUpdateCommand>.Ignored)
            )
            .Returns(new ApiClientOwnershipUpdateResult.FailureOwnershipTokenNotFound());
        using var client = SetUpClient();

        var response = await client.PutAsync(
            "/v3/apiClients/1/ownership",
            new StringContent(
                """{"apiClientId":1,"creatorOwnershipTokenId":999,"ownershipTokenIds":[]}""",
                Encoding.UTF8,
                "application/json"
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["detail"]!.GetValue<string>().Should().Be("One or more ownership tokens were not found.");
    }

    [Test]
    public async Task It_replaces_api_client_ownership()
    {
        ApiClientOwnershipUpdateCommand? capturedCommand = null;
        A.CallTo(() =>
                _ownershipTokenRepository.UpdateApiClientOwnership(A<ApiClientOwnershipUpdateCommand>.Ignored)
            )
            .Invokes(call => capturedCommand = call.GetArgument<ApiClientOwnershipUpdateCommand>(0))
            .Returns(new ApiClientOwnershipUpdateResult.Success());
        using var client = SetUpClient();

        var response = await client.PutAsync(
            "/v3/apiClients/1/ownership",
            new StringContent(
                """{"apiClientId":1,"creatorOwnershipTokenId":2,"ownershipTokenIds":[3]}""",
                Encoding.UTF8,
                "application/json"
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        capturedCommand.Should().NotBeNull();
        capturedCommand!.ApiClientId.Should().Be(1);
        capturedCommand.CreatorOwnershipTokenId.Should().Be(2);
        capturedCommand.OwnershipTokenIds.Should().Equal(3);
    }

    [Test]
    public async Task It_rejects_api_client_ownership_when_body_id_does_not_match_route_id()
    {
        using var client = SetUpClient();

        var response = await client.PutAsync(
            "/v3/apiClients/1/ownership",
            new StringContent(
                """{"apiClientId":999,"creatorOwnershipTokenId":2,"ownershipTokenIds":[3]}""",
                Encoding.UTF8,
                "application/json"
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() =>
                _ownershipTokenRepository.UpdateApiClientOwnership(A<ApiClientOwnershipUpdateCommand>.Ignored)
            )
            .MustNotHaveHappened();
    }
}
