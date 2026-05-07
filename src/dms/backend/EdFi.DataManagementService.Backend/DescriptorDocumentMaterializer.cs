// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class DescriptorDocumentMaterializer
{
    private const string DateOnlyFormat = "yyyy-MM-dd";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";
    private const string IdPropertyName = "id";
    private const string EtagPropertyName = "_etag";
    private const string LastModifiedDatePropertyName = "_lastModifiedDate";

    public static JsonObject Materialize(
        DescriptorReadRow descriptorRow,
        RelationalGetRequestReadMode readMode
    )
    {
        ArgumentNullException.ThrowIfNull(descriptorRow);

        var descriptorBody = BuildDescriptorBody(descriptorRow);

        if (readMode == RelationalGetRequestReadMode.StoredDocument)
        {
            return descriptorBody;
        }

        var externalResponse = (JsonObject)descriptorBody.DeepClone();

        externalResponse[IdPropertyName] = descriptorRow.DocumentUuid.ToString();
        externalResponse[EtagPropertyName] = RelationalApiMetadataFormatter.FormatEtag(descriptorBody);
        externalResponse[LastModifiedDatePropertyName] =
            descriptorRow.ContentLastModifiedAt.UtcDateTime.ToString(
                LastModifiedDateFormat,
                CultureInfo.InvariantCulture
            );

        return externalResponse;
    }

    private static JsonObject BuildDescriptorBody(DescriptorReadRow descriptorRow)
    {
        var descriptorBody = new JsonObject
        {
            ["namespace"] = descriptorRow.Namespace,
            ["codeValue"] = descriptorRow.CodeValue,
            ["shortDescription"] = descriptorRow.ShortDescription,
        };

        if (descriptorRow.Description is not null)
        {
            descriptorBody["description"] = descriptorRow.Description;
        }

        if (descriptorRow.EffectiveBeginDate is DateOnly effectiveBeginDate)
        {
            descriptorBody["effectiveBeginDate"] = effectiveBeginDate.ToString(
                DateOnlyFormat,
                CultureInfo.InvariantCulture
            );
        }

        if (descriptorRow.EffectiveEndDate is DateOnly effectiveEndDate)
        {
            descriptorBody["effectiveEndDate"] = effectiveEndDate.ToString(
                DateOnlyFormat,
                CultureInfo.InvariantCulture
            );
        }

        return descriptorBody;
    }
}
