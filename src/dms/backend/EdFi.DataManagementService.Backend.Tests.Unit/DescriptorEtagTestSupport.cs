// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

internal static class DescriptorEtagTestSupport
{
    public static VariantKey NoProfileNoLinksJsonVariantKey(string effectiveSchemaHash) =>
        VariantKeyFactory.Create(
            effectiveSchemaHash,
            ResponseFormat.Json,
            VariantKey.NoProfileCode,
            linksEnabled: false
        );
}
