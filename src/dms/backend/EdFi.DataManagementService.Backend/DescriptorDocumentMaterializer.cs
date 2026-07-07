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

    /// <summary>
    /// Materializes a descriptor document. <paramref name="composedEtag"/> must be the fully composed
    /// served <c>_etag</c> string (see <see cref="EdFi.DataManagementService.Backend.Etag.IServedEtagComposer"/>)
    /// for <see cref="RelationalGetRequestReadMode.ExternalResponse"/> reads; the caller decides the
    /// profile-sensitivity of that value, so this materializer performs no etag composition itself. Ignored
    /// (and may be <see langword="null"/>) for <see cref="RelationalGetRequestReadMode.StoredDocument"/> reads.
    /// </summary>
    public static JsonObject Materialize(
        DescriptorReadRow descriptorRow,
        RelationalGetRequestReadMode readMode,
        string? composedEtag
    )
    {
        ArgumentNullException.ThrowIfNull(descriptorRow);

        var descriptorBody = BuildDescriptorBody(descriptorRow);

        if (readMode == RelationalGetRequestReadMode.StoredDocument)
        {
            return descriptorBody;
        }

        if (composedEtag is null)
        {
            throw new InvalidOperationException(
                "Descriptor external response materialization requires a composed etag."
            );
        }

        var externalResponse = (JsonObject)descriptorBody.DeepClone();

        externalResponse[IdPropertyName] = descriptorRow.DocumentUuid.ToString();
        externalResponse[EtagPropertyName] = composedEtag;
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
