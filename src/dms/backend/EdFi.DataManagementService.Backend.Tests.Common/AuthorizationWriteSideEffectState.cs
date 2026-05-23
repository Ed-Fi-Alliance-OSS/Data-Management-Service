// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal sealed record AuthorizationDocumentState(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    long IdentityVersion,
    DateTime ContentLastModifiedAt,
    DateTime IdentityLastModifiedAt,
    DateTime CreatedAt
);

internal sealed record AuthorizationResourceTableState(
    string TableName,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows
);

internal sealed record AuthorizationWriteSideEffectState(
    AuthorizationDocumentState Document,
    IReadOnlyList<AuthorizationResourceTableState> ResourceTables,
    IReadOnlyList<ReferentialIdentityRow> ReferentialIdentities,
    long DocumentChangeEventCount
);
