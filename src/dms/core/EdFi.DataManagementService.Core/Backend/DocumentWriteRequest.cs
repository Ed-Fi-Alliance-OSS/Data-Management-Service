// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The members shared by requests that write a whole document to a document repository. Authorization
/// strategies are not among them: a PUT carries its Update evaluators, while a POST carries a policy per
/// action because it learns which action it performs only once the write observes its target.
/// </summary>
internal abstract record DocumentWriteRequest(
    /// <summary>
    /// The ResourceInfo of the document to write
    /// </summary>
    ResourceInfo ResourceInfo,
    /// <summary>
    /// The DocumentInfo of the document to write
    /// </summary>
    DocumentInfo DocumentInfo,
    /// <summary>
    /// The resolved runtime mapping set for the active relational request.
    /// </summary>
    MappingSet MappingSet,
    /// <summary>
    /// The EdfiDoc of the document to write, as a JsonNode
    /// </summary>
    JsonNode EdfiDoc,
    /// <summary>
    /// Request Header provided by the frontend service as a dictionary
    /// </summary>
    Dictionary<string, string> Headers,
    /// <summary>
    /// The request TraceId
    /// </summary>
    TraceId TraceId,
    /// <summary>
    /// The DocumentUuid of the document to write
    /// </summary>
    DocumentUuid DocumentUuid,
    /// <summary>
    /// Optional profile write context when a writable profile applies.
    /// </summary>
    BackendProfileWriteContext? BackendProfileWriteContext = null,
    /// <summary>
    /// The normalized request tenant key.
    /// </summary>
    string TenantKey = ""
) : IDocumentWriteRequest
{
    public WritePrecondition WritePrecondition { get; init; } = WritePreconditionFactory.Create(Headers);

    public RelationalAuthorizationContext AuthorizationContext { get; init; } =
        new RelationalAuthorizationContext([]);
}
