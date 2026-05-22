// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal sealed class StoredRelationshipAuthorizationOrchestrator(
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null
)
{
    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));

    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<StoredRelationshipAuthorizationBoundary> ResolveAsync(
        RelationalWriteExecutorRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (
            request.StoredRelationshipAuthorization
            is null
                or RelationshipAuthorizationResult.NoAuthorizationRequired
                or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired
        )
        {
            return new StoredRelationshipAuthorizationBoundary(
                request,
                null,
                PostTargetReevaluation: PostTargetReevaluationMode.Allowed,
                ExistingTargetLocked: false
            );
        }

        if (TryBuildPutStoredNoClaimsAuthorizationResult(request) is { } noClaimsResult)
        {
            return new StoredRelationshipAuthorizationBoundary(
                request,
                noClaimsResult,
                PostTargetReevaluation: PostTargetReevaluationMode.Allowed,
                ExistingTargetLocked: false
            );
        }

        var targetResolution = await ResolveTargetAsync(request, writeSession, cancellationToken)
            .ConfigureAwait(false);
        var postTargetReevaluation =
            request.TargetRequest is RelationalWriteTargetRequest.Post
                ? PostTargetReevaluationMode.Suppressed
                : PostTargetReevaluationMode.Allowed;

        if (targetResolution.ImmediateResult is not null)
        {
            return new StoredRelationshipAuthorizationBoundary(
                request,
                targetResolution.ImmediateResult,
                postTargetReevaluation,
                targetResolution.ExistingTargetLocked
            );
        }

        var executionRequest = targetResolution.ExecutionRequest;

        if (
            executionRequest.TargetContext is not RelationalWriteTargetContext.ExistingDocument existingTarget
        )
        {
            return new StoredRelationshipAuthorizationBoundary(
                executionRequest,
                null,
                postTargetReevaluation,
                targetResolution.ExistingTargetLocked
            );
        }

        var authorizationResult = await AuthorizeAsync(
                executionRequest,
                existingTarget,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return authorizationResult is null
            ? new StoredRelationshipAuthorizationBoundary(
                executionRequest,
                null,
                postTargetReevaluation,
                targetResolution.ExistingTargetLocked
            )
            : new StoredRelationshipAuthorizationBoundary(
                executionRequest,
                authorizationResult,
                postTargetReevaluation,
                targetResolution.ExistingTargetLocked
            );
    }

    private static RelationalWriteExecutorResult? TryBuildPutStoredNoClaimsAuthorizationResult(
        RelationalWriteExecutorRequest request
    ) =>
        request.OperationKind is RelationalWriteOperationKind.Put
        && request.StoredRelationshipAuthorization is RelationshipAuthorizationResult.NoClaims noClaims
            ? RelationalWriteExecutorResults.BuildNoClaimsStoredRelationshipAuthorizationResult(
                request.OperationKind,
                noClaims
            )
            : null;

    private async Task<StoredRelationshipAuthorizationTargetResolution> ResolveTargetAsync(
        RelationalWriteExecutorRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (request.TargetRequest is RelationalWriteTargetRequest.Post postTargetRequest)
        {
            return await ResolvePostTargetAsync(request, postTargetRequest, writeSession, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.TargetContext is not RelationalWriteTargetContext.ExistingDocument existingTarget)
        {
            return new StoredRelationshipAuthorizationTargetResolution(
                request,
                null,
                ExistingTargetLocked: false
            );
        }

        var lockedContentVersion = await RelationalWriteTargetLocking
            .TryLockExistingTargetAsync(
                request.MappingSet.Key.Dialect,
                existingTarget.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return lockedContentVersion is null
            ? new StoredRelationshipAuthorizationTargetResolution(
                request,
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists()),
                ExistingTargetLocked: false
            )
            : new StoredRelationshipAuthorizationTargetResolution(
                request with
                {
                    TargetContext = existingTarget with
                    {
                        ObservedContentVersion = lockedContentVersion.Value,
                    },
                },
                null,
                ExistingTargetLocked: true
            );
    }

    private async Task<StoredRelationshipAuthorizationTargetResolution> ResolvePostTargetAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetRequest.Post postTargetRequest,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        for (var attemptIndex = 0; attemptIndex < 2; attemptIndex++)
        {
            var targetLookupResult = await _targetLookupResolver
                .ResolveForPostAsync(
                    request.MappingSet,
                    request.WritePlan.Model.Resource,
                    postTargetRequest.ReferentialId,
                    postTargetRequest.CandidateDocumentUuid,
                    writeSession.Connection,
                    writeSession.Transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var targetContext = RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult);

            switch (targetContext)
            {
                case RelationalWriteTargetContext.CreateNew createNew:
                    return new StoredRelationshipAuthorizationTargetResolution(
                        request with
                        {
                            TargetContext = createNew,
                        },
                        null,
                        ExistingTargetLocked: false
                    );

                case RelationalWriteTargetContext.ExistingDocument existingTarget:
                    var lockedContentVersion = await RelationalWriteTargetLocking
                        .TryLockExistingTargetAsync(
                            request.MappingSet.Key.Dialect,
                            existingTarget.DocumentId,
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    if (lockedContentVersion is not null)
                    {
                        return new StoredRelationshipAuthorizationTargetResolution(
                            request with
                            {
                                TargetContext = existingTarget with
                                {
                                    ObservedContentVersion = lockedContentVersion.Value,
                                },
                            },
                            null,
                            ExistingTargetLocked: true
                        );
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Relational POST stored relationship authorization target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
                    );
            }
        }

        return new StoredRelationshipAuthorizationTargetResolution(
            request,
            new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict()),
            ExistingTargetLocked: false
        );
    }

    private async Task<RelationalWriteExecutorResult?> AuthorizeAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument existingTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        return request.StoredRelationshipAuthorization switch
        {
            null
            or RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired => null,

            RelationshipAuthorizationResult.NoClaims noClaims =>
                RelationalWriteExecutorResults.BuildNoClaimsStoredRelationshipAuthorizationResult(
                    request.OperationKind,
                    noClaims
                ),

            RelationshipAuthorizationResult.KnownButNotEnabled => throw new InvalidOperationException(
                "Known-but-not-enabled stored relationship authorization results must be handled by repository preflight before executor entry."
            ),

            RelationshipAuthorizationResult.SecurityConfigurationError => throw new InvalidOperationException(
                "Security-configuration stored relationship authorization results must be handled by repository preflight before executor entry."
            ),

            RelationshipAuthorizationResult.Authorized authorized => await ExecuteAsync(
                    request,
                    existingTarget,
                    authorized,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false),

            _ => throw new InvalidOperationException(
                $"Unsupported stored relationship authorization result '{request.StoredRelationshipAuthorization.GetType().Name}'."
            ),
        };
    }

    private async Task<RelationalWriteExecutorResult?> ExecuteAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument existingTarget,
        RelationshipAuthorizationResult.Authorized authorized,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (authorized.ClaimEducationOrganizationIdParameterization is null)
        {
            return RelationalWriteExecutorResults.BuildUnknownFailureResult(
                request.OperationKind,
                "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
            );
        }

        var authorizationExecutor = new SingleRecordRelationshipAuthorizationExecutor(
            writeSession.CreateCommandExecutor(),
            _relationalParameterConfigurator,
            _relationshipAuthorizationProviderFailureExtractor
        );
        var authorizationExecutionResult = await authorizationExecutor
            .ExecuteAsync(
                new SingleRecordRelationshipAuthorizationExecutionRequest(
                    request.MappingSet,
                    existingTarget.DocumentId,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization,
                    RelationalWriteExecutorResults.GetRelationshipAuthorizationAuth1Index(
                        request.OperationKind
                    )
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return authorizationExecutionResult switch
        {
            SingleRecordRelationshipAuthorizationExecutionResult.Authorized => null,
            SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                RelationalWriteExecutorResults.BuildRelationshipAuthorizationFailureResult(
                    request.OperationKind,
                    notAuthorized.RelationshipFailure
                ),
            SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget => request.OperationKind switch
            {
                RelationalWriteOperationKind.Post => new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureWriteConflict()
                ),
                RelationalWriteOperationKind.Put => new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureNotExists()
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.OperationKind, null),
            },
            SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                RelationalWriteExecutorResults.BuildUnknownFailureResult(
                    request.OperationKind,
                    invalidFailure.FailureMessage
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{authorizationExecutionResult.GetType().Name}'."
            ),
        };
    }

    private sealed record StoredRelationshipAuthorizationTargetResolution(
        RelationalWriteExecutorRequest ExecutionRequest,
        RelationalWriteExecutorResult? ImmediateResult,
        bool ExistingTargetLocked
    );
}

internal enum PostTargetReevaluationMode
{
    Allowed,
    Suppressed,
}

internal sealed record StoredRelationshipAuthorizationBoundary(
    RelationalWriteExecutorRequest ExecutionRequest,
    RelationalWriteExecutorResult? ImmediateResult,
    PostTargetReevaluationMode PostTargetReevaluation,
    bool ExistingTargetLocked
);
