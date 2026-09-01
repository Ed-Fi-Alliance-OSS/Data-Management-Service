// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

internal static class DocumentCacheQualificationServiceOverrides
{
    public static void UseInternalOnlyDocumentCacheDownstreamHistory(this IServiceCollection services)
    {
        services.RemoveAll<IDocumentCacheDownstreamPublicationHistoryProvider>();
        services.AddSingleton<
            IDocumentCacheDownstreamPublicationHistoryProvider,
            InternalOnlyDocumentCacheDownstreamPublicationHistoryProvider
        >();
    }

    private sealed class InternalOnlyDocumentCacheDownstreamPublicationHistoryProvider(
        TimeProvider timeProvider
    ) : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        private readonly TimeProvider _timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(targetKey);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new DocumentCacheDownstreamPublicationHistoryObservation(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    DocumentCacheDownstreamPublicationStatus.InternalOnly,
                    evidenceSource: "document-cache-performance-harness",
                    evidenceGenerationIdentifier: "DMS-1317-representative-qualification",
                    _timeProvider.GetUtcNow(),
                    "Representative qualification runs against the harness-owned internal-only test API."
                )
            );
        }
    }
}
