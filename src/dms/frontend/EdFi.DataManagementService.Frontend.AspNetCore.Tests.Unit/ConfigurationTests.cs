// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
                // The short-circuit response carries the generic Ed-Fi 500 body rather than nothing
                // at all; Given_A_Host_Short_Circuited_By_Invalid_Configuration asserts its shape.
                JsonNode.Parse(content)!["status"]!
                    .GetValue<int>()
                    .Should()
                    .Be(500);
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
            JsonNode.Parse(content)!["status"]!.GetValue<int>().Should().Be(500);
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
    /// <c>CorrelationIdMaxLength</c> is bounded on both sides, and both bounds are inclusive.
    /// The floor exists because <c>ExtractTraceIdFrom</c> pushes the server-generated
    /// <c>HttpContext.TraceIdentifier</c> through the same cap: Kestrel formats that value as a
    /// 13-character connection id, a colon and an 8-hex request number - 22 characters - so a cap
    /// below it would drop the request number and collapse every request on one connection onto a
    /// single correlation ID. 64 is the shortest cap that also leaves every common upstream scheme
    /// intact, the longest being a 55-character W3C traceparent. The ceiling bounds how much
    /// client-controlled text one request can push into every log event and error response body.
    /// </summary>
    [TestFixture]
    public class Given_A_Bound_App_Settings_With_A_Correlation_Id_Max_Length_Outside_The_Permitted_Range
    {
        private static AppSettings SettingsWith(int correlationIdMaxLength) =>
            new()
            {
                AuthenticationService = "http://localhost:5126/connect/token",
                Datastore = "postgresql",
                CorrelationIdHeader = "correlationid",
                CorrelationIdMaxLength = correlationIdMaxLength,
            };

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(22)]
        [TestCase(32)]
        [TestCase(63)]
        [TestCase(1025)]
        [TestCase(int.MaxValue)]
        public void It_fails_validation_naming_the_setting_the_value_and_the_range(int outOfRange)
        {
            var result = new AppSettingsValidator().Validate(null, SettingsWith(outOfRange));

            result.Succeeded.Should().BeFalse();
            result.FailureMessage.Should().Contain(nameof(AppSettings.CorrelationIdMaxLength));
            result.FailureMessage.Should().Contain(outOfRange.ToString(CultureInfo.InvariantCulture));
            result.FailureMessage.Should().Contain("64");
            result.FailureMessage.Should().Contain("1024");
        }

        /// <summary>
        /// The failure message is handed to <c>ILogger.LogCritical</c> by
        /// <c>ReportInvalidConfigurationMiddleware</c> as the message *template* argument, which
        /// Serilog then parses for property holes. A brace in the message would be read as a hole
        /// rather than logged literally, so the message must stay brace-free.
        /// </summary>
        [Test]
        public void It_produces_a_brace_free_failure_message()
        {
            var result = new AppSettingsValidator().Validate(null, SettingsWith(63));

            result.FailureMessage.Should().NotContain("{").And.NotContain("}");
        }

        /// <summary>
        /// The other side of the boundary. Without these the range check could be off by one - or
        /// exclude the very values the bounds are named for - and the rejection cases above would
        /// still pass.
        /// </summary>
        [TestCase(64)]
        [TestCase(255)]
        [TestCase(1024)]
        public void It_accepts_the_inclusive_bounds_and_the_default(int inRange)
        {
            new AppSettingsValidator().Validate(null, SettingsWith(inRange)).Succeeded.Should().BeTrue();
        }

        [Test]
        public void It_places_the_bounds_where_the_named_constants_say()
        {
            AppSettings.MinimumCorrelationIdMaxLength.Should().Be(64);
            AppSettings.MaximumCorrelationIdMaxLength.Should().Be(1024);
            AppSettings.DefaultCorrelationIdMaxLength.Should().Be(255);
        }

        /// <summary>
        /// The floor's reason for existing: the server-generated identifier DMS falls back to
        /// must survive the smallest cap an operator can configure.
        /// </summary>
        [Test]
        public void It_leaves_a_kestrel_trace_identifier_untruncated_at_the_floor()
        {
            const string KestrelTraceIdentifier = "0HNOIG2VLOC0S:00000001";

            KestrelTraceIdentifier
                .Length.Should()
                .BeLessThanOrEqualTo(AppSettings.MinimumCorrelationIdMaxLength);
        }
    }

    /// <summary>
    /// Regression coverage for the ConfigureEndpoints failure catch. Duplicate route qualifier
    /// segments survive AppSettingsValidator and reach CoreEndpointModule.BuildRoutePattern
    /// un-deduplicated, producing "/{districtId}/{districtId}/data/{**dmsPath}", which makes
    /// endpoint mapping throw. Before the catch existed the status file was stranded at Starting
    /// with no ErrorType or ErrorMessage.
    /// The trigger works only because <c>AppSettingsValidator</c> does not validate
    /// <c>RouteQualifierSegments</c> at all - it checks AuthenticationService, Datastore,
    /// MaxRequestBodySizeMegabytes, and CorrelationIdMaxLength. Adding a duplicate or format check there
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

    /// <summary>
    /// A host whose configuration failed validation still listens and still answers: every request,
    /// <c>/health</c> included, is short-circuited by <c>ReportInvalidConfigurationMiddleware</c>.
    /// This fixture covers what that answer owes the two audiences that see it - a body the client
    /// can correlate, and a critical log line the operator is not buried in.
    /// </summary>
    /// <remarks>
    /// The trigger is an out-of-range <c>CorrelationIdMaxLength</c> rather than, say, a missing
    /// AuthenticationService, because it is the failure that makes the correlation ID assertions
    /// below mean something: the setting that just failed validation is the very one the correlation
    /// ID pipeline reads, so there is no validated cap to normalize against and nothing cached on
    /// <c>HttpContext.Items</c> to reuse. The request log and the response body have to arrive at
    /// the same value independently, each through the documented default. 32 is also the shape of a
    /// realistic operator typo: it was accepted before the floor of 64 was introduced, which is what
    /// makes this short-circuit path materially more reachable than it was.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class Given_A_Host_Short_Circuited_By_Invalid_Configuration
    {
        private const int OutOfRangeCorrelationIdMaxLength = 32;

        /// <summary>
        /// The constant template <c>ReportInvalidConfigurationMiddleware</c> logs under. Spelled out
        /// rather than referenced, so that moving the failure message back into the template
        /// position fails this test instead of quietly rewriting what it asserts.
        /// </summary>
        private const string ExpectedLogTemplate = "Invalid DMS configuration: {ConfigurationError}";

        private static readonly string _middlewareCategory =
            typeof(ReportInvalidConfigurationMiddleware).FullName!;

        private WebApplicationFactory<Program> _factory = null!;
        private RecordingLoggerProvider _logs = null!;
        private CorrelationIdRecordingLoggerProvider _requestLog = null!;
        private ItemsObservingStartupFilter _items = null!;
        private CountingOptions<AppSettings> _appSettingsReads = null!;
        private string _statusDirectory = null!;

        [SetUp]
        public void Setup()
        {
            _logs = new RecordingLoggerProvider();
            _requestLog = new CorrelationIdRecordingLoggerProvider();
            _items = new ItemsObservingStartupFilter();
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration(
                    (context, configuration) =>
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["AppSettings:AuthenticationService"] = "http://localhost:5126/connect/token",
                                ["AppSettings:CorrelationIdMaxLength"] =
                                    OutOfRangeCorrelationIdMaxLength.ToString(CultureInfo.InvariantCulture),
                                ["AppSettings:StartupStatusFilePath"] = Path.Combine(
                                    _statusDirectory,
                                    "dms-startup-status.json"
                                ),
                            }
                        )
                );
                builder.ConfigureLogging(logging =>
                {
                    logging.AddProvider(_logs);
                    logging.AddProvider(_requestLog);
                });
                // AppSettingsValidator is registered by the application itself, so - unlike the
                // fixtures above - no validator is added here. A second registration would produce
                // a second copy of the same failure and make the "logged once" count below read 2.
                builder.ConfigureServices(services =>
                {
                    TestMockHelper.AddEssentialMocks(services);
                    services.AddSingleton<IStartupFilter>(_items);

                    // The application's own IOptions<AppSettings> behavior, with a read counter
                    // around it. OptionsManager is what the framework would have resolved anyway,
                    // and it caches nothing when validation fails, so every read still runs the
                    // validator and still throws - which is precisely the cost being counted.
                    services.AddSingleton<IOptions<AppSettings>>(serviceProvider =>
                        _appSettingsReads = new CountingOptions<AppSettings>(
                            new OptionsManager<AppSettings>(
                                serviceProvider.GetRequiredService<IOptionsFactory<AppSettings>>()
                            )
                        )
                    );
                });
            });
        }

        [TearDown]
        public void Teardown()
        {
            _factory.Dispose();
            _logs.Dispose();
            _requestLog.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public async Task It_answers_with_the_generic_ed_fi_problem_details_body()
        {
            using HttpClient client = _factory.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

            JsonObject body = JsonNode.Parse(content)!.AsObject();
            body["status"]!.GetValue<int>().Should().Be(500);
            body["title"]!.GetValue<string>().Should().Be("System Error");
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:system");
            body["detail"]!.GetValue<string>().Should().Be("An unexpected problem has occurred.");
            body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        }

        /// <summary>
        /// The half of the contract the body shape alone does not pin: a configuration failure
        /// message describes the host's own settings and belongs in the log, never in a response an
        /// unauthenticated client can read.
        /// </summary>
        [Test]
        public async Task It_keeps_the_validation_messages_out_of_the_response_body()
        {
            using HttpClient client = _factory.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();

            // Parsed first so this fails, rather than passing vacuously, if the body regresses to
            // being absent: an empty body names no setting either.
            JsonNode.Parse(content)!["detail"]!
                .GetValue<string>()
                .Should()
                .NotBeNullOrWhiteSpace();
            content.Should().NotContain(nameof(AppSettings.CorrelationIdMaxLength));
        }

        /// <summary>
        /// The configuration errors are a startup-time fact that cannot change while the process
        /// runs, so they are reported once, when the pipeline is built. Two requests are what
        /// distinguishes that from re-reporting them at traffic rate: per-request logging would leave
        /// nothing recorded before the first request and three events after the second.
        /// </summary>
        [Test]
        public async Task It_logs_each_configuration_failure_once_however_many_requests_arrive()
        {
            using HttpClient client = _factory.CreateClient();

            _logs.CriticalEventsFrom(_middlewareCategory).Should().HaveCount(1);

            (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

            _logs.CriticalEventsFrom(_middlewareCategory).Should().HaveCount(1);
        }

        /// <summary>
        /// The structured event has to carry the failure as data. Passing it as the message template
        /// instead would let any brace a future validation message contains be parsed as a property
        /// hole, so the event would lose the message and gain a bogus property - which is why this
        /// asserts on <c>{OriginalFormat}</c> and the named property rather than on rendered text.
        /// </summary>
        [Test]
        public void It_logs_the_failure_message_as_a_parameter_rather_than_as_the_template()
        {
            // Creating the client is what builds the pipeline, which is where the middleware - and
            // so the log event under test - is constructed.
            using HttpClient client = _factory.CreateClient();

            RecordedLogEvent logged = _logs.CriticalEventsFrom(_middlewareCategory).Single();

            logged.Properties["{OriginalFormat}"].Should().Be(ExpectedLogTemplate);
            logged
                .Properties["ConfigurationError"]
                .Should()
                .BeOfType<string>()
                .Which.Should()
                .Contain(nameof(AppSettings.CorrelationIdMaxLength));
        }

        /// <summary>
        /// FR-LOG-6 on the one path where the correlation ID pipeline cannot read its own
        /// configuration. The ingestion point falls back to normalizing the server-generated
        /// identifier against <c>DefaultCorrelationIdMaxLength</c>, once, and both the middleware
        /// writing the body and <c>LoggingMiddleware</c> writing the log event reach that one
        /// result, so the <c>correlationId</c> the client reads is still the <c>TraceId</c> it can
        /// search the logs for. A bodiless 500 offered the client nothing to search with at all.
        /// </summary>
        [Test]
        public async Task It_carries_the_correlation_id_the_request_was_logged_under()
        {
            using HttpClient client = _factory.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();

            string correlationId = JsonNode.Parse(content)!["correlationId"]!.GetValue<string>();
            _requestLog.LoggedTraceIds.Should().ContainSingle().Which.Should().Be(correlationId);
        }

        /// <summary>
        /// The fallback ingestion is cached on <c>HttpContext.Items</c> like any other, so this
        /// path has the same single ingestion result every later call site reads - here, the
        /// invalid-configuration middleware writing the response body. Nothing is cached when
        /// <c>LoggingMiddleware</c> only computes the value and returns it.
        /// </summary>
        [Test]
        public async Task It_caches_the_fallback_ingestion_on_http_context_items()
        {
            using HttpClient client = _factory.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/");
            string content = await response.Content.ReadAsStringAsync();

            string correlationId = JsonNode.Parse(content)!["correlationId"]!.GetValue<string>();
            _items
                .CachedIngestion.Should()
                .BeOfType<AspNetCoreFrontend.CorrelationIdIngestion>()
                .Which.TraceId.Value.Should()
                .Be(correlationId);
        }

        /// <summary>
        /// That the body's <c>correlationId</c> equals the logged <c>TraceId</c> cannot by itself
        /// show the body read the cached value: both sides derive it from the same pure function,
        /// so they agree whether or not anything is cached. What distinguishes the two is the cost
        /// of the second derivation - reading <c>IOptions&lt;AppSettings&gt;</c>, whose validation
        /// is what failed, so the read throws <c>OptionsValidationException</c> and the middleware
        /// falls back. One read per request is <c>LoggingMiddleware</c> ingesting; a second is the
        /// invalid-configuration middleware deriving the same value again, at the price of a second
        /// thrown exception on every request a host stuck in this mode answers.
        /// </summary>
        [Test]
        public async Task It_reads_the_cached_ingestion_rather_than_deriving_the_correlation_id_again()
        {
            using HttpClient client = _factory.CreateClient();

            // Startup reads AppSettings too, so the count is taken as a delta across one request.
            int readsBeforeRequest = _appSettingsReads.Reads;

            HttpResponseMessage response = await client.GetAsync("/");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (_appSettingsReads.Reads - readsBeforeRequest).Should().Be(1);
        }

        /// <summary>
        /// Runs ahead of the application's own middleware and reads <c>HttpContext.Items</c> on the
        /// way back out, once the pipeline behind it has answered. That is the only vantage point
        /// from which the cache is observable: the invalid-configuration middleware short-circuits
        /// the request, so nothing appended behind it ever runs.
        /// </summary>
        private sealed class ItemsObservingStartupFilter : IStartupFilter
        {
            public object? CachedIngestion { get; private set; }

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
                app =>
                {
                    app.Use(
                        async (context, nextMiddleware) =>
                        {
                            await nextMiddleware();

                            CachedIngestion = context.Items.TryGetValue(
                                AspNetCoreFrontend.CorrelationIdItemsKey,
                                out object? cached
                            )
                                ? cached
                                : null;
                        }
                    );
                    next(app);
                };
        }

        /// <summary>
        /// The framework's own <c>IOptions&lt;T&gt;</c> behavior with a count of how many times the
        /// value was read.
        /// </summary>
        private sealed class CountingOptions<TOptions>(IOptions<TOptions> inner) : IOptions<TOptions>
            where TOptions : class
        {
            private int _reads;

            public int Reads => Volatile.Read(ref _reads);

            public TOptions Value
            {
                get
                {
                    Interlocked.Increment(ref _reads);
                    return inner.Value;
                }
            }
        }

        /// <summary>
        /// One recorded log event, reduced to the three things the assertions above read.
        /// </summary>
        private sealed record RecordedLogEvent(
            LogLevel Level,
            string Category,
            IReadOnlyDictionary<string, object?> Properties
        );

        /// <summary>
        /// Records every event, with its structured state intact.
        /// <see cref="CorrelationIdRecordingLoggerProvider"/> cannot stand in: it is deliberately
        /// narrowed to the two request-logging event ids and discards the state of everything else.
        /// </summary>
        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            private readonly ConcurrentQueue<RecordedLogEvent> _events = new();

            public RecordedLogEvent[] CriticalEventsFrom(string category) =>
                [
                    .. _events.Where(recorded =>
                        recorded.Level == LogLevel.Critical
                        && string.Equals(recorded.Category, category, StringComparison.Ordinal)
                    ),
                ];

            public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, _events);

            public void Dispose() { }

            private sealed class Recorder(string category, ConcurrentQueue<RecordedLogEvent> events) : ILogger
            {
                public IDisposable BeginScope<TState>(TState state)
                    where TState : notnull => NullLogger.Instance.BeginScope(state);

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter
                )
                {
                    Dictionary<string, object?> properties = new(StringComparer.Ordinal);
                    if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                    {
                        foreach (KeyValuePair<string, object?> value in values)
                        {
                            properties[value.Key] = value.Value;
                        }
                    }

                    events.Enqueue(new RecordedLogEvent(logLevel, category, properties));
                }
            }
        }
    }
}
