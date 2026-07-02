// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// The fixed <see cref="VariantKey"/> for a descriptor representation. Descriptors have no readable
/// profile (profileCode "_") and no reference links (their served bytes never vary with the
/// ResourceLinks flag, so linkFlag is the constant "n"); only ContentVersion and schemaEpoch are
/// state-significant for a descriptor's If-Match.
/// </summary>
public static class DescriptorVariantKey
{
    public static VariantKey For(string effectiveSchemaHash) =>
        VariantKeyFactory.Create(
            effectiveSchemaHash,
            ResponseFormat.Json,
            VariantKey.NoProfileCode,
            linksEnabled: false
        );
}
