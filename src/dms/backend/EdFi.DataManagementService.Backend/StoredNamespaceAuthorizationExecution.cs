// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

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
