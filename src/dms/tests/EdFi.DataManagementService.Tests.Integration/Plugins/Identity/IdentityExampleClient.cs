// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>One request the example client sent and the status it got back.</summary>
internal sealed record IdentityClientRequest(HttpMethod Method, string PathAndQuery, HttpStatusCode Status);

/// <summary>How an example client run ended.</summary>
internal enum IdentityClientEnding
{
    /// <summary>A poll answered the complete payload.</summary>
    Completed,

    /// <summary>A poll answered the job-failed problem. The job will never produce results.</summary>
    JobFailed,

    /// <summary>The submission or a poll answered a status the client does not retry.</summary>
    Rejected,

    /// <summary>The poll budget ran out before the job completed.</summary>
    PollBudgetExhausted,
}

internal sealed record IdentityClientOutcome(
    IdentityClientEnding Ending,
    HttpStatusCode LastStatus,
    string? ProblemType,
    JsonNode? Payload
);

/// <summary>How an unknown-outcome create workflow ended.</summary>
internal enum IdentityCreateEnding
{
    /// <summary>The create answered an id.</summary>
    Created,

    /// <summary>The create outcome was unknown and an authoritative lookup returned the issued id.</summary>
    Recovered,

    /// <summary>The create outcome was unknown and could not be resolved safely; the client stopped.</summary>
    OperatorReconciliationRequired,

    /// <summary>The create answered a status that is not an unknown outcome.</summary>
    Rejected,
}

internal sealed record IdentityCreateOutcome(
    IdentityCreateEnding Ending,
    HttpStatusCode CreateStatus,
    string? UniqueId,
    string? Reason,
    HttpStatusCode? LookupStatus,
    IReadOnlyList<int?> LookupScores
);

/// <summary>
/// The search the client offers to reconcile an unknown create. Only the provider's documented exact
/// upstream-key lookup is authoritative; any other search is a scored demographic match.
/// </summary>
internal sealed record IdentityReconciliationLookup(string SearchObject, bool IsExactUpstreamKey)
{
    /// <summary>The fixture's exact lookup: a search object carrying only <c>upstreamKey</c>.</summary>
    public static IdentityReconciliationLookup ExactUpstreamKey(string upstreamKey) =>
        new(new JsonObject { ["upstreamKey"] = upstreamKey }.ToJsonString(), true);

    /// <summary>A demographic search, which the client must never accept as recovery.</summary>
    public static IdentityReconciliationLookup Demographic(string searchObject) => new(searchObject, false);
}

/// <summary>
/// The documented identity client behavior as a small test-side client: submit an async find or
/// search, follow the <c>Location</c> exactly as returned, poll until the payload is complete, stop
/// when the answer is the terminal <c>job-failed</c> problem, and never submit the original request a
/// second time. A poll answered with an upstream failure is retried against the same <c>Location</c>
/// within the poll budget, because that answer says nothing about the job's state.
/// </summary>
/// <remarks>
/// Every request it sends is recorded in <see cref="Requests"/>, so a case can show what the client did
/// not do as well as what it did.
/// </remarks>
internal sealed class IdentityExampleClient(HttpClient http, int maxPolls = 20)
{
    private readonly List<IdentityClientRequest> _requests = [];

    /// <summary>Every request sent, in order.</summary>
    public IReadOnlyList<IdentityClientRequest> Requests => _requests;

    /// <summary>Submits the body to the route, then follows the job it is given to the end.</summary>
    public async Task<IdentityClientOutcome> SubmitAndFollowAsync(string route, string jsonBody)
    {
        using HttpRequestMessage submit = new(HttpMethod.Post, route)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage accepted = await SendAsync(submit);

        if (accepted.StatusCode != HttpStatusCode.Accepted || accepted.Headers.Location is null)
        {
            return await OutcomeOfAsync(IdentityClientEnding.Rejected, accepted);
        }

        // Used byte for byte as returned; never rebuilt from the submitted request.
        string location = accepted.Headers.Location.OriginalString;

        for (int poll = 0; poll < maxPolls; poll++)
        {
            using HttpRequestMessage pollRequest = new(HttpMethod.Get, location);
            using HttpResponseMessage answer = await SendAsync(pollRequest);
            string text = await answer.Content.ReadAsStringAsync();
            JsonNode? body = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);

            if (answer.StatusCode == HttpStatusCode.OK)
            {
                if (body?["Status"]?.GetValue<string>() == "Complete")
                {
                    return new IdentityClientOutcome(
                        IdentityClientEnding.Completed,
                        answer.StatusCode,
                        null,
                        body
                    );
                }

                continue;
            }

            string? problemType = body?["type"]?.GetValue<string>();

            if (problemType == IdentityFailureResponse.JobFailedType)
            {
                return new IdentityClientOutcome(
                    IdentityClientEnding.JobFailed,
                    answer.StatusCode,
                    problemType,
                    body
                );
            }

            if (problemType == IdentityFailureResponse.UpstreamFailureType)
            {
                continue;
            }

            return new IdentityClientOutcome(
                IdentityClientEnding.Rejected,
                answer.StatusCode,
                problemType,
                body
            );
        }

