// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Resolves the data store an education-organization projection request names, within the
/// request's tenant and route context, and records it as the request's selected data store.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ResolveDataStoreMiddleware"/>, the store comes from the <c>dataStoreId</c>
/// parameter, not from the client's data-store assignment: a projection credential has none. The
/// checks run in a fixed order and the first failing one answers:
/// </para>
/// <list type="number">
/// <item>The store is looked up in the tenant's cached catalog. On a miss the catalog is reloaded
/// from the Configuration Service exactly once; a failed reload is 503 <c>service-unavailable</c>,
/// never a 404, because an outage says nothing about whether the store exists.</item>
/// <item>A store that is still absent, or whose route context does not match the request's route
/// qualifiers, is 404 <c>target-not-found</c> with one body, so the response does not say which.</item>
/// <item>An unrecognized provider, or a recognized one other than the dialect this deployment
/// registered, is 409 <c>target-provider-unsupported</c>. A legacy store with no provider token is
/// served by the registered dialect, which is how ordinary routing already treats it.</item>
/// <item>A store with no connection string is 503 <c>service-configuration-error</c>.</item>
/// </list>
/// <para>
/// Only the data store id, the sanitized tenant and correlation id, the state reached and exception
/// type names are logged; never an exception, its message, a store name or a connection string.
/// </para>
/// </remarks>
internal class ResolveEducationOrganizationProjectionTargetMiddleware(
    IDataStoreProvider _dataStoreProvider,
    IEnumerable<IRuntimeMappingSetCompiler> _runtimeCompilers,
    ILogger<ResolveEducationOrganizationProjectionTargetMiddleware> _logger
) : IPipelineStep
{
    /// <summary>
    /// The dialect this deployment serves, from the registered runtime mapping-set compiler, the same
    /// source <see cref="ResolveMappingSetMiddleware"/> uses. <c>AppSettings:Datastore</c> is
    /// deliberately not parsed here: it spells SQL Server <c>mssql</c>, the catalog spells it
    /// <c>sqlserver</c>, and the registered compiler is what actually serves the request.
    /// </summary>
    private readonly SqlDialect? _registeredDialect = _runtimeCompilers.FirstOrDefault()?.Dialect;

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        FrontendRequest request = requestInfo.FrontendRequest;
        CancellationToken cancellationToken = requestInfo.RequestCancellationToken;

        EducationOrganizationProjectionRequest projectionRequest =
            requestInfo.EducationOrganizationProjectionRequest
            ?? throw new InvalidOperationException(
                "The projection request was not parsed before target resolution. "
                    + "ParseEducationOrganizationProjectionRequestMiddleware must run first."
            );

        long dataStoreId = projectionRequest.DataStoreId;

        try
        {
            await _dataStoreProvider.RefreshInstancesIfExpiredAsync(request.Tenant, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // As in ResolveDataStoreMiddleware, a failed TTL refresh keeps the cached catalog. Only the
            // exception type is logged: a refresh failure's message can quote the Configuration
            // Service's address and response.
#pragma warning disable S6667
            _logger.LogWarning(
                "Projection target catalog refresh failed with {ExceptionType} for data store {DataStoreId}, tenant {Tenant}; using the cached catalog - {TraceId}",
                ex.GetType().Name,
                dataStoreId,
                SanitizedTenant(request),
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );
#pragma warning restore S6667
        }

        DataStore? store = _dataStoreProvider.GetById(dataStoreId, request.Tenant);

        if (store is null)
        {
            try
            {
                await _dataStoreProvider.LoadDataStores(request.Tenant, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
#pragma warning disable S6667
                _logger.LogError(
                    "Projection target catalog reload failed with {ExceptionType} for data store {DataStoreId}, tenant {Tenant} - {TraceId}",
                    ex.GetType().Name,
                    dataStoreId,
                    SanitizedTenant(request),
                    LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
                );
#pragma warning restore S6667

                requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.ServiceUnavailable(
                    request.TraceId
                );
                return;
            }

            store = _dataStoreProvider.GetById(dataStoreId, request.Tenant);
        }

        if (store is null || !RouteContextMatcher.IsMatch(store.RouteContext, request.RouteQualifiers))
        {
            _logger.LogInformation(
                "Projection target {TargetState} for data store {DataStoreId}, tenant {Tenant} - {TraceId}",
                store is null ? "NotFound" : "RouteContextMismatch",
                dataStoreId,
                SanitizedTenant(request),
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.TargetNotFound,
                request.TraceId
            );
            return;
        }

        // Unreachable in a deployment that starts: a relational backend registers its compiler. Thrown
        // rather than answered, so a composition defect is never reported as a client-visible state.
        SqlDialect registeredDialect =
            _registeredDialect
            ?? throw new InvalidOperationException(
                "No runtime mapping set compiler is registered, so the deployment's dialect is unknown."
            );

        if (!IsServedBy(store, registeredDialect))
        {
            _logger.LogWarning(
                "Projection target provider unsupported for data store {DataStoreId}, tenant {Tenant}: provider metadata {ProviderMetadataStatus}, registered dialect {RegisteredDialect} - {TraceId}",
                dataStoreId,
                SanitizedTenant(request),
                store.RelationalProviderMetadataStatus,
                registeredDialect,
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.TargetProviderUnsupported,
                request.TraceId
            );
            return;
        }

        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            _logger.LogError(
                "Projection target data store {DataStoreId}, tenant {Tenant} has no connection string configured - {TraceId}",
                dataStoreId,
                SanitizedTenant(request),
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.ServiceConfigurationError,
                request.TraceId
            );
            return;
        }

        requestInfo
            .ScopedServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(store);

        await next();
    }

    /// <summary>
    /// Whether this deployment serves the store's provider. <c>Missing</c> is a legacy store with no
    /// token and is served by the registered dialect; <c>Unknown</c> is a token the catalog could not
    /// normalize; <c>Supported</c> means only that the token was recognized, so it is compared.
    /// </summary>
    private static bool IsServedBy(DataStore store, SqlDialect registeredDialect) =>
        store.RelationalProviderMetadataStatus switch
        {
            RelationalProviderMetadataStatus.Missing => true,
            RelationalProviderMetadataStatus.Supported => DialectOf(store.RelationalProviderToken)
                == registeredDialect,
            _ => false,
        };

    /// <summary>The catalog vocabulary is kept: <c>postgresql</c> and <c>sqlserver</c>.</summary>
    private static SqlDialect? DialectOf(RelationalProviderToken? token) =>
        token?.Value switch
        {
            RelationalProviderToken.PostgresqlValue => SqlDialect.Pgsql,
            RelationalProviderToken.SqlServerValue => SqlDialect.Mssql,
            _ => null,
        };

    private static string SanitizedTenant(FrontendRequest request) =>
        LoggingSanitizer.SanitizeInternalValueForLogging(request.Tenant ?? "(none)");
}
