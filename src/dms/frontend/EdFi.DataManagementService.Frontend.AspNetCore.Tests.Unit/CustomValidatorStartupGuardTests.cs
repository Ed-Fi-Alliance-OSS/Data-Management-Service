// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
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
/// Shared scaffolding for the CustomValidatorRegistrationGuard test suite. This fixture boots real
/// hosts through <see cref="WebApplicationFactory{TEntryPoint}"/> and writes real startup status
/// files, so it is marked NonParallelizable rather than sharing a process-wide temp directory with
/// another fixture running concurrently.
/// </summary>
/// <remarks>
/// Follows the host-boot pattern in CoreAppSettingsStartupValidationTests.cs: a per-test temp
/// AppSettings:StartupStatusFilePath wired through ConfigureAppConfiguration, UseEnvironment("Test"),
/// and TestMockHelper.AddEssentialMocks through the test host's own ConfigureServices seam.
/// TestMockHelper.AddEssentialMocks does not fake the schema bootstrapper, so
/// LoadAndBuildEffectiveSchemaTask runs for real and the effective ApiSchema holds the genuine
/// bundled Ed-Fi Data Standard resources - which is what lets the AppliesTo fixtures below reference
/// a resource name that the guard can actually resolve.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class CustomValidatorStartupGuardTests
{
    /// <summary>
    /// A real, bundled (ProjectName, ResourceName) pair. Confirmed by reading
    /// EdFi.DataStandard52.ApiSchema/ApiSchema.json (the asset the "Test" environment host actually
    /// loads through LoadAndBuildEffectiveSchemaTask): projectSchema.projectName is "Ed-Fi", and its
    /// resourceSchemas carry an entry whose resourceName field is "School" (the "schools" key is the
    /// endpoint name, which the guard's lookup does not use). Matching is exact and ordinal on both
    /// components, so this pair is what a passing AppliesTo fixture must use, and altering either
    /// component's casing is what a wrong-cased fixture must use.
    /// </summary>
    private const string RealProjectName = "Ed-Fi";
    private const string RealResourceName = "School";

    private readonly List<string> _statusDirectoriesToClean = [];
    private readonly List<IDisposable> _disposables = [];

    private readonly List<string> _sharedStatusDirectoriesToClean = [];
    private readonly List<IDisposable> _sharedDisposables = [];
    private TestHost? _noValidatorHost;

    /// <summary>
    /// The zero-validator boot that several tests below are pure observations of: they read the
    /// guard's registration, its Order, and the Information record it emits on its success path, and
    /// none of them registers a validator or asserts the absence or count of any log record, so one
    /// boot serves all of them. Every boot loads the real Data Standard ApiSchema and primes the
    /// compiled-schema cache across every resource for both POST and PUT, so identical repeat boots
    /// are a material cost in a suite that runs as a unit test.
    /// </summary>
    private TestHost NoValidatorHost => _noValidatorHost!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _noValidatorHost = BuildHost(_ => { }, _sharedStatusDirectoriesToClean, _sharedDisposables);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (IDisposable disposable in _sharedDisposables)
        {
            disposable.Dispose();
        }
        _sharedDisposables.Clear();

        foreach (string statusDirectory in _sharedStatusDirectoriesToClean)
        {
            if (Directory.Exists(statusDirectory))
            {
                Directory.Delete(statusDirectory, recursive: true);
            }
        }
        _sharedStatusDirectoriesToClean.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
        _disposables.Clear();

        foreach (string statusDirectory in _statusDirectoriesToClean)
        {
            if (Directory.Exists(statusDirectory))
            {
                Directory.Delete(statusDirectory, recursive: true);
            }
        }
        _statusDirectoriesToClean.Clear();
    }

    /// <summary>
    /// A running test host bundled with the two observation seams every later test needs: the
    /// recording logger provider (for asserting on the guard's own log records) and the non-exiting
    /// process-exit double (for asserting an abort was requested without killing the test runner).
    /// </summary>
    private sealed record TestHost(
        WebApplicationFactory<Program> Factory,
        RecordingLoggerProvider LoggerProvider,
        RecordingStartupProcessExit ProcessExit
    );

    /// <summary>
    /// Builds a test host pointed at a per-test startup-status temp path, with the recording logger
    /// provider and the non-exiting process-exit double wired in, and hands the caller the test
    /// host's own ConfigureServices seam to register whatever ICustomResourceValidator fixtures a
    /// test needs.
    /// </summary>
    /// <remarks>
    /// This seam runs after WebApplicationBuilderExtensions.AddServices has already called
    /// AddCustomValidationGuard, so a validator registered here stands in for a plugin's
    /// registration: the guard must see it through the closure-captured IServiceCollection
    /// regardless of which registrant ran last.
    /// </remarks>
    private TestHost CreateHost(Action<IServiceCollection> configureValidators) =>
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

    /// <summary>
    /// Captures every log record emitted during a test host boot as (category, level, rendered
    /// message), so a test can assert on the guard's own Information/Warning records without
    /// depending on a fake ILogger&lt;T&gt; the DI container never resolves for the guard's actual
    /// category.
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
            ) => records.Enqueue((categoryName, logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Stands in for the production EnvironmentStartupProcessExit, which calls Environment.Exit and
    /// would terminate the test process. Records the call count and exit code instead, so an abort
    /// test can assert both the recorded exit and RunFatalAsync's rethrow.
    /// Written locally because neither existing repository precedent is usable here:
    /// NonExitingStartupProcessExit lives in EdFi.DataManagementService.Tests.Integration, a
    /// different assembly, and the RecordingStartupProcessExit in StartupPhaseExecutorTests.cs is
    /// private-nested inside its own fixture class.
    /// </summary>
    private sealed class RecordingStartupProcessExit : IStartupProcessExit
    {
        private int _exitCallCount;

        public int ExitCallCount => _exitCallCount;

        public int? ExitCode { get; private set; }

        public void Exit(int exitCode)
        {
            Interlocked.Increment(ref _exitCallCount);
            ExitCode = exitCode;
        }
    }

    /// <summary>
    /// A service type nothing in the test host ever registers, so a validator that declares it as a
    /// constructor dependency can never be activated. Used only by
    /// <see cref="UnconstructibleCustomResourceValidator"/>.
    /// </summary>
    private interface IServiceNobodyRegisters { }

    /// <summary>
    /// A validator whose AppliesTo genuinely matches a resource in the bundled effective ApiSchema,
    /// so the guard's lookup succeeds and no unmatched-resource warning is logged for it.
    /// </summary>
    private sealed class PassingCustomResourceValidator : ICustomResourceValidator
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

    /// <summary>
    /// A validator whose AppliesTo names a resource that exists in no project schema, so the guard's
    /// lookup misses and logs the unmatched-resource warning - the "legitimate extension resource
    /// this deployment lacks" case.
    /// </summary>
    private sealed class NonexistentResourceCustomResourceValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(RealProjectName, "ThisResourceDoesNotExistInAnyProjectSchema")];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator whose AppliesTo names the same resource as <see cref="PassingCustomResourceValidator"/>
    /// but with its casing altered. Matching is exact and ordinal, so this must also miss and warn -
    /// it will never match at request time either, and a case-insensitive lookup would silently hide
    /// that.
    /// </summary>
    private sealed class WrongCasedResourceCustomResourceValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(RealProjectName, RealResourceName.ToUpperInvariant())];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator whose AppliesTo carries a raw control character (ASCII BEL, decimal 7) in the
    /// resource name, exercising LoggingSanitizer.SanitizeForLogging on the path that logs every
    /// AppliesTo entry. The guard must log the sanitized form, never the raw string.
    /// </summary>
    private sealed class ControlCharacterAppliesToCustomResourceValidator : ICustomResourceValidator
    {
        // \u0007 is ASCII BEL: a genuine control character, well below LoggingSanitizer's
        // printable-character allowlist. Carried in BOTH components, because the guard sanitizes
        // each one separately and unsafe data in the resource name alone would leave the project
        // name's sanitizer call unproven.
        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource("Ed-\u0007Fi", "School\u0007Descriptor")];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator whose constructor takes a service nobody registers, so MS DI can never build one.
    /// Registered through TryAddEnumerable it passes the descriptor audit - the descriptor is
    /// correctly Transient with an ImplementationType - but fails the activation probe, which is the
    /// failure mode the probe exists to catch before it can surface as a 500 on the first matching
    /// write.
    /// </summary>
    private sealed class UnconstructibleCustomResourceValidator(IServiceNobodyRegisters dependency)
        : ICustomResourceValidator
    {
        // Exposed only so the constructor parameter is not flagged as unused; the guard's
        // activation probe is what proves this type cannot be resolved from the container, not any
        // behavior of this property.
        public IServiceNobodyRegisters Dependency { get; } = dependency;

        public IReadOnlyList<ValidatedResource> AppliesTo => [];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator that is registered and shaped correctly in every respect, but implements only
    /// <see cref="IAsyncDisposable"/> and not <see cref="IDisposable"/>. MS DI tracks a transient
    /// disposable resolved inside a scope, and disposing that scope synchronously throws for a
    /// service in this shape, so the guard's activation probe must dispose its throwaway scope
    /// asynchronously or this otherwise-valid validator takes the whole host down.
    /// </summary>
    private sealed class AsyncDisposableOnlyCustomResourceValidator
        : ICustomResourceValidator,
            IAsyncDisposable
    {
        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(RealProjectName, RealResourceName)];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator whose AppliesTo resource name closes the quoted bracket selector of the JsonPath
    /// the schema lookup builds and opens a second one naming a real resource. The name matches no
    /// resource, so the guard must warn - but a lookup that interpolates the name into a JsonPath
    /// instead reports a match against the smuggled selector and stays silent, which is a false
    /// negative in the one check this guard exists to perform.
    /// </summary>
    private sealed class InjectedSelectorAppliesToCustomResourceValidator : ICustomResourceValidator
    {
        internal const string InjectedResourceName = $"Missing\",\"{RealResourceName}";

        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(RealProjectName, InjectedResourceName)];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator whose AppliesTo resource name ends in a backslash. The log sanitizer's allowlist
    /// permits a backslash (it is there for Windows file paths), but interpolated into the quoted
    /// bracket selector the schema lookup builds it escapes the closing quote and leaves the query
    /// unterminated, so the name must be rejected on a narrower rule than "would the sanitizer keep
    /// it". No resource name in the bundled Data Standard contains one.
    /// </summary>
    private sealed class BackslashAppliesToCustomResourceValidator : ICustomResourceValidator
    {
        internal const string BackslashResourceName = RealResourceName + "\\";

        public IReadOnlyList<ValidatedResource> AppliesTo =>
            [new ValidatedResource(RealProjectName, BackslashResourceName)];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator that declares no AppliesTo entries at all. It is registered, constructible and
    /// audits clean, yet can never run for any resource, which is the same silently-absent-validation
    /// outcome the unmatched-entry warning exists to surface and is cheaper to detect.
    /// </summary>
    private sealed class EmptyAppliesToCustomResourceValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo => [];

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// A validator returning null from AppliesTo, violating the non-null contract the interface
    /// declares. Implementer code runs outside this repository, so the guard must report this as the
    /// never-runs case it is rather than dereferencing null and handing the operator a
    /// NullReferenceException with no indication of which registration produced it.
    /// </summary>
    private sealed class NullAppliesToCustomResourceValidator : ICustomResourceValidator
    {
        public IReadOnlyList<ValidatedResource> AppliesTo => null!;

        public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
            JsonNode document,
            ValidatedResourceInfo resource,
            CustomValidationOperation operation,
            ValidationScope scope,
            string traceId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
    }

    /// <summary>
    /// Four validators registered at once, each exercising a different AppliesTo outcome: one that
    /// matches a real resource, one naming a resource in no project schema, one wrong-cased, and one
    /// carrying a raw control character. Asserts the summary record's count, which is the only
    /// assertion in this suite that proves the guard audits and activates an entire set rather than a
    /// single registration, and that a miss on three of the four neither aborts startup nor stops the
    /// remaining entries from being processed.
    /// </summary>
    [Test]
    public async Task It_audits_and_activates_a_set_of_four_validators()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, PassingCustomResourceValidator>()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    NonexistentResourceCustomResourceValidator
                >()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    WrongCasedResourceCustomResourceValidator
                >()
            );
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    ControlCharacterAppliesToCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == "Custom validator registration guard audited and activated 4 ICustomResourceValidator registration(s)"
            );

        // Three of the four miss, and each must warn on its own entry: a loop that stopped at the
        // first miss, or one that aborted, would leave the later warnings absent.
        host.LoggerProvider.Records.Count(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message.Contains("matches no resource in the effective ApiSchema")
            )
            .Should()
            .Be(3);
    }

    /// <summary>
    /// Two invalid registrations at once. The audit aggregates rather than throwing on the first
    /// offender, so the operator gets one message naming both and does not have to restart DMS once
    /// per mistake. No other test registers more than a single invalid descriptor.
    /// </summary>
    [Test]
    public void It_aggregates_every_invalid_registration_into_one_message()
    {
        var sharedInstance = new PassingCustomResourceValidator();

        TestHost host = CreateHost(services =>
        {
            services.Add(ServiceDescriptor.Singleton<ICustomResourceValidator>(sharedInstance));
            services.Add(
                ServiceDescriptor.Scoped<
                    ICustomResourceValidator,
                    NonexistentResourceCustomResourceValidator
                >()
            );
        });

        Action startHost = () => host.Factory.CreateClient();

        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 2 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain($"'{typeof(PassingCustomResourceValidator).FullName}':")
            .And.Contain($"'{typeof(NonexistentResourceCustomResourceValidator).FullName}':")
            .And.Contain("lifetime is Scoped");

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// AddCustomValidationGuard registers the guard through the single-generic-argument factory
    /// overload (AddSingleton&lt;IDmsStartupTask&gt;(sp => ...)), which leaves the resulting
    /// descriptor's ImplementationType null - it only ever carries an ImplementationFactory. Counting
    /// descriptors by type would therefore silently pass on zero matches, so this resolves the full
    /// IEnumerable&lt;IDmsStartupTask&gt; and counts the actual instances that come back.
    /// CustomValidatorRegistrationGuard is internal to Core; naming it here compiles only because
    /// EdFi.DataManagementService.Core.csproj grants InternalsVisibleTo to this assembly.
    /// </summary>
    [Test]
    public void It_registers_the_guard_exactly_once()
    {
        TestHost host = NoValidatorHost;

        IEnumerable<IDmsStartupTask> startupTasks = host.Factory.Services.GetRequiredService<
            IEnumerable<IDmsStartupTask>
        >();

        startupTasks.OfType<CustomValidatorRegistrationGuard>().Should().ContainSingle();
    }

    /// <summary>
    /// CustomValidationServiceExtensions.cs's own comment records the rule that AddCustomValidationGuard
    /// must never register IServiceCollection itself as a service - the guard reaches the collection only
    /// through the closure captured at registration time, because IServiceCollection is declared in
    /// Microsoft.Extensions.DependencyInjection.Abstractions, a namespace a plugin is permitted to
    /// register into, and a guard that resolved it from DI could read whichever registration won the
    /// race instead of the one it closed over. This is a negative assertion, so it was verified by
    /// temporarily reintroducing that registration and confirming this test then fails.
    /// </summary>
    [Test]
    public void It_registers_no_service_collection_service()
    {
        TestHost host = NoValidatorHost;

        host.Factory.Services.GetService<IServiceCollection>().Should().BeNull();
    }

    /// <summary>
    /// Program.cs's RunByOrderRangeAsync windows only execute startup tasks whose Order falls in
    /// [0, 299] before request serving begins (the InitializeApiSchemas phase). Order 250 sits inside
    /// that window and above LoadAndBuildEffectiveSchemaTask's Order 100, whose effective ApiSchema the
    /// guard's AppliesTo warning reads - see CustomValidatorRegistrationGuard.cs's own comment on Order.
    /// This test only proves the value itself. That the Order actually causes execution is proven by
    /// It_runs_during_a_real_host_boot, which was verified by temporarily moving the Order outside
    /// every executed window and confirming that test then fails while nearly all others still pass.
    /// </summary>
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

        guard.Order.Should().Be(250);

        // Asserted against the window constant and the real task it must follow rather than against
        // restated literals, so that moving either the window bound or the schema task's Order fails
        // this test instead of leaving it agreeing with numbers that no longer match Program.cs.
        guard.Order.Should().BeInRange(0, DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum);

        int effectiveSchemaTaskOrder = startupTasks.OfType<LoadAndBuildEffectiveSchemaTask>().Single().Order;
        guard.Order.Should().BeGreaterThan(effectiveSchemaTaskOrder);
    }

    /// <summary>
    /// The keystone test for this story: boots a real host through WebApplicationFactory&lt;Program&gt;
    /// and asserts on the guard's own Information record captured by the recording logger provider -
    /// an observable effect of the guard actually running, not merely being registered in DI (It_registers_the_guard_exactly_once
    /// already covers presence). The guard emits this record
    /// unconditionally on its success path, even when zero validators are registered, so no fixture
    /// validator registration is needed here.
    /// </summary>
    /// <remarks>
    /// Required negative control: temporarily changing CustomValidatorRegistrationGuard's Order from
    /// 250 to 600 moves it outside every window Program.cs's RunByOrderRangeAsync executes before
    /// request serving begins (Program.cs:315, :329, :341), so the guard would still be registered but
    /// never run. At Order 600 this test fails - the record asserted on below is never captured -
    /// while the host still boots successfully and the great majority of this fixture still passes.
    /// The tests that also fail are the ones observing an effect of the guard having run (the
    /// descriptor-audit aborts, the activation abort, and the AppliesTo log assertions); everything
    /// asserting only registration or Order stays green. That a host can boot clean with the guard
    /// silently skipped is exactly the failure mode this criterion exists to catch. That change must
    /// never land here; Order stays 250.
    /// </remarks>
    [Test]
    public void It_runs_during_a_real_host_boot()
    {
        TestHost host = NoValidatorHost;

        using HttpClient client = host.Factory.CreateClient();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == "Custom validator registration guard audited and activated 0 ICustomResourceValidator registration(s)"
            );
    }

    /// <summary>
    /// A Singleton registration made through the type-based ServiceDescriptor overload carries no
    /// ImplementationInstance and no ImplementationFactory - only its Lifetime breaks the audit - so
    /// this isolates the lifetime rule from the ImplementationInstance rule that
    /// <see cref="It_aborts_startup_for_an_implementation_instance_descriptor"/> necessarily also
    /// trips. Both halves of the required assertion (the recorded Exit and the propagated throw) are
    /// asserted here, plus the offending descriptor's label and lifetime in the aggregated message, so
    /// a guard that aborted for the wrong reason would still fail this test.
    /// </summary>
    [Test]
    public void It_aborts_startup_for_a_non_transient_registration()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICustomResourceValidator, PassingCustomResourceValidator>()
            );
        });

        Action startHost = () => host.Factory.CreateClient();

        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 1 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain(
                $"'{typeof(PassingCustomResourceValidator).FullName}': lifetime is Singleton, but "
                    + "ICustomResourceValidator registrations must be Transient"
            )
            .And.NotContain("ImplementationInstance is set")
            .And.NotContain("ImplementationFactory delegate");

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// ServiceDescriptor.Singleton&lt;TService&gt;(instance) only ever produces a Singleton-lifetime
    /// descriptor with ImplementationInstance set - MS DI has no API that pairs an
    /// ImplementationInstance with a Transient lifetime - so this case cannot fail independently of
    /// <see cref="It_aborts_startup_for_a_non_transient_registration"/>'s lifetime rule. It exists so
    /// the audit's intent toward a shared-instance registration is explicit: the message must name the
    /// ImplementationInstance rule by its own text, not merely the lifetime rule the two cases share.
    /// </summary>
    [Test]
    public void It_aborts_startup_for_an_implementation_instance_descriptor()
    {
        var instance = new PassingCustomResourceValidator();

        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICustomResourceValidator>(instance));
        });

        Action startHost = () => host.Factory.CreateClient();

        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 1 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain($"'{instance.GetType().FullName}':")
            .And.Contain(
                "lifetime is Singleton, but ICustomResourceValidator registrations must be Transient"
            )
            .And.Contain(
                "registered as a shared instance (ImplementationInstance is set), which hands every "
                    + "request the same object regardless of the declared lifetime"
            );

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// services.Add, not TryAddEnumerable: TryAddEnumerable throws ArgumentException for a factory
    /// descriptor whose GetImplementationType() cannot be distinguished from its ServiceType - which is
    /// exactly what a factory descriptor with no ImplementationType looks like - so using it here would
    /// fail the fixture before the host ever reached the guard. The descriptor declares Transient, so
    /// the lifetime rule is not broken; only the ImplementationFactory being set breaks the audit,
    /// which is what forces the descriptor-shape half of the audit even though the declared lifetime is
    /// correct. sharedInstance is captured by the factory delegate and handed back on every resolution,
    /// which is the actual defect the ImplementationFactory rule exists to catch.
    /// </summary>
    [Test]
    public void It_aborts_startup_for_a_factory_descriptor()
    {
        var sharedInstance = new PassingCustomResourceValidator();

        TestHost host = CreateHost(services =>
        {
            services.Add(ServiceDescriptor.Transient<ICustomResourceValidator>(_ => sharedInstance));
        });

        Action startHost = () => host.Factory.CreateClient();

        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 1 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain("'<factory-based registration with no implementation type>':")
            .And.Contain(
                "registered through an ImplementationFactory delegate, which cannot be proven to "
                    + "construct a new instance on every resolution"
            )
            .And.NotContain("lifetime is");

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// The negative-space case for the three abort tests above: a descriptor registered properly -
    /// Transient lifetime, an ImplementationType, through TryAddEnumerable the way a plugin is expected
    /// to register - breaks none of the audit's rules, so the guard must let startup proceed. Asserts
    /// the host boots (no exception, no recorded Exit) and that it goes on to actually serve a request,
    /// not merely that construction succeeded.
    /// </summary>
    [Test]
    public async Task It_accepts_a_transient_implementation_type_descriptor()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, PassingCustomResourceValidator>()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();
    }

    /// <summary>
    /// The activation-probe abort: registers <see cref="UnconstructibleCustomResourceValidator"/>
    /// through TryAddEnumerable as Transient with an ImplementationType, so it passes the descriptor
    /// audit cleanly (none of the descriptor audit's rules are broken) and is only caught by the probe that
    /// resolves the full ICustomResourceValidator set from a throwaway scope - the check that exists
    /// specifically because an unsatisfiable constructor dependency would otherwise defer to per-request
    /// resolution and surface as a 500 on the first matching write instead of a startup failure.
    /// host.Factory.CreateClient() is what triggers the host build, and the guard's ExecuteAsync runs
    /// inside that build (Program.cs's RunByOrderRangeAsync window, entirely before app.Run() begins
    /// serving requests), so a throw out of CreateClient() itself - never returning an HttpClient at all
    /// - is proof the failure lands at startup and not as a request-time 500. Both halves of the
    /// required assertion (the recorded Exit and the propagated throw) are asserted here, same as every
    /// other abort test in this fixture, plus that the message names the offending validator type.
    /// </summary>
    [Test]
    public void It_aborts_startup_for_an_unconstructible_validator()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    UnconstructibleCustomResourceValidator
                >()
            );
        });

        Action startHost = () => host.Factory.CreateClient();

        // Asserts on MS DI's own attribution rather than any re-derived by the guard: for a
        // dependency one hop from the validator the framework message names both the unresolvable
        // service and the type it was activating, so a second per-type activation pass would only
        // reproduce this same string.
        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain(
                "Startup aborted: resolving the registered ICustomResourceValidator instances from a "
                    + "throwaway scope failed"
            )
            .And.Contain(nameof(UnconstructibleCustomResourceValidator))
            .And.Contain(nameof(IServiceNobodyRegisters));

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// The ordering-independence criterion: the test host's own ConfigureServices seam always runs
    /// after WebApplicationBuilderExtensions.AddServices has already called AddCustomValidationGuard,
    /// so registering <see cref="PassingCustomResourceValidator"/> there stands in for a plugin's
    /// registration - exactly the position a plugin-contributed registration will be in. The guard can
    /// only see this registration through the closure-captured IServiceCollection
    /// that CustomValidationServiceExtensions.cs documents, never a
    /// snapshot fixed at AddCustomValidationGuard's own call site, since nothing was registered yet when
    /// that call ran. Asserts on the guard's own per-entry AppliesTo Information record and the summary
    /// count record naming this one registration - not merely that the host booted, which would also
    /// pass if the guard silently saw nothing.
    /// </summary>
    [Test]
    public void It_audits_a_validator_registered_after_the_extension()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, PassingCustomResourceValidator>()
            );
        });

        using HttpClient client = host.Factory.CreateClient();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == $"ICustomResourceValidator '{typeof(PassingCustomResourceValidator).FullName}' "
                        + $"AppliesTo entry: ProjectName '{RealProjectName}', ResourceName '{RealResourceName}'"
            );

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == "Custom validator registration guard audited and activated 1 ICustomResourceValidator registration(s)"
            );

        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();
    }

    /// <summary>
    /// A deployment that has adopted no ICustomResourceValidator at all - the common case - must still
    /// boot. No fixture is registered here at all, so this proves the guard's zero-registration success
    /// path (already touched incidentally by <see cref="It_runs_during_a_real_host_boot"/>'s Information
    /// record assertion) also carries the host all the way through to actually serving a request, not
    /// merely emitting a record.
    /// </summary>
    [Test]
    public async Task It_boots_with_no_validators_registered()
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
    /// <see cref="ControlCharacterAppliesToCustomResourceValidator"/>'s AppliesTo.ResourceName carries a
    /// raw ASCII BEL control character. The per-entry AppliesTo Information record the guard logs must
    /// carry LoggingSanitizer.SanitizeForLogging's sanitized form - which strips control characters
    /// rather than replacing them, so "School\u0007Descriptor" becomes "SchoolDescriptor" - and never the
    /// raw string, since the raw AppliesTo value is implementer-controlled input flowing into a
    /// structured log record. The guard must also treat a name outside the sanitizer's allowlist as a
    /// lookup miss without attempting the schema lookup at all: interpolated into the JsonPath that
    /// lookup builds, a control character makes the query fail to parse, which would otherwise escape as
    /// an unhandled exception and abort startup rather than logging a sanitized warning. So the host
    /// must boot and go on to serve a request here, not merely avoid throwing during construction.
    /// </summary>
    [Test]
    public async Task It_sanitizes_applies_to_entries_in_the_log()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    ControlCharacterAppliesToCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();

        string sanitizedResourceName = LoggingSanitizer.SanitizeForLogging("School\u0007Descriptor");
        sanitizedResourceName.Should().Be("SchoolDescriptor");

        // Asserted across every record the host emitted, not just the guard's own: because the guard
        // never interpolates a name outside the sanitizer's allowlist into a JsonPath, the raw value
        // reaches no component at all, including the JsonPath helper that logs an unparseable query
        // unsanitized under its own logger category. If that lookup were ever attempted again for such
        // a name, the raw control character would reappear in some record and this assertion fails.
        (string Category, LogLevel Level, string Message) appliesToRecord = host
            .LoggerProvider.Records.Should()
            .ContainSingle(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message.StartsWith(
                    $"ICustomResourceValidator '{typeof(ControlCharacterAppliesToCustomResourceValidator).FullName}' AppliesTo entry:"
                )
            )
            .Subject;

        appliesToRecord
            .Message.Should()
            .Be(
                $"ICustomResourceValidator '{typeof(ControlCharacterAppliesToCustomResourceValidator).FullName}' "
                    + $"AppliesTo entry: ProjectName '{RealProjectName}', ResourceName '{sanitizedResourceName}'"
            );
        appliesToRecord.Message.Should().NotContain("\u0007");
        host.LoggerProvider.Records.Should().OnlyContain(record => !record.Message.Contains("\u0007"));
    }

    /// <summary>
    /// <see cref="NonexistentResourceCustomResourceValidator"/>'s AppliesTo names a resource that
    /// matches no project schema - the "legitimate extension resource this deployment lacks" case. The
    /// guard must warn rather than abort, and the host must actually go on to serve a request, not merely
    /// avoid throwing during construction: a warn-then-abort implementation would satisfy the Warning
    /// assertion alone but fail the request assertion below.
    /// </summary>
    [Test]
    public async Task It_warns_for_an_applies_to_entry_matching_no_resource()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    NonexistentResourceCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message
                    == $"ICustomResourceValidator '{typeof(NonexistentResourceCustomResourceValidator).FullName}' "
                        + $"AppliesTo entry ProjectName '{RealProjectName}', ResourceName "
                        + "'ThisResourceDoesNotExistInAnyProjectSchema' matches no resource in the "
                        + "effective ApiSchema and will never run. Expected for an extension resource "
                        + "this deployment lacks; otherwise check for a typo or case mismatch, since "
                        + "matching is exact and ordinal"
            );
    }

    /// <summary>
    /// <see cref="WrongCasedResourceCustomResourceValidator"/> names the same resource as
    /// <see cref="PassingCustomResourceValidator"/> but with its casing altered ("SCHOOL" instead of
    /// "School"). It must also warn, since matching is exact and ordinal and a wrong-cased entry will
    /// never match at request time either - a case-insensitive lookup would satisfy every other
    /// criterion in this fixture while silently diverging from the fan-in gate's own
    /// (ProjectName, ResourceName) matching. Asserts the host still boots and serves a request, the same
    /// as the other warning case.
    /// </summary>
    [Test]
    public async Task It_warns_for_a_wrong_cased_applies_to_entry()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    WrongCasedResourceCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();

        string wrongCasedResourceName = RealResourceName.ToUpperInvariant();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message
                    == $"ICustomResourceValidator '{typeof(WrongCasedResourceCustomResourceValidator).FullName}' "
                        + $"AppliesTo entry ProjectName '{RealProjectName}', ResourceName "
                        + $"'{wrongCasedResourceName}' matches no resource in the effective ApiSchema and "
                        + "will never run. Expected for an extension resource this deployment lacks; "
                        + "otherwise check for a typo or case mismatch, since matching is exact and "
                        + "ordinal"
            );
    }

    /// <summary>
    /// A keyed registration carries the right ServiceType, so it reaches the audit, but every unkeyed
    /// Implementation* accessor on a keyed descriptor returns null and unkeyed
    /// GetServices&lt;ICustomResourceValidator&gt;() never yields it. Left unrejected it therefore
    /// passes every shape rule, skips the activation probe, is never AppliesTo-checked, and is never
    /// invoked at request time, while the guard reports success. It must abort startup naming the
    /// implementation type, which for a keyed descriptor is only reachable through
    /// KeyedImplementationType.
    /// </summary>
    [Test]
    public void It_aborts_startup_for_a_keyed_registration()
    {
        TestHost host = CreateHost(services =>
        {
            services.AddKeyedTransient<ICustomResourceValidator, PassingCustomResourceValidator>(
                "plugin-supplied-key"
            );
        });

        Action startHost = () => host.Factory.CreateClient();

        startHost
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Startup aborted: 1 ICustomResourceValidator registration(s) are invalid.")
            .And.Contain($"'{typeof(PassingCustomResourceValidator).FullName}':")
            .And.Contain("registered as a keyed service")
            .And.NotContain("<factory-based registration with no implementation type>");

        host.ProcessExit.ExitCallCount.Should().Be(1);
        host.ProcessExit.ExitCode.Should().Be(-1);
    }

    /// <summary>
    /// The activation probe resolves validators into a throwaway scope and must dispose it
    /// asynchronously. A validator implementing only IAsyncDisposable is tracked by that scope, and a
    /// synchronous Dispose on a scope holding one throws InvalidOperationException from outside the
    /// probe's own try/catch, aborting startup for a registration that breaks no rule. The host must
    /// boot and go on to serve a request.
    /// </summary>
    [Test]
    public async Task It_boots_for_a_validator_that_is_only_async_disposable()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    AsyncDisposableOnlyCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);
        host.ProcessExit.ExitCode.Should().BeNull();

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Information
                && record.Message
                    == "Custom validator registration guard audited and activated 1 ICustomResourceValidator registration(s)"
            );
    }

    /// <summary>
    /// An AppliesTo resource name that smuggles a second bracket selector naming a real resource must
    /// still be reported as matching nothing. Request-time AppliesTo matching is exact and ordinal, so
    /// this entry can never match there; a startup lookup that interpolates the name into a JsonPath
    /// resolves the smuggled selector instead, finds the real resource, and stays silent about a
    /// validator that will never run.
    /// </summary>
    [Test]
    public async Task It_warns_for_an_applies_to_entry_carrying_an_injected_path_selector()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    InjectedSelectorAppliesToCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);

        string sanitizedInjectedName = LoggingSanitizer.SanitizeForLogging(
            InjectedSelectorAppliesToCustomResourceValidator.InjectedResourceName
        );

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message
                    == $"ICustomResourceValidator '{typeof(InjectedSelectorAppliesToCustomResourceValidator).FullName}' "
                        + $"AppliesTo entry ProjectName '{RealProjectName}', ResourceName "
                        + $"'{sanitizedInjectedName}' matches no resource in the effective ApiSchema and "
                        + "will never run. Expected for an extension resource this deployment lacks; "
                        + "otherwise check for a typo or case mismatch, since matching is exact and "
                        + "ordinal"
            );
    }

    /// <summary>
    /// A validator declaring no AppliesTo entries can never run for any resource. That is the same
    /// silently-absent-validation outcome the unmatched-entry warning exists to surface, so it warns
    /// rather than passing silently, and does not abort - an empty list is a plausible interim state
    /// in implementer code, not a registration mistake.
    /// </summary>
    [Test]
    public async Task It_warns_for_a_validator_declaring_no_applies_to_entries()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, EmptyAppliesToCustomResourceValidator>()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message
                    == $"ICustomResourceValidator '{typeof(EmptyAppliesToCustomResourceValidator).FullName}' "
                        + "declares no AppliesTo entries, so it can never run for any resource"
            );
    }

    /// <summary>
    /// AppliesTo returning null violates the interface contract, and implementer code runs outside
    /// this repository. The guard must treat it as the never-runs case rather than dereferencing it,
    /// which would abort startup with a NullReferenceException naming no registration at all.
    /// </summary>
    [Test]
    public async Task It_warns_for_a_validator_whose_applies_to_is_null()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<ICustomResourceValidator, NullAppliesToCustomResourceValidator>()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message
                    == $"ICustomResourceValidator '{typeof(NullAppliesToCustomResourceValidator).FullName}' "
                        + "declares no AppliesTo entries, so it can never run for any resource"
            );
    }

    /// <summary>
    /// A resource name the log sanitizer would preserve but the schema lookup's JsonPath cannot hold.
    /// The sanitizer allows a backslash, so an "is this name safe to log" test is not the same test
    /// as "is this name safe to interpolate into a bracket selector". It must warn as a miss, exactly
    /// like any other unmatchable name, rather than letting a parse failure abort startup.
    /// </summary>
    [Test]
    public async Task It_warns_for_an_applies_to_entry_the_lookup_path_cannot_hold()
    {
        TestHost host = CreateHost(services =>
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    ICustomResourceValidator,
                    BackslashAppliesToCustomResourceValidator
                >()
            );
        });

        using HttpClient client = host.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ProcessExit.ExitCallCount.Should().Be(0);

        host.LoggerProvider.Records.Should()
            .Contain(record =>
                record.Category == typeof(CustomValidatorRegistrationGuard).FullName
                && record.Level == LogLevel.Warning
                && record.Message.Contains(typeof(BackslashAppliesToCustomResourceValidator).FullName!)
                && record.Message.Contains("matches no resource in the effective ApiSchema")
            );
    }
}
