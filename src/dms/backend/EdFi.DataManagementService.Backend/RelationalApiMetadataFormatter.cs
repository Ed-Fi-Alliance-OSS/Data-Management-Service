// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalApiMetadataFormatter
{
    public static string FormatEtag(JsonNode document) => ResourceEtagFormatter.FormatEtag(document);

    public static string FormatEtag(ExtractedDescriptorBody descriptorBody)
    {
        ArgumentNullException.ThrowIfNull(descriptorBody);

        return FormatEtag(BuildCanonicalDescriptorDocument(descriptorBody));
    }

    private static JsonObject BuildCanonicalDescriptorDocument(ExtractedDescriptorBody descriptorBody)
    {
        var document = new JsonObject
        {
            ["namespace"] = descriptorBody.Namespace,
            ["codeValue"] = descriptorBody.CodeValue,
        };

        if (descriptorBody.ShortDescription is not null)
        {
            document["shortDescription"] = descriptorBody.ShortDescription;
        }

        if (descriptorBody.Description is not null)
        {
            document["description"] = descriptorBody.Description;
        }

        if (descriptorBody.EffectiveBeginDate is DateOnly effectiveBeginDate)
        {
            document["effectiveBeginDate"] = effectiveBeginDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        if (descriptorBody.EffectiveEndDate is DateOnly effectiveEndDate)
        {
            document["effectiveEndDate"] = effectiveEndDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        return document;
    }
}
