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
/// This executor intentionally has no stale-target result, and callers intentionally do not retry it.
/// A row concurrently deleted between the target lookup and this check matches none of the stored-row
/// <c>WHEN EXISTS</c> branches and falls through to namespace mismatch (see
/// <c>NamespaceAuthorizationSqlCompiler.AppendStoredCheckSql</c>), i.e. it fails closed as an
/// authorization denial. That is deliberate and safe: the only observable difference is a 403 instead
/// of a 404 for a resource that no longer exists. This is unlike relationship authorization, which
/// surfaces a stale-target result and is retried because it anchors its decision to a content version;
/// namespace authorization reports no content version, so there is nothing to re-anchor and nothing to
/// gain from retrying. Do not add a stale-target branch here or retry namespace checks to "fix" the
/// 403-vs-404 distinction.
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
                "Namespace authorization failed, but the AUTH1 failure metadata could not be mapped."
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
