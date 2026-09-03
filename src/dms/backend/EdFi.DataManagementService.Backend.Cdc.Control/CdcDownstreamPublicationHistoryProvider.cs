// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Answers the E18 downstream-publication-history question from the durable CDC binding state
/// store: whether this deployment ever bound the DocumentCache target to a Kafka topic.
/// </summary>
/// <remarks>
/// A binding record is the deployment's durable commitment that the target's projection is
/// published downstream. It is created before the guarded <c>Disabled -&gt; Tracking</c> transition
/// and is immutable for the life of the generation, so its presence — rather than a connector's
/// current runtime state — is what makes a target ineligible for the offline read-acceleration
/// toggle. Stopping a connector or removing the target from <c>DocumentCache:Targets</c> leaves the
/// record in place, which is why neither erases the history.
///
/// Retirement does remove it, though, which is why an absent binding is not on its own proof that a
/// target was never published: retiring one target while another remains would otherwise make the
/// retired one look internal-only. Retirement writes a retirement record before deleting the binding,
/// and this provider reads those records whenever no binding names the target. A retirement that
/// names it reports <c>Historical</c>.
///
/// Only a completed, fully readable, non-empty binding listing counts as evidence. A state store that
/// cannot be listed, that holds a record this build cannot read as a binding, or that holds no
/// binding at all yields <c>Unknown</c> so the E18 gate rejects without mutation. The empty case
/// rejects because a fresh volume, a mis-mounted root, and a root pointed at another deployment all
/// list empty, so an absent binding only means something once a readable one has shown that this is
/// the populated, authoritative store. Retirements are the exception to that reasoning: a deployment
/// that has retired nothing legitimately holds none, so an empty retirement listing is an answer
/// rather than a gap. The provider never reports <c>Possible</c>: it either reads the deployment's
/// records or it does not.
///
/// The deployment key is read from raw configuration rather than through <c>CdcControlOptions</c>
/// on purpose. Those options are validated on first resolution, which the administrative host
/// defers to a <c>cdc</c> verb; resolving them here would make every DocumentCache command fail on
/// CDC configuration it does not otherwise use.
/// </remarks>
public sealed class CdcDownstreamPublicationHistoryProvider(
    ICdcBindingLifecycleService bindingLifecycleService,
    IConfiguration configuration,
    TimeProvider timeProvider
) : IDocumentCacheDownstreamPublicationHistoryProvider
{
    public static readonly string DeploymentKeyConfigurationPath =
        $"{CdcControlOptions.SectionName}:{nameof(CdcControlOptions.DeploymentKey)}";

    private const string EvidenceSourceName = "cdc-binding-state-store";

    private readonly ICdcBindingLifecycleService _bindingLifecycleService =
        bindingLifecycleService ?? throw new ArgumentNullException(nameof(bindingLifecycleService));
    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        cancellationToken.ThrowIfCancellationRequested();

        string? deploymentKey = _configuration[DeploymentKeyConfigurationPath];
        if (string.IsNullOrWhiteSpace(deploymentKey))
        {
            return Unknown(
                targetKey,
                currentPhysicalSourceFingerprint,
                "No CDC deployment key is configured, so no durable binding evidence can be read."
            );
        }

        CdcBindingLifecycleListResult listResult = await _bindingLifecycleService
            .ListBindingsAsync(deploymentKey, cancellationToken)
            .ConfigureAwait(false);

        if (listResult.Status != CdcControlPlaneOperationStatus.Succeeded)
        {
            return Unknown(
                targetKey,
                currentPhysicalSourceFingerprint,
                "The CDC binding state store could not be listed, so downstream publication history is unavailable."
            );
        }

        if (listResult.States.Count == 0)
        {
            // A store holding nothing is indistinguishable from one this deployment has never
            // written to: a fresh volume, a mis-mounted root, or a root pointed at the wrong
            // deployment all list empty. Reading that as proof would admit the destructive commands
            // on exactly the evidence the deployment failed to supply, so at least one readable
            // binding must establish that the store is the populated, authoritative one before an
            // absent binding means anything.
            return Unknown(
                targetKey,
                currentPhysicalSourceFingerprint,
                "The CDC binding state store holds no bindings for this deployment, so it cannot be distinguished from an unwritten store."
            );
        }

        string bindingTenantKey =
            CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(targetKey.TenantKey) ?? targetKey.TenantKey;
        string bindingDataStoreId = targetKey.DataStoreId.ToString(CultureInfo.InvariantCulture);

        CdcBinding? matchedBinding = null;
        foreach (CdcBindingStateContract state in listResult.States)
        {
            if (state.State == CdcBindingState.BindingMismatch || state.Binding is null)
            {
                // One unreadable record makes the whole listing inconclusive: it may be the very
                // binding that would disqualify this target.
                return Unknown(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    "The CDC binding state store holds a record that could not be read as a binding."
                );
            }

            if (!IsSameTarget(state.Binding, bindingTenantKey, bindingDataStoreId))
            {
                continue;
            }

            // A binding on the currently resolved physical source is the strongest evidence, so it
            // wins over an earlier generation bound to a source this target no longer uses.
            if (IsSameSource(state.Binding, currentPhysicalSourceFingerprint))
            {
                matchedBinding = state.Binding;
                break;
            }

            matchedBinding ??= state.Binding;
        }

        if (matchedBinding is null)
        {
            // No live binding names this target, which is not yet the same as never having published
            // it: retirement removes the binding record it retires. The retirement records are the
            // durable trace of that, and they must be read before an absent binding may be read as
            // proof of an unpublished target.
            CdcRetirementListResult retirementsResult = await _bindingLifecycleService
                .ListRetirementsAsync(deploymentKey, cancellationToken)
                .ConfigureAwait(false);

            if (retirementsResult.Status != CdcControlPlaneOperationStatus.Succeeded)
            {
                return Unknown(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    "The CDC retirement records could not be listed, so downstream publication history is unavailable."
                );
            }

            CdcRetirement? matchedRetirement = retirementsResult.Retirements.FirstOrDefault(retirement =>
                IsSameTarget(retirement, bindingTenantKey, bindingDataStoreId)
            );

            if (matchedRetirement is not null)
            {
                return Observation(
                    targetKey,
                    currentPhysicalSourceFingerprint,
                    DocumentCacheDownstreamPublicationStatus.Historical,
                    matchedRetirement.Generation.ToString(CultureInfo.InvariantCulture),
                    "A retired CDC binding published this target downstream, so its projection is not internal-only."
                );
            }

            return Observation(
                targetKey,
                currentPhysicalSourceFingerprint,
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                evidenceGenerationIdentifier: null,
                "The deployment's CDC bindings name other targets, none binds this one, and none was ever retired for it, so its projection was never published downstream."
            );
        }

        bool sameSource = IsSameSource(matchedBinding, currentPhysicalSourceFingerprint);
        return Observation(
            targetKey,
            currentPhysicalSourceFingerprint,
            sameSource
                ? DocumentCacheDownstreamPublicationStatus.Active
                : DocumentCacheDownstreamPublicationStatus.Historical,
            matchedBinding.Generation.ToString(CultureInfo.InvariantCulture),
            sameSource
                ? "A CDC binding publishes this target's current physical source downstream."
                : "A CDC binding published this target under an earlier physical source."
        );
    }

    private static bool IsSameTarget(
        CdcBinding binding,
        string bindingTenantKey,
        string bindingDataStoreId
    ) =>
        // The tenant key is compared case-insensitively because DocumentCacheTargetKey treats
        // tenant keys that way. A case difference must not read as "no binding".
        string.Equals(binding.TenantKey, bindingTenantKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(binding.DataStoreId, bindingDataStoreId, StringComparison.Ordinal);

    private static bool IsSameTarget(
        CdcRetirement retirement,
        string bindingTenantKey,
        string bindingDataStoreId
    ) =>
        string.Equals(retirement.TenantKey, bindingTenantKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(retirement.DataStoreId, bindingDataStoreId, StringComparison.Ordinal);

    private static bool IsSameSource(
        CdcBinding binding,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint
    ) =>
        currentPhysicalSourceFingerprint is not null
        && string.Equals(
            binding.PhysicalSourceFingerprint,
            currentPhysicalSourceFingerprint.Value,
            StringComparison.Ordinal
        );

    private DocumentCacheDownstreamPublicationHistoryObservation Unknown(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        string diagnosticText
    ) =>
        Observation(
            targetKey,
            currentPhysicalSourceFingerprint,
            DocumentCacheDownstreamPublicationStatus.Unknown,
            evidenceGenerationIdentifier: null,
            diagnosticText
        );

    /// <summary>
    /// Reports the currently resolved fingerprint on every observation, including the rejecting
    /// ones. The observation describes what was found for this target at this source, and the
    /// status is what carries the finding; withholding the fingerprint would make the E18 evaluator
    /// reject for a source mismatch it did not actually observe.
    /// </summary>
    private DocumentCacheDownstreamPublicationHistoryObservation Observation(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        DocumentCacheDownstreamPublicationStatus status,
        string? evidenceGenerationIdentifier,
        string diagnosticText
    ) =>
        new(
            targetKey,
            currentPhysicalSourceFingerprint,
            status,
            EvidenceSourceName,
            evidenceGenerationIdentifier,
            _timeProvider.GetUtcNow(),
            diagnosticText
        );
}
