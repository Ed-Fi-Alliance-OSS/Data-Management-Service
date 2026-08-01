// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;

namespace EdFi.DataManagementService.Backend;

internal sealed class DocumentCacheAdministrativeWorkflowCancellationScope : IDisposable
{
    private readonly CancellationTokenSource? _linkedCancellationSource;

    public DocumentCacheAdministrativeWorkflowCancellationScope(
        CancellationToken token,
        CancellationTokenSource? linkedCancellationSource
    )
    {
        Token = token;
        _linkedCancellationSource = linkedCancellationSource;
    }

    public CancellationToken Token { get; }

    public void Dispose() => _linkedCancellationSource?.Dispose();
}

internal static class DocumentCacheAdministrativeWorkflow
{
    public static DocumentCacheAdministrativeWorkflowCancellationScope CreateCancellationScope(
        DocumentCacheAdministrativeCommandExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (cancellationToken.CanBeCanceled && context.WorkflowCancellationToken.CanBeCanceled)
        {
            CancellationTokenSource linkedCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    context.WorkflowCancellationToken
                );

            return new(linkedCancellationSource.Token, linkedCancellationSource);
        }

        return new(
            cancellationToken.CanBeCanceled ? cancellationToken : context.WorkflowCancellationToken,
            linkedCancellationSource: null
        );
    }

    public static Task<TResult> ExecuteInTransactionAsync<TResult>(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        IsolationLevel isolationLevel,
        Func<IRelationalWriteSession, Task<TResult>> executeAsync,
        bool commit,
        CancellationToken cancellationToken
    ) => ExecuteInTransactionAsync(mutexLease, isolationLevel, executeAsync, _ => commit, cancellationToken);

    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        IsolationLevel isolationLevel,
        Func<IRelationalWriteSession, Task<TResult>> executeAsync,
        Func<TResult, bool> shouldCommit,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(mutexLease);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(shouldCommit);

        await using IRelationalWriteSession session = await mutexLease
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            TResult result = await executeAsync(session).ConfigureAwait(false);
            if (shouldCommit(result))
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
