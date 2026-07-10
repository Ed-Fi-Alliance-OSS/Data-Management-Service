// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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
    private ApiIntegrationQueryRecorder? _queryRecorder;

    protected ApiIntegrationHarness Harness { get; private set; } = null!;

    /// <summary>The fixture this test class is bound to.</summary>
    protected abstract FixtureKey Fixture { get; }

    /// <summary>
    /// Datastore identifier consumed by <c>AppSettings:Datastore</c>; supplied by
    /// the per-dialect base class (for example, <c>"postgresql"</c> or <c>"mssql"</c>).
    /// </summary>
    protected abstract string Datastore { get; }

    /// <summary>
    /// Allows focused authorization scenarios to run the real authorization middleware while
    /// keeping the existing smoke scenarios on the historical bypassed path.
    /// </summary>
    protected virtual bool BypassAuthorization => true;

    /// <summary>
    /// EducationOrganizationIds returned from the fake JWT validation service.
    /// </summary>
    protected virtual IReadOnlyList<long> ClientEducationOrganizationIds => [];

    /// <summary>
    /// Captures compiled page keysets passed into the document hydrator for assertions
    /// that need SQL plan parameter metadata.
    /// </summary>
    protected virtual bool CaptureQueryPlans => false;

    /// <summary>Enables ASP.NET Core response compression for scenarios that exercise coding variants.</summary>
    protected virtual bool EnableAspNetCompression => false;

    /// <summary>
    /// Builds the claim set provider used by the in-process host.
    /// </summary>
    protected virtual IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new AllowAllClaimSetProvider(fixture);

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
        _queryRecorder = CaptureQueryPlans ? new ApiIntegrationQueryRecorder() : null;

        var fixtureContext = _fixtureContext;
        var leasedConnectionString = _leasedConnectionString;
        var startupStatusFilePath = _startupStatusFilePath;
        var queryRecorder = _queryRecorder;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");

            // UseSetting writes into the host's IConfiguration before AddServices() runs,
            // so options bound during service registration (e.g. AppSettings:Datastore) observe the harness-owned values without relying on
            // process environment variables or file-based appsettings.
            builder.UseSetting("AppSettings:UseApiSchemaPath", "true");
            builder.UseSetting("AppSettings:ApiSchemaPath", fixtureContext.ApiSchemaDirectory);
            builder.UseSetting("AppSettings:StartupStatusFilePath", startupStatusFilePath);
            builder.UseSetting("AppSettings:Datastore", Datastore);
            builder.UseSetting("AppSettings:BypassAuthorization", BypassAuthorization ? "true" : "false");
            builder.UseSetting(
                "AppSettings:EnableAspNetCompression",
                EnableAspNetCompression ? "true" : "false"
            );
            builder.UseSetting("ConfigurationServiceSettings:BaseUrl", "http://localhost/test-cms");
            builder.UseSetting("ConfigurationServiceSettings:ClientId", "test-cms-client");
            builder.UseSetting("ConfigurationServiceSettings:ClientSecret", "test-cms-secret");
            builder.UseSetting("ConfigurationServiceSettings:Scope", "edfi_admin_api/full_access");

            builder.ConfigureServices(services =>
            {
                ExternalDoublesRegistration.RegisterAll(
                    services,
                    fixtureContext,
                    leasedConnectionString,
                    CreateClaimSetProvider(fixtureContext),
                    ClientEducationOrganizationIds
                );

                if (queryRecorder is not null)
                {
                    services.AddSingleton(queryRecorder);
                    services.ReplaceDocumentHydratorWithRecorder();
                }
            });
        });

        _assertionConnection = await OpenAssertionConnectionAsync(_leasedConnectionString);
        var httpClient = _factory.CreateClient();
        Harness = new ApiIntegrationHarness(
            httpClient,
            _assertionConnection,
            _fixtureContext,
            _queryRecorder
        );
    }

    [TearDown]
    public async Task ApiIntegrationTearDown()
    {
        if (Harness is not null)
        {
            await Harness.DisposeAsync();
            Harness = null!;
        }
        else if (_assertionConnection is not null)
        {
            // Host startup failed before the harness was constructed; the assertion
            // connection is otherwise unreferenced. Dispose it directly so that the
            // leased database can be dropped without an open session blocking it.
            await _assertionConnection.DisposeAsync();
        }
        _assertionConnection = null;

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        if (_leasedConnectionString is not null)
        {
            await ReleaseDatabaseAsync(_leasedConnectionString);
            _leasedConnectionString = null;
        }

        _fixtureContext = null;
        _queryRecorder = null;

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
