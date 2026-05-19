// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

public sealed record SingleRecordRelationshipAuthorizationExecutionRequest(
    MappingSet MappingSet,
    long DocumentId,
    IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization,
    int EmittedAuth1Index
);

public abstract record SingleRecordRelationshipAuthorizationExecutionResult
{
    private SingleRecordRelationshipAuthorizationExecutionResult() { }

    public sealed record Authorized(long ObservedContentVersion)
        : SingleRecordRelationshipAuthorizationExecutionResult;

    public sealed record NotAuthorized(RelationshipAuthorizationFailure RelationshipFailure)
        : SingleRecordRelationshipAuthorizationExecutionResult;

    public sealed record StaleTarget() : SingleRecordRelationshipAuthorizationExecutionResult;

    public sealed record InvalidAuthorizationFailure(string FailureMessage)
        : SingleRecordRelationshipAuthorizationExecutionResult;
}

public interface ISingleRecordRelationshipAuthorizationExecutor
{
    Task<SingleRecordRelationshipAuthorizationExecutionResult> ExecuteAsync(
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class SingleRecordRelationshipAuthorizationExecutor(
    IRelationalCommandExecutor commandExecutor,
    IRelationalParameterConfigurator? parameterConfigurator = null
) : ISingleRecordRelationshipAuthorizationExecutor
{
    private const string AuthorizationResultColumn = "AuthorizationResult";
    private const string ContentVersionColumn = "ContentVersion";

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationalParameterConfigurator _parameterConfigurator =
        parameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;

    public async Task<SingleRecordRelationshipAuthorizationExecutionResult> ExecuteAsync(
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(request.MappingSet.Key.Dialect);
        var sqlPlan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                request.CheckSpecs,
                request.ClaimEducationOrganizationIdParameterization,
                request.EmittedAuth1Index
            )
        );

        try
        {
            return await _commandExecutor
                .ExecuteReaderAsync(
                    BuildCommand(sqlPlan, request, _parameterConfigurator),
                    ReadAuthorizedResultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
            when (TryMapRelationshipAuthorizationFailure(
                    request,
                    ex,
                    out SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized? notAuthorized
                )
            )
        {
            return notAuthorized!;
        }
        catch (DbException ex)
            when (IsRelationshipAuthorizationProviderFailure(request.MappingSet.Key.Dialect, ex))
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                "Relationship authorization failed, but the AUTH1 failure metadata could not be mapped."
            );
        }
    }

    private static RelationalCommand BuildCommand(
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        Dictionary<string, object?> valuesByParameterName = new(StringComparer.Ordinal)
        {
            [SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName] =
                request.DocumentId,
        };

        AddAuthorizationParameterValues(
            valuesByParameterName,
            request.ClaimEducationOrganizationIdParameterization
        );

        return new RelationalCommand(
            sqlPlan.AuthorizationSql,
            [
                .. sqlPlan.ParametersInOrder.Select(parameter =>
                    BuildParameter(
                        parameter,
                        valuesByParameterName[parameter.ParameterName],
                        parameterConfigurator
                    )
                ),
            ]
        );
    }

    private static RelationalParameter BuildParameter(
        QuerySqlParameter parameter,
        object? value,
        IRelationalParameterConfigurator parameterConfigurator
    ) =>
        parameter.Binding.Kind switch
        {
            QuerySqlParameterBindingKind.Scalar => new RelationalParameter(
                $"@{parameter.ParameterName}",
                value
            ),
            QuerySqlParameterBindingKind.PgsqlArray => new RelationalParameter(
                $"@{parameter.ParameterName}",
                RequireInt64List(value, parameter.ParameterName).ToArray()
            ),
            QuerySqlParameterBindingKind.MssqlStructured => new RelationalParameter(
                $"@{parameter.ParameterName}",
                CreateStructuredInt64Table(
                    parameter.Binding.StructuredColumnName
                        ?? throw new InvalidOperationException(
                            $"Structured binding for parameter '{parameter.ParameterName}' is missing a column name."
                        ),
                    RequireInt64List(value, parameter.ParameterName)
                ),
                dbParameter => parameterConfigurator.ConfigureParameter(dbParameter, parameter)
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(parameter),
                parameter.Binding.Kind,
                "Unsupported single-record authorization parameter binding kind."
            ),
        };

