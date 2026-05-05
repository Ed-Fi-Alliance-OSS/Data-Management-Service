// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed class DescriptorReadHandler(
    IRelationalCommandExecutor commandExecutor,
    ILogger<DescriptorReadHandler> logger
) : IDescriptorReadHandler
{
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly ILogger<DescriptorReadHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<GetResult> HandleGetByIdAsync(
        DescriptorGetByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor GET-by-id routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        if (!HasNoOpDescriptorGetAuthorization(request.AuthorizationStrategyEvaluators))
        {
            return new GetResult.GetFailureNotImplemented(
                BuildDescriptorGetAuthorizationNotImplementedMessage(
                    request.Resource,
                    request.AuthorizationStrategyEvaluators
                )
            );
        }

        RelationalCommand command;

        try
        {
            command = BuildGetByIdCommand(
                request.MappingSet.Key.Dialect,
                request.DocumentUuid,
                RelationalWriteSupport.GetResourceKeyIdOrThrow(request.MappingSet, request.Resource)
            );
        }
        catch (NotSupportedException ex)
        {
            return new GetResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new GetResult.UnknownFailure(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return new GetResult.UnknownFailure(ex.Message);
        }

        DescriptorReadRow? descriptorRow;

        try
        {
            descriptorRow = await _commandExecutor
                .ExecuteReaderAsync(
                    command,
                    DescriptorReadRowReader.ReadSingleOrDefaultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new GetResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new GetResult.UnknownFailure(ex.Message);
        }

        if (descriptorRow is null)
        {
            return new GetResult.GetFailureNotExists();
        }

        LogDiscriminatorMismatchIfPresent(request, descriptorRow);

        return new GetResult.GetSuccess(
            new DocumentUuid(descriptorRow.DocumentUuid),
            MaterializeDescriptorDocument(descriptorRow, request.ReadMode),
            descriptorRow.ContentLastModifiedAt.UtcDateTime,
            null
        );
    }

    public Task<QueryResult> HandleQueryAsync(
        DescriptorQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Descriptor query routed to descriptor read handler for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        return Task.FromResult<QueryResult>(
            new QueryResult.QueryFailureNotImplemented(
                $"Relational descriptor GET-many is not implemented for resource '{RelationalWriteSupport.FormatResource(request.Resource)}'."
            )
        );
    }

    private void LogDiscriminatorMismatchIfPresent(
        DescriptorGetByIdRequest request,
        DescriptorReadRow descriptorRow
    )
    {
        if (
            string.IsNullOrWhiteSpace(descriptorRow.Discriminator)
            || string.Equals(
                descriptorRow.Discriminator,
                request.Resource.ResourceName,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        _logger.LogWarning(
            "Descriptor GET-by-id read discriminator mismatch for {Resource}: document {DocumentUuid} "
                + "stored discriminator '{StoredDiscriminator}' did not match requested descriptor type "
                + "'{ExpectedDiscriminator}'. ResourceKeyId remained authoritative. - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            descriptorRow.DocumentUuid,
            descriptorRow.Discriminator,
            request.Resource.ResourceName,
            request.TraceId.Value
        );
    }

    private static JsonNode MaterializeDescriptorDocument(
        DescriptorReadRow descriptorRow,
        RelationalGetRequestReadMode readMode
    )
    {
        return DescriptorDocumentMaterializer.Materialize(descriptorRow, readMode);
    }

    private static bool HasNoOpDescriptorGetAuthorization(
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        return authorizationStrategyEvaluators.All(static evaluator =>
            string.Equals(
                evaluator.AuthorizationStrategyName,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                StringComparison.Ordinal
            )
        );
    }

    private static string BuildDescriptorGetAuthorizationNotImplementedMessage(
        QualifiedResourceName resource,
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        var strategyNames = authorizationStrategyEvaluators
            .Select(static evaluator => evaluator.AuthorizationStrategyName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(static name => $"'{name}'");

        return $"Relational descriptor GET authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + "when effective GET authorization requires filtering. Effective strategies: "
            + $"[{string.Join(", ", strategyNames)}]. Only requests with no authorization strategies or only "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' are currently supported.";
    }

    private static RelationalCommand BuildGetByIdCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        IReadOnlyList<RelationalParameter> parameters =
        [
            new(DocumentUuidParameterName, documentUuid.Value),
            new(ResourceKeyIdParameterName, resourceKeyId),
        ];

        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                SELECT
                    document."DocumentId" AS "DocumentId",
                    document."DocumentUuid" AS "DocumentUuid",
                    document."ContentLastModifiedAt" AS "ContentLastModifiedAt",
                    document."ResourceKeyId" AS "ResourceKeyId",
                    descriptor."Namespace" AS "Namespace",
                    descriptor."CodeValue" AS "CodeValue",
                    descriptor."ShortDescription" AS "ShortDescription",
                    descriptor."Description" AS "Description",
                    descriptor."EffectiveBeginDate" AS "EffectiveBeginDate",
                    descriptor."EffectiveEndDate" AS "EffectiveEndDate",
                    descriptor."Discriminator" AS "Discriminator"
                FROM dms."Document" document
                LEFT JOIN dms."Descriptor" descriptor
                    ON descriptor."DocumentId" = document."DocumentId"
                WHERE document."DocumentUuid" = @documentUuid
                    AND document."ResourceKeyId" = @resourceKeyId;
                """,
                parameters
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                SELECT
                    document.[DocumentId] AS [DocumentId],
                    document.[DocumentUuid] AS [DocumentUuid],
                    document.[ContentLastModifiedAt] AS [ContentLastModifiedAt],
                    document.[ResourceKeyId] AS [ResourceKeyId],
                    descriptor.[Namespace] AS [Namespace],
                    descriptor.[CodeValue] AS [CodeValue],
                    descriptor.[ShortDescription] AS [ShortDescription],
                    descriptor.[Description] AS [Description],
                    descriptor.[EffectiveBeginDate] AS [EffectiveBeginDate],
                    descriptor.[EffectiveEndDate] AS [EffectiveEndDate],
                    descriptor.[Discriminator] AS [Discriminator]
                FROM [dms].[Document] document
                LEFT JOIN [dms].[Descriptor] descriptor
                    ON descriptor.[DocumentId] = document.[DocumentId]
                WHERE document.[DocumentUuid] = @documentUuid
                    AND document.[ResourceKeyId] = @resourceKeyId;
                """,
                parameters
            ),
            _ => throw new NotSupportedException(
                $"Relational descriptor GET by id does not support SQL dialect '{dialect}'."
            ),
        };
    }
}
