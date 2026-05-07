// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DescriptorQueryRowsPage(long? TotalCount, IReadOnlyList<DescriptorReadRow> Rows);

internal sealed class DescriptorReadHandler(
    IRelationalCommandExecutor commandExecutor,
    IReadableProfileProjector readableProfileProjector,
    ILogger<DescriptorReadHandler> logger
) : IDescriptorReadHandler
{
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
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

        if (
            !RelationalReadGuardrails.HasOnlyNoFurtherAuthorizationRequired(
                request.AuthorizationStrategyEvaluators
            )
        )
        {
            return new GetResult.GetFailureNotImplemented(
                RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                    request.Resource,
                    request.AuthorizationStrategyEvaluators,
                    "descriptor GET",
                    "GET"
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
            MaterializeDescriptorDocument(request, descriptorRow),
            descriptorRow.ContentLastModifiedAt.UtcDateTime,
            null
        );
    }

    public async Task<QueryResult> HandleQueryAsync(
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

        if (
            !RelationalReadGuardrails.HasOnlyNoFurtherAuthorizationRequired(
                request.AuthorizationStrategyEvaluators
            )
        )
        {
            return new QueryResult.QueryFailureNotImplemented(
                RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                    request.Resource,
                    request.AuthorizationStrategyEvaluators,
                    "descriptor query",
                    "GET-many"
                )
            );
        }

        DescriptorQueryPreprocessingResult preprocessingResult;

        try
        {
            preprocessingResult = DescriptorQueryRequestPreprocessor.Preprocess(
                request.MappingSet,
                request.Resource,
                request.QueryElements
            );
        }
        catch (NotSupportedException ex)
        {
            return new QueryResult.QueryFailureNotImplemented(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            return new QueryResult.QuerySuccess([], request.PaginationParameters.TotalCount ? 0 : null);
        }

        DescriptorQueryRowsPage queryRowsPage;

        try
        {
            queryRowsPage = await ReadQueryRowsAsync(request, preprocessingResult, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (DescriptorReadInvariantException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        return new QueryResult.QuerySuccess(
            MaterializeDescriptorQueryDocuments(request, queryRowsPage.Rows),
            request.PaginationParameters.TotalCount
                ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                    request.Resource,
                    queryRowsPage.TotalCount,
                    "descriptor query"
                )
                : null
        );
    }

    internal Task<DescriptorQueryRowsPage> ReadQueryRowsAsync(
        DescriptorQueryRequest request,
        DescriptorQueryPreprocessingResult preprocessingResult,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preprocessingResult);

        if (preprocessingResult.Outcome is not RelationalQueryPreprocessingOutcome.Continue)
        {
            throw new ArgumentException(
                "Descriptor query row retrieval requires preprocessing results in the continue state.",
                nameof(preprocessingResult)
            );
        }

        var plannedQuery = new DescriptorQueryPageKeysetPlanner(request.MappingSet.Key.Dialect).Plan(
            request.MappingSet,
            request.Resource,
            preprocessingResult,
            request.PaginationParameters
        );
        var command = BuildQueryCommand(request.MappingSet.Key.Dialect, plannedQuery);

        return _commandExecutor.ExecuteReaderAsync(
            command,
            (reader, ct) => ReadQueryRowsPageAsync(reader, plannedQuery.Plan.TotalCountSql is not null, ct),
            cancellationToken
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

    private JsonArray MaterializeDescriptorQueryDocuments(
        DescriptorQueryRequest request,
        IReadOnlyList<DescriptorReadRow> descriptorRows
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptorRows);

        JsonArray edfiDocs = [];

        foreach (var descriptorRow in descriptorRows)
        {
            edfiDocs.Add(
                MaterializeDescriptorDocument(
                    descriptorRow,
                    RelationalGetRequestReadMode.ExternalResponse,
                    request.ReadableProfileProjectionContext
                )
            );
        }

        return edfiDocs;
    }

    private JsonNode MaterializeDescriptorDocument(
        DescriptorReadRow descriptorRow,
        RelationalGetRequestReadMode readMode,
        ReadableProfileProjectionContext? readableProfileProjectionContext
    )
    {
        var materializedDocument = DescriptorDocumentMaterializer.Materialize(descriptorRow, readMode);

        if (
            readMode != RelationalGetRequestReadMode.ExternalResponse
            || readableProfileProjectionContext is null
        )
        {
            return materializedDocument;
        }

        var projectedDocument = _readableProfileProjector.Project(
            materializedDocument,
            readableProfileProjectionContext.ContentTypeDefinition,
            readableProfileProjectionContext.IdentityPropertyNames
        );

        RelationalApiMetadataFormatter.RefreshEtag(projectedDocument);

        return projectedDocument;
    }

    private JsonNode MaterializeDescriptorDocument(
        DescriptorGetByIdRequest request,
        DescriptorReadRow descriptorRow
    ) =>
        MaterializeDescriptorDocument(
            descriptorRow,
            request.ReadMode,
            request.ReadableProfileProjectionContext
        );

    private static RelationalCommand BuildQueryCommand(SqlDialect dialect, PageKeysetSpec.Query plannedQuery)
    {
        ArgumentNullException.ThrowIfNull(plannedQuery);

        var pageRowsSql = BuildPageRowsSql(dialect, plannedQuery.Plan.PageDocumentIdSql);
        var commandText = plannedQuery.Plan.TotalCountSql is null
            ? pageRowsSql
            : $"{EnsureTrailingSemicolon(plannedQuery.Plan.TotalCountSql)}{Environment.NewLine}{Environment.NewLine}{pageRowsSql}";

        return new RelationalCommand(commandText, BuildQueryParameters(plannedQuery));
    }

    private static IReadOnlyList<RelationalParameter> BuildQueryParameters(PageKeysetSpec.Query plannedQuery)
    {
        ArgumentNullException.ThrowIfNull(plannedQuery);

        List<string> requiredParameterNames = [];
        HashSet<string> seenParameterNames = new(StringComparer.OrdinalIgnoreCase);

        AddParameterNames(
            plannedQuery.Plan.TotalCountParametersInOrder,
            requiredParameterNames,
            seenParameterNames
        );
        AddParameterNames(
            plannedQuery.Plan.PageParametersInOrder,
            requiredParameterNames,
            seenParameterNames
        );

        List<string> missingParameterNames = [];
        List<RelationalParameter> parameters = [];

        foreach (var parameterName in requiredParameterNames)
        {
            if (!plannedQuery.ParameterValues.TryGetValue(parameterName, out var parameterValue))
            {
                missingParameterNames.Add(parameterName);
                continue;
            }

            parameters.Add(new RelationalParameter($"@{parameterName}", parameterValue));
        }

        if (missingParameterNames.Count > 0)
        {
            throw new InvalidOperationException(
                "Descriptor query keyset is missing required parameter values for "
                    + $"[{string.Join(", ", missingParameterNames.Select(parameterName => $"'{parameterName}'"))}]."
            );
        }

        return parameters;
    }

    private static void AddParameterNames(
        IReadOnlyList<QuerySqlParameter>? parameterInventory,
        ICollection<string> requiredParameterNames,
        ISet<string> seenParameterNames
    )
    {
        if (parameterInventory is null)
        {
            return;
        }

        foreach (var parameterName in parameterInventory.Select(static parameter => parameter.ParameterName))
        {
            if (!seenParameterNames.Add(parameterName))
            {
                continue;
            }

            requiredParameterNames.Add(parameterName);
        }
    }

    private static async Task<DescriptorQueryRowsPage> ReadQueryRowsPageAsync(
        IRelationalCommandReader reader,
        bool hasTotalCount,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        long? totalCount = null;

        if (hasTotalCount)
        {
            totalCount = await ReadTotalCountAsync(reader, cancellationToken).ConfigureAwait(false);

            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Expected descriptor query row result set after total count but no more result sets were available."
                );
            }
        }

        var rows = await DescriptorReadRowReader
            .ReadAllAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        return new DescriptorQueryRowsPage(totalCount, rows);
    }

    private static async Task<long> ReadTotalCountAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Expected a descriptor query total count result row but none was returned."
            );
        }

        var totalCountValue = reader.GetFieldValue<object>(0);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Descriptor query total count result set returned multiple rows."
            );
        }

        return Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
    }

    private static string BuildPageRowsSql(SqlDialect dialect, string pageDocumentIdSql)
    {
        var pageDocumentIdSqlBody = StripTrailingSemicolon(pageDocumentIdSql);

        // The shared page compiler intentionally returns only a DocumentId keyset. Descriptor queries
        // root on dms.Document, so this performs a page-sized PK lookup instead of widening that contract.
        return dialect switch
        {
            SqlDialect.Pgsql => $$"""
                SELECT
                    page_document_ids."DocumentId" AS "DocumentId",
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
                FROM (
                {{pageDocumentIdSqlBody}}
                ) page_document_ids
                INNER JOIN dms."Document" document
                    ON document."DocumentId" = page_document_ids."DocumentId"
                LEFT JOIN dms."Descriptor" descriptor
                    ON descriptor."DocumentId" = page_document_ids."DocumentId"
                ORDER BY page_document_ids."DocumentId" ASC;
                """,
            SqlDialect.Mssql => $$"""
                SELECT
                    page_document_ids.[DocumentId] AS [DocumentId],
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
                FROM (
                {{pageDocumentIdSqlBody}}
                ) page_document_ids
                INNER JOIN [dms].[Document] document
                    ON document.[DocumentId] = page_document_ids.[DocumentId]
                LEFT JOIN [dms].[Descriptor] descriptor
                    ON descriptor.[DocumentId] = page_document_ids.[DocumentId]
                ORDER BY page_document_ids.[DocumentId] ASC;
                """,
            _ => throw new NotSupportedException(
                $"Relational descriptor GET-many row retrieval does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private static string EnsureTrailingSemicolon(string sql)
    {
        var trimmed = sql.AsSpan().TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == ';' ? sql : $"{trimmed};";
    }

    private static string StripTrailingSemicolon(string sql)
    {
        var trimmed = sql.AsSpan().TrimEnd();

        if (trimmed.Length > 0 && trimmed[^1] == ';')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.ToString();
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
