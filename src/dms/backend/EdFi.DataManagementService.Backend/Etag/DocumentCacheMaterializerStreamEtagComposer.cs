// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Etag;

internal static class DocumentCacheMaterializerStreamEtagComposer
{
    public static string ComposeForResource(
        IServedEtagComposer servedEtagComposer,
        MappingSet mappingSet,
        long contentVersion
    ) => Compose(servedEtagComposer, mappingSet, contentVersion, linksEnabled: true);

    public static string ComposeForDescriptor(
        IServedEtagComposer servedEtagComposer,
        MappingSet mappingSet,
        long contentVersion
    ) => Compose(servedEtagComposer, mappingSet, contentVersion, linksEnabled: false);

    private static string Compose(
        IServedEtagComposer servedEtagComposer,
        MappingSet mappingSet,
        long contentVersion,
        bool linksEnabled
    )
    {
        ArgumentNullException.ThrowIfNull(servedEtagComposer);
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (contentVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentVersion),
                contentVersion,
                "ContentVersion must be positive."
            );
        }

        return servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                linksEnabled,
                contentVersion,
                ResponseContentCoding.Identity
            )
        );
    }
}
