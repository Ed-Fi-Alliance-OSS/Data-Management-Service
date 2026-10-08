// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Extracted descriptor field values ready for SQL parameter binding.
/// </summary>
/// <param name="Namespace">The descriptor namespace (e.g., <c>uri://ed-fi.org/AcademicSubjectDescriptor</c>).</param>
/// <param name="CodeValue">The descriptor code value (e.g., <c>English</c>).</param>
/// <param name="ShortDescription">The short description (may be <c>null</c> when not provided).</param>
/// <param name="Description">The long description (may be <c>null</c> when not provided).</param>
/// <param name="EffectiveBeginDate">The optional effective begin date.</param>
/// <param name="EffectiveEndDate">The optional effective end date.</param>
public sealed record ExtractedDescriptorBody(
    string Namespace,
    string CodeValue,
    string? ShortDescription,
    string? Description,
    DateOnly? EffectiveBeginDate,
    DateOnly? EffectiveEndDate
)
{
    /// <summary>
    /// The whole descriptor URI reconstructed in original case for identity comparisons.
    /// </summary>
    public string Uri => $"{Namespace}#{CodeValue}";
}

/// <summary>
/// Extracts stored descriptor field values from a validated request body.
/// </summary>
public static class DescriptorWriteBodyExtractor
{
    /// <summary>
    /// Extracts descriptor fields from <paramref name="requestBody" />.
    /// </summary>
    /// <remarks>
    /// This method assumes the request body has already passed JSON schema validation
    /// in the pipeline. Missing required fields indicate an internal pipeline bug.
    /// </remarks>
    public static ExtractedDescriptorBody Extract(JsonNode requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var ns =
            requestBody["namespace"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Descriptor request body is missing required field 'namespace'. "
                    + "This indicates JSON schema validation did not run."
            );

        var codeValue =
            requestBody["codeValue"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Descriptor request body is missing required field 'codeValue'. "
                    + "This indicates JSON schema validation did not run."
            );

        var shortDescription = requestBody["shortDescription"]?.GetValue<string>();
        var description = requestBody["description"]?.GetValue<string>();
        var effectiveBeginDate = ParseDateOnly(requestBody["effectiveBeginDate"]);
        var effectiveEndDate = ParseDateOnly(requestBody["effectiveEndDate"]);

        return new ExtractedDescriptorBody(
            ns,
            codeValue,
            shortDescription,
            description,
            effectiveBeginDate,
            effectiveEndDate
        );
    }

    private static DateOnly? ParseDateOnly(JsonNode? node)
    {
        if (node?.GetValue<string>() is not string dateString)
        {
            return null;
        }

        return DateOnly.ParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
