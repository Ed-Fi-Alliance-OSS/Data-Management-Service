// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.Integration;

/// <summary>
/// Abstract per-test lifecycle for API integration tests. Boots an in-process DMS
/// host via <see cref="WebApplicationFactory{TEntryPoint}"/> wired to the active
/// fixture's ApiSchema directory and a per-test leased database. The dialect-specific
/// hooks (<see cref="LeaseDatabaseAsync"/>, <see cref="OpenAssertionConnectionAsync"/>,
/// <see cref="ReleaseDatabaseAsync"/>) and the <see cref="Datastore"/> identifier are
/// supplied by per-dialect derived bases.
/// </summary>
[Category("ApiIntegration")]
public abstract class ApiIntegrationTestBase
{
    private WebApplicationFactory<Program>? _factory;
    private string? _leasedConnectionString;
    private DbConnection? _assertionConnection;
    private FixtureContext? _fixtureContext;
    private string? _startupStatusFilePath;

    protected ApiIntegrationHarness Harness { get; private set; } = null!;

    /// <summary>The fixture this test class is bound to.</summary>
    protected abstract FixtureKey Fixture { get; }

    /// <summary>
    /// Datastore identifier consumed by <c>AppSettings:Datastore</c>; supplied by
    /// the per-dialect base class (for example, <c>"postgresql"</c> or <c>"mssql"</c>).
    /// </summary>
    protected abstract string Datastore { get; }

    /// <summary>
    /// Provisions a fresh per-test database from the dialect's baseline and returns its
    /// connection string. Implementations must guarantee the returned database is owned
    /// by this test and will be dropped in <see cref="ReleaseDatabaseAsync"/>.
    /// </summary>
    protected abstract Task<string> LeaseDatabaseAsync(FixtureContext fixture);

    /// <summary>Opens a <see cref="DbConnection"/> against the leased database for post-HTTP assertions.</summary>
    protected abstract Task<DbConnection> OpenAssertionConnectionAsync(string leasedConnectionString);

    /// <summary>Releases (drops) the leased database identified by <paramref name="leasedConnectionString"/>.</summary>
    protected abstract Task ReleaseDatabaseAsync(string leasedConnectionString);

    [SetUp]
    public async Task ApiIntegrationSetUp()
    {
        _fixtureContext = FixtureContextLoader.Load(Fixture);
        _leasedConnectionString = await LeaseDatabaseAsync(_fixtureContext);
        _startupStatusFilePath = Path.Combine(Path.GetTempPath(), $"api-int-startup-{Guid.NewGuid():N}.json");

        var fixtureContext = _fixtureContext;
        var leasedConnectionString = _leasedConnectionString;
        var startupStatusFilePath = _startupStatusFilePath;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:UseRelationalBackend"] = "true",
                            ["AppSettings:UseApiSchemaPath"] = "true",
                            ["AppSettings:ApiSchemaPath"] = fixtureContext.ApiSchemaDirectory,
                            ["AppSettings:StartupStatusFilePath"] = startupStatusFilePath,
                            ["AppSettings:Datastore"] = Datastore,
                            ["AppSettings:QueryHandler"] = "postgresql",
                            ["AppSettings:DeployDatabaseOnStartup"] = "false",
                            ["AppSettings:BypassAuthorization"] = "true",
                            ["ConfigurationServiceSettings:BaseUrl"] = "http://localhost/test-cms",
                            ["ConfigurationServiceSettings:ClientId"] = "test-cms-client",
                            ["ConfigurationServiceSettings:ClientSecret"] = "test-cms-secret",
                            ["ConfigurationServiceSettings:Scope"] = "edfi_admin_api/full_access",
                        }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                ExternalDoublesRegistration.RegisterAll(services, fixtureContext, leasedConnectionString);
            });
        });

        _assertionConnection = await OpenAssertionConnectionAsync(_leasedConnectionString);
        var httpClient = _factory.CreateClient();
        Harness = new ApiIntegrationHarness(httpClient, _assertionConnection, _fixtureContext);
    }

    [TearDown]
    public async Task ApiIntegrationTearDown()
    {
        if (Harness is not null)
        {
            await Harness.DisposeAsync();
            Harness = null!;
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
        _assertionConnection = null;

        if (_leasedConnectionString is not null)
        {
            await ReleaseDatabaseAsync(_leasedConnectionString);
            _leasedConnectionString = null;
        }

        _fixtureContext = null;

        if (_startupStatusFilePath is not null && File.Exists(_startupStatusFilePath))
        {
            try
            {
                File.Delete(_startupStatusFilePath);
            }
            catch
            {
                // Best-effort cleanup; never mask test failures.
            }
            _startupStatusFilePath = null;
        }
    }
}
