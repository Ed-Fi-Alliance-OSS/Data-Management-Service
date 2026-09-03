// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.CustomValidation;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The CustomValidatorRegistrationGuard cases that genuinely need a real host boot: that the
/// frontend registers the guard exactly once and never registers IServiceCollection itself, that the
/// guard's Order actually causes it to execute before requests are served, that the closure sees a
/// registration made after the extension's own call site, and that an abort reaches the process-exit
/// signal. Every other case - the descriptor audit, the activation probe, and the AppliesTo matching
/// and warning paths - is covered directly against the guard in
/// EdFi.DataManagementService.Core.Tests.Unit/Startup/CustomValidatorRegistrationGuardTests.cs,
/// because a full WebApplicationFactory boot loads the real Data Standard ApiSchema and primes the
/// compiled-schema cache over every resource for both POST and PUT, which is too expensive to spend
/// on decisions the guard makes over a hand-built ServiceCollection.
/// </summary>
/// <remarks>
/// Boots real hosts and writes real startup status files, so NonParallelizable rather than sharing a
/// process-wide temp directory with another fixture running concurrently. Follows the host-boot
/// pattern in CoreAppSettingsStartupValidationTests.cs: a per-test temp
/// AppSettings:StartupStatusFilePath through ConfigureAppConfiguration, UseEnvironment("Test"), and
/// TestMockHelper.AddEssentialMocks through the test host's own ConfigureServices seam. Those mocks
/// do not fake the schema bootstrapper, so LoadAndBuildEffectiveSchemaTask runs for real and the
/// effective ApiSchema holds the genuine bundled resources, which is what lets the fixture below
/// name a resource the guard can actually resolve.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_A_Host_With_The_Custom_Validator_Startup_Guard
{
    /// <summary>
    /// A real, bundled (ProjectName, ResourceName) pair from EdFi.DataStandard52.ApiSchema, which is
    /// the schema the "Test" environment host actually loads: projectSchema.projectName is "Ed-Fi",
    /// and one of its resourceSchemas carries resourceName "School". Matching is exact and ordinal
    /// on both components.
    /// </summary>
    private const string RealProjectName = "Ed-Fi";
    private const string RealResourceName = "School";

    private readonly List<string> _statusDirectoriesToClean = [];
    private readonly List<IDisposable> _disposables = [];

    // Tracked separately from the per-test lists above, which [TearDown] clears after every test.
    // The shared host has to outlive each test that reads it, so it must not be registered for
    // per-test disposal.
    private readonly List<string> _sharedStatusDirectoriesToClean = [];
    private readonly List<IDisposable> _sharedDisposables = [];
    private TestHost? _noValidatorHost;

    /// <summary>
    /// The zero-validator boot that the observation-only tests share. They read the guard's
    /// registration, its Order, and the record it emits on its success path; none of them registers a
    /// validator or asserts the absence or count of any record, so one boot serves all of them.
    /// </summary>
    private TestHost NoValidatorHost => _noValidatorHost!;

    [OneTimeSetUp]
    public void OneTimeSetUp() =>
        _noValidatorHost = BuildHost(_ => { }, _sharedStatusDirectoriesToClean, _sharedDisposables);

    [OneTimeTearDown]
    public void OneTimeTearDown() => DisposeHosts(_sharedStatusDirectoriesToClean, _sharedDisposables);

    [TearDown]
    public void TearDown() => DisposeHosts(_statusDirectoriesToClean, _disposables);

    private static void DisposeHosts(List<string> statusDirectories, List<IDisposable> disposables)
    {
        foreach (IDisposable disposable in disposables)
        {
            disposable.Dispose();
        }
        disposables.Clear();

        foreach (string statusDirectory in statusDirectories)
        {
            if (Directory.Exists(statusDirectory))
            {
                Directory.Delete(statusDirectory, recursive: true);
            }
        }
        statusDirectories.Clear();
    }

    private sealed record TestHost(
        WebApplicationFactory<Program> Factory,
        RecordingLoggerProvider LoggerProvider,
        RecordingStartupProcessExit ProcessExit
    );

    private TestHost BuildHost(Action<IServiceCollection> configureValidators) =>
        BuildHost(configureValidators, _statusDirectoriesToClean, _disposables);

    private static TestHost BuildHost(
        Action<IServiceCollection> configureValidators,
        List<string> statusDirectories,
        List<IDisposable> disposables
    )
    {
        string statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        statusDirectories.Add(statusDirectory);
        string statusFilePath = Path.Combine(statusDirectory, "dms-startup-status.json");

        var loggerProvider = new RecordingLoggerProvider();
        var processExit = new RecordingStartupProcessExit();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:StartupStatusFilePath"] = statusFilePath,
                        }
                    )
            );
            // Added through the test host's own ConfigureLogging seam, which runs after
            // WebApplicationBuilderExtensions' ClearProviders() call - probed to still capture
            // DmsStartupOrchestrator's own records emitted while startup tasks run.
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                // The production registration - AddSingleton<IStartupProcessExit,
                // EnvironmentStartupProcessExit>() - calls Environment.Exit and would kill the test
                // runner, so it must be replaced before any abort path can run.
                services.Replace(ServiceDescriptor.Singleton<IStartupProcessExit>(processExit));

                configureValidators(services);
            });
        });
        disposables.Add(factory);

        return new TestHost(factory, loggerProvider, processExit);
    }

    [Test]
    public void It_registers_the_guard_exactly_once()
    {
        TestHost host = NoValidatorHost;

        // AddCustomValidationGuard registers through the single-generic-argument factory overload,
        // which leaves the descriptor's ImplementationType null, so counting descriptors by type
        // would silently pass on zero matches. This resolves the actual instances instead.
        IEnumerable<IDmsStartupTask> startupTasks = host.Factory.Services.GetRequiredService<
            IEnumerable<IDmsStartupTask>
        >();

        startupTasks.OfType<CustomValidatorRegistrationGuard>().Should().ContainSingle();
    }

    /// <summary>
    /// The guard must reach the collection only through the closure captured at registration time,
    /// never by resolving IServiceCollection from DI: that type is declared in a namespace a plugin
    /// is permitted to register into, so a guard that resolved it could read whichever registration
    /// won the race instead of the one it closed over. A negative assertion, so it was verified by
    /// temporarily adding that registration and confirming this test then fails.
    /// </summary>
    [Test]
    public void It_registers_no_service_collection_service()
    {
        TestHost host = NoValidatorHost;

        host.Factory.Services.GetService<IServiceCollection>().Should().BeNull();
    }

    [Test]
    public void It_orders_the_guard_inside_an_executed_window()
    {
        TestHost host = NoValidatorHost;

        IEnumerable<IDmsStartupTask> startupTasks = host.Factory.Services.GetRequiredService<
            IEnumerable<IDmsStartupTask>
        >();
        CustomValidatorRegistrationGuard guard = startupTasks
            .OfType<CustomValidatorRegistrationGuard>()
            .Single();

        // Asserted against the window constant and the real task it must follow, rather than against
        // restated literals, so that moving either the window bound or the schema task's Order fails
        // this test instead of leaving it agreeing with numbers that no longer match Program.cs.
        guard.Order.Should().BeInRange(0, DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum);

        int effectiveSchemaTaskOrder = startupTasks.OfType<LoadAndBuildEffectiveSchemaTask>().Single().Order;
        guard.Order.Should().BeGreaterThan(effectiveSchemaTaskOrder);
    }

    /// <summary>
    /// Asserts on the guard's own record captured during a real boot, which is an observable effect
    /// of it having run rather than merely being registered.
    /// </summary>
    /// <remarks>
    /// Required negative control: temporarily moving the guard's Order to 600 puts it outside every
    /// window Program.cs's RunByOrderRangeAsync executes before request serving begins
    /// (Program.cs:315, :329, :341), so the guard is still registered but never runs. At Order 600
    /// this test fails, the record below never being captured, while the host still boots
    /// successfully and the registration and Order tests above still pass. That a host can boot clean
    /// with the guard silently skipped is exactly the failure mode this case exists to catch.
    /// </remarks>
    [Test]
    public async Task It_runs_during_a_real_host_boot()
    {
        TestHost host = NoValidatorHost;

        using HttpClient client = host.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == "Custom validator registration guard audited and activated 0 ICustomResourceValidator registration(s)"
            );
    }

    /// <summary>
    /// The ordering-independence case, which only a real host can show: this seam always runs after
    /// WebApplicationBuilderExtensions.AddServices has already called AddCustomValidationGuard, so a
    /// validator registered here stands in for a plugin's registration. The guard can only see it
    /// through the closure-captured collection, never a snapshot fixed at its own call site, since
    /// nothing was registered when that call ran.
    /// </summary>
    [Test]
    public async Task It_audits_a_validator_registered_after_the_extension()
    {
        TestHost host = BuildHost(services =>
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, BundledResourceValidator>()
            )
        );

        using HttpClient client = host.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == $"ICustomResourceValidator '{typeof(BundledResourceValidator).FullName}' "
                        + $"AppliesTo entry: ProjectName '{RealProjectName}', ResourceName '{RealResourceName}'"
            );

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == "Custom validator registration guard audited and activated 1 ICustomResourceValidator registration(s)"
            );
    }

    /// <summary>
    /// The fatal-path wiring, which only a real host can show: the guard's exception has to travel
    /// DmsStartupOrchestrator to StartupPhaseExecutor.RunFatalAsync, which signals process exit with
    /// -1 and then rethrows. What makes each registration shape invalid is covered directly in Core;
    /// this proves an abort actually terminates startup rather than being logged and swallowed.
    /// </summary>
    [Test]
    public void It_signals_process_exit_when_the_guard_aborts()
    {
        TestHost host = BuildHost(services =>
            services.Add(ServiceDescriptor.Scoped<ICustomResourceValidator, BundledResourceValidator>())
        );

        Action startHost = () => host.Factory.CreateClient();

        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("ICustomResourceValidator registration(s) are invalid.");

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// Captures every log record emitted during a boot as (category, level, rendered message), so a
    /// test can assert on the guard's own records without depending on a fake ILogger&lt;T&gt; the
    /// container never resolves for the guard's actual category.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _records = [];

        public IReadOnlyCollection<(string Category, LogLevel Level, string Message)> Records =>
            _records.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _records);

        public void Dispose() { }

        private sealed class RecordingLogger(
            string categoryName,
            ConcurrentQueue<(string Category, LogLevel Level, string Message)> records
        ) : ILogger
        {
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
                records.Enqueue((categoryName, logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>
    /// Records the requested exit instead of performing it. The production registration calls
    /// Environment.Exit, which would kill the test runner before RunFatalAsync's rethrow could be
    /// observed. Written locally because the existing copies in this repository
    /// (NonExitingStartupProcessExit in EdFi.DataManagementService.Tests.Integration, and the one in
    /// StartupPhaseExecutorTests.cs) are in another assembly or private-nested in their own fixture.
    /// </summary>
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

    /// <summary>
    /// A validator naming a resource that exists in the bundled schema, so the guard's lookup
    /// succeeds and no unmatched-resource warning is logged for it.
    /// </summary>
    private sealed class BundledResourceValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(RealProjectName, RealResourceName)];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }
}
