// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.Api.Plugins.Hosting;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The plugin startup check cases that genuinely need a real host boot: that the check actually runs
/// during startup rather than merely being registered, and that the contract registry production
/// carries is the one the design specifies.
/// </summary>
/// <remarks>
/// One boot serves all of them, because none registers a plugin or asserts the absence of a record.
/// Follows the host-boot harness in CustomValidatorRegistrationGuardHostTests: a per-fixture temporary
/// startup status file, UseEnvironment("Test"), the essential mocks through the test host's own
/// ConfigureServices seam, and a recording logger provider added through its ConfigureLogging seam,
/// which runs after the production ClearProviders call. NonParallelizable because it boots a real host
/// and writes a real startup status file.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class Given_a_host_with_the_plugin_startup_check
{
    private WebApplicationFactory<Program>? _factory;
    private PluginRecordingLoggerProvider _loggerProvider = null!;
    private string _statusDirectory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _loggerProvider = new PluginRecordingLoggerProvider();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:StartupStatusFilePath"] = Path.Combine(
                                _statusDirectory,
                                "dms-startup-status.json"
                            ),
                        }
                    )
            );
            builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
            builder.ConfigureServices(TestMockHelper.AddEssentialMocks);
        });

        // Forces the host to build and its startup tasks to run.
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
        _loggerProvider.Dispose();

        if (Directory.Exists(_statusDirectory))
        {
            Directory.Delete(_statusDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> Factory => _factory!;

    /// <summary>
    /// Registered, once, and as an instance the container did not have to activate.
    /// </summary>
    [Test]
    public void It_registers_the_audit_input_the_composition_phase_produced()
    {
        Factory.Services.GetService<PluginAuditInput>().Should().NotBeNull();
        Factory.Services.GetServices<PluginAuditInput>().Should().ContainSingle();
    }

    [Test]
    public void It_registers_the_check_exactly_once_inside_an_executed_window()
    {
        IDmsStartupTask task = Factory
            .Services.GetRequiredService<IEnumerable<IDmsStartupTask>>()
            .Should()
            .ContainSingle(startupTask => startupTask.GetType().Name == "PluginRegistrationGuard")
            .Subject;

        task.Order.Should().Be(260);
        task.Order.Should().BeInRange(200, DmsStartupTaskOrderRanges.ApiSchemaInitializationMaximum);
        task.Order.Should().BeGreaterThan(250);
    }

    /// <summary>
    /// Registered is not the same as executed, and the ordering band only means anything if the task
    /// actually runs. Its own success record is the evidence, which is the precedent the custom
    /// validator guard's host tests set for the same claim.
    /// </summary>
    [Test]
    public void It_actually_executed_during_startup()
    {
        _loggerProvider
            .Records.Should()
            .Contain(record =>
                record.Message.Contains(
                    "Plugin registration guard accepted the contributions of",
                    StringComparison.Ordinal
                )
            );
    }

    /// <summary>
    /// A plugin-free boot is unaffected: the composition phase is a no-op, the singleton is still
    /// registered, and the check runs and finds nothing. That is what lets this story assert the check
    /// executes before the host-integration story wires the loader into Program.cs.
    /// </summary>
    [Test]
    public void It_reports_no_plugin_contributions_on_a_plugin_free_boot()
    {
        Factory.Services.GetRequiredService<PluginAuditInput>().Records.Should().BeEmpty();
        _loggerProvider
            .Records.Should()
            .Contain(record =>
                record.Message.Contains("accepted the contributions of 0 plugin(s)", StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// The value the loader's version-skew preflight consumes, read off the registry production
    /// actually registered rather than off one the test built. Two names, and the assembly name rather
    /// than the package id: the custom validator contract is declared in the assembly
    /// EdFi.DataManagementService.CustomValidation and packed under the id EdFi.Api.CustomValidation,
    /// and an assembly reference carries an assembly name, so the package id in this set would match
    /// nothing and would silently check nothing.
    /// </summary>
    [Test]
    public void It_derives_the_contract_assembly_names_the_skew_preflight_needs()
    {
        PluginContractRegistry registry = Factory.Services.GetRequiredService<PluginAuditInput>().Registry;

        registry
            .ContractAssemblyNames.Should()
            .Equal("EdFi.Api.Plugins", "EdFi.DataManagementService.CustomValidation");
        registry.ContractAssemblyNames.Should().NotContain("EdFi.Api.CustomValidation");
    }

    /// <summary>
    /// Nothing in this story registers the service collection, and the custom validator guard reaches
    /// its own collection through a closure for the same reason. A negative assertion over the built
    /// container, so a later registration of it anywhere fails here.
    /// </summary>
    [Test]
    public void It_registers_no_service_collection_service()
    {
        Factory.Services.GetService<IServiceCollection>().Should().BeNull();
    }

    private sealed class PluginRecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _records = [];

        internal IReadOnlyCollection<(string Category, LogLevel Level, string Message)> Records =>
            _records.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _records);

        public void Dispose()
        {
            // Nothing to release: the loggers this hands out only append to the queue above.
        }

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
}
