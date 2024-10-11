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
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using NUnit.Framework.Internal;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class SecuredModuleTests
{
    [Test]
    public async Task Given_a_client_with_valid_role()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
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
        var response = await client.GetAsync("/secure");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain(AuthenticationConstants.Client_Id);
    }

    [Test]
    public async Task Given_a_client_with_invalid_role()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
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
        var response = await client.GetAsync("/secure");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
