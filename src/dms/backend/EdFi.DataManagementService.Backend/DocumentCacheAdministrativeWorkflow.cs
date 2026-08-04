// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;

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
        Func<IRelationalWriteSession, CancellationToken, Task<TResult>> executeAsync,
        bool commit,
        CancellationToken cancellationToken,
        Action<TResult>? beforeCommit = null
    ) =>
        ExecuteInTransactionAsync(
            mutexLease,
            isolationLevel,
            executeAsync,
            _ => commit,
            cancellationToken,
            beforeCommit
        );

    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        IsolationLevel isolationLevel,
        Func<IRelationalWriteSession, CancellationToken, Task<TResult>> executeAsync,
        Func<TResult, bool> shouldCommit,
        CancellationToken cancellationToken,
        Action<TResult>? beforeCommit = null
    )
    {
        ArgumentNullException.ThrowIfNull(mutexLease);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(shouldCommit);

        await using IRelationalWriteSession session = await mutexLease
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        CancellationToken activeTransactionCancellationToken = CancellationToken.None;
        try
        {
            TResult result = await executeAsync(session, activeTransactionCancellationToken)
                .ConfigureAwait(false);
            bool commitTransaction = shouldCommit(result);

            if (commitTransaction)
            {
                beforeCommit?.Invoke(result);
            }

            ThrowIfSessionLost(mutexLease);

            if (commitTransaction)
            {
                await session.CommitAsync(activeTransactionCancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.RollbackAsync(activeTransactionCancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception exception) when (IsSessionLoss(mutexLease, exception))
        {
            throw CreateSessionLostException(mutexLease);
        }
        catch (Exception exception)
        {
            try
            {
                await session.RollbackAsync(activeTransactionCancellationToken).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
                when (ShouldClassifyRollbackFailureAsSessionLoss(mutexLease, exception, rollbackException))
            {
                throw CreateSessionLostException(mutexLease);
            }

            throw;
        }
    }

    internal static bool IsSessionLoss(IDocumentCacheAdministrativeMutexLease mutexLease, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(mutexLease);
        ArgumentNullException.ThrowIfNull(exception);

        return exception is DocumentCacheAdministrativeMutexSessionLostException
            || (
                !mutexLease.IsSessionOpen
                && exception is DbException or InvalidOperationException or ObjectDisposedException
            );
    }

    private static void ThrowIfSessionLost(IDocumentCacheAdministrativeMutexLease mutexLease)
    {
        if (!mutexLease.IsSessionOpen)
        {
            throw CreateSessionLostException(mutexLease);
        }
    }

    private static bool ShouldClassifyRollbackFailureAsSessionLoss(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        Exception originalException,
        Exception rollbackException
    ) =>
        !ShouldPreserveOriginalClassification(originalException)
        && IsSessionLoss(mutexLease, rollbackException);

    private static bool ShouldPreserveOriginalClassification(Exception exception) =>
        exception is OperationCanceledException
        || DocumentCacheProviderCommandTimeoutClassifier.IsProviderCommandTimeout(exception);

    private static DocumentCacheAdministrativeMutexSessionLostException CreateSessionLostException(
        IDocumentCacheAdministrativeMutexLease mutexLease
    ) => new(mutexLease.ProviderToken);
}
