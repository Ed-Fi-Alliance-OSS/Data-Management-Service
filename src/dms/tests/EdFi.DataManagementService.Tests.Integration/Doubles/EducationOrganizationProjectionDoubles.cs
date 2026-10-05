// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Claims;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>A bearer token, the client it authenticates and the claim set its token names.</summary>
internal sealed record ProjectionCredential(string Token, string ClientId, string ClaimSetName);

/// <summary>
/// The credential shapes the education-organization projection scenarios authenticate with. Only
/// <see cref="ProjectionReaderClaimSetName"/> grants the projection claim, and only <c>Read</c>; the
/// smoke claim set the rest of the suite uses does not grant it.
/// </summary>
internal static class EducationOrganizationProjectionCredentials
{
    public const string ProjectionReaderClaimSetName = "EdOrgProjectionReader";
    public const string IdentityOnlyClaimSetName = "IdentityOnly";
    public const string ProjectionWithoutReadClaimSetName = "EdOrgProjectionWithoutRead";
    public const string ProjectionMisconfiguredClaimSetName = "EdOrgProjectionMisconfigured";

    /// <summary>The projection credential, bound to tenant A. It has no data stores.</summary>
    public static readonly ProjectionCredential Projection = new(
        "projection-token",
        "projection-client",
        ProjectionReaderClaimSetName
    );

    /// <summary>The projection credential of tenant B.</summary>
    public static readonly ProjectionCredential ProjectionTenantB = new(
        "projection-b-token",
        "projection-b-client",
        ProjectionReaderClaimSetName
    );

    /// <summary>The suite's smoke credential: every fixture resource, no service claim.</summary>
    public static readonly ProjectionCredential Resource = new(
        ExternalDoublesConstants.SmokeToken,
        ExternalDoublesConstants.SmokeClientId,
        ExternalDoublesConstants.SmokeClaimSetName
    );

    public static readonly ProjectionCredential IdentityOnly = new(
        "identity-token",
        "identity-client",
        IdentityOnlyClaimSetName
    );

    public static readonly ProjectionCredential ProjectionWithoutRead = new(
        "projection-without-read-token",
        "projection-without-read-client",
        ProjectionWithoutReadClaimSetName
    );

    public static readonly ProjectionCredential ProjectionMisconfigured = new(
        "projection-misconfigured-token",
        "projection-misconfigured-client",
        ProjectionMisconfiguredClaimSetName
    );

    public static readonly ProjectionCredential UnknownClaimSet = new(
        "unknown-claim-set-token",
        "unknown-claim-set-client",
        "NoSuchClaimSet"
    );

    /// <summary>Grants the projection, but its client has no application in tenant A.</summary>
    public static readonly ProjectionCredential Unbound = new(
        "unbound-token",
        "unbound-client",
        ProjectionReaderClaimSetName
    );

    /// <summary>Grants the projection, but its client's binding cannot be determined.</summary>
    public static readonly ProjectionCredential BindingUnavailable = new(
        "binding-unavailable-token",
        "binding-unavailable-client",
        ProjectionReaderClaimSetName
    );

    public static IReadOnlyList<ProjectionCredential> All { get; } =
    [
        Projection,
        ProjectionTenantB,
        Resource,
        IdentityOnly,
        ProjectionWithoutRead,
        ProjectionMisconfigured,
        UnknownClaimSet,
        Unbound,
        BindingUnavailable,
    ];
}

