// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// How the stored relationship authorization participates in a composite command.
/// </summary>
internal enum StoredRelationshipDisposition
{
    /// <summary>No executable stored relationship authorization applies.</summary>
    None,

    /// <summary>The check was co-batched into the composite command.</summary>
    Emitted,

    /// <summary>
    /// The claim list binds as a table-valued parameter, which the composite rewriter cannot rename, so
    /// the check runs as its own ordered segment on the same session for an observed existing target.
    /// </summary>
    Standalone,

    /// <summary>A deferred denial: the caller holds no claims that could authorize.</summary>
    DeferredNoClaims,

    /// <summary>
    /// Executable checks arrived without claim parameterization; an observed existing target maps to the
    /// caller's unknown-failure result.
    /// </summary>
    Unbuildable,
}

/// <summary>How the stored relationship authorization participates, and what it needs to be emitted.</summary>
internal sealed record StoredRelationshipStatementPlan(
    StoredRelationshipDisposition Disposition,
    int Ordinal = -1,
    RelationshipAuthorizationResult.Authorized? Authorized = null,
    RelationshipAuthorizationResult.NoClaims? NoClaims = null
);

/// <summary>What an emitted stored namespace check needs in order to map its provider failure back.</summary>
internal sealed record StoredNamespaceStatementPlan(
    IReadOnlyList<NamespaceAuthorizationCheckSpec> Checks,
    NamespacePrefixParameterization PrefixParameterization
);

/// <summary>
/// What emitted stored custom-view checks need in order to map their provider failure back.
/// </summary>
/// <remarks>
/// Holds the request's whole planned list rather than the appended runs. A request can append custom views
/// both before and after the namespace statement, and every statement in the command shares one provider
/// exception, so a <c>cv1</c> index is resolved against the full list — which is exactly what keeps the two
/// runs' indexes from colliding.
/// </remarks>
internal sealed record StoredCustomViewStatementPlan(
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> PlannedChecks
);

/// <summary>The stored relationship check's decoded success row.</summary>
internal sealed record StoredRelationshipAuthorizationRow(int AuthorizationResult, long ContentVersion);

/// <summary>
/// A denial a stored authorization statement raised, classified independently of the operation whose
/// result type it becomes. Each caller maps it to its own result shape; the classification itself cannot
/// diverge between them.
/// </summary>
internal abstract record StoredAuthorizationDenial
{
    private StoredAuthorizationDenial() { }

    /// <summary>The check found no target row. Unreachable while the capture lock holds.</summary>
    public sealed record StaleTarget : StoredAuthorizationDenial;

    public sealed record NamespaceNotAuthorized(NamespaceAuthorizationFailure Failure)
        : StoredAuthorizationDenial;

    public sealed record CustomViewNotAuthorized(CustomViewAuthorizationFailure Failure)
        : StoredAuthorizationDenial;

    public sealed record RelationshipNotAuthorized(RelationshipAuthorizationFailure Failure)
        : StoredAuthorizationDenial;

    public sealed record SecurityConfiguration(
        string[] Messages,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics
    ) : StoredAuthorizationDenial;
}

/// <summary>
/// Co-batches the stored namespace and stored relationship <c>AUTH1</c> checks against a captured target,
/// and classifies the provider failure a denial raises.
/// </summary>
/// <remarks>
/// <para>
/// Statement order is precedence order: a command aborts at its first <c>AUTH1</c>, so emitting namespace
/// before relationship is what makes a namespace denial win. Every write verb that authorizes stored values
/// against a locked target shares this one implementation, so that precedence — and the failure
/// classification the caller renders — cannot drift between the write executor's first phase and the delete
/// command.
/// </para>
/// <para>
/// Both checks are written to be vacuous when the capture observed nothing: the namespace checks carry the
/// carrier's row guard, and the relationship check's own target CTE yields no row. A command must serve both
/// branches, because choosing per branch would require knowing the target before the command runs.
/// </para>
/// </remarks>
internal static class RelationalCompositeStoredAuthorization
{
    public const string NamespaceLabel = "stored-namespace-authorization";
    public const string CustomViewLabel = "stored-custom-view-authorization";
    public const string RelationshipLabel = "stored-relationship-authorization";

