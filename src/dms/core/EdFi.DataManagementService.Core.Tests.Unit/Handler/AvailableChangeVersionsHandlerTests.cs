// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class Given_AvailableChangeVersionsHandler
{
    private static RequestInfo CreateRequestInfo(IServiceProvider scopedServiceProvider)
    {
        return new RequestInfo(
            new FrontendRequest(
                Path: "/changeQueries/v1/availableChangeVersions",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("trace-id"),
                RouteQualifiers: []
            ),
            RequestMethod.GET,
            scopedServiceProvider
        );
    }

    private static async Task Execute(RequestInfo requestInfo)
    {
        var handler = new AvailableChangeVersionsHandler(NullLogger<AvailableChangeVersionsHandler>.Instance);

        await ((IPipelineStep)handler).Execute(requestInfo, () => Task.CompletedTask);
    }

    [Test]
    public async Task It_returns_oldest_zero_and_newest_from_repository()
    {
        var repository = A.Fake<IChangeQueryRepository>();
        A.CallTo(() => repository.GetNewestChangeVersion(A<CancellationToken>._)).Returns(42L);

        RequestInfo requestInfo = CreateRequestInfo(new SingleServiceProvider(repository));

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        requestInfo
            .FrontendResponse.Body!.ToJsonString()
            .Should()
            .Be("{\"oldestChangeVersion\":0,\"newestChangeVersion\":42}");
    }

    [Test]
    public async Task It_returns_zero_newest_for_empty_database()
    {
        var repository = A.Fake<IChangeQueryRepository>();
        A.CallTo(() => repository.GetNewestChangeVersion(A<CancellationToken>._)).Returns(0L);

        RequestInfo requestInfo = CreateRequestInfo(new SingleServiceProvider(repository));

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        requestInfo
            .FrontendResponse.Body!.ToJsonString()
            .Should()
            .Be("{\"oldestChangeVersion\":0,\"newestChangeVersion\":0}");
    }

    [Test]
    public async Task It_returns_404_when_change_query_repository_is_not_registered()
    {
        RequestInfo requestInfo = CreateRequestInfo(new SingleServiceProvider(repository: null));

        await Execute(requestInfo);

        requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().Contain("urn:ed-fi:api:not-found");
    }

    /// <summary>
    /// Minimal scoped service provider that resolves only IChangeQueryRepository, mirroring how the
    /// handler resolves the repository from the request scope. A null repository models a
    /// datastore configuration where Change Queries are unsupported.
    /// </summary>
    private sealed class SingleServiceProvider(IChangeQueryRepository? repository) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IChangeQueryRepository) ? repository : null;
    }
}