    private static void AddAuthorizationParameterValues(
        IDictionary<string, object?> parameterValues,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        switch (authorizationClaimParameterization.Kind)
        {
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray:
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured:
                parameterValues[authorizationClaimParameterization.BaseParameterName] =
                    authorizationClaimParameterization.ClaimEducationOrganizationIds;
                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar:
                for (
                    var parameterIndex = 0;
                    parameterIndex < authorizationClaimParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    parameterValues[
                        authorizationClaimParameterization.ParameterNamesInOrder[parameterIndex]
                    ] = authorizationClaimParameterization.ClaimEducationOrganizationIds[parameterIndex];
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(authorizationClaimParameterization),
                    authorizationClaimParameterization.Kind,
                    "Unsupported authorization claim EdOrg parameterization kind."
                );
        }
    }

    private static async Task<SingleRecordRelationshipAuthorizationExecutionResult> ReadAuthorizedResultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget();
        }

        var authorizationResult = reader.GetRequiredFieldValue<int>(AuthorizationResultColumn);

        if (authorizationResult != 1)
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                $"Relationship authorization returned unexpected result '{authorizationResult}'."
            );
        }

        return new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(
            reader.GetRequiredFieldValue<long>(ContentVersionColumn)
        );
    }

    private static bool TryMapRelationshipAuthorizationFailure(
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        DbException exception,
        out SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized? notAuthorized
    )
    {
        notAuthorized = null;

        if (
            !TryParseRelationshipAuthorizationFailure(
                request.MappingSet.Key.Dialect,
                exception,
                out var payload
            )
        )
        {
            return false;
        }

        if (payload is null)
        {
            return false;
        }

        if (
            !RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
                payload,
                request.CheckSpecs,
                request.ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds,
                out var relationshipFailure
            ) || relationshipFailure is null
        )
        {
            return false;
        }

        notAuthorized = new SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized(
            relationshipFailure
        );
        return true;
    }

    private static bool TryParseRelationshipAuthorizationFailure(
        SqlDialect dialect,
        DbException exception,
        out RelationshipAuthorizationAuth1FailurePayload? payload
    ) =>
        RelationshipAuthorizationAuth1FailurePayloadCodec.TryParseProviderFailure(
            dialect,
            GetProviderErrorCode(dialect, exception),
            GetProviderMessage(dialect, exception),
            out payload
        );

    private static bool IsRelationshipAuthorizationProviderFailure(
        SqlDialect dialect,
        DbException exception
    ) =>
        RelationshipAuthorizationAuth1FailurePayloadCodec.TryExtractProviderPayload(
            dialect,
            GetProviderErrorCode(dialect, exception),
            GetProviderMessage(dialect, exception),
            out _
        );

    private static string? GetProviderErrorCode(SqlDialect dialect, DbException exception) =>
        dialect is SqlDialect.Pgsql ? GetStringProperty(exception, "SqlState") : null;

    private static string GetProviderMessage(SqlDialect dialect, DbException exception) =>
        dialect is SqlDialect.Pgsql
            ? GetStringProperty(exception, "MessageText") ?? exception.Message
            : exception.Message;

    private static string? GetStringProperty(DbException exception, string propertyName) =>
        exception.GetType().GetProperty(propertyName)?.GetValue(exception) as string;

    private static IReadOnlyList<long> RequireInt64List(object? value, string parameterName)
    {
        if (value is IReadOnlyList<long> int64Values)
        {
            return int64Values;
        }

        throw new InvalidOperationException(
            "Single-record authorization parameter "
                + $"'{parameterName}' requires an IReadOnlyList<long> runtime value."
        );
    }

    private static DataTable CreateStructuredInt64Table(
        string structuredColumnName,
        IReadOnlyList<long> int64Values
    )
    {
        DataTable structuredTable = new();
        structuredTable.Columns.Add(structuredColumnName, typeof(long));

        foreach (var value in int64Values)
        {
            structuredTable.Rows.Add(value);
        }

        return structuredTable;
    }
}

internal static class SingleRecordRelationshipAuthorizationSqlSpecDefaults
{
    public const string DocumentIdParameterName = "DocumentId";
}
