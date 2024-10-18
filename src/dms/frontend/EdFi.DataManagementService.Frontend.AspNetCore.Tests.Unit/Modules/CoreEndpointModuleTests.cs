// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using FluentAssertions;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class CoreEndpointModuleTests
{
    [TestFixture]
    public class When_authorization_enabled_and_given_a_valid_client_token
    {
        private HttpResponseMessage? _response;

        [SetUp]
        public async Task SetUp()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                       (context, configuration) =>
                       {
                           configuration.AddInMemoryCollection(
                               new Dictionary<string, string?>
                               {
                                   ["IdentitySettings:EnforceAuthorization"] = "true"
                               }
                           );
                       }
                   );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(AuthenticationConstants.AuthenticationSchema, options => { });

                        collection.AddAuthorization(options => options.AddPolicy(SecurityConstants.ServicePolicy,
                        policy => policy.RequireClaim(ClaimTypes.Role, AuthenticationConstants.Role)));

                    }
                );
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
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestFixture]
    public class When_authorization_enabled_and_given_a_invalid_client_token
    {
        private HttpResponseMessage? _response;

        [SetUp]
        public async Task SetUp()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                       (context, configuration) =>
                       {
                           configuration.AddInMemoryCollection(
                               new Dictionary<string, string?>
                               {
                                   ["IdentitySettings:EnforceAuthorization"] = "true"
                               }
                           );
                       }
                   );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddAuthentication(AuthenticationConstants.AuthenticationSchema)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(AuthenticationConstants.AuthenticationSchema, options => { });

                        collection.AddAuthorization(options => options.AddPolicy(SecurityConstants.ServicePolicy,
                        policy => policy.RequireClaim(ClaimTypes.Role, "invalid-role")));

                    }
                );
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
            _response!.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [TestFixture]
    public class When_authorization_disabled
    {
        private HttpResponseMessage? _response;

        [SetUp]
        public async Task SetUp()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                       (context, configuration) =>
                       {
                           configuration.AddInMemoryCollection(
                               new Dictionary<string, string?>
                               {
                                   ["IdentitySettings:EnforceAuthorization"] = "false"
                               }
                           );
                       }
                   );
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
            _response!.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
