// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Helper class to create test IFrontendResponse instances
/// </summary>
internal static class TestFrontendResponse
{
    public static IFrontendResponse Create(
        int statusCode,
        JsonNode? body = null,
        Dictionary<string, string>? headers = null
    )
    {
        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(statusCode);
        A.CallTo(() => response.Body).Returns(body);
        A.CallTo(() => response.Headers).Returns(headers ?? new Dictionary<string, string>());
        return response;
    }
}

[TestFixture]
[NonParallelizable]
public class ManagementEndpointModuleTests
{
    private IApiService _apiService = null!;
    private ILogger<ManagementEndpointModule> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _apiService = A.Fake<IApiService>();
        _logger = A.Fake<ILogger<ManagementEndpointModule>>();
    }

    [TestFixture]
    public class EndpointConfigurationTests : ManagementEndpointModuleTests
    {
        [Test]
        public async Task MapEndpoints_EnabledInConfig_MapsEndpoint()
        {
            // Arrange
            var successResponse = TestFrontendResponse.Create(
                statusCode: 200,
                body: JsonNode.Parse("{\"message\":\"Schema reloaded successfully\"}")
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(successResponse));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        // Override configuration
                        var configurationValues = new Dictionary<string, string?>
                        {
                            { "AppSettings:EnableManagementEndpoints", "true" },
                        };
                        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(configurationValues)
                            .Build();
                        collection.AddSingleton<IConfiguration>(configuration);

                        // Add mocked dependencies
                        collection.AddTransient((x) => _apiService);
                        collection.AddTransient((x) => _logger);
                    }
                );
            });

            using var client = factory.CreateClient();

            // Act
            var response = await client.PostAsync("/management/reload-api-schema", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => _apiService.ReloadApiSchemaAsync()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task MapEndpoints_DisabledInConfig_Returns404()
        {
            // Arrange
            var notFoundResponse = TestFrontendResponse.Create(statusCode: 404, body: null);
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(notFoundResponse));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        // Override configuration - EnableManagementEndpoints is false by default
                        var configurationValues = new Dictionary<string, string?>
                        {
                            { "AppSettings:EnableManagementEndpoints", "false" },
                        };
                        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(configurationValues)
                            .Build();
                        collection.AddSingleton<IConfiguration>(configuration);

                        // Add mocked dependencies
                        collection.AddTransient((x) => _apiService);
                        collection.AddTransient((x) => _logger);
                    }
                );
            });

            using var client = factory.CreateClient();

            // Act
            var response = await client.PostAsync("/management/reload-api-schema", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => _apiService.ReloadApiSchemaAsync()).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class ReloadEndpointTests : ManagementEndpointModuleTests
    {
        [Test]
        public async Task ReloadApiSchema_Success_Returns200()
        {
            // Arrange
            var successResponse = TestFrontendResponse.Create(
                statusCode: 200,
                body: JsonNode.Parse("{\"message\":\"Schema reloaded successfully\"}")
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(successResponse));

            // Act
            var result = await ManagementEndpointModule.ReloadApiSchema(_apiService, _logger);

            // Assert
            result.Should().BeAssignableTo<IResult>();
            var resultType = result.GetType();
            resultType.Name.Should().StartWith("Ok");

            // Verify status code through reflection since it's an anonymous type
            var statusCodeProperty = resultType.GetProperty("StatusCode");
            statusCodeProperty.Should().NotBeNull();
            statusCodeProperty!.GetValue(result).Should().Be(200);

            // Verify the value contains the expected message
            var valueProperty = resultType.GetProperty("Value");
            valueProperty.Should().NotBeNull();
            var value = valueProperty!.GetValue(result) as JsonNode;
            value.Should().NotBeNull();
            value!["message"]?.GetValue<string>().Should().Be("Schema reloaded successfully");
        }

        [Test]
        public async Task ReloadApiSchema_Failure_Returns500()
        {
            // Arrange
            var failureResponse = TestFrontendResponse.Create(
                statusCode: 500,
                body: JsonNode.Parse(
                    "{\"title\":\"Schema Reload Failed\",\"detail\":\"The API schema could not be reloaded. Check server logs for details.\"}"
                )
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(failureResponse));

            // Act
            var result = await ManagementEndpointModule.ReloadApiSchema(_apiService, _logger);

            // Assert
            result.Should().BeAssignableTo<IResult>();
            var resultType = result.GetType();
            resultType.Name.Should().StartWith("JsonHttpResult");

            // Verify status code through reflection
            var statusCodeProperty = resultType.GetProperty("StatusCode");
            statusCodeProperty.Should().NotBeNull();
            statusCodeProperty!.GetValue(result).Should().Be(500);

            // Verify the JSON response body
            var valueProperty = resultType.GetProperty("Value");
            valueProperty.Should().NotBeNull();
            var value = valueProperty!.GetValue(result) as JsonNode;
            value.Should().NotBeNull();
            value!["title"]?.GetValue<string>().Should().Be("Schema Reload Failed");
            value!
                ["detail"]
                ?.GetValue<string>()
                .Should()
                .Be("The API schema could not be reloaded. Check server logs for details.");
        }

        [Test]
        public async Task ReloadApiSchema_LogsAppropriately()
        {
            // Arrange
            var successResponse = TestFrontendResponse.Create(
                statusCode: 200,
                body: JsonNode.Parse("{\"message\":\"Schema reloaded successfully\"}")
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(successResponse));

            // Act
            await ManagementEndpointModule.ReloadApiSchema(_apiService, _logger);

            // Assert - Check that logging occurred
            A.CallTo(_logger)
                .Where(call =>
                    call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Information
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task ReloadApiSchema_Failure_LogsInformationOnly()
        {
            // Arrange
            var failureResponse = TestFrontendResponse.Create(
                statusCode: 500,
                body: JsonNode.Parse(
                    "{\"title\":\"Schema Reload Failed\",\"detail\":\"The API schema could not be reloaded. Check server logs for details.\"}"
                )
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(failureResponse));

            // Act
            await ManagementEndpointModule.ReloadApiSchema(_apiService, _logger);

            // Assert - Frontend only logs the initial request, error logging happens in the service
            A.CallTo(_logger)
                .Where(call =>
                    call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Information
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(_logger)
                .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
                .MustNotHaveHappened();
        }

        [Test]
        public async Task ReloadApiSchema_Exception_PropagatesUp()
        {
            // Arrange
            var exception = new InvalidOperationException("Test exception");
            A.CallTo(() => _apiService.ReloadApiSchemaAsync()).Throws(exception);

            // Act & Assert
            var act = async () => await ManagementEndpointModule.ReloadApiSchema(_apiService, _logger);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Test exception");
        }
    }

    [TestFixture]
    public class IntegrationTests : ManagementEndpointModuleTests
    {
        [Test]
        public async Task ReloadSchema_FullFlow_Success()
        {
            // Arrange
            var successResponse = TestFrontendResponse.Create(
                statusCode: 200,
                body: JsonNode.Parse("{\"message\":\"Schema reloaded successfully\"}")
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(successResponse));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        // Override configuration
                        var configurationValues = new Dictionary<string, string?>
                        {
                            { "AppSettings:EnableManagementEndpoints", "true" },
                        };
                        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(configurationValues)
                            .Build();
                        collection.AddSingleton<IConfiguration>(configuration);

                        // Add mocked dependencies
                        collection.AddTransient((x) => _apiService);
                        collection.AddTransient((x) => _logger);
                    }
                );
            });

            using var client = factory.CreateClient();

            // Act
            var response = await client.PostAsync("/management/reload-api-schema", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("Schema reloaded successfully");

            // Verify the service was called
            A.CallTo(() => _apiService.ReloadApiSchemaAsync()).MustHaveHappenedOnceExactly();

            // Verify logging occurred
            A.CallTo(_logger)
                .Where(call =>
                    call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Information
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task ReloadSchema_ServiceFailure_Returns500()
        {
            // Arrange
            var failureResponse = TestFrontendResponse.Create(
                statusCode: 500,
                body: JsonNode.Parse(
                    "{\"title\":\"Schema Reload Failed\",\"detail\":\"The API schema could not be reloaded. Check server logs for details.\"}"
                )
            );
            A.CallTo(() => _apiService.ReloadApiSchemaAsync())
                .Returns(Task.FromResult<IFrontendResponse>(failureResponse));

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        // Override configuration
                        var configurationValues = new Dictionary<string, string?>
                        {
                            { "AppSettings:EnableManagementEndpoints", "true" },
                        };
                        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(configurationValues)
                            .Build();
                        collection.AddSingleton<IConfiguration>(configuration);

                        // Add mocked dependencies
                        collection.AddTransient((x) => _apiService);
                        collection.AddTransient((x) => _logger);
                    }
                );
            });

            using var client = factory.CreateClient();

            // Act
            var response = await client.PostAsync("/management/reload-api-schema", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("Schema Reload Failed");

            // Verify only information logging occurred (error logging happens in the service)
            A.CallTo(_logger)
                .Where(call =>
                    call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Information
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(_logger)
                .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
                .MustNotHaveHappened();
        }
    }
}
