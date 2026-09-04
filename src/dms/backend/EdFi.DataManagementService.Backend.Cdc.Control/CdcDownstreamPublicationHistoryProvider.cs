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
/// Evidence is a record that names the target: a readable binding, or a retirement. Nothing else is
/// evidence. A state store that cannot be listed, that holds a record this build cannot read as a
/// binding, or that simply holds no record naming the requested target all yield <c>Unknown</c>, so
/// the E18 gate rejects without mutation.
///
/// The absence of a record is deliberately not read as proof that the target was never published.
/// Absence would prove that only if this store were known to be a complete history of every target
/// for the deployment's lifetime, and nothing establishes that: a listing proves which records exist
/// now, not that none was ever lost, and a restored, copied, partially migrated, or mis-mounted root
/// answers a listing exactly as an intact one does. Reading absence as proof would let a root that
/// happens to hold one unrelated binding authorize the destructive E18 commands for a target it says
/// nothing about, so <c>InternalOnly</c> is reported only from a record that names the target — which,
/// there being no durable record of a non-event, means it is not currently reachable. That is the
/// same answer the default provider gives, and it leaves the E18 destructive commands rejected as
/// they already were; what this provider adds over the default is the positive proof of <c>Active</c>
/// and <c>Historical</c>, which is what actually keeps a published target out of those commands.
///
/// A retirement is consulted even when no binding is live, because retiring the deployment's only
/// binding leaves the retirement as the sole trace of what was published. The provider never reports
/// <c>Possible</c>: it either reads a record naming the target or it does not.
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
            // No live binding names this target, which is not the same as never having published it:
            // retirement removes the binding record it retires, and the retirement record is the
            // durable trace of what that generation published. It is read whether the listing is empty
            // or names other targets, because retiring the deployment's only binding empties the
            // binding tree and leaves the retirement as the sole evidence.
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

            // No record names this target. That is not proof the target was never published: it would
            // be only if this store were known to hold every record the deployment has ever written,
            // and a listing establishes what exists now rather than what was never lost. A restored,
            // copied, partially migrated, or mis-mounted root answers identically to an intact one,
            // and other targets' bindings say nothing about this one. Proving internal-only from
            // absence needs positive durable evidence about this target - a record initialized for it
            // and irreversibly transitioned when publication occurs - which no contract in this
            // deployment produces, so the honest answer is that the history is unknown.
            return Unknown(
                targetKey,
                currentPhysicalSourceFingerprint,
                "No CDC binding or retirement record names this target, and an absent record is not evidence that its projection was never published downstream."
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
