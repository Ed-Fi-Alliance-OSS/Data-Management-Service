// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

internal sealed class StoredRelationshipAuthorizationOrchestrator(
    IRelationalWriteTargetLookupResolver targetLookupResolver,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null,
    ILogger? relationshipAuthorizationLogger = null
)
{
    private readonly IRelationalWriteTargetLookupResolver _targetLookupResolver =
        targetLookupResolver ?? throw new ArgumentNullException(nameof(targetLookupResolver));

    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;
    private readonly ILogger _relationshipAuthorizationLogger =
        relationshipAuthorizationLogger ?? NullLogger.Instance;

    public async Task<StoredRelationshipAuthorizationBoundary> ResolveAsync(
        RelationalWriteExecutorRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (request.PostRelationshipAuthorizationPlans is not null)
        {
            return await ResolvePostRelationshipAuthorizationPlansAsync(
                    request,
                    request.PostRelationshipAuthorizationPlans,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var storedRelationshipRequiresAuthorization =
            request.StoredRelationshipAuthorization
            is not (
                null
                or RelationshipAuthorizationResult.NoAuthorizationRequired
                or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired
            );

        if (!storedRelationshipRequiresAuthorization && request.StoredNamespaceAuthorization is null)
        {
            return new StoredRelationshipAuthorizationBoundary(
                request,
                null,
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

    private async Task<StoredRelationshipAuthorizationBoundary> ResolvePostRelationshipAuthorizationPlansAsync(
        RelationalWriteExecutorRequest request,
        PostRelationshipAuthorizationPlans postRelationshipAuthorizationPlans,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (request.TargetRequest is not RelationalWriteTargetRequest.Post postTargetRequest)
        {
            throw new InvalidOperationException(
                "POST relationship authorization plan selection requires a POST target request."
            );
        }

        var targetResolution = await ResolvePostTargetAsync(
                request,
                postTargetRequest,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        const PostTargetReevaluationMode postTargetReevaluation = PostTargetReevaluationMode.Suppressed;

        if (targetResolution.ImmediateResult is not null)
        {
            return new StoredRelationshipAuthorizationBoundary(
                request,
                targetResolution.ImmediateResult,
                postTargetReevaluation,
                targetResolution.ExistingTargetLocked
            );
        }

        var executionRequest = targetResolution.ExecutionRequest with
        {
            PostRelationshipAuthorizationPlans = null,
        };

        switch (executionRequest.TargetContext)
        {
            case RelationalWriteTargetContext.CreateNew:
                return ResolveCreateNewPostRelationshipAuthorizationPlan(
                    executionRequest,
                    postRelationshipAuthorizationPlans,
                    postTargetReevaluation,
                    targetResolution.ExistingTargetLocked
                );

            case RelationalWriteTargetContext.ExistingDocument existingTarget:
                return await ResolveExistingPostRelationshipAuthorizationPlanAsync(
                        executionRequest,
                        postRelationshipAuthorizationPlans,
                        existingTarget,
                        postTargetReevaluation,
                        targetResolution.ExistingTargetLocked,
                        writeSession,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            default:
                throw new InvalidOperationException(
                    $"POST relationship authorization plan selection does not support target context '{executionRequest.TargetContext.GetType().Name}'."
                );
        }
    }

    private static StoredRelationshipAuthorizationBoundary ResolveCreateNewPostRelationshipAuthorizationPlan(
        RelationalWriteExecutorRequest request,
        PostRelationshipAuthorizationPlans postRelationshipAuthorizationPlans,
        PostTargetReevaluationMode postTargetReevaluation,
        bool existingTargetLocked
    )
    {
        if (postRelationshipAuthorizationPlans.CreateNewImmediateResult is not null)
        {
            return new StoredRelationshipAuthorizationBoundary(
                request,
                postRelationshipAuthorizationPlans.CreateNewImmediateResult,
                postTargetReevaluation,
                existingTargetLocked
            );
        }

        return new StoredRelationshipAuthorizationBoundary(
            request with
            {
                StoredRelationshipAuthorization = null,
                ProposedRelationshipAuthorization =
                    postRelationshipAuthorizationPlans.CreateNewProposedRelationshipAuthorization,
            },
            null,
            postTargetReevaluation,
            existingTargetLocked
        );
    }

    private async Task<StoredRelationshipAuthorizationBoundary> ResolveExistingPostRelationshipAuthorizationPlanAsync(
        RelationalWriteExecutorRequest request,
        PostRelationshipAuthorizationPlans postRelationshipAuthorizationPlans,
        RelationalWriteTargetContext.ExistingDocument existingTarget,
        PostTargetReevaluationMode postTargetReevaluation,
        bool existingTargetLocked,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var existingResourcePlan = postRelationshipAuthorizationPlans.ExistingResourcePlan;
        var executionRequest = request with
        {
            StoredRelationshipAuthorization = existingResourcePlan.StoredValues,
            ProposedRelationshipAuthorization = GetExistingResourceProposedAuthorization(
                existingResourcePlan
            ),
        };
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
                existingTargetLocked
            )
            : new StoredRelationshipAuthorizationBoundary(
                executionRequest,
                authorizationResult,
                postTargetReevaluation,
                existingTargetLocked
            );
    }

    private static RelationshipAuthorizationResult.Authorized? GetExistingResourceProposedAuthorization(
        RelationshipAuthorizationUpdatePlan existingResourcePlan
    ) =>
        existingResourcePlan.ProposedValues switch
        {
            RelationshipAuthorizationResult.Authorized authorized => authorized,
            RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired => null,
            RelationshipAuthorizationResult.NoClaims => null,
            RelationshipAuthorizationResult.KnownButNotEnabled => throw new InvalidOperationException(
                "Known-but-not-enabled POST relationship authorization results must be handled by repository preflight before executor entry."
            ),
            RelationshipAuthorizationResult.SecurityConfigurationError => throw new InvalidOperationException(
                "Security-configuration POST relationship authorization results must be handled by repository preflight before executor entry."
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported existing-resource POST proposed relationship authorization result '{existingResourcePlan.ProposedValues.GetType().Name}'."
            ),
        };

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
        // Namespace AND-composes before the relationship OR group; both run against the one locked
        // target and before any precondition result.
        var namespaceResult = await AuthorizeStoredNamespaceAsync(
                request,
                existingTarget,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (namespaceResult is not null)
        {
            return namespaceResult;
        }

        return request.StoredRelationshipAuthorization switch
        {
            null
            or RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired => null,

            RelationshipAuthorizationResult.NoClaims noClaims =>
                RelationalWriteExecutorResults.BuildNoClaimsRelationshipAuthorizationResult(
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

    private async Task<RelationalWriteExecutorResult?> AuthorizeStoredNamespaceAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument existingTarget,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var namespaceAuthorization = request.StoredNamespaceAuthorization;

        if (namespaceAuthorization is null)
        {
            return null;
        }

        return await StoredNamespaceAuthorizationExecution
            .ExecuteAsync(
                writeSession.CreateCommandExecutor(),
                _relationshipAuthorizationProviderFailureExtractor,
                request.MappingSet,
                existingTarget.DocumentId,
                namespaceAuthorization,
                onNotAuthorized: failure =>
                    RelationalWriteExecutorResults.BuildNamespaceAuthorizationFailureResult(
                        request.OperationKind,
                        failure
                    ),
                onInvalidAuthorizationFailure: (failureMessage, diagnostics) =>
                    RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                        request.OperationKind,
                        [failureMessage],
                        diagnostics
                    ),
                onStaleTarget: () =>
                    RelationalWriteExecutorResults.BuildStaleTargetResult(request.OperationKind),
                cancellationToken
            )
            .ConfigureAwait(false);
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
            _relationshipAuthorizationProviderFailureExtractor,
            _relationshipAuthorizationLogger
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
                    ),
                    authorized.ExecutableShape
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
                RelationalWriteExecutorResults.BuildSecurityConfigurationFailureResult(
                    request.OperationKind,
                    [invalidFailure.FailureMessage],
                    invalidFailure.Diagnostics
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

/// <summary>
/// Runs the stored namespace authorization check for an already-resolved target against a session
/// command executor and maps the four-case <see cref="NamespaceAuthorizationExecutionResult"/> onto a
/// caller-specific result. The authorized case is always a null result; the three failure cases
/// (not-authorized, invalid-authorization, stale-target) are mapped by the supplied factories, so every
/// call site shares one execution shape and a new execution result case forces a single edit here.
/// </summary>
internal static class StoredNamespaceAuthorizationExecution
{
    public static async Task<TResult?> ExecuteAsync<TResult>(
        IRelationalCommandExecutor commandExecutor,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization namespaceAuthorization,
        Func<NamespaceAuthorizationFailure, TResult> onNotAuthorized,
        Func<string, SecurityConfigurationFailureDiagnostic[]?, TResult> onInvalidAuthorizationFailure,
        Func<TResult> onStaleTarget,
        CancellationToken cancellationToken = default
    )
        where TResult : class
    {
        var namespaceExecutor = new NamespaceAuthorizationExecutor(commandExecutor, providerFailureExtractor);

        var executionResult = await namespaceExecutor
            .ExecuteAsync(
                new NamespaceAuthorizationExecutionRequest(
                    mappingSet,
                    documentId,
                    ProposedNamespace: null,
                    namespaceAuthorization.Checks,
                    namespaceAuthorization.NamespacePrefixParameterization
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            NamespaceAuthorizationExecutionResult.Authorized => null,
            NamespaceAuthorizationExecutionResult.NotAuthorized notAuthorized => onNotAuthorized(
                notAuthorized.Failure
            ),
            NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                onInvalidAuthorizationFailure(invalidFailure.FailureMessage, invalidFailure.Diagnostics),
            NamespaceAuthorizationExecutionResult.StaleTarget => onStaleTarget(),
            _ => throw new InvalidOperationException(
                $"Unsupported namespace authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }
}