/// <summary>
/// The claim sets behind <see cref="EducationOrganizationProjectionCredentials"/>: the smoke claim set
/// unchanged, a claim set granting only <c>Read</c> on the projection claim, one granting the identity
/// claim, one granting the projection claim without <c>Read</c>, and one whose projection <c>Read</c>
/// names a strategy the service claim does not allow.
/// </summary>
internal sealed class EducationOrganizationProjectionClaimSetProvider(FixtureContext fixture)
    : IClaimSetProvider
{
    private static readonly AuthorizationStrategy[] _noFurtherAuthorizationRequired =
    [
        new(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired),
    ];

    private readonly AllowAllClaimSetProvider _smoke = new(fixture);

    public async Task<IList<ClaimSet>> GetAllClaimSets(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        IList<ClaimSet> smoke = await _smoke.GetAllClaimSets(tenant, cancellationToken);
        string projection = Conventions.EducationOrganizationProjectionServiceClaimUri;
        string identity = $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity";

        return
        [
            .. smoke,
            new ClaimSet(
                EducationOrganizationProjectionCredentials.ProjectionReaderClaimSetName,
                [new ResourceClaim(projection, "Read", _noFurtherAuthorizationRequired)]
            ),
            new ClaimSet(
                EducationOrganizationProjectionCredentials.IdentityOnlyClaimSetName,
                [
                    new ResourceClaim(identity, "Create", _noFurtherAuthorizationRequired),
                    new ResourceClaim(identity, "Read", _noFurtherAuthorizationRequired),
                ]
            ),
            new ClaimSet(
                EducationOrganizationProjectionCredentials.ProjectionWithoutReadClaimSetName,
                [
                    new ResourceClaim(projection, "Create", _noFurtherAuthorizationRequired),
                    new ResourceClaim(projection, "Update", _noFurtherAuthorizationRequired),
                    new ResourceClaim(projection, "ReadChanges", _noFurtherAuthorizationRequired),
                ]
            ),
            new ClaimSet(
                EducationOrganizationProjectionCredentials.ProjectionMisconfiguredClaimSetName,
                [
                    new ResourceClaim(
                        projection,
                        "Read",
                        [new AuthorizationStrategy("RelationshipsWithEdOrgsOnly")]
                    ),
                ]
            ),
        ];
    }
}

/// <summary>
/// Resolves each <see cref="EducationOrganizationProjectionCredentials"/> token to its client and claim
/// set. Any other token is invalid. A projection credential carries no data store, as provisioned; the
/// resource credential carries the data stores its seeding routes use.
/// </summary>
internal sealed class CredentialJwtValidationService(IReadOnlyList<long> resourceDataStoreIds)
    : IJwtValidationService
{
    private readonly Dictionary<string, ProjectionCredential> _credentials =
        EducationOrganizationProjectionCredentials.All.ToDictionary(credential => credential.Token);

    public Task<(ClaimsPrincipal?, ClientAuthorizations?)> ValidateAndExtractClientAuthorizationsAsync(
        string token,
        CancellationToken cancellationToken
    ) => Task.FromResult(Resolve(token));

    public Task<(ClaimsPrincipal?, ClientAuthorizations?)> ValidateAndExtractClientAuthorizationsAsync(
        string authorizationHeader,
        int tokenStartIndex,
        CancellationToken cancellationToken
    ) => Task.FromResult(Resolve(authorizationHeader[tokenStartIndex..].Trim()));

    private (ClaimsPrincipal?, ClientAuthorizations?) Resolve(string token)
    {
        if (!_credentials.TryGetValue(token, out ProjectionCredential? credential))
        {
            return (null, null);
        }

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new Claim("client_id", credential.ClientId)], "test")
        );
        IReadOnlyList<long> dataStoreIds =
            credential == EducationOrganizationProjectionCredentials.Resource ? resourceDataStoreIds : [];

        return (
            principal,
            new ClientAuthorizations(
                credential.Token,
                credential.ClientId,
                credential.ClaimSetName,
                [],
                [],
                [.. dataStoreIds.Select(static id => new DataStoreId(id))]
            )
        );
    }
}

