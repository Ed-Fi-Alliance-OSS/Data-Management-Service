// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// A first phase for executor unit tests that performs the phase's observations through the fixture's
/// fakeable seams — the target lookup resolver, the session reference resolver, and the
/// current-state loader — while every decision runs through the production first phase's own policy
/// functions (missing-PUT shaping, plan selection, stored relationship classification, and the shared
/// standalone stored-authorization execution and mapping). Tests therefore script data, not policy,
/// and the composite command's emission and decoding are covered by their own fixtures and the live
/// provider tests.
/// </summary>
internal sealed class FakeSequentialRelationalWriteFirstPhase(
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    RecordingSessionReferenceResolver referenceResolver,
    IRelationalWriteCurrentStateLoader currentStateLoader,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
    Microsoft.Extensions.Logging.ILogger? logger = null
) : IRelationalWriteFirstPhase
{
    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<RelationalWriteFirstPhaseResolution> ResolveAsync(
        RelationalWriteExecutorInput input,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        var lookupExecutor = writeSession.CreateCommandExecutor();

        RelationalWriteTargetLookupResult lookupResult = input.TargetRequest switch
        {
            RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid) =>
                await targetLookupResolver.ResolveForPostAsync(
                    input.MappingSet,
                    input.WritePlan.Model.Resource,
                    referentialId,
                    candidateDocumentUuid,
                    lookupExecutor,
                    cancellationToken
                ),
            RelationalWriteTargetRequest.Put(var documentUuid) =>
                await targetLookupResolver.ResolveForPutAsync(
                    input.MappingSet,
                    input.WritePlan.Model.Resource,
                    documentUuid,
                    lookupExecutor,
                    cancellationToken
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported target request type '{input.TargetRequest.GetType().Name}'."
            ),
        };

        if (lookupResult is RelationalWriteTargetLookupResult.NotFound)
        {
            return RelationalWriteFirstPhaseResolution.Immediate(
                CompositeRelationalWriteFirstPhase.BuildMissingPutTargetResult(input)
            );
        }

        var targetContext =
            RelationalWriteSupport.TryTranslateTargetContext(lookupResult)
            ?? throw new InvalidOperationException(
                $"Unsupported target lookup result type '{lookupResult.GetType().Name}'."
            );

        var (executionRequest, planSelectionImmediateResult) =
            CompositeRelationalWriteFirstPhase.ApplyTargetAndPlanSelection(input, targetContext);

        if (planSelectionImmediateResult is not null)
        {
            return RelationalWriteFirstPhaseResolution.Immediate(
                planSelectionImmediateResult,
                CompositeRelationalWriteFirstPhase.SelectPostAction(input, targetContext)
            );
        }

        RelationalWriteLockedTarget? lockedTarget = null;
        RelationalWriteCurrentState? currentState = null;

        if (executionRequest.TargetContext is RelationalWriteTargetContext.ExistingDocument existingTarget)
        {
            lockedTarget = RelationalWriteLockedTarget.FromCaptureOutcome(
                new RelationalCompositeStatementOutcome(
                    0,
                    "capture-target",
                    new RelationalCompositeCapturedTarget(
                        existingTarget.DocumentId,
                        existingTarget.ObservedContentVersion,
                        existingTarget.DocumentUuid.Value
                    )
                ),
                writeSession
            );

            if (
                await ResolveStoredAuthorizationAsync(
                    input,
                    executionRequest,
                    existingTarget,
                    writeSession,
                    cancellationToken
                ) is
                { } storedAuthorizationResult
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(
                    storedAuthorizationResult,
                    CompositeRelationalWriteFirstPhase.SelectPostAction(input, targetContext)
                );
            }

            if (
                RelationalWriteExecutorResults.BuildMissingExistingDocumentReadPlanResult(executionRequest) is
                { } missingReadPlanResult
            )
            {
                return RelationalWriteFirstPhaseResolution.Immediate(
                    missingReadPlanResult,
                    CompositeRelationalWriteFirstPhase.SelectPostAction(input, targetContext)
                );
            }

            var includeDescriptorProjection =
                executionRequest.ProfileWriteContext is not null
                || RelationalWriteExecutionStateResolver.GetEtagPreconditionEvaluation(input)
                    is EtagPreconditionEvaluation.DeferredUntilAfterProposedAuthorization;

            currentState =
                await currentStateLoader.LoadAsync(
                    new RelationalWriteCurrentStateLoadRequest(
                        executionRequest.ExistingDocumentReadPlan!,
                        existingTarget,
                        includeDescriptorProjection
                    ),
                    writeSession,
                    cancellationToken
                )
                ?? throw new InvalidOperationException(
                    $"Current-state hydration returned no metadata for locked document id {existingTarget.DocumentId}."
                );
        }

        var resolvedReferences = referenceResolver.Resolve(
            input.ReferenceResolutionRequest,
            writeSession.CreateCommandExecutor()
        );

        return new RelationalWriteFirstPhaseResolution(
            new RelationalWriteFirstPhaseOutcome(
                executionRequest,
                lockedTarget,
                resolvedReferences,
                currentState
            ),
            null
        );
    }

    private async Task<RelationalWriteExecutorResult?> ResolveStoredAuthorizationAsync(
        RelationalWriteExecutorInput input,
        RelationalWriteExecutorRequest executionRequest,
        RelationalWriteTargetContext.ExistingDocument existingTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        // Namespace AND-composes before the relationship OR-group; production expresses that as
        // statement order, this seam as call order.
        if (executionRequest.StoredNamespaceAuthorization is { } namespaceAuthorization)
        {
            var namespaceResult = await StoredNamespaceAuthorizationExecution.ExecuteAsync(
                writeSession.CreateCommandExecutor(),
                _providerFailureExtractor,
                executionRequest.MappingSet,
                existingTarget.DocumentId,
                namespaceAuthorization,
                onNotAuthorized: failure =>
                    RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                        executionRequest.OperationKind,
                        failure
                    ),
                onInvalidAuthorizationFailure: (failureMessage, diagnostics) =>
                    RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                        executionRequest.OperationKind,
                        [failureMessage],
                        diagnostics
                    ),
                onStaleTarget: () =>
                    RelationalWriteExecutorResults.BuildStaleTargetResult(executionRequest.OperationKind),
                cancellationToken
            );

            if (namespaceResult is not null)
            {
                return namespaceResult;
            }
        }

        var relationshipPlan = CompositeRelationalWriteFirstPhase.ClassifyStoredRelationshipDisposition(
            input
        );

        switch (relationshipPlan.Disposition)
        {
            case StoredRelationshipDisposition.None:
                return null;

            case StoredRelationshipDisposition.DeferredNoClaims:
                return RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
                    executionRequest.OperationKind,
                    relationshipPlan.NoClaims!
                );

            case StoredRelationshipDisposition.Unbuildable:
                return RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    executionRequest.OperationKind,
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                );

            case StoredRelationshipDisposition.Emitted
            or StoredRelationshipDisposition.Standalone:
                return await CompositeRelationalWriteFirstPhase.ExecuteStandaloneStoredRelationshipAsync(
                    executionRequest,
                    relationshipPlan.Authorized!,
                    existingTarget.DocumentId,
                    writeSession,
                    _relationalParameterConfigurator,
                    _providerFailureExtractor,
                    logger ?? NullLogger.Instance,
                    cancellationToken
                );

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(input),
                    relationshipPlan.Disposition,
                    "Unsupported stored relationship disposition."
                );
        }
    }
}

