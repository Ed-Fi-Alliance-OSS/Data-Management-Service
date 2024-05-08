// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.Model;

namespace EdFi.DataManagementService.Api.Core.Backend;

public record UpdateRequest(
    ReferentialId ReferentialId,
    ResourceInfo ResourceInfo,
    DocumentInfo DocumentInfo,
    JsonNode EdfiDoc,
    bool validateDocumentReferencesExist,
    TraceId TraceId,
    DocumentUuid DocumentUuid
)
    : UpsertRequest(
        ReferentialId,
        ResourceInfo,
        DocumentInfo,
        EdfiDoc,
        validateDocumentReferencesExist,
        TraceId
    );
