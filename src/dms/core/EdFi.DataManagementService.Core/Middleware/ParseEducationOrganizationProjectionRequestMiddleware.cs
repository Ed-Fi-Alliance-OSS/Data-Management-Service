// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Accepts the query parameters of an education-organization projection request and records them
/// on <see cref="RequestInfo.EducationOrganizationProjectionRequest"/>. Runs after authorization, so
/// an unauthorized caller learns nothing about which parameter values would be accepted.
/// </summary>
/// <remarks>
/// Parameter names match case-insensitively, and unknown parameters are ignored. The checks run in a
/// fixed order, and the first failing group answers:
/// <list type="number">
/// <item>Any of the four names supplied more than once → 400 <c>parameter-validation-failed</c>
/// naming each repeated parameter. Repeats come first because no single value of a repeated
/// parameter can be trusted to validate the others.</item>
/// <item><c>dataStoreId</c> and <c>limit</c> → one 400 <c>parameter-validation-failed</c> naming
/// both when both are wrong.</item>
/// <item><c>contractVersion</c> → 400 <c>unsupported-contract-version</c>.</item>
/// <item><c>cursor</c> → 400 <c>invalid-cursor</c>. Last, because a cursor is judged against the
/// accepted data store id and contract version: its binding covers the contract version, so a
/// cursor cannot be checked until the version it is checked against is known to be served.</item>
/// </list>
/// The cursor binding is computed from this request's own tenant, contract version and route
/// qualifiers, so a cursor issued for one tenant or route context is refused in another. Nothing a
/// client sent is logged; a refused cursor is logged by reason only.
/// </remarks>
internal class ParseEducationOrganizationProjectionRequestMiddleware(
    EducationOrganizationProjectionSettings _settings,
    TimeProvider _timeProvider,
    ILogger<ParseEducationOrganizationProjectionRequestMiddleware> _logger
) : IPipelineStep
{
    internal const string DataStoreIdParameter = "dataStoreId";
    internal const string LimitParameter = "limit";
    internal const string CursorParameter = "cursor";
    internal const string ContractVersionParameter = "contractVersion";

    /// <summary>
    /// The parameters this endpoint owns, with the name an error message uses for each, in the
    /// order repeated-parameter errors are listed.
    /// </summary>
    private static readonly (string Name, string MessageName)[] _ownedParameters =
    [
        (DataStoreIdParameter, "DataStoreId"),
        (LimitParameter, "Limit"),
        (CursorParameter, "Cursor"),
        (ContractVersionParameter, "ContractVersion"),
    ];

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        FrontendRequest request = requestInfo.FrontendRequest;

        _logger.LogDebug(
            "Entering ParseEducationOrganizationProjectionRequestMiddleware - {TraceId}",
            request.TraceId.Value
        );

        string[] repeatedErrors =
        [
            .. _ownedParameters
                .Where(parameter => IsRepeated(request, parameter.Name))
                .Select(parameter => $"{parameter.MessageName} must not be supplied more than once."),
        ];

        if (repeatedErrors.Length > 0)
        {
            _logger.LogDebug("Projection request repeats a parameter - {TraceId}", request.TraceId.Value);
            requestInfo.FrontendResponse = ParameterValidationFailed(repeatedErrors, request);
            return;
        }

        List<string> parameterErrors = [];

        if (!TryParsePositiveInt32(Lookup(request, DataStoreIdParameter), out int dataStoreId))
        {
            parameterErrors.Add("DataStoreId must be set to a numeric value between 1 and 2147483647.");
        }

        int limit = _settings.MaximumPageSize;
        string? limitText = Lookup(request, LimitParameter);

        if (
            limitText is not null
            && (!TryParsePositiveInt32(limitText, out limit) || limit > _settings.MaximumPageSize)
        )
        {
            parameterErrors.Add(
                $"Limit must be omitted or set to a numeric value between 1 and {_settings.MaximumPageSize}."
            );
        }

        if (parameterErrors.Count > 0)
        {
            _logger.LogDebug("Projection request parameter is invalid - {TraceId}", request.TraceId.Value);
            requestInfo.FrontendResponse = ParameterValidationFailed([.. parameterErrors], request);
            return;
        }

        string contractVersion =
            Lookup(request, ContractVersionParameter) ?? ProjectionContractVersions.Default;

        if (!ProjectionContractVersions.IsSupported(contractVersion))
        {
            _logger.LogDebug(
                "Projection request names an unsupported contract version - {TraceId}",
                request.TraceId.Value
            );
            requestInfo.FrontendResponse = ProjectionProblem(
                EducationOrganizationProjectionProblem.UnsupportedContractVersion,
                request
            );
            return;
        }

        string bindingHash = ProjectionCursorCodec.ComputeBindingHash(
            request.Tenant,
            contractVersion,
            request.RouteQualifiers
        );

        ProjectionCursor? cursor = null;
        string? cursorText = Lookup(request, CursorParameter);

        if (
            cursorText is not null
            && !ProjectionCursorCodec.TryAccept(
                cursorText,
                dataStoreId,
                bindingHash,
                _timeProvider.GetUtcNow(),
                TimeSpan.FromMinutes(_settings.CursorLifetimeMinutes),
                out cursor,
                out ProjectionCursorRejection rejection
            )
        )
        {
            _logger.LogDebug(
                "Projection request cursor refused: {CursorRejection} - {TraceId}",
                rejection,
                request.TraceId.Value
            );
            requestInfo.FrontendResponse = ProjectionProblem(
                EducationOrganizationProjectionProblem.InvalidCursor,
                request
            );
            return;
        }

        requestInfo.EducationOrganizationProjectionRequest = new EducationOrganizationProjectionRequest(
            dataStoreId,
            limit,
            contractVersion,
            bindingHash,
            cursor
        );

        await next();
    }

    /// <summary>
    /// A parameter is repeated when the frontend reported it, or when the query dictionary itself
    /// holds more than one letter-case spelling of it.
    /// </summary>
    private static bool IsRepeated(FrontendRequest request, string name) =>
        request.RepeatedQueryParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || request.QueryParameters.Keys.Count(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
        ) > 1;

    /// <summary>
    /// The value of a parameter matched case-insensitively, or null when it is absent. Repeats have
    /// already been refused, so at most one entry matches.
    /// </summary>
    private static string? Lookup(FrontendRequest request, string name) =>
        request
            .QueryParameters.FirstOrDefault(pair =>
                string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
            )
            .Value;

    /// <summary>
    /// Decimal digits only, no sign or whitespace, from 1 to <see cref="int.MaxValue"/>.
    /// </summary>
    private static bool TryParsePositiveInt32(string? text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    /// <summary>
    /// Every failure this step writes is a problem document. The media type is part of the contract:
    /// the Configuration Service reads a problem <c>type</c> only from
    /// <c>application/problem+json</c>, so the <see cref="FrontendResponse"/> default of
    /// <c>application/json</c> would hide the type it classifies on.
    /// </summary>
    private const string ProblemContentType = "application/problem+json";

    private static FrontendResponse ParameterValidationFailed(string[] errors, FrontendRequest request) =>
        new(
            StatusCode: 400,
            Body: FailureResponse.ForParameterValidation(errors, request.TraceId),
            Headers: [],
            ContentType: ProblemContentType
        );

    private static FrontendResponse ProjectionProblem(
        EducationOrganizationProjectionProblem problem,
        FrontendRequest request
    ) =>
        new(
            StatusCode: problem.Status,
            Body: FailureResponse.ForEducationOrganizationProjection(problem, request.TraceId),
            Headers: [],
            ContentType: ProblemContentType
        );
}
