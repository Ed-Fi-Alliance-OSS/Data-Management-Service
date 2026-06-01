// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

public sealed record NamespaceAuthorizationExecutionRequest(
    MappingSet MappingSet,
    long DocumentId,
    string? ProposedNamespace,
    IReadOnlyList<NamespaceAuthorizationCheckSpec> Checks,
    NamespacePrefixParameterization NamespacePrefixParameterization
);

public abstract record NamespaceAuthorizationExecutionResult
{
    private NamespaceAuthorizationExecutionResult() { }

    public sealed record Authorized() : NamespaceAuthorizationExecutionResult;

    public sealed record NotAuthorized(NamespaceAuthorizationFailure Failure)
        : NamespaceAuthorizationExecutionResult;

    public sealed record InvalidAuthorizationFailure(string FailureMessage)
        : NamespaceAuthorizationExecutionResult;

    /// <summary>
    /// The stored target row no longer exists: it was deleted between the unlocked target lookup and the
    /// stored namespace check. Read callers re-resolve the target and surface the resulting 404; locked
    /// write/delete callers never observe this because they row-lock the target before the check.
    /// </summary>
    public sealed record StaleTarget : NamespaceAuthorizationExecutionResult;
}

public interface INamespaceAuthorizationExecutor
{
    Task<NamespaceAuthorizationExecutionResult> ExecuteAsync(
        NamespaceAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Executes co-batched single-record namespace authorization SQL. The SQL emits one
/// <c>SELECT CASE ... END;</c> per planned check; the first failing check raises AUTH1 with an
/// <c>ns1|index|kind</c> payload and aborts the batch. A clean run authorizes the record.
/// </summary>
/// <remarks>
/// The reader callback advances through every co-batched statement's result set so a failure in a
/// later statement (e.g. the proposed check of a PUT) is forced to execute and surfaces as a
/// <see cref="DbException"/>.
/// <para>
/// A stored-row check whose target no longer exists raises the <c>StoredTargetMissing</c> AUTH1 kind
/// (see <c>NamespaceAuthorizationSqlCompiler.AppendStoredCheckSql</c>), which this executor maps to
/// <see cref="NamespaceAuthorizationExecutionResult.StaleTarget"/> rather than to a namespace-mismatch
/// denial. The unlocked GET-by-id read boundary retries on that result and re-resolves the target so a
/// row deleted between the target lookup and this check surfaces as a 404 instead of a 403. Locked
/// write and delete callers row-lock the target before this check runs, so they never observe the stale
/// result; they map it defensively to a write-conflict/not-exists outcome.
/// </para>
/// </remarks>
internal sealed class NamespaceAuthorizationExecutor(
    IRelationalCommandExecutor commandExecutor,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
) : INamespaceAuthorizationExecutor
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<NamespaceAuthorizationExecutionResult> ExecuteAsync(
        NamespaceAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialect = request.MappingSet.Key.Dialect;
        var compiler = new NamespaceAuthorizationSqlCompiler(dialect);
        var sqlPlan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                request.Checks,
                request.NamespacePrefixParameterization,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                NamespaceAuthorizationSqlSpecDefaults.ProposedNamespaceParameterName
            )
        );
        var plannedCheckValueSources = request.Checks.Select(static check => check.ValueSource).ToArray();

        try
        {
            return await _commandExecutor
                .ExecuteReaderAsync(
                    BuildCommand(sqlPlan, request),
                    ReadAuthorizedResultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
            when (NamespaceAuthorizationProviderFailureMapper.IsStaleStoredTargetFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor,
                    plannedCheckValueSources
                )
            )
        {
            // The stored target row vanished between the unlocked lookup and this check. Surface a
            // stale-target result so the read boundary can re-resolve the target instead of mapping the
            // missing row to a namespace-mismatch denial.
            return new NamespaceAuthorizationExecutionResult.StaleTarget();
        }
        catch (DbException ex)
            when (NamespaceAuthorizationProviderFailureMapper.TryMapNamespaceAuthorizationFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor,
                    plannedCheckValueSources,
                    request.NamespacePrefixParameterization.ConfiguredPrefixesInOrder,
                    out var namespaceFailure
                )
            )
        {
            return new NamespaceAuthorizationExecutionResult.NotAuthorized(namespaceFailure!);
        }
        catch (DbException ex)
            when (NamespaceAuthorizationProviderFailureMapper.IsNamespaceAuthorizationProviderFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor
                )
            )
        {
            return new NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure(
                NamespaceAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata
            );
        }
    }

    private static RelationalCommand BuildCommand(
        NamespaceAuthorizationSqlPlan sqlPlan,
        NamespaceAuthorizationExecutionRequest request
    )
    {
        Dictionary<string, object?> valuesByParameterName = new(StringComparer.Ordinal);

        NamespaceAuthorizationCommandParameterBuilder.AddParameterValues(
            valuesByParameterName,
            request.NamespacePrefixParameterization,
            request.DocumentId,
            request.ProposedNamespace
        );

        return new RelationalCommand(
            sqlPlan.AuthorizationSql,
            [
                .. sqlPlan.ParametersInOrder.Select(parameter =>
                    NamespaceAuthorizationCommandParameterBuilder.BuildParameter(
                        parameter,
                        valuesByParameterName[parameter.ParameterName]
                    )
                ),
            ]
        );
    }

    private static async Task<NamespaceAuthorizationExecutionResult> ReadAuthorizedResultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        // Advance through every co-batched statement's result set so each check executes and any
        // later-statement AUTH1 failure surfaces as a DbException. A clean run means every check
        // authorized.
        var hasMoreResultSets = true;
        while (hasMoreResultSets)
        {
            hasMoreResultSets = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        return new NamespaceAuthorizationExecutionResult.Authorized();
    }
}

internal static class NamespaceAuthorizationSqlSpecDefaults
{
    public const string DocumentIdParameterName = "documentId";
    public const string ProposedNamespaceParameterName = "proposedNamespace";
    public const string NamespacePrefixesParameterName = "namespacePrefixes";
}
