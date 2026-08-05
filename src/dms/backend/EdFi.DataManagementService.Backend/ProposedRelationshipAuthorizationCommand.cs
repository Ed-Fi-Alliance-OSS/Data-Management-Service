// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The single source of truth for the proposed-value relationship <c>AUTH1</c> statement: its SQL, its
/// parameter binding, its success row, and the mapping from a provider failure to the write executor's
/// authorization exceptions.
/// </summary>
/// <remarks>
/// Three call sites emit this statement — prefixed onto the <c>dms.Document</c> insert of an authorized
/// POST create, standalone on the write session, and co-batched into the authorization-only second
/// command. They share this class so the emitted SQL and the observable denial cannot drift apart between
/// them, which is what keeps the AUTH1 index and the failure payload meaningful regardless of which
/// command carried the check.
/// </remarks>
internal static class ProposedRelationshipAuthorizationCommand
{
    public const string AuthorizationResultColumn = "AuthorizationResult";

    /// <summary>
    /// The write plan's own column-binding parameter names. The compiler must avoid them so the
    /// authorization statement can share a command with write DML that binds them.
    /// </summary>
    private static readonly ConditionalWeakTable<
        ResourceWritePlan,
        string[]
    > _reservedWriteParameterNamesByPlan = new();

    public static RelationalCommand Build(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(runtimeCheck);
        ArgumentNullException.ThrowIfNull(parameterConfigurator);

        var reservedParameterNames = GetReservedWriteParameterNames(writePlan);
        var sqlPlan = runtimeCheck.ExecutableShape is { } executableShape
            ? SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                executableShape,
                runtimeCheck.ClaimEducationOrganizationIdParameterization,
                runtimeCheck.EmittedAuth1Index,
                reservedParameterNames
            )
            : SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
                mappingSet,
                new SingleRecordRelationshipAuthorizationSqlSpec(
                    runtimeCheck.CheckSpecs,
                    runtimeCheck.ClaimEducationOrganizationIdParameterization,
                    runtimeCheck.EmittedAuth1Index,
                    ReservedParameterNames: reservedParameterNames
                )
            );

        return new RelationalCommand(
            sqlPlan.AuthorizationSql,
            BuildParameters(sqlPlan, runtimeCheck, parameterConfigurator)
        );
    }

    /// <summary>
    /// Runs the statement as its own command on the write session's transaction. This is the ordered
    /// segment a structured claim parameterization or a command-budget boundary selects; it observes no
    /// target and holds no state, so running it standalone changes nothing but the command count.
    /// </summary>
    public static async Task ExecuteStandaloneAsync(
        IRelationalWriteSession writeSession,
        RelationalCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(writeSession);
        ArgumentNullException.ThrowIfNull(command);

        await using var dbCommand = writeSession.CreateCommand(command);
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await ReadAndValidateResultAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and validates the statement's success row. A denial never reaches here — it aborts the
    /// command through the AUTH1 device — so an unexpected value is an invariant failure.
    /// </summary>
    public static async Task<object?> ReadAndValidateResultAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Proposed relationship authorization did not return an authorization result."
            );
        }

        var authorizationResult = Convert.ToInt32(
            reader.GetValue(reader.GetOrdinal(AuthorizationResultColumn)),
            CultureInfo.InvariantCulture
        );

        if (authorizationResult != 1)
        {
            throw new InvalidOperationException(
                $"Proposed relationship authorization returned unexpected result '{authorizationResult}'."
            );
        }

        return null;
    }

    public static bool TryMapFailure(
        SqlDialect dialect,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck,
        DbException exception,
        out RelationshipAuthorizationFailure? relationshipFailure,
        out RelationshipAuthorizationProviderFailureDiagnostic? invalidFailureDiagnostic
    ) =>
        RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            dialect,
            exception,
            providerFailureExtractor,
            runtimeCheck.EmittedAuth1Index,
            runtimeCheck.CheckSpecs,
            runtimeCheck.ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds,
            out relationshipFailure,
            out invalidFailureDiagnostic
        );

    /// <summary>
    /// Rethrows a provider failure as the authorization exception the write executor maps to a result, or
    /// rethrows the original when the failure is not an authorization denial at all. Never returns.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowMappedFailure(
        SqlDialect dialect,
        IRelationshipAuthorizationProviderFailureExtractor providerFailureExtractor,
        ILogger logger,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck,
        DbException exception
    )
    {
        if (
            TryMapFailure(
                dialect,
                providerFailureExtractor,
                runtimeCheck,
                exception,
                out var relationshipFailure,
                out var invalidFailureDiagnostic
            )
        )
        {
            throw new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure!);
        }

        if (invalidFailureDiagnostic is not null)
        {
            RelationshipAuthorizationProviderFailureMapper.LogInvalidFailurePayload(
                logger,
                invalidFailureDiagnostic
            );

            throw new RelationalWriteInvalidRelationshipAuthorizationFailureException(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError,
                AuthorizationSecurityConfigurationDiagnostics.ForRelationshipAuthorizationAuth1(
                    invalidFailureDiagnostic,
                    runtimeCheck.CheckSpecs
                )
            );
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
        throw new InvalidOperationException("Unreachable relationship authorization failure mapping state.");
    }

    private static IReadOnlyList<RelationalParameter> BuildParameters(
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        Dictionary<string, object?> valuesByParameterName = new(
            sqlPlan.ParametersInOrder.Count,
            StringComparer.Ordinal
        );

        AddProposedValueParameterValues(valuesByParameterName, sqlPlan, runtimeCheck);
        RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
            valuesByParameterName,
            runtimeCheck.ClaimEducationOrganizationIdParameterization
        );

        List<RelationalParameter> parameters = new(sqlPlan.ParametersInOrder.Count);

        foreach (var parameter in sqlPlan.ParametersInOrder)
        {
            parameters.Add(
                RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                    parameter,
                    valuesByParameterName[parameter.ParameterName],
                    parameterConfigurator
                )
            );
        }

        return parameters;
    }

    private static void AddProposedValueParameterValues(
        IDictionary<string, object?> valuesByParameterName,
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        ProposedRelationshipAuthorizationRuntimeCheck runtimeCheck
    )
    {
        Dictionary<
            (int StrategyOrdinal, int SubjectOrdinal),
            ProposedRelationshipAuthorizationRuntimeValue
        > valuesByOrdinal = new(CountRuntimeSubjects(runtimeCheck.Strategies));

        foreach (var strategy in runtimeCheck.Strategies)
        {
            foreach (var subject in strategy.Subjects)
            {
                valuesByOrdinal.Add((strategy.StrategyOrdinal, subject.SubjectOrdinal), subject.RuntimeValue);
            }
        }

        foreach (var proposedValueParameter in sqlPlan.ProposedValueParametersInOrder)
        {
            if (
                !valuesByOrdinal.TryGetValue(
                    (proposedValueParameter.StrategyOrdinal, proposedValueParameter.SubjectOrdinal),
                    out var value
                )
            )
            {
                throw new InvalidOperationException(
                    "Proposed relationship authorization SQL requested a runtime value for "
                        + $"strategy '{proposedValueParameter.StrategyOrdinal}' subject '{proposedValueParameter.SubjectOrdinal}', "
                        + "but no extracted value was available."
                );
            }

            valuesByParameterName[proposedValueParameter.ParameterName] = value switch
            {
                ProposedRelationshipAuthorizationRuntimeValue.SubjectValue subjectValue => subjectValue.Value,
                ProposedRelationshipAuthorizationRuntimeValue.TransitivePeopleFirstHopAnchorValue anchorValue =>
                    anchorValue.Value,
                _ => throw new InvalidOperationException(
                    $"Unsupported proposed relationship authorization runtime value '{value.GetType().Name}'."
                ),
            };
        }
    }

    private static int CountRuntimeSubjects(
        IReadOnlyList<ProposedRelationshipAuthorizationRuntimeStrategy> strategies
    )
    {
        var count = 0;

        foreach (var strategy in strategies)
        {
            count += strategy.Subjects.Count;
        }

        return count;
    }

    private static IReadOnlyList<string> GetReservedWriteParameterNames(ResourceWritePlan writePlan) =>
        _reservedWriteParameterNamesByPlan.GetValue(writePlan, BuildReservedWriteParameterNames);

    private static string[] BuildReservedWriteParameterNames(ResourceWritePlan writePlan)
    {
        var columnBindingCount = 0;

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            columnBindingCount += tablePlan.ColumnBindings.Length;
        }

        List<string> reservedNames = new(columnBindingCount);
        HashSet<string> seenNames = new(columnBindingCount, StringComparer.OrdinalIgnoreCase);

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            foreach (var binding in tablePlan.ColumnBindings)
            {
                var parameterName = binding.ParameterName.TrimStart('@');

                if (seenNames.Add(parameterName))
                {
                    reservedNames.Add(parameterName);
                }
            }
        }

        return [.. reservedNames];
    }
}