    /// <summary>
    /// Classifies how a stored relationship authorization result participates in the attempt. Shared with
    /// test seams so classification cannot drift.
    /// </summary>
    public static StoredRelationshipStatementPlan Classify(
        RelationshipAuthorizationResult? storedAuthorization
    )
    {
        switch (storedAuthorization)
        {
            case null
            or RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return new StoredRelationshipStatementPlan(StoredRelationshipDisposition.None);

            case RelationshipAuthorizationResult.NoClaims noClaims:
                return new StoredRelationshipStatementPlan(
                    StoredRelationshipDisposition.DeferredNoClaims,
                    NoClaims: noClaims
                );

            case RelationshipAuthorizationResult.KnownButNotEnabled:
                throw new InvalidOperationException(
                    "Known-but-not-enabled stored relationship authorization results must be handled by repository preflight before executor entry."
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError:
                throw new InvalidOperationException(
                    "Security-configuration stored relationship authorization results must be handled by repository preflight before executor entry."
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                if (authorized.ClaimEducationOrganizationIdParameterization is not { } parameterization)
                {
                    return new StoredRelationshipStatementPlan(
                        StoredRelationshipDisposition.Unbuildable,
                        Authorized: authorized
                    );
                }

                return
                    parameterization.Kind
                    is AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured
                    ? new StoredRelationshipStatementPlan(
                        StoredRelationshipDisposition.Standalone,
                        Authorized: authorized
                    )
                    : new StoredRelationshipStatementPlan(
                        StoredRelationshipDisposition.Emitted,
                        Authorized: authorized
                    );

            default:
                throw new InvalidOperationException(
                    $"Unsupported stored relationship authorization result '{storedAuthorization.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Appends the stored namespace checks, or reports that they do not fit this command's remaining
    /// parameter budget so the caller can select ordered segments instead.
    /// </summary>
    public static bool TryAppendNamespace(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        MappingSet mappingSet,
        RelationalWriteNamespaceAuthorization? namespaceAuthorization,
        out StoredNamespaceStatementPlan? statementPlan
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (namespaceAuthorization is null)
        {
            statementPlan = null;
            return true;
        }

        var sqlPlan = new NamespaceAuthorizationSqlCompiler(mappingSet.Key.Dialect).Compile(
            new NamespaceAuthorizationSqlSpec(
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                NamespaceAuthorizationSqlSpecDefaults.ProposedNamespaceParameterName,
                RowGuardPredicateSql: carrier.CapturedTargetPresentPredicate
            )
        );
        var command = NamespaceAuthorizationExecutor.BuildCommand(
            sqlPlan,
            new NamespaceAuthorizationExecutionRequest(
                mappingSet,
                DocumentId: 0L,
                ProposedNamespace: null,
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization
            )
        );

        if (
            !builder.Fits(
                CountParametersAfterSubstitution(
                    command,
                    NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName
                )
            )
        )
        {
            statementPlan = null;
            return false;
        }

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            BuildCarrierSubstitutions(
                carrier,
                NamespaceAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                carrier.CapturedTargetIdExpression
            )
        );
        var resultSetCount = namespaceAuthorization.Checks.Count;

        builder.Append(
            NamespaceLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, readCancellation) =>
                RelationalCompositeResultSetSpan.ConsumeAsync(reader, resultSetCount, readCancellation),
            resultSetCount
        );

        statementPlan = new StoredNamespaceStatementPlan(
            namespaceAuthorization.Checks,
            namespaceAuthorization.NamespacePrefixParameterization
        );
        return true;
    }

    /// <summary>
    /// Appends one run of stored custom-view checks behind the carrier's row guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers append up to two runs — the custom views the CMS configured before <c>NamespaceBased</c> and
    /// those configured after — so that the AND filters execute in configured order and the first failure is
    /// the one reported. Both runs carry indexes from the request's single planned list, so the
    /// <see cref="StoredCustomViewStatementPlan"/> a caller keeps is the same regardless of how many runs
    /// were appended.
    /// </para>
    /// <para>
    /// No parameter-budget fallback exists, and none is reachable: a stored custom-view statement's only
    /// parameter is the target <c>DocumentId</c>, which the carrier substitutes away, leaving the statement
    /// with none. A run that did not fit would have to become an ordered segment running <em>after</em> this
    /// command, which would place it after the namespace check it may be configured before — silently
    /// inverting the order that decides which denial the caller sees. So a non-fit throws instead: it is a
    /// defect in the budget accounting, not a case to degrade into.
    /// </para>
    /// </remarks>
    public static void AppendCustomViewRun(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        MappingSet mappingSet,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> runChecks
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(runChecks);

        if (runChecks.Count == 0)
        {
            return;
        }

        var sqlPlan = new SingleRecordCustomViewAuthorizationSqlCompiler(mappingSet.Key.Dialect).Compile(
            new SingleRecordCustomViewAuthorizationSqlSpec(
                runChecks,
                CustomViewAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                RowGuardPredicateSql: carrier.CapturedTargetPresentPredicate
            )
        );

        if (sqlPlan.EmittedCheckIndexesInOrder.Count == 0)
        {
            // Every check in the run is decided in C# — only reachable for a proposed self-basis check, which
            // a stored run never contains.
            return;
        }

        var command = CustomViewAuthorizationExecutor.BuildCommand(
            sqlPlan,
            new CustomViewAuthorizationExecutionRequest(mappingSet, DocumentId: 0L, runChecks)
        );
        var parametersAfterSubstitution = CountParametersAfterSubstitution(
            command,
            CustomViewAuthorizationSqlSpecDefaults.DocumentIdParameterName
        );

        if (parametersAfterSubstitution != 0 || !builder.Fits(parametersAfterSubstitution))
        {
            throw new InvalidOperationException(
                $"Stored custom view authorization statements must bind no parameters after carrier substitution, but {parametersAfterSubstitution} remained; a run that cannot be co-batched would have to execute after the namespace check it may precede."
            );
        }

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            BuildCarrierSubstitutions(
                carrier,
                CustomViewAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                carrier.CapturedTargetIdExpression
            )
        );
        var resultSetCount = sqlPlan.EmittedCheckIndexesInOrder.Count;

        builder.Append(
            CustomViewLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, readCancellation) =>
                RelationalCompositeResultSetSpan.ConsumeAsync(reader, resultSetCount, readCancellation),
            resultSetCount
        );
    }

    /// <summary>
    /// Appends the stored relationship check when its disposition is
    /// <see cref="StoredRelationshipDisposition.Emitted"/> and it fits, and reports whether it did.
    /// </summary>
    public static bool TryAppendRelationship(
        RelationalCompositeCommandBuilder builder,
        IRelationalCompositeTargetCarrier carrier,
        MappingSet mappingSet,
        StoredRelationshipStatementPlan classifiedPlan,
        int emittedAuth1Index,
        IRelationalParameterConfigurator relationalParameterConfigurator,
        out StoredRelationshipStatementPlan statementPlan
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(classifiedPlan);

        if (classifiedPlan.Disposition is not StoredRelationshipDisposition.Emitted)
        {
            statementPlan = classifiedPlan;
            return true;
        }

        var authorized = classifiedPlan.Authorized!;
        var command = BuildRelationshipCommand(
            mappingSet,
            authorized,
            authorized.ClaimEducationOrganizationIdParameterization!,
            emittedAuth1Index,
            relationalParameterConfigurator
        );

        if (
            !builder.Fits(
                CountParametersAfterSubstitution(
                    command,
                    SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName
                )
            )
        )
        {
            statementPlan = classifiedPlan;
            return false;
        }

        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            command,
            builder.Allocator,
            builder.NextOrdinal,
            BuildCarrierSubstitutions(
                carrier,
                SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName,
                carrier.CapturedTargetIdExpression
            )
        );
        var ordinal = builder.Append(
            RelationshipLabel,
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            ReadRelationshipRowAsync
        );

        statementPlan = classifiedPlan with { Ordinal = ordinal };
        return true;
    }

