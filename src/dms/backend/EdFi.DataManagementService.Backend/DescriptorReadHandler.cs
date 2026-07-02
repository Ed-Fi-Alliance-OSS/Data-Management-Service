// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DescriptorQueryRowsPage(long? TotalCount, IReadOnlyList<DescriptorReadRow> Rows);

internal sealed class DescriptorReadHandler(
    IRelationalCommandExecutor commandExecutor,
    IReadableProfileProjector readableProfileProjector,
    IEtagComposer etagComposer,
    ILogger<DescriptorReadHandler> logger
) : IDescriptorReadHandler
{
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";

    // The descriptor page query binds a single ResourceKeyId discriminator parameter on top of the paging
    // parameters; see DescriptorQueryPageKeysetPlanner. Counted into the non-authorization parameter budget.
    private const int DescriptorQueryResourceKeyParameterCount = 1;
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
    private readonly IEtagComposer _etagComposer =
        etagComposer ?? throw new ArgumentNullException(nameof(etagComposer));
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

        // StoredDocument reads are internal read-modify-write fetches that bypass per-record
        // authorization exactly as the generic single-record path does: the caller was already
        // authorized for the operation that triggered the fetch. Only ExternalResponse reads run the
        // namespace authorization preflight and the in-memory stored-namespace check below.
        NamespacePrefixParameterization? namespacePrefixParameterization = null;

        if (request.ReadMode != RelationalGetRequestReadMode.StoredDocument)
        {
            // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) and
            // unsupported strategies resolve before any SQL roundtrip. The stored namespace check itself
            // runs in memory against the namespace value materialized by the existing single SELECT.
            var authorizationPreflight = ResolveDescriptorReadAuthorization(
                request.MappingSet,
                request.Resource,
                request.AuthorizationStrategyEvaluators,
                request.RelationalAuthorizationContext,
                NamespaceAuthorizationOperation.ReadSingle,
                "descriptor GET",
                "GET"
            );

            switch (authorizationPreflight)
            {
                case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                    return new GetResult.GetFailureNotImplemented(notImplemented.FailureMessage);
                case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                    return new GetResult.GetFailureSecurityConfiguration(
                        configError.Errors,
                        configError.Diagnostics
                    );
                case DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                    return new GetResult.GetFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure);
            }

            namespacePrefixParameterization = (
                (DescriptorReadAuthorizationPreflightOutcome.Proceed)authorizationPreflight
            ).NamespacePrefixParameterization;
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

        // The descriptor row reader emits the same Namespace column the orchestrator resolved as
        // the stored authorization source, so the namespace check runs against that materialized
        // value without a second SQL roundtrip. The stored-namespace mismatch and uninitialized
        // failure kinds are constructed directly here because no AUTH1 codec mediates the
        // single-record path.
        if (namespacePrefixParameterization is not null)
        {
            var namespaceFailure = EvaluateStoredNamespace(
                descriptorRow.Namespace,
                namespacePrefixParameterization
            );

            if (namespaceFailure is not null)
            {
                return new GetResult.GetFailureNamespaceNotAuthorized(namespaceFailure);
            }
        }
        else if (string.IsNullOrEmpty(descriptorRow.Namespace))
        {
            // Without namespace authorization configured, the stored-namespace-uninitialized 403
            // path does not apply, so a null stored Namespace is genuine descriptor row corruption.
            // Surface it as an UnknownFailure with the same column-naming diagnostic the row
            // reader produces for the other required descriptor columns.
            return new GetResult.UnknownFailure(
                $"Descriptor read corruption detected for DocumentId {descriptorRow.DocumentId} "
                    + $"(ResourceKeyId={descriptorRow.ResourceKeyId}): dms.Descriptor.Namespace must not be null."
            );
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

        var authorizationPreflight = ResolveDescriptorReadAuthorization(
            request.MappingSet,
            request.Resource,
            request.AuthorizationStrategyEvaluators,
            request.RelationalAuthorizationContext,
            NamespaceAuthorizationOperation.ReadMany,
            "descriptor query",
            "GET-many"
        );

        switch (authorizationPreflight)
        {
            case DescriptorReadAuthorizationPreflightOutcome.NotImplemented notImplemented:
                return new QueryResult.QueryFailureNotImplemented(notImplemented.FailureMessage);
            case DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                return new QueryResult.QueryFailureSecurityConfiguration(
                    configError.Errors,
                    configError.Diagnostics
                );
            case DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                return new QueryResult.QueryFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure);
        }

        var proceed = (DescriptorReadAuthorizationPreflightOutcome.Proceed)authorizationPreflight;

        // The descriptor page subquery roots on dms.Document while Namespace lives on the joined
        // dms.Descriptor row. The page SQL compiler aliases the namespace check to the descriptor
        // join, so the planner consumes the orchestrator's namespace check specs + prefix
        // parameterization through PageDocumentIdAuthorizationSpec.
        var authorizationSpec = BuildDescriptorQueryAuthorizationSpec(proceed);

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

        // Descriptor queries are namespace-only, but the page query still composes the namespace prefix
        // list with the query filter, paging, ResourceKeyId, and change-version parameters. Fail closed
        // if that exceeds SQL Server's per-command parameter ceiling rather than letting the query fail
        // at execution.
        if (
            BuildDescriptorQueryParameterBudgetFailure(
                request.MappingSet.Key.Dialect,
                request.Resource,
                proceed.NamespacePrefixParameterization,
                preprocessingResult.QueryElementsInOrder.Count,
                CountChangeVersionParameters(request.ChangeVersionRange)
            ) is
            { } parameterBudgetFailure
        )
        {
            return parameterBudgetFailure;
        }

        DescriptorQueryRowsPage queryRowsPage;

        try
        {
            queryRowsPage = await ReadQueryRowsAsync(
                    request,
                    preprocessingResult,
                    authorizationSpec,
                    cancellationToken
                )
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

    /// <summary>
    /// Counts the change-version parameters the descriptor page query will bind: one per supplied
    /// bound (minChangeVersion / maxChangeVersion), zero when no window applies.
    /// </summary>
    private static int CountChangeVersionParameters(ChangeVersionRange changeVersionRange) =>
        (changeVersionRange.MinChangeVersion is null ? 0 : 1)
        + (changeVersionRange.MaxChangeVersion is null ? 0 : 1);

    /// <summary>
    /// Returns a security-configuration failure when the descriptor page query's namespace prefix
    /// parameters, plus its query filter, paging, ResourceKeyId, and change-version parameters, exceed
    /// SQL Server's per-command parameter ceiling; otherwise <see langword="null"/>. The dialect gate
    /// lives in <see cref="AuthorizationParameterBudget.ExceedsCommandParameterLimit"/>.
    /// </summary>
    private static QueryResult? BuildDescriptorQueryParameterBudgetFailure(
        SqlDialect dialect,
        QualifiedResourceName resource,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        int queryFilterParameterCount,
        int changeVersionParameterCount
    )
    {
        var nonAuthorizationParameterCount =
            queryFilterParameterCount
            + AuthorizationParameterBudget.PaginationParameterCount
            + DescriptorQueryResourceKeyParameterCount
            + changeVersionParameterCount;

        if (
            !AuthorizationParameterBudget.ExceedsCommandParameterLimit(
                dialect,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount
            )
        )
        {
            return null;
        }

        return new QueryResult.QueryFailureSecurityConfiguration(
            [
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixParameterization?.ConfiguredPrefixesInOrder.Count ?? 0,
                    0,
                    nonAuthorizationParameterCount
                ),
            ],
            AuthorizationSecurityConfigurationDiagnostics.ForCommandParameterCapExceeded(resource)
        );
    }

    internal Task<DescriptorQueryRowsPage> ReadQueryRowsAsync(
        DescriptorQueryRequest request,
        DescriptorQueryPreprocessingResult preprocessingResult,
        PageDocumentIdAuthorizationSpec? authorizationSpec = null,
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
            request.PaginationParameters,
            authorizationSpec,
            request.ChangeVersionRange
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

        // Build the fixed descriptor variant key once per request rather than per row.
        var variantKey = DescriptorVariantKey.For(request.MappingSet.Key.EffectiveSchemaHash);

        foreach (var descriptorRow in descriptorRows)
        {
            edfiDocs.Add(
                MaterializeDescriptorDocument(
                    descriptorRow,
                    RelationalGetRequestReadMode.ExternalResponse,
                    request.ReadableProfileProjectionContext,
                    variantKey
                )
            );
        }

        return edfiDocs;
    }

    private JsonNode MaterializeDescriptorDocument(
        DescriptorReadRow descriptorRow,
        RelationalGetRequestReadMode readMode,
        ReadableProfileProjectionContext? readableProfileProjectionContext,
        VariantKey variantKey
    )
    {
        var materializedDocument = DescriptorDocumentMaterializer.Materialize(
            descriptorRow,
            readMode,
            _etagComposer,
            variantKey
        );

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

        return projectedDocument;
    }

    private JsonNode MaterializeDescriptorDocument(
        DescriptorGetByIdRequest request,
        DescriptorReadRow descriptorRow
    ) =>
        MaterializeDescriptorDocument(
            descriptorRow,
            request.ReadMode,
            request.ReadableProfileProjectionContext,
            DescriptorVariantKey.For(request.MappingSet.Key.EffectiveSchemaHash)
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

        List<QuerySqlParameter> requiredParameters = [];
        HashSet<string> seenParameterNames = new(StringComparer.OrdinalIgnoreCase);

        AddParameters(plannedQuery.Plan.TotalCountParametersInOrder, requiredParameters, seenParameterNames);
        AddParameters(plannedQuery.Plan.PageParametersInOrder, requiredParameters, seenParameterNames);

        List<string> missingParameterNames = [];
        List<RelationalParameter> parameters = [];

        foreach (var queryParameter in requiredParameters)
        {
            if (
                !plannedQuery.ParameterValues.TryGetValue(
                    queryParameter.ParameterName,
                    out var parameterValue
                )
            )
            {
                missingParameterNames.Add(queryParameter.ParameterName);
                continue;
            }

            parameters.Add(
                NamespaceAuthorizationCommandParameterBuilder.BuildParameter(queryParameter, parameterValue)
            );
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

    private static void AddParameters(
        IReadOnlyList<QuerySqlParameter>? parameterInventory,
        ICollection<QuerySqlParameter> requiredParameters,
        ISet<string> seenParameterNames
    )
    {
        if (parameterInventory is null)
        {
            return;
        }

        foreach (var parameter in parameterInventory)
        {
            if (!seenParameterNames.Add(parameter.ParameterName))
            {
                continue;
            }

            requiredParameters.Add(parameter);
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
                    document."ContentVersion" AS "ContentVersion",
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
                    document.[ContentVersion] AS [ContentVersion],
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

    /// <summary>
    /// Plans descriptor GET / query namespace authorization through the relational authorization
    /// orchestrator before any SQL is built. Strategies other than <c>NamespaceBased</c> /
    /// <c>NoFurtherAuthorizationRequired</c> fail closed; the namespace planner terminals
    /// (no configured prefixes, no usable root column, MSSQL prefix cap) short-circuit with no DB
    /// roundtrip; otherwise the configured namespace prefixes are surfaced for the in-memory
    /// stored-value check on GET-by-id or for SQL emission on query.
    /// </summary>
    private static DescriptorReadAuthorizationPreflightOutcome ResolveDescriptorReadAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators,
        RelationalAuthorizationContext authorizationContext,
        NamespaceAuthorizationOperation operation,
        string operationLabel,
        string actionLabel
    )
    {
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            authorizationStrategyEvaluators
        );
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            operation,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        return orchestratorOutcome switch
        {
            RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot =>
                new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
                    [
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ],
                    RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                ),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes =>
                new DescriptorReadAuthorizationPreflightOutcome.NamespaceNotAuthorized(
                    NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                ),
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => new DescriptorReadAuthorizationPreflightOutcome.NotImplemented(
                RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                    resource,
                    authorizationStrategyEvaluators,
                    operationLabel,
                    actionLabel
                )
            ),
            RelationalAuthorizationPlanOutcome.Plan plan => BuildDescriptorReadPlanPreflight(
                mappingSet,
                authorizationContext,
                plan
            ),
            RelationalAuthorizationPlanOutcome.StillUnsupported =>
                new DescriptorReadAuthorizationPreflightOutcome.NotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        resource,
                        authorizationStrategyEvaluators,
                        operationLabel,
                        actionLabel
                    )
                ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                BuildDescriptorReadSecurityConfigurationError(resource, securityConfigurationError),
            _ => throw new InvalidOperationException(
                $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
            ),
        };
    }

    private static DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError BuildDescriptorReadSecurityConfigurationError(
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError
    )
    {
        var failure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            securityConfigurationError.NonNamespaceConfiguredStrategies,
            securityConfigurationError.RelationshipClassification
        );

        return new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
            failure.Errors,
            failure.Diagnostics
        );
    }

    private static DescriptorReadAuthorizationPreflightOutcome BuildDescriptorReadPlanPreflight(
        MappingSet mappingSet,
        RelationalAuthorizationContext authorizationContext,
        RelationalAuthorizationPlanOutcome.Plan plan
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return DescriptorReadAuthorizationPreflightOutcome.Proceed.NoAuthorization;
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage,
                out var securityConfigurationDiagnostics
            )
        )
        {
            return new DescriptorReadAuthorizationPreflightOutcome.SecurityConfigurationError(
                [securityConfigurationMessage],
                securityConfigurationDiagnostics
            );
        }

        return new DescriptorReadAuthorizationPreflightOutcome.Proceed(
            plan.NamespaceChecks,
            namespacePrefixParameterization
        );
    }

    private static PageDocumentIdAuthorizationSpec? BuildDescriptorQueryAuthorizationSpec(
        DescriptorReadAuthorizationPreflightOutcome.Proceed proceed
    )
    {
        if (proceed.NamespaceChecks.Count == 0)
        {
            return null;
        }

        // No relational relationship strategies participate in descriptor queries; pass an empty
        // strategy list so the compiler emits namespace-only authorization.
        return new PageDocumentIdAuthorizationSpec(
            Strategies: [],
            NamespaceChecks: proceed.NamespaceChecks,
            NamespacePrefixParameterization: proceed.NamespacePrefixParameterization
        );
    }

    private static NamespaceAuthorizationFailure? EvaluateStoredNamespace(
        string? storedNamespace,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        if (string.IsNullOrEmpty(storedNamespace))
        {
            return new NamespaceAuthorizationFailure(
                NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
                NamespaceAuthorizationFailureValueSource.Stored,
                EmittedAuth1Index: 0,
                AuthorizationStrategyNameConstants.NamespaceBased,
                [.. namespacePrefixParameterization.ConfiguredPrefixesInOrder]
            );
        }

        // The single-record GET-by-id check mirrors the LIKE prefix filter the GET-many and write paths
        // emit so it accepts and rejects the same stored namespaces for the same caller. The match and
        // its dialect case sensitivity live on the shared parameterization, next to the SQL escaping it
        // mirrors, instead of being re-derived here.
        if (namespacePrefixParameterization.MatchesAnyPrefix(storedNamespace))
        {
            return null;
        }

        return new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            [.. namespacePrefixParameterization.ConfiguredPrefixesInOrder]
        );
    }

    private abstract record DescriptorReadAuthorizationPreflightOutcome
    {
        private DescriptorReadAuthorizationPreflightOutcome() { }

        public sealed record NotImplemented(string FailureMessage)
            : DescriptorReadAuthorizationPreflightOutcome;

        public sealed record SecurityConfigurationError(
            string[] Errors,
            SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
        ) : DescriptorReadAuthorizationPreflightOutcome;

        public sealed record NamespaceNotAuthorized(NamespaceAuthorizationFailure Failure)
            : DescriptorReadAuthorizationPreflightOutcome;

        /// <param name="NamespaceChecks">
        /// Planner-emitted check specs (used by the GET-many SQL emission path).
        /// </param>
        /// <param name="NamespacePrefixParameterization">
        /// Dialect-specific prefix parameterization; non-null exactly when namespace authorization
        /// applies. Drives the GET-many SQL emission and the GET-by-id in-memory stored-value check.
        /// </param>
        public sealed record Proceed(
            IReadOnlyList<NamespaceAuthorizationCheckSpec> NamespaceChecks,
            NamespacePrefixParameterization? NamespacePrefixParameterization
        ) : DescriptorReadAuthorizationPreflightOutcome
        {
            public static Proceed NoAuthorization { get; } = new([], null);
        }
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
                    document."ContentVersion" AS "ContentVersion",
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
                    document.[ContentVersion] AS [ContentVersion],
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
