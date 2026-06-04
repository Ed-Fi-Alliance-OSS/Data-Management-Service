// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

public static class SecurityConfigurationProblemDetails
{
    public const string Type = "urn:ed-fi:api:system:configuration:security";
    public const string Title = "Security Configuration Error";
    public const int Status = 500;

    public const string Detail =
        "A security configuration problem was detected. The request cannot be authorized.";
}

public static class SecurityConfigurationFailureMessages
{
    public const string MissingSecurityMetadata =
        "No security metadata has been configured for this resource.";

    public static string NoAuthorizationStrategies(
        string actionName,
        IEnumerable<string> matchedResourceClaimUris,
        string matchedResourceClaimName
    ) =>
        $"No authorization strategies were defined for the requested action '{actionName}' against resource URIs {FormatBracketedQuotedList(matchedResourceClaimUris)} matched by the caller's claim '{matchedResourceClaimName}'.";

    public static string UnknownAuthorizationStrategies(IEnumerable<string> unavailableStrategyNames) =>
        $"Could not find authorization strategy implementations for the following strategy names: {FormatQuotedCsv(DistinctInFirstOccurrenceOrder(unavailableStrategyNames))}.";

    public static string CustomViewBasisPropertyUnavailable(
        string targetEntityName,
        string propertyName,
        string basisEntityName
    ) =>
        $"Unable to find a property on the authorization subject entity type '{targetEntityName}' corresponding to the '{propertyName}' property on the custom authorization view's basis entity type '{basisEntityName}' in order to perform authorization. Should a different authorization strategy be used?";

    private static string FormatBracketedQuotedList(IEnumerable<string> values) =>
        $"[{FormatQuotedCsv(values)}]";

    private static string[] DistinctInFirstOccurrenceOrder(IEnumerable<string> values)
    {
        HashSet<string> seenValues = new(StringComparer.Ordinal);
        return [.. values.Where(seenValues.Add)];
    }

    private static string FormatQuotedCsv(IEnumerable<string> values)
    {
        string[] valuesArray = values.ToArray();
        if (valuesArray.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        return $"'{string.Join("', '", valuesArray)}'";
    }
}

/// <summary>
/// Optional structured context for security-configuration failures that originate below the Core HTTP
/// response boundary. Core combines these fields with request context when it emits the response log.
/// </summary>
public sealed record SecurityConfigurationFailureDiagnostic(
    string? ProviderOrPlannerFailureKind = null,
    string? ResourceFullName = null,
    string[]? ConfiguredStrategyNames = null,
    int[]? ConfiguredStrategyIndexes = null,
    string? RequestSurface = null,
    string? CmsAction = null,
    string? TargetResourceFullName = null,
    string? BasisResourceFullName = null,
    string? MissingPropertyName = null,
    string? PhysicalPath = null
);
