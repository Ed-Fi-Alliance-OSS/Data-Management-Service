// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.TestBase;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class CoreEndpointModuleTests
{
    [TestFixture]
    public class When_calling_data_endpoint : FrontendTestBase
    {
        private HttpResponseMessage? _response;

        [SetUp]
        public async Task SetUp()
        {
            // Arrange
            var claimSetCacheService = A.Fake<IClaimSetCacheService>();
            A.CallTo(() => claimSetCacheService.GetClaimSets()).Returns([]);
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.Get(A<FrontendRequest>.Ignored)).Returns(new FakeFrontendResponse());

            await using var factory = CreateTestFactory(services =>
            {
                services.AddTransient((x) => apiService);
                services.AddTransient((x) => claimSetCacheService);
            });

            using var client = factory.CreateClient();

            // Act
            _response = await client.GetAsync("/data/ed-fi/students");
        }

        [TearDown]
        public void TearDownAttribute()
        {
            _response?.Dispose();
        }

        [Test]
        public void Then_it_responds_with_status_OK()
        {
            // Frontend no longer handles authentication - it passes through to Core
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}

public record FakeFrontendResponse : IFrontendResponse
{
    public int StatusCode => 200;

    public JsonNode? Body => null;

    public Dictionary<string, string> Headers => [];

    public string? LocationHeaderPath => null;

    public string? ContentType => "application/json";
}