/// <summary>
/// The sequential first phase's reference resolution: records each request and the session executor it was
/// resolved on, and resolves the references a test scripted by natural key, or fails them with a scripted
/// reason. Every other reference is missing.
/// The resolver's own lookup and classification are covered by its fixtures and the live provider tests.
/// </summary>
internal sealed class RecordingSessionReferenceResolver
{
    private readonly Dictionary<ReferenceLookupKey, NaturalKeyLookupMatch> _matches = new(
        ReferenceLookupKeyComparer.Instance
    );

    private readonly Dictionary<ReferenceLookupKey, DocumentReferenceFailureReason> _failures = new(
        ReferenceLookupKeyComparer.Instance
    );

    public List<ReferenceResolverRequest> Requests { get; } = [];

    public IRelationalCommandExecutor? CapturedCommandExecutor { get; private set; }

    public int CallCount => Requests.Count;

    public void Script(DocumentReference reference, long documentId, short resourceKeyId) =>
        _matches[KeyOf(reference.ResourceInfo, reference.DocumentIdentity)] = new(
            documentId,
            null,
            resourceKeyId
        );

    public void Script(DocumentReference reference, DocumentReferenceFailureReason failureReason) =>
        _failures[KeyOf(reference.ResourceInfo, reference.DocumentIdentity)] = failureReason;

    public void Script(
        DescriptorReference reference,
        int descriptorId,
        long documentId,
        short resourceKeyId
    ) =>
        _matches[KeyOf(reference.ResourceInfo, reference.DocumentIdentity)] = new(
            documentId,
            descriptorId,
            resourceKeyId
        );

    public ResolvedReferenceSet Resolve(ReferenceResolverRequest request, IRelationalCommandExecutor executor)
    {
        Requests.Add(request);
        CapturedCommandExecutor = executor;

        Dictionary<JsonPath, ResolvedDocumentReference> documentsByPath = [];
        List<DocumentReferenceFailure> documentFailures = [];

        foreach (var reference in request.DocumentReferences)
        {
            var key = KeyOf(reference.ResourceInfo, reference.DocumentIdentity);

            if (_matches.TryGetValue(key, out var match))
            {
                documentsByPath.Add(reference.Path, new(reference, match.DocumentId, match.ResourceKeyId));
            }
            else
            {
                documentFailures.Add(
                    DocumentReferenceFailure.From(
                        reference,
                        _failures.GetValueOrDefault(key, DocumentReferenceFailureReason.Missing)
                    )
                );
            }
        }

        Dictionary<JsonPath, ResolvedDescriptorReference> descriptorsByPath = [];
        List<DescriptorReferenceFailure> descriptorFailures = [];

        foreach (var reference in request.DescriptorReferences)
        {
            if (
                _matches.TryGetValue(KeyOf(reference.ResourceInfo, reference.DocumentIdentity), out var match)
                && match.DescriptorId is { } descriptorId
            )
            {
                descriptorsByPath.Add(
                    reference.Path,
                    new(reference, descriptorId, match.DocumentId, match.ResourceKeyId)
                );
            }
            else
            {
                descriptorFailures.Add(
                    DescriptorReferenceFailure.From(reference, DescriptorReferenceFailureReason.Missing)
                );
            }
        }

        return new ResolvedReferenceSet(
            documentsByPath,
            descriptorsByPath,
            new ResolvedDocumentReferenceMapFactory().Create(documentsByPath.Values),
            documentFailures,
            descriptorFailures
        );
    }

    private static ReferenceLookupKey KeyOf(BaseResourceInfo resourceInfo, DocumentIdentity identity) =>
        new(RelationalWriteSupport.ToQualifiedResourceName(resourceInfo), identity);
}