/// <summary>
/// The client-to-tenant bindings the Configuration Service would report: an application per bound
/// client and tenant, a deliberately unavailable binding, and <c>NotFound</c> for every other pair.
/// </summary>
internal sealed class TenantBindingApplicationContextProvider(
    IReadOnlyDictionary<(string ClientId, string Tenant), IReadOnlyList<long>> bindings,
    string unavailableClientId
) : IApplicationContextProvider
{
    public Task<ApplicationContextResult> GetApplicationByClientIdAsync(
        string clientId,
        string? tenant,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(Resolve(clientId, tenant));

    public Task<ApplicationContextResult> ReloadApplicationByClientIdAsync(
        string clientId,
        string? tenant,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(Resolve(clientId, tenant));

    private ApplicationContextResult Resolve(string clientId, string? tenant)
    {
        if (string.Equals(clientId, unavailableClientId, StringComparison.Ordinal))
        {
            return new ApplicationContextResult.Unavailable();
        }

        return bindings.TryGetValue((clientId, tenant ?? ""), out IReadOnlyList<long>? dataStoreIds)
            ? new ApplicationContextResult.Success(
                new ApplicationContext(
                    Id: 1,
                    ApplicationId: 1,
                    ClientId: clientId,
                    ClientUuid: ExternalDoublesConstants.StableClientUuid,
                    DataStoreIds: [.. dataStoreIds],
                    CreatorOwnershipTokenId: null,
                    OwnershipTokenIds: []
                )
            )
            : new ApplicationContextResult.NotFound();
    }
}

/// <summary>
/// A multi-tenant data-store catalog with the Configuration Service's two views: what DMS has cached,
/// and what the Configuration Service would return on a reload. A store registered after startup is
/// only in the second until a request misses and reloads. A tenant can be made to fail every reload.
/// </summary>
internal sealed class ProjectionDataStoreCatalog : IDataStoreProvider
{
    /// <summary>Text a failed reload carries, which must never reach a response or a log.</summary>
    public const string HostileOutageMessage = "cms-outage-hostile-text Password=cms-outage-secret";

    private readonly ConcurrentDictionary<string, TenantCatalog> _tenants = new(StringComparer.Ordinal);

    public ProjectionDataStoreCatalog(IReadOnlyDictionary<string, IReadOnlyList<DataStore>> startup)
    {
        foreach ((string tenant, IReadOnlyList<DataStore> stores) in startup)
        {
            _tenants[tenant] = new TenantCatalog(stores);
        }
    }

    /// <summary>Adds a store to the Configuration Service's view only, as a store created after startup.</summary>
    public void Register(string tenant, DataStore store)
    {
        TenantCatalog catalog = _tenants[tenant];
        lock (catalog)
        {
            catalog.Remote = [.. catalog.Remote.Where(existing => existing.Id != store.Id), store];
        }
    }

    /// <summary>Makes every later reload of the tenant's catalog fail.</summary>
    public void FailReloads(string tenant) => _tenants[tenant].FailReloads = true;

    /// <summary>How many times the tenant's catalog was reloaded.</summary>
    public int ReloadCount(string tenant) => Volatile.Read(ref _tenants[tenant].ReloadCount);

    public Task<IList<DataStore>> LoadDataStores(
        string? tenant = null,
        CancellationToken cancellationToken = default
    )
    {
        TenantCatalog catalog = Catalog(tenant);
        Interlocked.Increment(ref catalog.ReloadCount);

        if (catalog.FailReloads)
        {
            throw new HttpRequestException(HostileOutageMessage);
        }

        lock (catalog)
        {
            catalog.Cached = catalog.Remote;
            return Task.FromResult<IList<DataStore>>([.. catalog.Cached]);
        }
    }

    public Task RefreshInstancesIfExpiredAsync(
        string? tenant = null,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public IReadOnlyList<DataStore> GetAll(string? tenant = null) => Catalog(tenant).Cached;

    public DataStore? GetById(long id, string? tenant = null) =>
        Catalog(tenant).Cached.FirstOrDefault(store => store.Id == id);

    public bool IsLoaded(string? tenant = null) => _tenants.ContainsKey(tenant ?? "");

    public Task<IList<string>> LoadTenants(CancellationToken cancellationToken = default) =>
        Task.FromResult<IList<string>>([.. _tenants.Keys]);

    public bool TenantExists(string tenant) => _tenants.ContainsKey(tenant);

    public IReadOnlyList<string> GetLoadedTenantKeys() => [.. _tenants.Keys];

    private TenantCatalog Catalog(string? tenant) =>
        _tenants.TryGetValue(tenant ?? "", out TenantCatalog? catalog) ? catalog : TenantCatalog.Empty;

    private sealed class TenantCatalog(IReadOnlyList<DataStore> stores)
    {
        public static readonly TenantCatalog Empty = new([]);

        public volatile IReadOnlyList<DataStore> Cached = stores;
        public volatile IReadOnlyList<DataStore> Remote = stores;
        public volatile bool FailReloads;
        public int ReloadCount;
    }
}

/// <summary>One captured log event.</summary>
internal sealed record CapturedLogEvent(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, string> Properties,
    string? Exception
)
{
    /// <summary>Everything the event carries, as one string to search.</summary>
    public string Text =>
        $"{Category} {Message} {string.Join(" ", Properties.Select(property => $"{property.Key}={property.Value}"))} {Exception}";
}

/// <summary>
/// Captures every logger category at <c>Trace</c> beside the host's own logging, so a scenario can
/// assert what the production components wrote.
/// </summary>
internal sealed class ProjectionLogRecorder : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEvent> _events = new();

    public IReadOnlyList<CapturedLogEvent> Events => [.. _events];

    public void Clear() => _events.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _events);

    public void Dispose() { }

    /// <summary>Adds the recorder to the host and lets it see <c>Trace</c> from every category.</summary>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(this);
        services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(
                new LoggerFilterRule(typeof(ProjectionLogRecorder).FullName, null, LogLevel.Trace, null)
            )
        );
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLogEvent> events) : ILogger
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
            Dictionary<string, string> properties = new(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach ((string key, object? value) in pairs)
                {
                    properties[key] = value?.ToString() ?? "";
                }
            }

            events.Enqueue(
                new CapturedLogEvent(
                    category,
                    logLevel,
                    formatter(state, exception),
                    properties,
                    exception?.ToString()
                )
            );
        }
    }
}

