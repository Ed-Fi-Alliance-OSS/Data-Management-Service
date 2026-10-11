// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class DocumentCacheMaterializerDescriptorMappingSet
{
    public static MappingSet Create(SqlDialect dialect) =>
        DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(dialect);
}
