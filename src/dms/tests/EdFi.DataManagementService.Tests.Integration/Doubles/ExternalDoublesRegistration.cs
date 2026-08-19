// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Replaces every DI binding the API integration harness needs to fake (auth/CMS/
/// application-context/profile-catalog/DMS-instance) and leaves the rest of the
/// DMS HTTP pipeline real. Called from the test base's WebApplicationFactory
/// ConfigureServices override.
/// </summary>
internal static class ExternalDoublesRegistration
{
    /// <param name="clientNamespacePrefixes">
    /// Namespace prefixes the fake JWT carries for NamespaceBased scenarios. Empty by default.
    /// </param>
    /// <param name="providerFailureTransform">
    /// When supplied, replaces the authorization provider-failure extractor with a recording double that
    /// rewrites the extracted payload after the real provider exception was raised. Null leaves the production
    /// extraction in place, which is the historical behavior for every other scenario.
    /// </param>
    /// <param name="providerFailureRecorder">
    /// Collects the real provider exceptions observed while <paramref name="providerFailureTransform"/> is
    /// active, so a scenario can assert genuine <c>SqlException</c> provenance.
    /// </param>
    public static void RegisterAll(
        IServiceCollection services,
        FixtureContext fixture,
        string leasedConnectionString,
        IClaimSetProvider claimSetProvider,
        IReadOnlyList<long> clientEducationOrganizationIds,
        IReadOnlyList<string>? clientNamespacePrefixes = null,
        Func<
            RelationshipAuthorizationProviderFailure,
            RelationshipAuthorizationProviderFailure
        >? providerFailureTransform = null,
        ApiIntegrationProviderFailureRecorder? providerFailureRecorder = null,
        RelationalProviderToken? relationalProviderToken = null,
        DocumentCacheReadAcquisitionFailureRecorder? documentCacheReadAcquisitionFailureRecorder = null,
        DocumentCacheDirectFillTimeoutRecorder? documentCacheDirectFillTimeoutRecorder = null,
        DocumentCacheReadTelemetryRecorder? documentCacheReadTelemetryRecorder = null,
        IReadOnlyList<string>? assignedProfileNames = null
    )
    {
        if (
            documentCacheReadAcquisitionFailureRecorder is not null
            && documentCacheDirectFillTimeoutRecorder is not null
        )
        {
            throw new InvalidOperationException(
                "Cache read acquisition failure and direct-fill timeout doubles cannot be active together."
            );
        }

        services.RemoveAll<IJwtValidationService>();
        services.RemoveAll<IConfigurationManager<OpenIdConnectConfiguration>>();
        services.RemoveAll<IClaimSetProvider>();
        services.RemoveAll<IApplicationContextProvider>();
        services.RemoveAll<IDataStoreProvider>();
        services.RemoveAll<IProfileCmsProvider>();
        services.RemoveAll<IStartupProcessExit>();

        services.AddSingleton<IJwtValidationService>(
            FakeJwtValidationService.Allowing(
                ExternalDoublesConstants.SmokeToken,
                ExternalDoublesConstants.SmokeClientId,
                clientEducationOrganizationIds,
                clientNamespacePrefixes
            )
        );

        if (providerFailureTransform is not null && providerFailureRecorder is not null)
        {
            // The backend registers the production extractor with TryAdd before ConfigureServices runs, so the
            // existing registration has to be removed rather than merely attempted.
            services.RemoveAll<IRelationshipAuthorizationProviderFailureExtractor>();
            services.AddSingleton(providerFailureRecorder);
            services.AddScoped<IRelationshipAuthorizationProviderFailureExtractor>(
                serviceProvider => new RecordingProviderFailureExtractor(
                    serviceProvider.GetRequiredService<ApiIntegrationProviderFailureRecorder>(),
                    providerFailureTransform
                )
            );
        }

        services.AddSingleton(FakeOidcConfigurationManager.Stable());
        services.AddSingleton(claimSetProvider);
        services.AddSingleton<IApplicationContextProvider>(FakeApplicationContextProvider.Stable());
        services.AddSingleton<IDataStoreProvider>(
            FakeDataStoreProvider.WithSingleInstance(
                id: ExternalDoublesConstants.StableDataStoreId,
                connectionString: leasedConnectionString,
                relationalProviderToken
            )
        );
        if (relationalProviderToken is not null)
        {
            services.RemoveAll<IDocumentCacheTargetRegistry>();
            services.AddSingleton<IDocumentCacheTargetRegistry>(
                serviceProvider => new DocumentCacheTargetRegistry(
                    serviceProvider.GetRequiredService<IDataStoreProvider>(),
                    serviceProvider.GetRequiredService<IDocumentCacheTargetContextBuilder>(),
                    serviceProvider.GetRequiredService<IOptions<DocumentCacheOptions>>(),
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    serviceProvider.GetRequiredService<ILogger<DocumentCacheTargetRegistry>>()
                )
            );
        }
        if (documentCacheReadAcquisitionFailureRecorder is not null)
        {
            services.RemoveAll<IDocumentCacheReadLookupAdapter>();
            services.RemoveAll<IDocumentCacheReadTelemetry>();
            services.AddSingleton(documentCacheReadAcquisitionFailureRecorder);
            services.AddScoped<
                IDocumentCacheReadLookupAdapter,
                AcquisitionFailureDocumentCacheReadLookupAdapter
            >();
            services.AddSingleton<IDocumentCacheReadTelemetry, RecordingDocumentCacheReadTelemetry>();
        }
        if (documentCacheDirectFillTimeoutRecorder is not null)
        {
            services.RemoveAll<IDocumentCacheMaterializer>();
            services.RemoveAll<IDocumentCacheReadTelemetry>();
            services.AddSingleton(documentCacheDirectFillTimeoutRecorder);
            services.AddScoped<IDocumentCacheMaterializer, TimingOutDocumentCacheMaterializer>();
            services.AddSingleton<
                IDocumentCacheReadTelemetry,
                DirectFillTimeoutRecordingDocumentCacheReadTelemetry
            >();
        }
        if (documentCacheReadTelemetryRecorder is not null)
        {
            services.RemoveAll<IDocumentCacheReadTelemetry>();
            services.AddSingleton(documentCacheReadTelemetryRecorder);
            services.AddSingleton<IDocumentCacheReadTelemetry, TelemetryOnlyDocumentCacheReadTelemetry>();
        }
        services.AddSingleton<IProfileCmsProvider>(
            FakeProfileCmsProvider.FromFixture(fixture, assignedProfileNames)
        );
        services.AddSingleton<IStartupProcessExit, NonExitingStartupProcessExit>();
    }
}
