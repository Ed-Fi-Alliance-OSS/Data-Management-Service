// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
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
    private readonly Dictionary<DataStoreDerivativeType, string> _leasedDerivativeConnectionStrings = new();
    private readonly List<string> _extraLeases = [];
    private DbConnection? _assertionConnection;
    private FixtureContext? _fixtureContext;
    private string? _startupStatusFilePath;
    private ApiIntegrationQueryRecorder? _queryRecorder;
    private ApiIntegrationProviderFailureRecorder? _providerFailureRecorder;
    private DocumentCacheReadAcquisitionFailureRecorder? _documentCacheReadAcquisitionFailureRecorder;
    private DocumentCacheDirectFillTimeoutRecorder? _documentCacheDirectFillTimeoutRecorder;
    private DocumentCacheReadTelemetryRecorder? _documentCacheReadTelemetryRecorder;

    protected ApiIntegrationHarness Harness { get; private set; } = null!;

    /// <summary>The fixture this test class is bound to.</summary>
    protected abstract FixtureKey Fixture { get; }

    /// <summary>
    /// Derivative kinds this fixture wants provisioned as their own leased databases, each seeded
    /// distinguishably so a response proves which one served it. Empty by default, so no existing
    /// fixture leases anything extra.
    /// </summary>
    protected virtual IReadOnlyList<DataStoreDerivativeType> LeasedDerivatives => [];

    /// <summary>
    /// The leased connection string for a derivative this fixture asked for. Throws rather than
    /// returning the primary's, because a silent fallback would make every routing assertion pass.
    /// </summary>
    protected string DerivativeConnectionString(DataStoreDerivativeType derivativeType) =>
        _leasedDerivativeConnectionStrings.TryGetValue(derivativeType, out string? connectionString)
            ? connectionString
            : throw new InvalidOperationException(
                $"No {derivativeType} database was leased for this fixture. Add it to {nameof(LeasedDerivatives)}."
            );

    /// <summary>The primary's leased connection string.</summary>
    protected string PrimaryConnectionString =>
        _leasedConnectionString
        ?? throw new InvalidOperationException("The primary database has not been leased yet.");

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
    /// Namespace prefixes returned from the fake JWT validation service, for NamespaceBased scenarios.
    /// </summary>
    protected virtual IReadOnlyList<string> ClientNamespacePrefixes => [];

    /// <summary>
    /// When set, replaces the authorization provider-failure extractor with a recording double that rewrites
    /// the extracted payload after the production authorization path raised a real provider exception. Used by
    /// malformed-AUTH1-payload scenarios; null leaves the production extraction in place.
    /// </summary>
    protected virtual Func<
        RelationshipAuthorizationProviderFailure,
        RelationshipAuthorizationProviderFailure
    >? ProviderFailureTransform => null;

    /// <summary>
    /// Captures compiled page keysets passed into the document hydrator for assertions
    /// that need SQL plan parameter metadata.
    /// </summary>
    protected virtual bool CaptureQueryPlans => false;

    /// <summary>Enables ASP.NET Core response compression for scenarios that exercise coding variants.</summary>
    protected virtual bool EnableAspNetCompression => false;

    /// <summary>Enables the <c>/{tenant}</c> route-qualifier segment for multitenancy-aware scenarios.</summary>
    protected virtual bool MultiTenancy => false;

    /// <summary>
    /// When supplied, replaces only the singleton CMS-facing <c>IConfigurationServiceApplicationProvider</c>
    /// instead of the whole <c>IApplicationContextProvider</c>, so a scenario can observe the real per-request
    /// memoization performed by the production <c>CachedApplicationContextProvider</c>. Null keeps every other
    /// scenario's historical always-stable fake.
    /// </summary>
    protected virtual IConfigurationServiceApplicationProvider? ApplicationContextConfigurationProviderOverride =>
        null;

    /// <summary>Enables the DMS DocumentCache read-acceleration path for cache-backed read scenarios.</summary>
    protected virtual bool EnableDocumentCacheReadAcceleration => false;

    /// <summary>Forces cache read lookup to report an expected adapter acquisition failure.</summary>
    protected virtual bool ForceDocumentCacheReadLookupAdapterAcquisitionFailure => false;

    /// <summary>Forces direct-fill materialization to wait until the direct-fill timeout cancels it.</summary>
    protected virtual bool ForceDocumentCacheDirectFillTimeout => false;

    /// <summary>Records cache read telemetry without replacing production cache lookup or direct-fill services.</summary>
    protected virtual bool RecordDocumentCacheReadTelemetry => false;

    /// <summary>Direct-fill timeout used by cache-backed API integration scenarios.</summary>
    protected virtual string DocumentCacheReadAccelerationDirectFillTimeout => "00:00:00.250";

    /// <summary>Controls the public ResourceLinks response flag for scenarios that exercise link stripping.</summary>
    protected virtual bool ResourceLinksEnabled => true;

    /// <summary>
    /// Replaces the deployed maximum page size for this fixture's host, or null to leave it in place.
    /// </summary>
    /// <remarks>
    /// Partition sizing is the reason this exists: the mandatory minimum partition size is a multiple of
    /// the configured maximum page size, so at the deployed value a collection has to hold thousands of
    /// rows before it can be cut into more than one partition. A fixture that needs several partitions
    /// lowers the page size instead of seeding that many documents over HTTP.
    /// </remarks>
    protected virtual int? MaximumPageSizeOverride => null;

    /// <summary>
    /// Empties the body of the first page that hydrates rows, while leaving its selected maximum in
    /// place. Default false, so no existing fixture changes behavior.
    /// </summary>
    /// <remarks>
    /// The continuation header is gated on a non-null selected maximum rather than on the response
    /// body, which is what keeps a walk advancing past keys whose rows were deleted before hydration
    /// completed. Selection and projection are statements inside one command batch, so no test can land
    /// a delete between them; this seam makes the resulting response observable over HTTP without
    /// changing production behavior.
    /// </remarks>
    protected virtual bool SuppressHydratedRowsOnce => false;

    /// <summary>
    /// Profile names assigned to the requesting application, by name. Empty means no assignment.
    /// </summary>
    /// <remarks>
    /// This is what separates the two profile branches. With no assignment a request that names no
    /// profile is answered by the no-profiles-assigned exit and never reaches implicit selection, so a
    /// scenario asserting that an unreadable profile is correctly excluded from a GET has to assign one
    /// to exercise the code it claims to cover.
    /// </remarks>
    protected virtual IReadOnlyList<string> AssignedProfileNames => [];

    /// <summary>
    /// Replaces the data-store provider the host resolves, for a fixture that publishes derivatives or
    /// changes its configuration between requests. Null keeps the single-instance stub.
    /// </summary>
    protected virtual IDataStoreProvider? CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    ) => null;

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

    /// <summary>
    /// Provisions one more database from the same baseline, for a fixture that needs several
    /// distinguishable targets in one test. Released by <see cref="ReleaseAdditionalDatabaseAsync" />.
    /// </summary>
    protected abstract Task<string> LeaseAdditionalDatabaseAsync(FixtureContext fixture);

    /// <summary>Releases one database taken through <see cref="LeaseAdditionalDatabaseAsync" />.</summary>
    protected abstract Task ReleaseAdditionalDatabaseAsync(string leasedConnectionString);

    /// <summary>
    /// Switches a leased database's reachability at the server, for a fixture that proves a request
    /// never touched a target other than the one it selected.
    /// </summary>
    protected abstract IDerivativeTargetReachability Reachability { get; }

    /// <summary>
    /// Hook for a fixture that must substitute a production service in the booted host, for example to
    /// force one validation stage to fail so that a precedence rule between stages becomes observable.
    /// It runs after the standard doubles are registered, so a replacement made here wins.
    /// </summary>
    protected virtual void ConfigureAdditionalServices(IServiceCollection services) { }

    [SetUp]
    public async Task ApiIntegrationSetUp()
    {
        _fixtureContext = FixtureContextLoader.Load(Fixture);
        _leasedConnectionString = await LeaseDatabaseAsync(_fixtureContext);

        foreach (DataStoreDerivativeType derivativeType in LeasedDerivatives)
        {
            // A separate provisioned database per derivative, so a request that reaches the wrong one
            // returns different rows rather than the same rows from a shared database.
            string derivativeConnectionString = await LeaseAdditionalDatabaseAsync(_fixtureContext);
            _leasedDerivativeConnectionStrings[derivativeType] = derivativeConnectionString;
            _extraLeases.Add(derivativeConnectionString);
        }

        _startupStatusFilePath = Path.Combine(Path.GetTempPath(), $"api-int-startup-{Guid.NewGuid():N}.json");
        _queryRecorder = CaptureQueryPlans ? new ApiIntegrationQueryRecorder() : null;
        _documentCacheReadAcquisitionFailureRecorder = ForceDocumentCacheReadLookupAdapterAcquisitionFailure
            ? new DocumentCacheReadAcquisitionFailureRecorder()
            : null;
        _documentCacheDirectFillTimeoutRecorder = ForceDocumentCacheDirectFillTimeout
            ? new DocumentCacheDirectFillTimeoutRecorder()
            : null;
        _documentCacheReadTelemetryRecorder = RecordDocumentCacheReadTelemetry
            ? new DocumentCacheReadTelemetryRecorder()
            : null;
        var providerFailureTransform = ProviderFailureTransform;
        _providerFailureRecorder = providerFailureTransform is null
            ? null
            : new ApiIntegrationProviderFailureRecorder();

        var fixtureContext = _fixtureContext;
        var leasedConnectionString = _leasedConnectionString;
        var startupStatusFilePath = _startupStatusFilePath;
        var queryRecorder = _queryRecorder;
        var documentCacheReadAcquisitionFailureRecorder = _documentCacheReadAcquisitionFailureRecorder;
        var documentCacheDirectFillTimeoutRecorder = _documentCacheDirectFillTimeoutRecorder;
        var documentCacheReadTelemetryRecorder = _documentCacheReadTelemetryRecorder;
        var providerFailureRecorder = _providerFailureRecorder;
        var clientNamespacePrefixes = ClientNamespacePrefixes;
        var assignedProfileNames = AssignedProfileNames;
        var suppressHydratedRowsOnce = SuppressHydratedRowsOnce;
        var multiTenancy = MultiTenancy;
        var applicationContextConfigurationProviderOverride = ApplicationContextConfigurationProviderOverride;
        IDataStoreProvider? dataStoreProviderOverride = CreateDataStoreProvider(
            _fixtureContext,
            _leasedConnectionString
        );

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
            builder.UseSetting("AppSettings:MultiTenancy", multiTenancy ? "true" : "false");
            builder.UseSetting(
                "AppSettings:EnableAspNetCompression",
                EnableAspNetCompression ? "true" : "false"
            );
            builder.UseSetting(
                "DataManagement:ResourceLinks:Enabled",
                ResourceLinksEnabled ? "true" : "false"
            );
            if (MaximumPageSizeOverride is { } maximumPageSize)
            {
                builder.UseSetting(
                    "AppSettings:MaximumPageSize",
                    maximumPageSize.ToString(CultureInfo.InvariantCulture)
                );
            }
            builder.UseSetting(
                "DataManagement:DocumentCache:ReadAcceleration:Enabled",
                EnableDocumentCacheReadAcceleration ? "true" : "false"
            );
            if (EnableDocumentCacheReadAcceleration)
            {
                builder.UseSetting("DataManagement:DocumentCache:Targets:0:TenantKey", "");
                builder.UseSetting(
                    "DataManagement:DocumentCache:Targets:0:DataStoreId",
                    ExternalDoublesConstants.StableDataStoreId.ToString()
                );
                builder.UseSetting(
                    "DataManagement:DocumentCache:ReadAcceleration:DirectFillTimeout",
                    DocumentCacheReadAccelerationDirectFillTimeout
                );
                builder.UseSetting("DataManagement:DocumentCache:Projector:PollInterval", "01:00:00");
            }
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
                    ClientEducationOrganizationIds,
                    clientNamespacePrefixes,
                    providerFailureTransform,
                    providerFailureRecorder,
                    EnableDocumentCacheReadAcceleration ? GetRelationalProviderToken() : null,
                    documentCacheReadAcquisitionFailureRecorder,
                    documentCacheDirectFillTimeoutRecorder,
                    documentCacheReadTelemetryRecorder,
                    assignedProfileNames,
                    applicationContextConfigurationProviderOverride,
                    dataStoreProviderOverride
                );

                if (queryRecorder is not null)
                {
                    services.AddSingleton(queryRecorder);
                    services.ReplaceDocumentHydratorWithRecorder();
                    services.ReplaceRelationalCommandExecutorWithRecorder();
                }

                if (suppressHydratedRowsOnce)
                {
                    services.SuppressHydratedRowsOnce();
                }

                ConfigureAdditionalServices(services);
            });
        });

        _assertionConnection = await OpenAssertionConnectionAsync(_leasedConnectionString);
        var httpClient = _factory.CreateClient();
        if (EnableDocumentCacheReadAcceleration)
        {
            await _factory
                .Services.GetRequiredService<IDocumentCacheProjectionSupervisor>()
                .RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
        }

        Harness = new ApiIntegrationHarness(
            httpClient,
            _assertionConnection,
            _fixtureContext,
            _queryRecorder,
            _providerFailureRecorder,
            _documentCacheReadAcquisitionFailureRecorder,
            _documentCacheDirectFillTimeoutRecorder,
            _documentCacheReadTelemetryRecorder
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

        foreach (string extraLease in _extraLeases)
        {
            await ReleaseAdditionalDatabaseAsync(extraLease);
        }

        _extraLeases.Clear();
        _leasedDerivativeConnectionStrings.Clear();

        _fixtureContext = null;
        _queryRecorder = null;
        _providerFailureRecorder = null;
        _documentCacheReadAcquisitionFailureRecorder = null;
        _documentCacheDirectFillTimeoutRecorder = null;
        _documentCacheReadTelemetryRecorder = null;

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

    private RelationalProviderToken GetRelationalProviderToken() =>
        string.Equals(Datastore, "postgresql", StringComparison.OrdinalIgnoreCase)
            ? RelationalProviderToken.Postgresql
            : RelationalProviderToken.SqlServer;
}
