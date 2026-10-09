// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>A projection page that parsed strictly.</summary>
internal sealed record ProjectionPage(
    string ContractVersion,
    int DataStoreId,
    string? NextCursor,
    IReadOnlyList<EducationOrganizationProjectionItem> Items
);

/// <summary>
/// Strict parsing of a 200 projection page (DMS-1440 spec §5.4): the four page members and the five item members are
/// all required, nullable ones included; names match case-sensitively; unknown members, duplicate members, comments,
/// trailing commas, numbers in strings, fractions, out-of-range numbers, <c>null</c> where the contract has no
/// <c>null</c>, a <c>null</c> item and an empty <c>nextCursor</c> are refused. Values are not checked against each
/// other or the request; the reader does that.
/// </summary>
internal static class ProjectionResponseParser
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>The page members as they arrive; <c>items</c> may hold a <c>null</c>, which the parser refuses.</summary>
    private sealed record PageDocument(
        string ContractVersion,
        int DataStoreId,
        string? NextCursor,
        List<EducationOrganizationProjectionItem?> Items
    );

    /// <summary>The page, or <c>null</c> when the body breaks any rule above.</summary>
    public static ProjectionPage? Parse(ReadOnlySpan<byte> body)
    {
        PageDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PageDocument>(body, Options);
        }
        catch (JsonException)
        {
            // Malformed or non-conforming JSON, or invalid UTF-8 or UTF-16 inside a string: the serializer reports
            // the reader's own InvalidOperationException for those as a JsonException.
            return null;
        }

        if (document is null || document.NextCursor is "" || document.Items.Contains(null))
        {
            return null;
        }

        return new ProjectionPage(
            document.ContractVersion,
            document.DataStoreId,
            document.NextCursor,
            document.Items!
        );
    }
}
