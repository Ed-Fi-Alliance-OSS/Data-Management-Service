// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

/// <summary>One Configuration Service answer: its status, its body, and the Location of a created record.</summary>
internal sealed record ConfigurationServiceAnswer(HttpStatusCode Status, string Body, Uri? Location);

/// <summary>Sends one Configuration Service request: a method, a path relative to the service, a JSON body.</summary>
internal delegate Task<ConfigurationServiceAnswer> ConfigurationServiceCall(
    HttpMethod method,
    string path,
    string json
);

/// <summary>
/// The Configuration Service records one projection scenario creates. Each is tracked the moment the service
/// answers with its id, before any later request or check, so a scenario that fails part-way still has every
/// record it created removed. <see cref="RemoveAllAsync"/> deletes them in dependency order (applications, claim
/// sets, vendors, data stores), newest first within each, and keeps going when a deletion fails.
/// </summary>
internal sealed class ProjectionScenarioRecords(ConfigurationServiceCall configurationService)
{
    private readonly List<int> _applicationIds = [];
    private readonly List<int> _claimSetIds = [];
    private readonly List<int> _vendorIds = [];
    private readonly List<int> _dataStoreIds = [];

    public async Task<int> CreateDataStoreAsync(string connectionString)
    {
        ConfigurationServiceAnswer answer = await configurationService(
            HttpMethod.Post,
            "v3/dataStores",
            JsonSerializer.Serialize(
                new
                {
                    dataStoreType = "Test",
                    name = $"E2E Projection {Guid.NewGuid()}",
                    connectionString,
                }
            )
        );
        int id = CreatedId(answer, "creating a data store");
        _dataStoreIds.Add(id);
        return id;
    }

    public async Task<int> CreateVendorAsync(string namespacePrefixes)
    {
        ConfigurationServiceAnswer answer = await configurationService(
            HttpMethod.Post,
            "v3/vendors",
            JsonSerializer.Serialize(
                new
                {
                    company = $"E2E Projection {Guid.NewGuid()}",
                    contactName = "Projection E2E",
                    contactEmailAddress = "projection-e2e@example.com",
                    namespacePrefixes,
                }
            )
        );
        int id = CreatedId(answer, "creating a vendor");
        _vendorIds.Add(id);
        return id;
    }

    /// <summary>Creates an application and returns the credentials of its default client.</summary>
    public async Task<ClientCredentials> CreateApplicationAsync(
        int vendorId,
        string claimSetName,
        IReadOnlyList<int> dataStoreIds
    )
    {
        ConfigurationServiceAnswer answer = await configurationService(
            HttpMethod.Post,
            "v3/applications",
            JsonSerializer.Serialize(
                new
                {
                    vendorId,
                    applicationName = "E2E Projection",
                    claimSetName,
                    educationOrganizationIds = Array.Empty<long>(),
                    dataStoreIds,
                }
            )
        );
        int id = CreatedId(answer, $"creating an application on claim set {claimSetName}");
        _applicationIds.Add(id);

        JsonNode? application = JsonNode.Parse(answer.Body);
        string? key = application?["key"]?.GetValue<string>();
        string? secret = application?["secret"]?.GetValue<string>();
        if (key is null || secret is null)
        {
            throw new InvalidOperationException($"Application {id} was created without client credentials.");
        }

        return new ClientCredentials(key, secret);
    }

    /// <summary>Imports a claim set and returns its id; the claim set is tracked before the caller checks it.</summary>
    public async Task<int> ImportClaimSetAsync(JsonObject claimSet)
    {
        ConfigurationServiceAnswer answer = await configurationService(
            HttpMethod.Post,
            "v3/claimSets/import",
            claimSet.ToJsonString()
        );
        int id = CreatedId(answer, "importing a claim set");
        _claimSetIds.Add(id);
        return id;
    }

    /// <summary>
    /// Deletes every tracked record in dependency order and stops tracking them. A record already gone (404) counts
    /// as removed. Every deletion is attempted even when an earlier one fails or throws (any exception but one
    /// that leaves the process unusable); the failures are returned, each naming the record and the status or
    /// exception type, never a body or exception message that could carry a credential.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveAllAsync()
    {
        List<string> failures = [];
        foreach (
            (string collection, List<int> ids) in new[]
            {
                ("v3/applications", _applicationIds),
                ("v3/claimSets", _claimSetIds),
                ("v3/vendors", _vendorIds),
                ("v3/dataStores", _dataStoreIds),
            }
        )
        {
            foreach (int id in Enumerable.Reverse(ids).ToArray())
            {
                string path = $"{collection}/{id}";
                try
                {
                    ConfigurationServiceAnswer answer = await configurationService(
                        HttpMethod.Delete,
                        path,
                        string.Empty
                    );
                    if (
                        answer.Status
                        is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound)
                    )
                    {
                        failures.Add($"deleting {path} answered {(int)answer.Status}");
                    }
                }
                catch (Exception exception)
                    when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    failures.Add($"deleting {path} failed with {exception.GetType().Name}");
                }
            }

            ids.Clear();
        }

        return failures;
    }

    /// <summary>
    /// The id of a record the service answered 201 for: the last Location segment, otherwise the body's
    /// <c>id</c>. Any other answer created nothing to track and throws.
    /// </summary>
    private static int CreatedId(ConfigurationServiceAnswer answer, string action)
    {
        if (answer.Status != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"Setup failed: {action} answered {(int)answer.Status}.");
        }

        if (
            answer.Location is { } location
            && int.TryParse(
                location.OriginalString.TrimEnd('/').Split('/')[^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int fromLocation
            )
        )
        {
            return fromLocation;
        }

        try
        {
            if (
                JsonNode.Parse(answer.Body)?["id"] is JsonValue idValue
                && idValue.TryGetValue(out int fromBody)
            )
            {
                return fromBody;
            }
        }
        catch (JsonException)
        {
            // Not JSON: no id in the body either.
        }

        throw new InvalidOperationException($"Setup failed: {action} answered 201 with no record id.");
    }
}
