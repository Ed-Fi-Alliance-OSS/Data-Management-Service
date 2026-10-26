// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// The published descriptions, response descriptions and examples of the /v3/apiClients
/// operations. The examples assume Application 1 holds education organizations 255901 and 255902.
/// </summary>
internal static class ApiClientOpenApiMetadata
{
    private const string ScopeRules =
        "educationOrganizationIds and inheritsApplicationEducationOrganizations set the API client's "
        + "education organization scope. A list without the flag, or with false, narrows the client to "
        + "that list, even when it equals the Application's list; duplicate ids are removed. true "
        + "without a list, or with a list equal to the Application's non-empty list in any order, makes the "
        + "client inherit its Application's education organizations, and its effective list follows later "
        + "changes to them. "
        + "400 (urn:ed-fi:management-api:bad-request:data, with the error on the field concerned) "
        + "rejects an empty or null list, a null flag, false without a list, true with any other list, "
        + "and a list with an education organization that is not on the client's Application, its new "
        + "Application on a move. A successful create or update applies the client's effective list to "
        + "tokens requested afterwards; tokens already issued keep their claims until they expire.";

    private const string ApplicationSync =
        "An Application update (PUT /v3/applications/{id}) refreshes the identity provider claims of "
        + "the Application's first API client only. Every client's effective list reflects the "
        + "Application change at once, but another client's tokens can keep its earlier list until "
        + "that client is updated with PUT /v3/apiClients/{id}.";

    public const string InsertDescription =
        "Creates an API client of an Application and returns its credentials. "
        + ScopeRules
        + " With neither field the client inherits. "
        + ApplicationSync;

    public const string UpdateDescription =
        "Updates an API client. "
        + ScopeRules
        + " With neither field the client keeps its flag and list. An update that moves the client "
        + "(a new applicationId) with neither field moves an inheriting client to the new "
        + "Application's list; a narrowed client keeps its list when every education organization in "
        + "it is on the new Application, and otherwise the update is rejected with 409 "
        + "urn:ed-fi:management-api:conflict:education-organization-scope and nothing changes. To move "
        + "it anyway, send educationOrganizationIds on the new Application or "
        + "inheritsApplicationEducationOrganizations true. dataStoreIds is a full replacement, "
        + "independent of the scope. "
        + ApplicationSync;

    public const string ReadDescription =
        "inheritsApplicationEducationOrganizations and educationOrganizationIds report the API "
        + "client's scope. educationOrganizationIds is its effective list, in ascending order: its "
        + "Application's current list when it inherits, its own list when it is narrowed. It is never "
        + "null; [] means no education organization access, which a narrowed client reaches only when "
        + "every one of its education organizations is removed from its Application. "
        + ApplicationSync;

    public const string ResetDescription =
        "Generates a new secret for the API client; the previous secret stops working. Only the "
        + "secret changes: the client's education organizations, including a narrowed list, and its "
        + "other claims are kept as they are.";

    public const string Unauthorized = "No valid access token was sent.";
    public const string Forbidden =
        "The access token lacks the service role or the scope this operation requires.";
    public const string ServerError = "An unexpected error occurred.";

    public const string UpdateServerError =
        "An unexpected error occurred, including an identity provider that reports, for this update, "
        + "a client already associated with another API client; that client is left untouched.";

    public const string InvalidId =
        "The id is not an integer (urn:ed-fi:management-api:bad-request:parameter).";
    public const string NotFound = "The API client does not exist.";
    public const string ConcurrentChange =
        "A concurrent change to the API client's Application held the request up; retry the request.";

    public const string InsertBadRequest =
        "The request is invalid: a field failed validation, another API client of the Application "
        + "already has the name (on Name), or the education organization scope breaks a rule above "
        + "(urn:ed-fi:management-api:bad-request:data, with the error on the field concerned).";

    public const string InsertConflict =
        "The Application or a data store does not exist "
        + "(urn:ed-fi:management-api:conflict:unresolved-reference), or the generated client key is "
        + "already in use, or the identity provider client the request created is already associated "
        + "with another API client (urn:ed-fi:management-api:conflict); retry the request after a key "
        + "collision.";

    public const string InsertBadGateway =
        "The identity provider reported a failure; no API client record was saved.";

