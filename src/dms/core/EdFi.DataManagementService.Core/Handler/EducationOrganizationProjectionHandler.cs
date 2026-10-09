// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SetResult = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Answers one page of the education-organization projection: reads the complete set, validates it,
/// selects parents, compares its digest with the walk's, and returns the items after the cursor.
/// </summary>
/// <remarks>
/// <para>
/// Every page reads and validates the whole set, so a contradiction anywhere fails every page, and a
/// page is answered only from a set whose projected content equals the first page's. The processing
/// stages run in a fixed order with cancellation checkpoints (<see cref="ProjectionProcessing"/>):
/// validation, parent selection, hashing, slicing and publishing. Nothing is written to the response
/// until the last checkpoint has passed.
/// </para>
/// <para>
/// The reader reads the request's effective target, which the pipeline sets to the resolved store's
/// primary database before any step opens a connection.
/// </para>
/// <para>
/// Only the outcome, typed reasons, the reader's log-safe description, row and item counts, elapsed
/// time, the data store id and the sanitized tenant and correlation id are logged; never an
/// identifier, name or cursor from the set.
/// </para>
/// </remarks>
internal sealed class EducationOrganizationProjectionHandler(
    EducationOrganizationProjectionSettings _settings,
    TimeProvider _timeProvider,
    IProjectionProcessingObserver _observer,
    ILogger<EducationOrganizationProjectionHandler> _logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        FrontendRequest request = requestInfo.FrontendRequest;
        CancellationToken cancellationToken = requestInfo.RequestCancellationToken;

        // Composition defects rather than request outcomes: the parse and mapping steps always run first.
        EducationOrganizationProjectionRequest projectionRequest =
            requestInfo.EducationOrganizationProjectionRequest
            ?? throw new InvalidOperationException(
                "The projection request was not parsed before the projection handler. "
                    + "ParseEducationOrganizationProjectionRequestMiddleware must run first."
            );

        MappingSet mappingSet =
            requestInfo.MappingSet
            ?? throw new InvalidOperationException(
                "The mapping set was not resolved before the projection handler. "
                    + "ResolveEducationOrganizationProjectionMappingSetMiddleware must run first."
            );

        IEducationOrganizationProjectionSetReader reader =
            requestInfo.ScopedServiceProvider.GetRequiredService<IEducationOrganizationProjectionSetReader>();

        var elapsed = Stopwatch.StartNew();

        SetResult result = await reader.ReadSetAsync(
            new EducationOrganizationProjectionSetReadRequest(
                mappingSet,
                _settings.MaxProjectionRows,
                _settings.ReadLockTimeoutSeconds,
                _settings.ReadCommandTimeoutSeconds
            ),
            cancellationToken
        );

        long readMilliseconds = elapsed.ElapsedMilliseconds;

        if (result is not SetResult.Set set)
        {
            requestInfo.FrontendResponse = Failure(result, projectionRequest, request);
            return;
        }

        requestInfo.FrontendResponse = Process(set.Rows, projectionRequest, request, cancellationToken);

        _logger.LogDebug(
            "Projection page for data store {DataStoreId}, tenant {Tenant}: status {StatusCode}, {RowCount} rows, read {ReadMilliseconds} ms, total {ElapsedMilliseconds} ms - {TraceId}",
            projectionRequest.DataStoreId,
            SanitizedTenant(request),
            requestInfo.FrontendResponse.StatusCode,
            set.Rows.Count,
            readMilliseconds,
            elapsed.ElapsedMilliseconds,
            SanitizedTraceId(request)
        );
    }

    /// <summary>
    /// The processing of a read set, in the order the contract requires: the whole set is validated
    /// and hashed before the digest is compared and before the page is sliced.
    /// </summary>
    private FrontendResponse Process(
        IReadOnlyList<EducationOrganizationProjectionRow> rows,
        EducationOrganizationProjectionRequest projectionRequest,
        FrontendRequest request,
        CancellationToken cancellationToken
    )
    {
        ProjectionProcessing.EnterStage(_observer, ProjectionProcessingStage.Validation, cancellationToken);

        ProjectionSetValidation validation = ProjectionSetValidator.Validate(
            rows,
            _observer,
            cancellationToken
        );

        switch (validation)
        {
            case ProjectionSetValidation.UnsupportedDiscriminator:
                _logger.LogError(
                    "Projection set for data store {DataStoreId}, tenant {Tenant} holds a discriminator outside the core types - {TraceId}",
                    projectionRequest.DataStoreId,
                    SanitizedTenant(request),
                    SanitizedTraceId(request)
                );
                return Problem(EducationOrganizationProjectionProblem.ProjectionUnsupported, request);

            case ProjectionSetValidation.DataInvalid invalid:
                _logger.LogWarning(
                    "Projection set for data store {DataStoreId}, tenant {Tenant} is invalid ({Reason}) - {TraceId}",
                    projectionRequest.DataStoreId,
                    SanitizedTenant(request),
                    invalid.Reason,
                    SanitizedTraceId(request)
                );
                return Problem(EducationOrganizationProjectionProblem.ProjectionDataInvalid, request);
        }

        ValidatedProjectionSet validated = ((ProjectionSetValidation.Valid)validation).Set;

        ProjectionProcessing.EnterStage(_observer, ProjectionProcessingStage.Precedence, cancellationToken);
        IReadOnlyList<ProjectionItem> items = ProjectionSetValidator.SelectParents(
            validated,
            _observer,
            cancellationToken
        );

        ProjectionProcessing.EnterStage(_observer, ProjectionProcessingStage.Hashing, cancellationToken);
        string digest = ProjectionDigest.ToCursorField(
            ProjectionDigest.Compute(items, _observer, cancellationToken)
        );

        ProjectionCursor? cursor = projectionRequest.Cursor;

        if (cursor is not null && !string.Equals(cursor.Digest, digest, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Projection set for data store {DataStoreId}, tenant {Tenant} changed during the walk - {TraceId}",
                projectionRequest.DataStoreId,
                SanitizedTenant(request),
                SanitizedTraceId(request)
            );
            return Problem(EducationOrganizationProjectionProblem.ProjectionChanged, request);
        }

        ProjectionProcessing.EnterStage(_observer, ProjectionProcessingStage.Slicing, cancellationToken);

        // Items are in strictly ascending id order, so the page starts at the first id after the
        // position. There is no sentinel: on the first page zero and negative ids are included.
        int start = cursor is null ? 0 : FirstIndexAfter(items, cursor.LastEducationOrganizationId);
        int available = items.Count - start;

        if (cursor is not null && available == 0)
        {
            // The set is unchanged, so DMS never issued this position: a cursor is issued only when an
            // item follows it. Answering an empty continuation page would break the rule that only the
            // empty set has an empty page.
            _logger.LogInformation(
                "Projection request cursor refused: {CursorRejection} - {TraceId}",
                "PositionAtEnd",
                SanitizedTraceId(request)
            );
            return Problem(EducationOrganizationProjectionProblem.InvalidCursor, request);
        }

        int pageCount = Math.Min(projectionRequest.Limit, available);
        string? nextCursor =
            available > projectionRequest.Limit
                ? ProjectionCursorCodec.Encode(
                    new ProjectionCursor(
                        projectionRequest.DataStoreId,
                        items[start + pageCount - 1].EducationOrganizationId,
                        digest,
                        cursor?.WalkIssuedAtUnixSeconds ?? _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                        projectionRequest.BindingHash
                    )
                )
                : null;

        ProjectionProcessing.EnterStage(_observer, ProjectionProcessingStage.Publishing, cancellationToken);

        var pageItems = new JsonArray();

        for (int index = start; index < start + pageCount; index++)
        {
            pageItems.Add(ToJson(items[index]));
        }

        return new FrontendResponse(
            StatusCode: 200,
            Body: new JsonObject
            {
                ["contractVersion"] = projectionRequest.ContractVersion,
                ["dataStoreId"] = projectionRequest.DataStoreId,
                ["nextCursor"] = nextCursor,
                ["items"] = pageItems,
            },
            Headers: []
        );
    }

    /// <summary>
    /// Translates every read outcome other than a set into the projection's taxonomy. Each is logged
    /// with its typed reason only.
    /// </summary>
    private FrontendResponse Failure(
        SetResult result,
        EducationOrganizationProjectionRequest projectionRequest,
        FrontendRequest request
    )
    {
        switch (result)
        {
            case SetResult.TooLarge tooLarge:
                _logger.LogWarning(
                    "Projection set for data store {DataStoreId}, tenant {Tenant} exceeds {MaxProjectionRows} rows - {TraceId}",
                    projectionRequest.DataStoreId,
                    SanitizedTenant(request),
                    tooLarge.MaxProjectionRows,
                    SanitizedTraceId(request)
                );
                return Problem(EducationOrganizationProjectionProblem.ProjectionTooLarge, request);

            case SetResult.MappingIncompatible mapping:
                _logger.LogError(
                    "Projection mapping incompatible ({Reason}, {ResourceName}) for data store {DataStoreId}, tenant {Tenant} - {TraceId}",
                    mapping.Incompatibility.Reason,
                    mapping.Incompatibility.ResourceName ?? "(none)",
                    projectionRequest.DataStoreId,
                    SanitizedTenant(request),
                    SanitizedTraceId(request)
                );
                return Problem(EducationOrganizationProjectionProblem.ProjectionUnsupported, request);

            case SetResult.SchemaIncompatible schema:
                _logger.LogError(
                    "Projection target schema incompatible ({Reason}, provider code {ProviderCode}) for data store {DataStoreId}, tenant {Tenant} - {TraceId}",
                    schema.Reason,
                    schema.ProviderCode ?? "(none)",
                    projectionRequest.DataStoreId,
                    SanitizedTenant(request),
                    SanitizedTraceId(request)
                );
                return Problem(EducationOrganizationProjectionProblem.TargetSchemaIncompatible, request);

            case SetResult.TargetUnavailable unavailable:
                _logger.LogWarning(
                    "Projection target unavailable in stage {Stage} ({Failure}) for data store {DataStoreId}, tenant {Tenant} - {TraceId}",
                    unavailable.Stage,
                    unavailable.Describe,
                    projectionRequest.DataStoreId,
                    SanitizedTenant(request),
                    SanitizedTraceId(request)
                );
                return Problem(EducationOrganizationProjectionProblem.TargetUnavailable, request);

            default:
                // Unreachable: the result union has a private constructor. Thrown rather than answered,
                // so a new outcome cannot be reported as some existing one.
                throw new InvalidOperationException(
                    $"Unhandled projection set result '{result.GetType().Name}'."
                );
        }
    }

    /// <summary>The index of the first item whose id is greater than <paramref name="position"/>.</summary>
    private static int FirstIndexAfter(IReadOnlyList<ProjectionItem> items, long position)
    {
        int low = 0;
        int high = items.Count;

        while (low < high)
        {
            int middle = low + ((high - low) / 2);

            if (items[middle].EducationOrganizationId <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Exactly the five members of a response item.</summary>
    private static JsonObject ToJson(ProjectionItem item) =>
        new()
        {
            ["educationOrganizationId"] = item.EducationOrganizationId,
            ["nameOfInstitution"] = item.NameOfInstitution,
            ["shortNameOfInstitution"] = item.ShortNameOfInstitution,
            ["discriminator"] = ProjectionSetValidator.WireDiscriminator(item.Kind),
            ["parentId"] = item.ParentId,
        };

    private static FrontendResponse Problem(
        EducationOrganizationProjectionProblem problem,
        FrontendRequest request
    ) => EducationOrganizationProjectionResponse.For(problem, request.TraceId);

    private static string SanitizedTenant(FrontendRequest request) =>
        LoggingSanitizer.SanitizeInternalValueForLogging(request.Tenant ?? "(none)");

    private static string SanitizedTraceId(FrontendRequest request) =>
        LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value);
}