    public static RelationalCommand BuildRelationshipCommand(
        MappingSet mappingSet,
        RelationshipAuthorizationResult.Authorized authorized,
        AuthorizationClaimEducationOrganizationIdParameterization parameterization,
        int emittedAuth1Index,
        IRelationalParameterConfigurator relationalParameterConfigurator
    )
    {
        ArgumentNullException.ThrowIfNull(authorized);

        var sqlPlan = authorized.ExecutableShape is { } executableShape
            ? SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                executableShape,
                parameterization,
                emittedAuth1Index
            )
            : SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    authorized.CheckSpecs,
                    parameterization,
                    emittedAuth1Index
                )
            );

        if (sqlPlan.ProposedValueParametersInOrder.Count > 0)
        {
            throw new InvalidOperationException(
                "Single-record relationship authorization executor cannot execute proposed-value checks without extracted runtime values."
            );
        }

        return SingleRecordRelationshipAuthorizationExecutor.BuildCommand(
            sqlPlan,
            new SingleRecordRelationshipAuthorizationExecutionRequest(
                mappingSet,
                DocumentId: 0L,
                authorized.CheckSpecs,
                parameterization,
                emittedAuth1Index,
                authorized.ExecutableShape
            ),
            relationalParameterConfigurator
        );
    }

    /// <summary>
    /// Classifies a provider failure raised by a command carrying stored authorization statements. Anything
    /// this does not recognize returns <see langword="null"/> so the caller's existing database-failure
    /// handling stays authoritative.
    /// </summary>
    public static StoredAuthorizationDenial? TryClassifyDenial(
        SqlDialect dialect,
        DbException exception,
        StoredNamespaceStatementPlan? namespacePlan,
        StoredRelationshipStatementPlan? relationshipPlan,
        int emittedAuth1Index,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        ILogger logger,
        StoredCustomViewStatementPlan? customViewPlan = null
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(providerFailureExtractor);
        ArgumentNullException.ThrowIfNull(logger);

        // Which family raised the abort is decided by the payload's discriminator, not by the order these
        // arms are tried: each family yields on a payload it does not own. Statement order in the command is
        // what enforces the configured AND precedence, and the command aborts at its first failure, so only
        // one family can ever have a payload to claim.
        if (customViewPlan is not null)
        {
            if (
                CustomViewAuthorizationProviderFailureMapper.IsStaleStoredTargetFailure(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    customViewPlan.PlannedChecks
                )
            )
            {
                return new StoredAuthorizationDenial.StaleTarget();
            }

            if (
                CustomViewAuthorizationProviderFailureMapper.TryMapCustomViewAuthorizationFailure(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    customViewPlan.PlannedChecks,
                    out var customViewFailure
                )
            )
            {
                return new StoredAuthorizationDenial.CustomViewNotAuthorized(customViewFailure!);
            }

            if (
                CustomViewAuthorizationProviderFailureMapper.IsUnmappableCustomViewPayload(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    customViewPlan.PlannedChecks
                )
            )
            {
                return new StoredAuthorizationDenial.SecurityConfiguration(
                    [CustomViewAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata],
                    AuthorizationSecurityConfigurationDiagnostics.ForCustomViewAuthorizationAuth1(
                        customViewPlan.PlannedChecks
                    )
                );
            }
        }

        if (namespacePlan is not null)
        {
            var plannedCheckValueSources = namespacePlan
                .Checks.Select(static check => check.ValueSource)
                .ToArray();

            if (
                NamespaceAuthorizationProviderFailureMapper.IsStaleStoredTargetFailure(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    plannedCheckValueSources
                )
            )
            {
                return new StoredAuthorizationDenial.StaleTarget();
            }

            if (
                NamespaceAuthorizationProviderFailureMapper.TryMapNamespaceAuthorizationFailure(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    plannedCheckValueSources,
                    namespacePlan.PrefixParameterization.ConfiguredPrefixesInOrder,
                    out var namespaceFailure
                )
            )
            {
                return new StoredAuthorizationDenial.NamespaceNotAuthorized(namespaceFailure!);
            }

            if (
                NamespaceAuthorizationProviderFailureMapper.TryBuildInvalidAuthorizationFailureDiagnostics(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    plannedCheckValueSources,
                    namespacePlan.Checks,
                    out var namespaceDiagnostics
                )
            )
            {
                return new StoredAuthorizationDenial.SecurityConfiguration(
                    [NamespaceAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata],
                    namespaceDiagnostics
                );
            }
        }

        if (
            relationshipPlan is
            { Disposition: StoredRelationshipDisposition.Emitted, Authorized: { } authorized }
        )
        {
            if (
                RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
                    dialect,
                    exception,
                    providerFailureExtractor,
                    emittedAuth1Index,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization!.ClaimEducationOrganizationIds,
                    out var relationshipFailure,
                    out var invalidFailureDiagnostic
                )
            )
            {
                return new StoredAuthorizationDenial.RelationshipNotAuthorized(relationshipFailure!);
            }

            if (invalidFailureDiagnostic is not null)
            {
                RelationshipAuthorizationProviderFailureMapper.LogInvalidFailurePayload(
                    logger,
                    invalidFailureDiagnostic
                );

                return new StoredAuthorizationDenial.SecurityConfiguration(
                    [
                        RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError,
                    ],
                    AuthorizationSecurityConfigurationDiagnostics.ForRelationshipAuthorizationAuth1(
                        invalidFailureDiagnostic,
                        authorized.CheckSpecs
                    )
                );
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the substitutions a co-batched statement needs: the target parameter replaced by the carrier
    /// expression, plus identity mappings for the carrier's own reserved names so the token rewriter leaves
    /// them alone.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildCarrierSubstitutions(
        IRelationalCompositeTargetCarrier carrier,
        string parameterName,
        string expression
    )
    {
        ArgumentNullException.ThrowIfNull(carrier);

        Dictionary<string, string> substitutions = new(StringComparer.OrdinalIgnoreCase);

        foreach (var reservedName in carrier.ReservedNames)
        {
            var bareName = reservedName.TrimStart('@');
            substitutions[bareName] = $"@{bareName}";
        }

        substitutions[parameterName.TrimStart('@')] = expression;
        return substitutions;
    }

    public static int CountParametersAfterSubstitution(
        RelationalCommand command,
        params string[] substitutedParameterNames
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        HashSet<string> substitutedBareNames = new(
            substitutedParameterNames.Select(static name => name.TrimStart('@')),
            StringComparer.OrdinalIgnoreCase
        );

        return command.Parameters.Count(parameter =>
            !substitutedBareNames.Contains(parameter.Name.TrimStart('@'))
        );
    }

    public static async Task<object?> ReadRelationshipRowAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = new StoredRelationshipAuthorizationRow(
            reader.GetInt32(reader.GetOrdinal("AuthorizationResult")),
            reader.GetInt64(reader.GetOrdinal("ContentVersion"))
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Stored relationship authorization returned more than one row for a single locked target."
            );
        }

        return row;
    }
}
