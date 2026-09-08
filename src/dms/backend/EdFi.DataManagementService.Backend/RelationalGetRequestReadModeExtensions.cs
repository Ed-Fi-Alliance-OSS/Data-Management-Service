// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalGetRequestReadModeExtensions
{
    public static RelationalReadMaterializationMode ToMaterializationMode(
        this RelationalGetRequestReadMode readMode
    ) =>
        readMode switch
        {
            RelationalGetRequestReadMode.StoredDocument => RelationalReadMaterializationMode.StoredDocument,
            RelationalGetRequestReadMode.ExternalResponse =>
                RelationalReadMaterializationMode.ExternalResponse,
            _ => throw new ArgumentOutOfRangeException(
                nameof(readMode),
                readMode,
                "Unsupported relational GET read mode."
            ),
        };
}