    public const string UpdateBadRequest =
        "The request is invalid: the id is not an integer (urn:ed-fi:management-api:bad-request:parameter), "
        + "a field failed validation, the route and body ids differ, another "
        + "API client of the Application already has the name (on Name), or the education organization "
        + "scope breaks a rule above (urn:ed-fi:management-api:bad-request:data, with the error on the "
        + "field concerned).";

    public const string UpdateConflict =
        "The Application or a data store does not exist "
        + "(urn:ed-fi:management-api:conflict:unresolved-reference); a narrowed client moved with "
        + "neither scope field is not covered by its new Application "
        + "(urn:ed-fi:management-api:conflict:education-organization-scope); or a concurrent change to "
        + "the Application held the request up, in which case retry the request "
        + "(urn:ed-fi:management-api:conflict).";

    public const string UpdateBadGateway =
        "The identity provider reported a failure before the API client record was updated; the record "
        + "is unchanged, and the identity provider's state is not confirmed. Retry the update.";

    public const string DeleteBadGateway =
        "The identity provider reported a failure deleting the client; the API client record is kept. "
        + "Retry the delete.";

    public const string ResetBadGateway =
        "The identity provider reported a failure, so no new secret is returned and whether the "
        + "previous secret still works is not confirmed. Retry the reset.";

    public const string CollectionBadRequest = "A paging or ordering parameter is invalid.";

    public static void SetResponseDescriptions(
        OpenApiOperation operation,
        params (string Status, string Description)[] descriptions
    )
    {
        foreach ((string status, string description) in descriptions)
        {
            if (
                operation.Responses is not null
                && operation.Responses.TryGetValue(status, out var iResponse)
                && iResponse is OpenApiResponse response
            )
            {
                response.Description = description;
            }
        }
    }

