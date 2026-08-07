// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Several fixtures here isolate the startup status file by overriding
/// <c>AppSettings:StartupStatusFilePath</c> through <c>ConfigureAppConfiguration</c>. That override
/// reaches only the writes issued after <c>builder.Build()</c>, by the DI-resolved
/// StartupPhaseExecutor. It cannot reach the ConfigureServices or BuildApplication phases:
/// Program.cs constructs its bootstrap signal from <c>builder.Configuration</c> before the host
/// exists, so that signal has already resolved its path - to the machine-shared
/// <c>Path.Combine(Path.GetTempPath(), "dms-startup-status.json")</c> default - by the time this
/// callback runs, and both pre-host phases still write there. Harmless for the assertions here,
/// which all target post-Build writes, but it means "this fixture isolates the status file" is only
/// true from Build onward. Asserting on a pre-host write needs process-level environment variables
/// instead; see
/// <see cref="ConfigurationTests.Given_A_Process_Level_Configuration_Failure_Before_The_Host_Is_Built"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ConfigurationTests
{
    private sealed class RecordingStartupProcessExit : IStartupProcessExit
    {
        public int ExitCallCount { get; private set; }

        public int? ExitCode { get; private set; }

        public void Exit(int exitCode)
        {
            ExitCallCount++;
            ExitCode = exitCode;
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_App_Settings
    {
        protected WebApplicationFactory<Program>? Factory;
        protected string StatusDirectory = null!;
        protected string StatusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            StatusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            StatusFilePath = Path.Combine(StatusDirectory, "dms-startup-status.json");

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:AuthenticationService"] = null,
                                ["AppSettings:StartupStatusFilePath"] = StatusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        // Add validators to trigger ReportInvalidConfigurationMiddleware
                        collection.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            Factory!.Dispose();

            if (Directory.Exists(StatusDirectory))
            {
                Directory.Delete(StatusDirectory, recursive: true);
            }
        }

        [TestFixture]
        public class When_Requesting_Any_Endpoint_Should_Return_InternalServerError
            : Given_A_Configuration_With_Invalid_App_Settings
        {
            [Test]
            public async Task When_no_authentication_service()
            {
                // Arrange
                using var client = Factory!.CreateClient();

                // Act
                var response = await client.GetAsync("/");
                string content = await response.Content.ReadAsStringAsync();

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                content.Should().Be(string.Empty);
            }

            [Test]
            public async Task It_writes_failed_startup_status_instead_of_completed()
            {
                // Arrange
                using var client = Factory!.CreateClient();

                // Act
                var response = await client.GetAsync("/");

                // Assert
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                File.Exists(StatusFilePath).Should().BeTrue();

                var startupStatus = JsonNode.Parse(await File.ReadAllTextAsync(StatusFilePath))!.AsObject();

                startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
                startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureEndpoints);
                startupStatus["Summary"]!
                    .GetValue<string>()
                    .Should()
                    .Contain("Configuration validation failed");
                startupStatus["ErrorType"]!
                    .GetValue<string>()
                    .Should()
                    .Be(nameof(OptionsValidationException));
                startupStatus["ErrorMessage"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Default_Max_Request_Body_Size
    {
        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(collection => TestMockHelper.AddEssentialMocks(collection));
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        /// <summary>
        /// Guards the success side of the ConfigureEndpoints try/catch. Endpoint configuration is
        /// wrapped, but WriteReady sits outside the guard, so a change that moves it inside or
        /// swallows the exception instead of rethrowing would leave the file short of Ready with
        /// every other test still green.
        /// </summary>
        [Test]
        public void It_writes_ready_startup_status_once_endpoint_configuration_succeeds()
        {
            // Act
            using var client = _factory!.CreateClient();

            // Assert
            File.Exists(_statusFilePath).Should().BeTrue();
            var startupStatus = JsonNode.Parse(File.ReadAllText(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Ready");
            startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.Ready);
        }

        [Test]
        public void It_uses_the_configured_default_request_body_size_for_host_limits()
        {
            using var client = _factory!.CreateClient();

            var formOptions = _factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;
            var kestrelOptions = _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
            var appSettings = _factory.Services.GetRequiredService<IOptions<AppSettings>>().Value;

            appSettings
                .MaxRequestBodySizeMegabytes.Should()
                .Be(AppSettings.DefaultMaxRequestBodySizeMegabytes);

            long maxRequestBodySizeBytes =
                (long)appSettings.MaxRequestBodySizeMegabytes * AppSettings.BytesPerMegabyte;
            maxRequestBodySizeBytes.Should().Be(formOptions.ValueLengthLimit);
            maxRequestBodySizeBytes.Should().Be(formOptions.MultipartBodyLengthLimit);
            maxRequestBodySizeBytes.Should().Be(kestrelOptions.Limits.MaxRequestBodySize);
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_Max_Request_Body_Size
    {
        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:AuthenticationService"] = "http://localhost:5126/connect/token",
                                ["AppSettings:MaxRequestBodySizeMegabytes"] = "0",
                                ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        // Add validators to trigger ReportInvalidConfigurationMiddleware
                        collection.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public async Task It_returns_internal_server_error_when_max_request_body_size_is_invalid()
        {
            // Arrange
            using var client = _factory!.CreateClient();

            // Act
            var response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            content.Should().Be(string.Empty);
            File.Exists(_statusFilePath).Should().BeTrue();

            var startupStatus = JsonNode.Parse(await File.ReadAllTextAsync(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
            startupStatus["ErrorMessage"]!.GetValue<string>().Should().Contain("MaxRequestBodySizeMegabytes");
        }
    }

    [TestFixture]
    public class Given_DocumentCache_Target_Initialization_Is_Deferred
    {
        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;
        private IDocumentCacheTargetRegistry _targetRegistry = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
            _startupProcessExit = new RecordingStartupProcessExit();

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        collection.Replace(
                            ServiceDescriptor.Singleton<IStartupProcessExit>(_startupProcessExit)
                        );
                        collection.RemoveAll<IHostedService>();

                        _targetRegistry = A.Fake<IDocumentCacheTargetRegistry>();
                        A.CallTo(() =>
                                _targetRegistry.RefreshAsync(
                                    DocumentCacheTargetRefreshReason.Startup,
                                    A<CancellationToken>.Ignored
                                )
                            )
                            .ThrowsAsync(
                                new InvalidOperationException("DocumentCache target refresh failed.")
                            );
                        collection.Replace(
                            ServiceDescriptor.Singleton<IDocumentCacheTargetRegistry>(_targetRegistry)
                        );
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public void It_does_not_refresh_DocumentCache_targets_during_Program_startup()
        {
            // Act
            using var client = _factory!.CreateClient();

            // Assert
            _startupProcessExit.ExitCallCount.Should().Be(0);
            A.CallTo(() =>
                    _targetRegistry.RefreshAsync(
                        DocumentCacheTargetRefreshReason.Startup,
                        A<CancellationToken>.Ignored
                    )
                )
                .MustNotHaveHappened();

            File.Exists(_statusFilePath).Should().BeTrue();
            var startupStatus = JsonNode.Parse(File.ReadAllText(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Ready");
            startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.Ready);
        }
    }

    /// <summary>
    /// Regression coverage for the configuration-binding catch in <c>ReportInvalidConfiguration</c>.
    /// Options binding is lazy, so forcing <c>IOptions&lt;AppSettings&gt;.Value</c> is the first
    /// eager bind in startup; a non-numeric value for the <c>int</c>
    /// <c>MaxRequestBodySizeMegabytes</c> fails conversion and surfaces as
    /// <see cref="InvalidOperationException"/>, not <see cref="OptionsValidationException"/>. That
    /// call is the last statement in the unguarded window between the BuildApplication phase and
    /// the first fatal phase, so before the catch existed it escaped every status guard and left the
    /// file reading Completed/BuildApplication on a dead process - worse than a stranded Starting,
    /// because Completed reads as success. The statements ahead of it in that window are still
    /// unguarded by design; the comment on that catch in <c>Program.cs</c> records why.
    /// Contrast <see cref="Given_A_Configuration_With_Invalid_Max_Request_Body_Size"/>, which uses
    /// "0": a value that binds and then fails the validator, taking the
    /// <see cref="OptionsValidationException"/> route to a host that stays up serving 500s.
    /// </summary>
    [TestFixture]
    public class Given_A_Configuration_With_A_Non_Numeric_Max_Request_Body_Size
    {
        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:MaxRequestBodySizeMegabytes"] = "ten",
                                ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(collection => TestMockHelper.AddEssentialMocks(collection));
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public void It_writes_failed_startup_status_when_configuration_cannot_be_bound()
        {
            // Act
            Action act = () => _factory!.CreateClient();

            // Assert
            // Fail-fast is preserved: a value that cannot be bound is not recoverable by the
            // short-circuit middleware, so the catch rethrows rather than letting the host serve.
            act.Should().Throw<InvalidOperationException>();

            File.Exists(_statusFilePath).Should().BeTrue();
            var startupStatus = JsonNode.Parse(File.ReadAllText(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
            startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureEndpoints);
            startupStatus["Summary"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "Configuration could not be read or bound. DMS cannot start without valid configuration values."
                );
            startupStatus["ErrorType"]!.GetValue<string>().Should().Be(nameof(InvalidOperationException));
            startupStatus["ErrorMessage"]!.GetValue<string>().Should().Contain("MaxRequestBodySizeMegabytes");
        }
    }

    [TestFixture]
    public class Given_A_Bound_App_Settings_Without_Max_Request_Body_Size
    {
        [Test]
        public void It_uses_the_default_request_body_size_and_validates_successfully()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AppSettings:AuthenticationService"] = "http://localhost:5126/connect/token",
                        ["AppSettings:Datastore"] = "postgresql",
                        ["AppSettings:CorrelationIdHeader"] = "correlationid",
                    }
                )
                .Build();

            var appSettings = new AppSettings
            {
                AuthenticationService = "placeholder",
                Datastore = "postgresql",
                CorrelationIdHeader = "correlationid",
            };
            configuration.GetSection("AppSettings").Bind(appSettings);

            appSettings
                .MaxRequestBodySizeMegabytes.Should()
                .Be(AppSettings.DefaultMaxRequestBodySizeMegabytes);

            var validator = new AppSettingsValidator();
            validator.Validate(null, appSettings).Succeeded.Should().BeTrue();
        }
    }

    /// <summary>
    /// Regression coverage for the ConfigureEndpoints failure catch. Duplicate route qualifier
    /// segments survive AppSettingsValidator and reach CoreEndpointModule.BuildRoutePattern
    /// un-deduplicated, producing "/{districtId}/{districtId}/data/{**dmsPath}", which makes
    /// endpoint mapping throw. Before the catch existed the status file was stranded at Starting
    /// with no ErrorType or ErrorMessage.
    /// The trigger works only because <c>AppSettingsValidator</c> does not validate
    /// <c>RouteQualifierSegments</c> at all - it checks AuthenticationService, Datastore, and
    /// MaxRequestBodySizeMegabytes and nothing else. Adding a duplicate or format check there
    /// would intercept "districtId,districtId" as an <see cref="OptionsValidationException"/>
    /// before endpoint mapping runs, and this test would start failing for a reason that has
    /// nothing to do with the catch it guards. If that happens, replace the trigger rather than
    /// deleting or weakening the assertions: this fixture is the only coverage for the
    /// endpoint-mapping catch in <c>Program.cs</c>, so a dropped assertion takes that route to
    /// zero. A verified replacement is the single segment "dmsPath", which yields
    /// "/{dmsPath}/data/{**dmsPath}" - it collides with the catch-all parameter name hardcoded in
    /// BuildRoutePattern rather than with another configured segment, so a within-list duplicate
    /// or format check does not intercept it.
    /// </summary>
    [TestFixture]
    public class Given_A_Configuration_With_Duplicate_Route_Qualifier_Segments
    {
        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:RouteQualifierSegments"] = "districtId,districtId",
                                ["AppSettings:StartupStatusFilePath"] = _statusFilePath,
                            }
                        );
                    }
                );
                builder.ConfigureServices(collection => TestMockHelper.AddEssentialMocks(collection));
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public void It_writes_failed_startup_status_and_does_not_start_the_host()
        {
            // Act
            Action act = () => _factory!.CreateClient();

            // Assert
            // Fail-fast is preserved: the catch writes the failure and rethrows rather than
            // letting a half-configured host serve traffic.
            act.Should().Throw<RoutePatternException>();

            File.Exists(_statusFilePath).Should().BeTrue();
            var startupStatus = JsonNode.Parse(File.ReadAllText(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
            startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureEndpoints);
            startupStatus["Summary"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "Middleware and endpoint configuration failed. DMS cannot serve requests without mapped HTTP endpoints."
                );
            startupStatus["ErrorType"]!.GetValue<string>().Should().Be(nameof(RoutePatternException));
            startupStatus["ErrorMessage"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// Coverage for the two phases that run before the application host exists
    /// (<c>ConfigureServices</c>, <c>BuildApplication</c>), which are written by
    /// <c>RunBootstrapPhase</c> through the bootstrap signal constructed at the very top of
    /// <c>Program</c>.
    /// <para>
    /// These phases are unreachable by the <c>ConfigureAppConfiguration</c> +
    /// <c>AddInMemoryCollection</c> pattern every other fixture here uses: those callbacks are
    /// applied during <c>builder.Build()</c>, by which point the <c>ConfigureServices</c> phase has
    /// already run and the bootstrap signal has already resolved its path. Process environment
    /// variables are the only injection point early enough, because <c>WebApplication.CreateBuilder</c>
    /// has read them before the first phase starts. Both the status path and the failure trigger
    /// therefore have to be set that way.
    /// </para>
    /// <para>
    /// Setting process-global state is safe here because every fixture that boots
    /// <c>WebApplicationFactory&lt;Program&gt;</c> is non-parallelizable, so no other host can be
    /// starting while these variables are set, and no other fixture reads them. The
    /// <c>NonParallelizable</c> below is redundant with the containing class today, and deliberately
    /// kept: this is the only fixture here whose correctness depends on serialization rather than
    /// merely benefiting from it, so it carries its own guard should the class-level attribute ever
    /// be relaxed.
    /// </para>
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_A_Process_Level_Configuration_Failure_Before_The_Host_Is_Built
    {
        private const string StatusFilePathVariable = "AppSettings__StartupStatusFilePath";
        private const string ForwardedHeadersVariable = "AppSettings__ReverseProxy__UseForwardedHeaders";

        private WebApplicationFactory<Program>? _factory;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            Environment.SetEnvironmentVariable(StatusFilePathVariable, _statusFilePath);

            // Read inside the ConfigureServices phase body, at the Get<ReverseProxySettings>() call.
            // A non-boolean here fails conversion while the phase is still running.
            Environment.SetEnvironmentVariable(ForwardedHeadersVariable, "maybe");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection => TestMockHelper.AddEssentialMocks(collection));
            });
        }

        [TearDown]
        public void Teardown()
        {
            Environment.SetEnvironmentVariable(StatusFilePathVariable, null);
            Environment.SetEnvironmentVariable(ForwardedHeadersVariable, null);

            _factory!.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public void It_writes_failed_startup_status_for_the_configure_services_phase()
        {
            // Act
            Action act = () => _factory!.CreateClient();

            // Assert
            act.Should().Throw<InvalidOperationException>();

            File.Exists(_statusFilePath).Should().BeTrue();
            var startupStatus = JsonNode.Parse(File.ReadAllText(_statusFilePath))!.AsObject();

            startupStatus["State"]!.GetValue<string>().Should().Be("Failed");
            startupStatus["Phase"]!.GetValue<string>().Should().Be(DmsStartupPhases.ConfigureServices);
            startupStatus["Summary"]!
                .GetValue<string>()
                .Should()
                .Be("Configuring DMS services failed before the application host was built.");
            startupStatus["ErrorType"]!.GetValue<string>().Should().Be(nameof(InvalidOperationException));
            startupStatus["ErrorMessage"]!.GetValue<string>().Should().Contain("UseForwardedHeaders");
        }
    }

    [TestFixture]
    public class Given_A_Configuration_With_Invalid_Connection_Strings
    {
        private WebApplicationFactory<Program>? _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:DatabaseConnection"] = null,
                            }
                        );
                    }
                );
                builder.ConfigureServices(
                    (collection) =>
                    {
                        TestMockHelper.AddEssentialMocks(collection);
                        // Add validators to trigger ReportInvalidConfigurationMiddleware
                        collection.AddSingleton<IValidateOptions<AppSettings>, AppSettingsValidator>();
                    }
                );
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory!.Dispose();
        }
    }
}
