// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Plugins;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql.CustomValidation;

/// <summary>
/// The same allowlisted plugin, staged the same way, but configured with a <c>BaseAddress</c> that
/// otherwise names a running stub and carries a query string.
/// </summary>
/// <remarks>
/// A sibling fixture rather than another case in <see cref="Given_TheUniqueIdValidatorPluginIsAllowlisted"/>,
/// for the same reason as every other <c>BaseAddress</c>-variant sibling in this directory:
/// <c>UniqueIdValidation:BaseAddress</c> comes from the host's configuration and is fixed for that
/// host's lifetime, so a different value needs its own fixture class. The stub is started so the
/// address is otherwise a real, reachable one; the plugin's <c>ConfigureHttpClient</c> guard refuses
/// it for its query string before any client is ever built, so the stub is never actually dialed.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheUniqueIdValidatorPluginHasABaseAddressWithAQuery
    : PostgresqlApiIntegrationTestBase
{
    private string _pluginRoot = string.Empty;
    private UniqueIdServiceStub _stub = null!;
    private PluginLogCapture _logCapture = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string>
        {
            ["Plugins:Directory"] = _pluginRoot,
            ["Plugins:Allowed"] = UniqueIdValidationPluginScenario.PluginName,
            ["UniqueIdValidation:BaseAddress"] = $"{_stub.BaseAddress}?tenant=a",
        };

    /// <summary>
    /// Wired the same way <see cref="Given_TheUniqueIdValidatorPluginIsAllowlisted"/> wires its own
    /// capture: through this base-class hook, so the provider it registers survives the production
    /// host's own <c>ClearProviders()</c> call. A fresh instance per call, because the base class
    /// boots a new host - and calls this hook again - once per test.
    /// </summary>
    protected override void ConfigureAdditionalServices(IServiceCollection services)
    {
        _logCapture = new PluginLogCapture();

        Logger captureLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_logCapture)
            .CreateLogger();

        services.AddSingleton<ILoggerProvider>(
            new SerilogLoggerProvider(captureLogger, dispose: true)
        );
    }

    /// <summary>
    /// OneTimeSetUp rather than SetUp, for the same reason every other sibling fixture in this
    /// directory uses it: the base class reads <see cref="AdditionalHostSettings"/> while booting the
    /// host in its own per-test SetUp, and a derived SetUp runs after that one.
    /// </summary>
    [OneTimeSetUp]
    public async Task StageThePluginAndStartTheStub()
    {
        _pluginRoot = PluginHostProbe.CreatePluginRootFromSource(
            PluginHostProbe.CustomValidationFixtureRoot,
            UniqueIdValidationPluginScenario.PluginName
        );
        _stub = await UniqueIdServiceStub.StartAsync();
    }

    [OneTimeTearDown]
    public async Task RemoveThePluginAndStopTheStub()
    {
        PluginHostProbe.DeleteIfPresent(_pluginRoot);
        await _stub.DisposeAsync();
    }

    [SetUp]
    public void ResetTheStub() => _stub.Reset();

    [Test]
    public Task It_fails_the_write_when_the_base_address_has_a_query() =>
        UniqueIdValidationPluginScenario.It_fails_the_write_when_the_base_address_has_a_query(
            Harness,
            _stub,
            _logCapture
        );

    [Test]
    public Task It_creates_a_non_person_resource_when_the_base_address_has_a_query() =>
        UniqueIdValidationPluginScenario.It_creates_a_non_person_resource_when_the_base_address_has_a_query(
            Harness
        );
}