        return new IdentityClientOutcome(
            IdentityClientEnding.PollBudgetExhausted,
            HttpStatusCode.Accepted,
            null,
            null
        );
    }

    /// <summary>
    /// Creates an identity. A <c>502</c> from create is an unknown outcome: the upstream may have issued
    /// the id. The client never repeats the create. It runs the offered lookup once and adopts the id
    /// only when the lookup is the provider's exact upstream-key lookup and answered one match with
    /// score 100. Anything else - a lookup that is unavailable, an empty answer (which cannot establish
    /// absence), a scored demographic match, or several matches - stops for operator reconciliation,
    /// because retrying create would issue a duplicate.
    /// </summary>
    public async Task<IdentityCreateOutcome> CreateWithReconciliationAsync(
        string createRoute,
        string searchRoute,
        string createBody,
        IdentityReconciliationLookup lookup
    )
    {
        using HttpRequestMessage create = new(HttpMethod.Post, createRoute)
        {
            Content = new StringContent(createBody, Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage created = await SendAsync(create);

        if (created.StatusCode == HttpStatusCode.OK)
        {
            string id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!.GetValue<string>();
            return new IdentityCreateOutcome(
                IdentityCreateEnding.Created,
                created.StatusCode,
                id,
                null,
                null,
                []
            );
        }

        if (created.StatusCode != HttpStatusCode.BadGateway)
        {
            return new IdentityCreateOutcome(
                IdentityCreateEnding.Rejected,
                created.StatusCode,
                null,
                null,
                null,
                []
            );
        }

        using HttpRequestMessage search = new(HttpMethod.Post, searchRoute)
        {
            Content = new StringContent($"[{lookup.SearchObject}]", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage lookedUp = await SendAsync(search);

        IdentityCreateOutcome Stop(string reason, IReadOnlyList<int?>? scores = null) =>
            new(
                IdentityCreateEnding.OperatorReconciliationRequired,
                created.StatusCode,
                null,
                $"operator reconciliation required: {reason}",
                lookedUp.StatusCode,
                scores ?? []
            );

        if (lookedUp.StatusCode != HttpStatusCode.OK)
        {
            return Stop("the reconciliation lookup is unavailable");
        }

        JsonNode? answer = JsonNode.Parse(await lookedUp.Content.ReadAsStringAsync());
        JsonArray matches = answer?["SearchResponses"]?[0]?["Responses"]?.AsArray() ?? [];
        List<int?> scores = [.. matches.Select(match => match?["Score"]?.GetValue<int>())];

        if (!lookup.IsExactUpstreamKey)
        {
            return Stop("a demographic match is not proof of the issuance", scores);
        }

        if (matches.Count == 0)
        {
            return Stop("an empty answer cannot establish that nothing was issued", scores);
        }

        if (matches.Count > 1 || scores[0] != 100)
        {
            return Stop("the lookup answer is not a single exact match", scores);
        }

        return new IdentityCreateOutcome(
            IdentityCreateEnding.Recovered,
            created.StatusCode,
            matches[0]!["UniqueId"]!.GetValue<string>(),
            null,
            lookedUp.StatusCode,
            scores
        );
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        HttpResponseMessage response = await http.SendAsync(request);
        _requests.Add(
            new IdentityClientRequest(request.Method, request.RequestUri!.OriginalString, response.StatusCode)
        );
        return response;
    }

    private static async Task<IdentityClientOutcome> OutcomeOfAsync(
        IdentityClientEnding ending,
        HttpResponseMessage response
    )
    {
        string text = await response.Content.ReadAsStringAsync();
        JsonNode? body = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        return new IdentityClientOutcome(
            ending,
            response.StatusCode,
            body?["type"]?.GetValue<string>(),
            body
        );
    }
}
