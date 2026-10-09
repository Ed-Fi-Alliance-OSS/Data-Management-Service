// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Mssql;

/// <summary>
/// The education-organization projection deployment (<see cref="EducationOrganizationProjectionHost"/>)
/// on SQL Server, over the Data Standard 5.2 schema.
/// </summary>
public abstract class MssqlEducationOrganizationProjectionTestBase : MssqlApiIntegrationTestBase
{
    private readonly ProjectionLogRecorder _logs = new();
    private readonly RedirectingFingerprintReader _fingerprints = new();
    private readonly SwitchableMappingSetProvider _mapping = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private ProjectionDataStoreCatalog? _catalog;
    private string? _unresolvable;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool MultiTenancy => true;

    protected override bool BypassAuthorization => false;

    protected override string RouteQualifierSegments =>
        EducationOrganizationProjectionHost.RouteQualifierSegments;

    protected override TimeProvider TimeProviderOverride => _clock;

    /// <summary>The read's lock timeout; long by default, so only a deliberate timeout ends a wait.</summary>
    protected virtual int ReadLockTimeoutSeconds => 60;

    /// <summary>The row cap, or null for the shipped default.</summary>
    protected virtual int? MaxProjectionRows => null;

    /// <summary>Requests allowed per host before the rate limiter answers, or null for the shipped limit.</summary>
    protected virtual int? RateLimitPermits => null;

    /// <summary>The primary store's published derivatives, or null when it publishes none.</summary>
    protected virtual IReadOnlyDictionary<DataStoreDerivativeType, string>? PrimaryDerivatives => null;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        EducationOrganizationProjectionHost.HostSettings(
            ReadLockTimeoutSeconds,
            MaxProjectionRows,
            RateLimitPermits
        );

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new EducationOrganizationProjectionClaimSetProvider(fixture);

    protected override IDataStoreProvider CreateDataStoreProvider(
        FixtureContext fixture,
        string primaryConnectionString
    )
    {
        // NUnit reuses the fixture instance, so per-test state starts over with each host.
        _logs.Clear();
        _fingerprints.ResetReads();
        _mapping.Disarm();

        _unresolvable = EducationOrganizationProjectionEngine.Mssql.WithUnresolvableHost(
            primaryConnectionString
        );
        _fingerprints.Redirect(_unresolvable, primaryConnectionString);
        _catalog = EducationOrganizationProjectionHost.CreateCatalog(
            EducationOrganizationProjectionEngine.Mssql,
            primaryConnectionString,
            Reachability.AbsentDatabaseConnectionString(primaryConnectionString),
            _unresolvable,
            PrimaryDerivatives
        );
        return _catalog;
    }

    protected override void ConfigureAdditionalServices(IServiceCollection services) =>
        EducationOrganizationProjectionHost.ConfigureServices(services, _logs, _fingerprints, _mapping);

    internal EducationOrganizationProjectionContext Context =>
        new(
            Harness,
            EducationOrganizationProjectionEngine.Mssql,
            PrimaryConnectionString,
            _unresolvable!,
            _catalog!,
            _logs,
            _fingerprints,
            _mapping,
            _clock,
            Reachability,
            LeaseScenarioDatabaseAsync
        );
}
