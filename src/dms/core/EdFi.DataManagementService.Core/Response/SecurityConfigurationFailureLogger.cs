// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Response;

internal static class SecurityConfigurationFailureLogger
{
    public static void Log(
        ILogger logger,
        RequestInfo requestInfo,
        IReadOnlyList<string> errors,
        IReadOnlyList<string>? matchedResourceClaimUris = null,
        string? matchedResourceClaimName = null,
        string? assignedClaimSetName = null,
        string? cmsAction = null,
        IReadOnlyList<string>? configuredStrategyNames = null,
        SecurityConfigurationFailureDiagnostic[]? diagnostics = null
    )
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(requestInfo);
        ArgumentNullException.ThrowIfNull(errors);

        SecurityConfigurationFailureDiagnostic[] diagnosticsArray = diagnostics ?? [];

        string[] diagnosticStrategyNames = DistinctSorted(
            diagnosticsArray.SelectMany(static diagnostic => diagnostic.ConfiguredStrategyNames ?? [])
        );
        int[] diagnosticStrategyIndexes = DistinctSorted(
            diagnosticsArray.SelectMany(static diagnostic => diagnostic.ConfiguredStrategyIndexes ?? [])
        );
        string[] failureKinds = DistinctSorted(
            diagnosticsArray.Select(static diagnostic => diagnostic.ProviderOrPlannerFailureKind)
        );
        string[] diagnosticResourceFullNames = DistinctSorted(
            diagnosticsArray.Select(static diagnostic => diagnostic.ResourceFullName)
        );
        string[] targetResourceFullNames = DistinctSorted(
            diagnosticsArray.Select(static diagnostic => diagnostic.TargetResourceFullName)
        );
        string[] basisResourceFullNames = DistinctSorted(
            diagnosticsArray.Select(static diagnostic => diagnostic.BasisResourceFullName)
        );
        string[] missingPropertyNames = DistinctSorted(
            diagnosticsArray.Select(static diagnostic => diagnostic.MissingPropertyName)
        );
        string[] physicalPaths = DistinctSorted(
            diagnosticsArray.Select(static diagnostic => diagnostic.PhysicalPath)
        );

        string requestSurface =
            diagnosticsArray
                .Select(static diagnostic => diagnostic.RequestSurface)
                .FirstOrDefault(static requestSurface => !string.IsNullOrWhiteSpace(requestSurface))
            ?? BuildRequestSurface(requestInfo);
        string resolvedCmsAction = cmsAction ?? GetCmsActionName(requestInfo);
        string resourceFullName =
            diagnosticResourceFullNames.FirstOrDefault() ?? BuildResourceFullName(requestInfo);
        IReadOnlyList<string> resolvedMatchedResourceClaimUris = matchedResourceClaimUris ?? [];
        string[] resolvedStrategyNames =
            diagnosticStrategyNames.Length > 0
                ? diagnosticStrategyNames
                : DistinctSorted(configuredStrategyNames ?? requestInfo.ResourceActionAuthStrategies);

        logger.LogError(
            "SecurityConfigurationFailure. Tenant: {Tenant}; CorrelationId: {CorrelationId}; HttpMethod: {HttpMethod}; RoutePath: {RoutePath}; RequestSurface: {RequestSurface}; CmsAction: {CmsAction}; AssignedClaimSet: {AssignedClaimSet}; MatchedResourceClaimUris: {MatchedResourceClaimUris}; MatchedResourceClaimName: {MatchedResourceClaimName}; ResourceFullName: {ResourceFullName}; ConfiguredStrategyNames: {ConfiguredStrategyNames}; ConfiguredStrategyIndexes: {ConfiguredStrategyIndexes}; ProviderOrPlannerFailureKinds: {ProviderOrPlannerFailureKinds}; TargetResourceFullNames: {TargetResourceFullNames}; BasisResourceFullNames: {BasisResourceFullNames}; MissingPropertyNames: {MissingPropertyNames}; PhysicalPaths: {PhysicalPaths}; SecurityConfigurationErrors: {SecurityConfigurationErrors}",
            requestInfo.FrontendRequest.Tenant ?? string.Empty,
            requestInfo.FrontendRequest.TraceId.Value,
            requestInfo.Method.ToString(),
            requestInfo.FrontendRequest.Path,
            requestSurface,
            resolvedCmsAction,
            assignedClaimSetName ?? requestInfo.ClientAuthorizations.ClaimSetName,
            resolvedMatchedResourceClaimUris,
            matchedResourceClaimName ?? string.Empty,
            resourceFullName,
            resolvedStrategyNames,
            diagnosticStrategyIndexes,
            failureKinds,
            targetResourceFullNames,
            basisResourceFullNames,
            missingPropertyNames,
            physicalPaths,
            errors
        );
    }

    private static string[] DistinctSorted(IEnumerable<string?> values) =>
        [
            .. values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal),
        ];

    private static int[] DistinctSorted(IEnumerable<int> values) =>
        [.. values.Distinct().OrderBy(static value => value)];

    private static string BuildRequestSurface(RequestInfo requestInfo)
    {
        string operation = requestInfo.Method switch
        {
            RequestMethod.GET when requestInfo.PathComponents.HasDocumentUuidSegment => "GetById",
            RequestMethod.GET => "GetMany",
            RequestMethod.POST => "Create",
            RequestMethod.PUT => "Update",
            RequestMethod.DELETE => "Delete",
            _ => requestInfo.Method.ToString(),
        };
        string resourceKind = requestInfo.ResourceInfo.IsDescriptor ? "Descriptor" : "Resource";

        return $"{operation}{resourceKind}";
    }

    private static string GetCmsActionName(RequestInfo requestInfo) =>
        requestInfo.Method switch
        {
            RequestMethod.POST => "Create",
            RequestMethod.GET => "Read",
            RequestMethod.PUT => "Update",
            RequestMethod.DELETE => "Delete",
            _ => requestInfo.Method.ToString(),
        };

    private static string BuildResourceFullName(RequestInfo requestInfo)
    {
        string projectName = requestInfo.ResourceInfo.ProjectName.Value;
        string resourceName = requestInfo.ResourceInfo.ResourceName.Value;

        if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(resourceName))
        {
            return $"{projectName}.{resourceName}";
        }

        return resourceName;
    }
}
