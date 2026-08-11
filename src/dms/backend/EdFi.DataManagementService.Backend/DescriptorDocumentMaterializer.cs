// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend;

internal static class DescriptorDocumentMaterializer
{
    private const string DateOnlyFormat = "yyyy-MM-dd";
    private const string IdPropertyName = "id";
    private const string EtagPropertyName = "_etag";
    private const string LastModifiedDatePropertyName = "_lastModifiedDate";

    /// <summary>
    /// Materializes a descriptor document. <paramref name="composedEtag"/> must be the fully composed
    /// served <c>_etag</c> string (see <see cref="EdFi.DataManagementService.Backend.Etag.IServedEtagComposer"/>)
    /// for <see cref="RelationalReadMaterializationMode.ExternalResponse"/> reads; the caller decides the
    /// profile-sensitivity of that value, so this materializer performs no etag composition itself. Ignored
    /// (and may be <see langword="null"/>) for stored-document and cache-projection reads.
    /// </summary>
    public static JsonObject Materialize(
        DescriptorReadRow descriptorRow,
        RelationalReadMaterializationMode materializationMode,
        string? composedEtag
    )
    {
        ArgumentNullException.ThrowIfNull(descriptorRow);

        var descriptorBody = BuildDescriptorBody(descriptorRow);

        return materializationMode switch
        {
            RelationalReadMaterializationMode.StoredDocument => descriptorBody,
            RelationalReadMaterializationMode.ExternalResponse => InjectExternalResponseMetadata(
                descriptorBody,
                descriptorRow,
                composedEtag
            ),
            RelationalReadMaterializationMode.CacheProjection => InjectCacheProjectionMetadata(
                descriptorBody,
                descriptorRow
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(materializationMode),
                materializationMode,
                "Unsupported descriptor read materialization mode."
            ),
        };
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

    private static JsonObject InjectExternalResponseMetadata(
        JsonObject descriptorBody,
        DescriptorReadRow descriptorRow,
        string? composedEtag
    )
    {
        if (composedEtag is null)
        {
            throw new InvalidOperationException(
                "Descriptor external response materialization requires a composed etag."
            );
        }

        descriptorBody[IdPropertyName] = descriptorRow.DocumentUuid.ToString();
        descriptorBody[EtagPropertyName] = composedEtag;
        descriptorBody[LastModifiedDatePropertyName] = FormatLastModifiedDate(descriptorRow);

        return descriptorBody;
    }

    private static JsonObject InjectCacheProjectionMetadata(
        JsonObject descriptorBody,
        DescriptorReadRow descriptorRow
    )
    {
        descriptorBody[IdPropertyName] = descriptorRow.DocumentUuid.ToString();
        descriptorBody[LastModifiedDatePropertyName] = FormatLastModifiedDate(descriptorRow);
        descriptorBody.Remove(EtagPropertyName);

        return descriptorBody;
    }

    private static string FormatLastModifiedDate(DescriptorReadRow descriptorRow) =>
        LastModifiedDateFormatter.Format(descriptorRow.ContentLastModifiedAt);
}
