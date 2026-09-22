// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using AppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// IdentityEndpointModule route mapping, tenant/qualifier prefixing, body parsing, Content-Type
/// handling, and the D15 qualifier-collision fail-closed guard (C2, C3, B9 frontend half).
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdentityEndpointModuleTests
{
    private static IFrontendResponse FakeResponse(int statusCode = 200, JsonNode? body = null)
    {
        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(statusCode);
        A.CallTo(() => response.Body).Returns(body ?? new JsonObject());
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    private static Dictionary<string, string?> ToggleOnConfig(
        bool multiTenancy = false,
        string routeQualifierSegments = ""
    ) =>
        new()
        {
            ["AppSettings:EnableIdentityManagement"] = "true",
            ["AppSettings:MultiTenancy"] = multiTenancy ? "true" : "false",
            ["AppSettings:RouteQualifierSegments"] = routeQualifierSegments,
        };

    private static WebApplicationFactory<Program> CreateFactory(
        IApiService apiService,
        Dictionary<string, string?> configuration,
        ILoggerProvider? loggerProvider = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            }
            builder.ConfigureAppConfiguration(
                (context, configurationBuilder) => configurationBuilder.AddInMemoryCollection(configuration)
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddTransient(_ => apiService);
            });
        });
    }

    [TestFixture]
    public class Given_Multitenant_Routing_With_Route_Qualifiers
    {
        [Test]
        public async Task It_routes_results_with_no_token_to_get_by_id_with_id_results()
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? captured = null;
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Invokes((FrontendRequest request, string _, CancellationToken _) => captured = request)
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(
                apiService,
                ToggleOnConfig(multiTenancy: true, routeQualifierSegments: "districtId,schoolYear")
            );
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/tenant-a/255901/2026/identity/v2/identities/results");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, "results", A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .MustNotHaveHappened();

            captured.Should().NotBeNull();
            captured!.Tenant.Should().Be("tenant-a");
            captured
                .RouteQualifiers[new RouteQualifierName("districtId")]
                .Should()
                .Be(new RouteQualifierValue("255901"));
            captured
                .RouteQualifiers[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2026"));
            captured.Path.Should().Be("/tenant-a/255901/2026/identity/v2/identities/{id}");
        }

        [Test]
        public async Task It_routes_results_with_a_token_to_results()
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? captured = null;
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Invokes((FrontendRequest request, string _, CancellationToken _) => captured = request)
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(
                apiService,
                ToggleOnConfig(multiTenancy: true, routeQualifierSegments: "districtId,schoolYear")
            );
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/tenant-a/255901/2026/identity/v2/identities/results/tok");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => apiService.IdentityResults(A<FrontendRequest>._, "tok", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .MustNotHaveHappened();

            captured.Should().NotBeNull();
            captured!.Tenant.Should().Be("tenant-a");
            captured
                .RouteQualifiers[new RouteQualifierName("districtId")]
                .Should()
                .Be(new RouteQualifierValue("255901"));
            captured
                .RouteQualifiers[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2026"));
            captured.Path.Should().Be("/tenant-a/255901/2026/identity/v2/identities/results/{token}");
        }
    }

    [TestFixture]
    public class Given_Post_Handlers_Parse_The_Body
    {
        [Test]
        public async Task It_parses_the_create_body_into_ParsedBody()
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? captured = null;
            A.CallTo(() => apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .Invokes((FrontendRequest request, CancellationToken _) => captured = request)
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(apiService, ToggleOnConfig());
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities")
            {
                Content = new StringContent("""{"firstName":"Jane"}""", Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
            captured.Should().NotBeNull();
            captured!.ParsedBody.Should().NotBeNull();
            captured.ParsedBody!["firstName"]!.GetValue<string>().Should().Be("Jane");
            captured.Path.Should().Be("/identity/v2/identities");
        }

        [Test]
        public async Task It_parses_the_find_body_into_ParsedBody()
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? captured = null;
            A.CallTo(() => apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .Invokes((FrontendRequest request, CancellationToken _) => captured = request)
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(apiService, ToggleOnConfig());
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/find")
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Should().NotBeNull();
            captured!.ParsedBody.Should().NotBeNull();
            captured.ParsedBody!.AsArray().Count.Should().Be(1);
            captured.Path.Should().Be("/identity/v2/identities/find");
        }

        [Test]
        public async Task It_parses_the_search_body_into_ParsedBody()
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? captured = null;
            A.CallTo(() => apiService.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
                .Invokes((FrontendRequest request, CancellationToken _) => captured = request)
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(apiService, ToggleOnConfig());
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/search")
            {
                Content = new StringContent("""[{"firstName":"Jane"}]""", Encoding.UTF8, "application/json"),
            };

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Should().NotBeNull();
            captured!.ParsedBody.Should().NotBeNull();
            captured.ParsedBody!.AsArray().Count.Should().Be(1);
            captured.Path.Should().Be("/identity/v2/identities/search");
        }
    }

    [TestFixture]
    public class Given_Get_Handlers_Ignore_Content_Type
    {
        [Test]
        public async Task A_get_by_id_with_a_non_json_content_type_is_not_415_and_the_fake_is_called()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(apiService, ToggleOnConfig());
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/identity/v2/identities/605943412");
            request.Headers.TryAddWithoutValidation("Content-Type", "text/plain");

            var response = await client.SendAsync(request);

            response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, "605943412", A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task A_results_poll_with_a_non_json_content_type_is_not_415_and_the_fake_is_called()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Returns(Task.FromResult(FakeResponse()));

            await using var factory = CreateFactory(apiService, ToggleOnConfig());
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/identity/v2/identities/results/tok");
            request.Headers.TryAddWithoutValidation("Content-Type", "text/plain");

            var response = await client.SendAsync(request);

            response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            A.CallTo(() => apiService.IdentityResults(A<FrontendRequest>._, "tok", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Request_Aborted_Is_Propagated_To_The_Facade
    {
        private static IOptions<AppSettings> FrontendAppSettingsOptions() =>
            Options.Create(
                new AppSettings
                {
                    AuthenticationService = "test",
                    Datastore = "postgresql",
                    CorrelationIdHeader = "X-Correlation-ID",
                }
            );

        private static IOptions<KestrelServerOptions> KestrelOptions() =>
            Options.Create(new KestrelServerOptions());

        private static DefaultHttpContext HttpContextFor(
            string method,
            string path,
            CancellationToken requestAborted
        )
        {
            var httpContext = new DefaultHttpContext { RequestAborted = requestAborted };
            httpContext.Request.Method = method;
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("api.example.test");
            httpContext.Request.Path = path;
            return httpContext;
        }

        [Test]
        public async Task It_passes_request_aborted_to_identity_create()
        {
            var apiService = A.Fake<IApiService>();
            CancellationToken captured = default;
            A.CallTo(() => apiService.IdentityCreate(A<FrontendRequest>._, A<CancellationToken>._))
                .Invokes(
                    (FrontendRequest _, CancellationToken cancellationToken) => captured = cancellationToken
                )
                .Returns(Task.FromResult(FakeResponse()));
            using var requestAbortedSource = new CancellationTokenSource();
            var httpContext = HttpContextFor(
                HttpMethods.Post,
                "/identity/v2/identities",
                requestAbortedSource.Token
            );

            await AspNetCoreFrontend.IdentityCreate(
                httpContext,
                apiService,
                FrontendAppSettingsOptions(),
                KestrelOptions()
            );

            captured.Should().Be(requestAbortedSource.Token);
        }

        [Test]
        public async Task It_passes_request_aborted_to_identity_get_by_id()
        {
            var apiService = A.Fake<IApiService>();
            CancellationToken captured = default;
            A.CallTo(() =>
                    apiService.IdentityGetById(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Invokes(
                    (FrontendRequest _, string _, CancellationToken cancellationToken) =>
                        captured = cancellationToken
                )
                .Returns(Task.FromResult(FakeResponse()));
            using var requestAbortedSource = new CancellationTokenSource();
            var httpContext = HttpContextFor(
                HttpMethods.Get,
                "/identity/v2/identities/605943412",
                requestAbortedSource.Token
            );

            await AspNetCoreFrontend.IdentityGetById(
                httpContext,
                apiService,
                "605943412",
                FrontendAppSettingsOptions(),
                KestrelOptions()
            );

            captured.Should().Be(requestAbortedSource.Token);
        }

        [Test]
        public async Task It_passes_request_aborted_to_identity_find()
        {
            var apiService = A.Fake<IApiService>();
            CancellationToken captured = default;
            A.CallTo(() => apiService.IdentityFind(A<FrontendRequest>._, A<CancellationToken>._))
                .Invokes(
                    (FrontendRequest _, CancellationToken cancellationToken) => captured = cancellationToken
                )
                .Returns(Task.FromResult(FakeResponse()));
            using var requestAbortedSource = new CancellationTokenSource();
            var httpContext = HttpContextFor(
                HttpMethods.Post,
                "/identity/v2/identities/find",
                requestAbortedSource.Token
            );

            await AspNetCoreFrontend.IdentityFind(
                httpContext,
                apiService,
                FrontendAppSettingsOptions(),
                KestrelOptions()
            );

            captured.Should().Be(requestAbortedSource.Token);
        }

        [Test]
        public async Task It_passes_request_aborted_to_identity_search()
        {
            var apiService = A.Fake<IApiService>();
            CancellationToken captured = default;
            A.CallTo(() => apiService.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
                .Invokes(
                    (FrontendRequest _, CancellationToken cancellationToken) => captured = cancellationToken
                )
                .Returns(Task.FromResult(FakeResponse()));
            using var requestAbortedSource = new CancellationTokenSource();
            var httpContext = HttpContextFor(
                HttpMethods.Post,
                "/identity/v2/identities/search",
                requestAbortedSource.Token
            );

            await AspNetCoreFrontend.IdentitySearch(
                httpContext,
                apiService,
                FrontendAppSettingsOptions(),
                KestrelOptions()
            );

            captured.Should().Be(requestAbortedSource.Token);
        }

        [Test]
        public async Task It_passes_request_aborted_to_identity_results()
        {
            var apiService = A.Fake<IApiService>();
            CancellationToken captured = default;
            A.CallTo(() =>
                    apiService.IdentityResults(A<FrontendRequest>._, A<string>._, A<CancellationToken>._)
                )
                .Invokes(
                    (FrontendRequest _, string _, CancellationToken cancellationToken) =>
                        captured = cancellationToken
                )
                .Returns(Task.FromResult(FakeResponse()));
            using var requestAbortedSource = new CancellationTokenSource();
            var httpContext = HttpContextFor(
                HttpMethods.Get,
                "/identity/v2/identities/results/tok",
                requestAbortedSource.Token
            );

            await AspNetCoreFrontend.IdentityResults(
                httpContext,
                apiService,
                "tok",
                FrontendAppSettingsOptions(),
                KestrelOptions()
            );

            captured.Should().Be(requestAbortedSource.Token);
        }
    }

    /// <summary>
    /// D15's fail-closed qualifier-name collision guard is a module-mapping-time concern shared by
    /// every fixed-route module built from the same configured RouteQualifierSegments, not an
    /// identity-only one: booting the full host with a colliding configuration would fail every
    /// module's own MapEndpoints (ASP.NET Core's route pattern parser itself rejects a route
    /// template with a duplicate parameter name under its own case-insensitive comparison), not
    /// just IdentityEndpointModule's. So this exercises IdentityEndpointModule.MapEndpoints in
    /// isolation, the same unit the production fail-closed check runs in, without booting Program.
    /// </summary>
    [TestFixture]
    public class Given_Colliding_Route_Qualifier_Names
    {
        [Test]
        public void It_does_not_map_identity_routes_and_logs_an_error()
        {
            using WebApplication app = WebApplication.CreateBuilder().Build();
            var logger = new RecordingLogger<IdentityEndpointModule>();
            var module = new IdentityEndpointModule(
                Options.Create(
                    new AppSettings
                    {
                        AuthenticationService = "test",
                        Datastore = "postgresql",
                        CorrelationIdHeader = "X-Correlation-ID",
                        MultiTenancy = false,
                        RouteQualifierSegments = "DistrictId,districtid",
                    }
                ),
                Options.Create(
                    new CoreAppSettings { AllowIdentityUpdateOverrides = "", EnableIdentityManagement = true }
                ),
                logger
            );

            module.MapEndpoints(app);

            IEndpointRouteBuilder endpointRouteBuilder = app;
            endpointRouteBuilder
                .DataSources.SelectMany(dataSource => dataSource.Endpoints)
                .Should()
                .BeEmpty();
            logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Message.Contains("collide", StringComparison.OrdinalIgnoreCase)
                );
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
