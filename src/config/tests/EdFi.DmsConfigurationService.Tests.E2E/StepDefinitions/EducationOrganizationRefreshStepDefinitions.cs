// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

/// <summary>
/// The education organization refresh contract as the deployed stack answers it: the queued-job response and the
/// status of the job it names.
/// </summary>
public partial class StepDefinitions
{
    private const string RefreshJobIdKey = "jobId";

    /// <summary>Sends a bodiless POST with one extra header, such as the tenant header.</summary>
    [When("a POST request is made to {string} with header {string} value {string} and no body")]
    public async Task WhenAPostRequestIsMadeToWithHeaderAndNoBody(string url, string header, string value)
    {
        url = await ReplaceIdsAsync(url);
        var headers = new Dictionary<string, string>(_authHeaders, StringComparer.OrdinalIgnoreCase)
        {
            [header] = await ReplaceIdsAsync(value),
        };
        _apiResponse = await playwrightContext.ApiRequestContext!.PostAsync(url, new() { Headers = headers });
        await ExtractIdFromHeader(_apiResponse);
    }

    /// <summary>
    /// The 202 jobQueuedResult: exactly a job identifier and <paramref name="message"/>, and a Location that is the
    /// relative status route of that same job. The identifier is captured as {jobId}.
    /// </summary>
    [Then("the response queues a refresh job with message {string}")]
    public async Task ThenTheResponseQueuesARefreshJobWithMessage(string message)
    {
        string text = await _apiResponse.TextAsync();
        _apiResponse.Status.Should().Be(202, text);
        _apiResponse.Headers["content-type"].Should().StartWith("application/json");

        JsonObject body = JsonNode.Parse(text)!.AsObject();
        body.Select(property => property.Key).Should().Equal(["jobId", "message"], text);
        body["message"]!.GetValue<string>().Should().Be(message);
        string jobId = body["jobId"]!.GetValue<string>();
        Guid.TryParse(jobId, out _).Should().BeTrue($"the job identifier '{jobId}' should be a GUID");

        _apiResponse
            .Headers.TryGetValue("location", out string? location)
            .Should()
            .BeTrue("the queued response should carry a Location header");
        location
            .Should()
            .Be($"/v3/jobs/{jobId}", "Location is the job's status route, relative to the path base");
        _ids[RefreshJobIdKey] = jobId;
    }

    /// <summary>The job status response names the captured job, in <paramref name="status"/>, not finished.</summary>
    [Then("the response reports the captured refresh job as {string} and unfinished")]
    public async Task ThenTheResponseReportsTheCapturedRefreshJobAs(string status)
    {
        string text = await _apiResponse.TextAsync();
        _apiResponse.Status.Should().Be(200, text);

        JsonObject body = JsonNode.Parse(text)!.AsObject();
        body["jobId"]!.GetValue<string>().Should().Be(_ids[RefreshJobIdKey]);
        body["status"]!.GetValue<string>().Should().Be(status);
        body["createdAt"]!.GetValue<DateTime>().Should().BeAfter(DateTime.MinValue);
        body["finishedAt"].Should().BeNull(text);
        body["errorMessage"].Should().BeNull(text);
    }

    /// <summary>
    /// The current response is exactly the tenant aggregate of <paramref name="tenantName"/>: each data store the
    /// same stack lists at <c>/v3/dataStores</c>, in identifier order, unmanaged and with no published education
    /// organizations, since no refresh runs in this stack. The list must include the store named
    /// <paramref name="dataStoreName"/>, so the comparison never passes on two empty lists.
    /// </summary>
    [Then("the response is the {string} tenant aggregate of every data store, including {string}")]
    public async Task ThenTheResponseIsTheTenantAggregateOfEveryDataStore(
        string tenantName,
        string dataStoreName
    )
    {
        string text = await _apiResponse.TextAsync();
        _apiResponse.Status.Should().Be(200, text);
        dataStoreName = await ReplaceIdsAsync(dataStoreName);

        JsonArray stores = await ListDataStoresAsync();
        stores
            .Select(store => store!["name"]!.GetValue<string>())
            .Should()
            .Contain(dataStoreName, "the data store created by this scenario should be listed");

        JsonObject expected = new()
        {
            ["id"] = tenantName,
            ["name"] = tenantName,
            ["dataStores"] = new JsonArray([
                .. stores
                    .OrderBy(store => store!["id"]!.GetValue<int>())
                    .Select(store =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["id"] = store!["id"]!.GetValue<int>(),
                                ["dataStoreManageId"] = null,
                                ["name"] = store["name"]!.GetValue<string>(),
                                ["dataStoreType"] = store["dataStoreType"]!.GetValue<string>(),
                                ["status"] = "Created",
                                ["databaseTemplate"] = null,
                                ["databaseName"] = null,
                                ["educationOrganizations"] = new JsonArray(),
                            }
                    ),
            ]),
        };

        // Property order and explicit nulls are part of the contract, so the text is compared exactly.
        JsonNode.Parse(text)!.ToJsonString().Should().Be(expected.ToJsonString());
    }

    private async Task<JsonArray> ListDataStoresAsync()
    {
        var response = await playwrightContext.ApiRequestContext!.GetAsync(
            "/v3/dataStores",
            new() { Headers = _authHeaders }
        );
        string text = await response.TextAsync();
        response.Status.Should().Be(200, text);
        return JsonNode.Parse(text)!.AsArray();
    }
}