/// <summary>
/// Answers the fingerprint read for chosen connection strings from another database, and counts every
/// read by connection string. A store whose host cannot be resolved can then hold a cached, matching
/// verdict, so a request reaches the projection read's own connection acquisition.
/// </summary>
internal sealed class RedirectingFingerprintReader : IDatabaseFingerprintReader
{
    private readonly ConcurrentDictionary<string, string> _redirects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
    private IDatabaseFingerprintReader? _inner;

    public void Redirect(string connectionString, string answeredFrom) =>
        _redirects[connectionString] = answeredFrom;

    public int ReadsOf(string connectionString) => _reads.GetValueOrDefault(connectionString);

    public void ResetReads() => _reads.Clear();

    public IReadOnlyCollection<string> ReadConnectionStrings => [.. _reads.Keys];

    public Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
    {
        _reads.AddOrUpdate(target.ConnectionString, 1, static (_, count) => count + 1);
        IDatabaseFingerprintReader inner =
            _inner
            ?? throw new InvalidOperationException("The production fingerprint reader was not attached.");

        return _redirects.TryGetValue(target.ConnectionString, out string? answeredFrom)
            ? inner.ReadFingerprintAsync(new EffectiveDataStoreTarget(target.Kind, answeredFrom))
            : inner.ReadFingerprintAsync(target);
    }

    /// <summary>Wraps the host's registered reader.</summary>
    public void Register(IServiceCollection services) =>
        ProjectionServiceDecoration.Decorate<IDatabaseFingerprintReader>(
            services,
            inner =>
            {
                _inner = inner;
                return this;
            }
        );
}

/// <summary>
/// The host's mapping-set provider, which, while armed, fails the way an unavailable mapping set does,
/// carrying a message and a diagnostic that must never reach a response or a log.
/// </summary>
internal sealed class SwitchableMappingSetProvider : IMappingSetProvider
{
    public const string HostileMessage =
        "mapping-hostile-message Server=mapping-host;Password=mapping-secret";
    public const string HostileDiagnostic =
        "mapping-hostile-diagnostic \"edfi\".\"School\" Password=diag-secret";

    private IMappingSetProvider? _inner;
    private volatile bool _armed;

    public void Arm() => _armed = true;

    public void Disarm() => _armed = false;

    public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken)
    {
        if (_armed)
        {
            throw new MappingSetUnavailableException(HostileMessage, [HostileDiagnostic]);
        }

        IMappingSetProvider inner =
            _inner
            ?? throw new InvalidOperationException("The production mapping set provider was not attached.");
        return inner.GetOrCreateAsync(key, cancellationToken);
    }

    /// <summary>Wraps the host's registered provider.</summary>
    public void Register(IServiceCollection services) =>
        ProjectionServiceDecoration.Decorate<IMappingSetProvider>(
            services,
            inner =>
            {
                _inner = inner;
                return this;
            }
        );
}

internal static class ProjectionServiceDecoration
{
    /// <summary>
    /// Replaces the last registration of <typeparamref name="TService"/> with a singleton built from
    /// the instance that registration would have produced.
    /// </summary>
    public static void Decorate<TService>(IServiceCollection services, Func<TService, TService> decorate)
        where TService : class
    {
        ServiceDescriptor registered = services.Last(descriptor =>
            descriptor.ServiceType == typeof(TService)
        );
        services.RemoveAll<TService>();
        services.AddSingleton(serviceProvider =>
            decorate(
                (TService)(
                    registered.ImplementationInstance
                    ?? registered.ImplementationFactory?.Invoke(serviceProvider)
                    ?? ActivatorUtilities.CreateInstance(serviceProvider, registered.ImplementationType!)
                )
            )
        );
    }
}