    public static void SetInsertExamples(OpenApiOperation operation)
    {
        SetRequestExamples(
            operation,
            new()
            {
                ["inherit"] = (
                    "An identity-only client that inherits its Application's education organizations",
                    """
                    {
                      "applicationId": 1,
                      "name": "Identity Only Client",
                      "isApproved": true,
                      "dataStoreIds": []
                    }
                    """
                ),
                ["narrowed"] = (
                    "A client narrowed to one of its Application's education organizations",
                    """
                    {
                      "applicationId": 1,
                      "name": "LEA 255901 Client",
                      "isApproved": true,
                      "dataStoreIds": [],
                      "educationOrganizationIds": [255901]
                    }
                    """
                ),
                ["explicitInherit"] = (
                    "A client that inherits, stated explicitly",
                    """
                    {
                      "applicationId": 1,
                      "name": "Inheriting Client",
                      "isApproved": true,
                      "dataStoreIds": [],
                      "inheritsApplicationEducationOrganizations": true
                    }
                    """
                ),
            }
        );
        SetResponseExamples(
            operation,
            "201",
            new()
            {
                ["credentials"] = (
                    "The new API client's credentials",
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "LEA 255901 Client",
                      "key": "6f3f0c2e-6d0b-4f4c-9a51-2f8f0a8d3c11",
                      "secret": "s3cr3t-Example-Value-Of-Minimum-Length-32!"
                    }
                    """
                ),
            }
        );
    }

    public static void SetUpdateExamples(OpenApiOperation operation) =>
        SetRequestExamples(
            operation,
            new()
            {
                ["keepScope"] = (
                    "Change other fields and keep the client's flag and list",
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Identity Only Client",
                      "isApproved": true,
                      "dataStoreIds": []
                    }
                    """
                ),
                ["narrow"] = (
                    "Narrow the client to one of its Application's education organizations",
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "LEA 255901 Client",
                      "isApproved": true,
                      "dataStoreIds": [],
                      "educationOrganizationIds": [255901]
                    }
                    """
                ),
                ["inherit"] = (
                    "Make the client inherit its Application's education organizations",
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Inheriting Client",
                      "isApproved": true,
                      "dataStoreIds": [],
                      "inheritsApplicationEducationOrganizations": true
                    }
                    """
                ),
                ["roundTrip"] = (
                    "Send back an inheriting client as read, which keeps it inheriting",
                    """
                    {
                      "id": 1,
                      "applicationId": 1,
                      "name": "Inheriting Client",
                      "isApproved": true,
                      "dataStoreIds": [],
                      "inheritsApplicationEducationOrganizations": true,
                      "educationOrganizationIds": [255901, 255902]
                    }
                    """
                ),
            }
        );

    private const string InheritingClient = """
        {
          "id": 1,
          "applicationId": 1,
          "clientId": "6f3f0c2e-6d0b-4f4c-9a51-2f8f0a8d3c11",
          "clientUuid": "0b8c1f6e-3d3a-4b8e-9b5e-6a1f2c3d4e5f",
          "name": "Inheriting Client",
          "isApproved": true,
          "creatorOwnershipTokenId": null,
          "ownershipTokenIds": [],
          "dataStoreIds": [1],
          "inheritsApplicationEducationOrganizations": true,
          "educationOrganizationIds": [255901, 255902]
        }
        """;

    private const string NarrowedClient = """
        {
          "id": 2,
          "applicationId": 1,
          "clientId": "9a2d7e41-1c5b-4f0e-8d3a-7b6c5d4e3f21",
          "clientUuid": "5e4d3c2b-1a0f-4e9d-8c7b-6a5f4e3d2c1b",
          "name": "LEA 255901 Client",
          "isApproved": true,
          "creatorOwnershipTokenId": null,
          "ownershipTokenIds": [],
          "dataStoreIds": [1],
          "inheritsApplicationEducationOrganizations": false,
          "educationOrganizationIds": [255901]
        }
        """;

    private const string EmptyNarrowedClient = """
        {
          "id": 3,
          "applicationId": 1,
          "clientId": "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f",
          "clientUuid": "f0e1d2c3-b4a5-4968-8776-655443322110",
          "name": "LEA 255903 Client",
          "isApproved": true,
          "creatorOwnershipTokenId": null,
          "ownershipTokenIds": [],
          "dataStoreIds": [],
          "inheritsApplicationEducationOrganizations": false,
          "educationOrganizationIds": []
        }
        """;

    public static void SetReadItemExamples(OpenApiOperation operation) =>
        SetResponseExamples(
            operation,
            "200",
            new()
            {
                ["inheriting"] = ("An inheriting client", InheritingClient),
                ["narrowed"] = ("A narrowed client", NarrowedClient),
                ["narrowedToNothing"] = (
                    "A narrowed client whose education organizations were all removed from its Application",
                    EmptyNarrowedClient
                ),
            }
        );

    public static void SetReadCollectionExamples(OpenApiOperation operation) =>
        SetResponseExamples(
            operation,
            "200",
            new()
            {
                ["clients"] = (
                    "An inheriting, a narrowed and an emptied narrowed client",
                    $"[{InheritingClient}, {NarrowedClient}, {EmptyNarrowedClient}]"
                ),
            }
        );

    private static void SetRequestExamples(
        OpenApiOperation operation,
        Dictionary<string, (string Summary, string Value)> examples
    )
    {
        if (
            operation.RequestBody is OpenApiRequestBody requestBody
            && requestBody.Content is not null
            && requestBody.Content.TryGetValue("application/json", out var mediaType)
        )
        {
            mediaType.Example = null;
            mediaType.Examples = ToExamples(examples);
        }
    }

    private static void SetResponseExamples(
        OpenApiOperation operation,
        string status,
        Dictionary<string, (string Summary, string Value)> examples
    )
    {
        if (
            operation.Responses is not null
            && operation.Responses.TryGetValue(status, out var iResponse)
            && iResponse is OpenApiResponse response
            && response.Content is not null
            && response.Content.TryGetValue("application/json", out var mediaType)
        )
        {
            mediaType.Example = null;
            mediaType.Examples = ToExamples(examples);
        }
    }

    private static Dictionary<string, IOpenApiExample> ToExamples(
        Dictionary<string, (string Summary, string Value)> examples
    ) =>
        examples.ToDictionary(
            example => example.Key,
            example =>
                (IOpenApiExample)
                    new OpenApiExample
                    {
                        Summary = example.Value.Summary,
                        Value = JsonNode.Parse(example.Value.Value),
                    }
        );
}
