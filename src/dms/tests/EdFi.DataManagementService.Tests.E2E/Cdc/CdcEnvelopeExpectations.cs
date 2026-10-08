// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Independent expected public state, built solely from API bodies and canonical metadata.
/// These two scenario resources contain no document references needing stream-link reconstruction.</summary>
internal static class CdcEnvelopeExpectations
{
    public static JsonElement Create(CdcApiResource resource, JsonObject apiBody, CdcSourceDocument source)
    {
        string resourceName = resource switch
        {
            CdcApiResource.Student => "Student",
            CdcApiResource.SchoolTypeDescriptor => "SchoolTypeDescriptor",
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };
        if (source.ResourceName != resourceName || source.ResourceKeyId <= 0 || source.ContentVersion <= 0)
        {
            throw new InvalidOperationException("Canonical source metadata does not match the API resource.");
        }
        JsonObject document = (JsonObject)apiBody.DeepClone();
        string uuid = source.DocumentUuid.ToString("D");
        string timestamp = source.ContentLastModifiedAt.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture
        );
        document["id"] = uuid;
        document["_lastModifiedDate"] = timestamp;
        // Never copy the HTTP ETag: ordinary API links may be disabled, while the stream uses links.
        document["_etag"] = new ServedEtagComposer().Compose(
            new ServedEtagContext(
                source.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: resource == CdcApiResource.Student,
                source.ContentVersion
            )
        );
        return JsonSerializer.SerializeToElement(
            new JsonObject
            {
                ["contractVersion"] = 1,
                ["documentUuid"] = uuid,
                ["projectName"] = source.ProjectName,
                ["resourceName"] = source.ResourceName,
                ["resourceVersion"] = source.ResourceVersion,
                ["contentVersion"] = source.ContentVersion,
                ["lastModifiedAt"] = timestamp,
                ["document"] = document,
            }
        );
    }
}
