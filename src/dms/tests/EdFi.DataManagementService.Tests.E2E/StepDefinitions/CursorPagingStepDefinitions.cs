// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    /// <summary>
    /// Steps for the public cursor and partition contract: walking a collection by continuation,
    /// consuming every partition a response hands out, and reading the served OpenAPI documents.
    /// </summary>
    /// <remarks>
    /// A partial of the main step definitions rather than a new binding class, so the request context,
    /// authorization headers, and current-response handling are the ones every other scenario uses. A
    /// second class would need its own client and would drift from them.
    /// </remarks>
    public partial class StepDefinitions
    {
        private const string NextPageTokenHeader = "next-page-token";

        /// <summary>
        /// A walk cannot loop forever on a continuation that fails to advance: it exhausts this and
        /// fails with the pages it did retrieve rather than hanging.
        /// </summary>
        private const int MaximumWalkedPages = 60;

        private readonly List<string> _walkedDocumentIds = [];
        private readonly List<string> _partitionTokens = [];
        private string _partitionsCollectionUrl = string.Empty;
        private int _walkedPages;
        private bool _walkEndedEmptyWithoutContinuation;

        /// <summary>
        /// Enters a cursor walk the way a client does: an ordinary page first, then the continuation it
        /// hands back, until a request offers none.
        /// </summary>
        [When("a cursor walk is made over {string} with page size {int}")]
        public Task WhenACursorWalkIsMadeOver(string url, int pageSize) =>
            CursorWalkAsync(url, pageSize, string.Empty);

        [When("a cursor walk is made over {string} with page size {int} repeating the query {string}")]
        public Task WhenACursorWalkIsMadeOverRepeating(string url, int pageSize, string repeatedQuery) =>
            CursorWalkAsync(url, pageSize, repeatedQuery);

        private async Task CursorWalkAsync(string url, int pageSize, string repeatedQuery)
        {
            string resolved = AddDataPrefixIfNecessary(url);
            string separator = string.IsNullOrEmpty(repeatedQuery) ? string.Empty : "&" + repeatedQuery;

            _walkedDocumentIds.Clear();
            _walkedPages = 0;
            _walkEndedEmptyWithoutContinuation = false;

            var firstPage = await GetAsync($"{resolved}?limit={pageSize}{separator}");
            (List<string> ids, string? continuation) = await ReadPageAsync(firstPage, pageSize);

            _walkedPages++;
            _walkedDocumentIds.AddRange(ids);

            while (continuation is not null && _walkedPages < MaximumWalkedPages)
            {
                var page = await GetAsync(
                    $"{resolved}?pageToken={Uri.EscapeDataString(continuation)}"
                        + $"&pageSize={pageSize}{separator}"
                );
                (ids, continuation) = await ReadPageAsync(page, pageSize);

                _walkedPages++;
                _walkedDocumentIds.AddRange(ids);

                if (continuation is null)
                {
                    _walkEndedEmptyWithoutContinuation = ids.Count == 0;
                }
            }

            _walkedPages
                .Should()
                .BeLessThan(
                    MaximumWalkedPages,
                    "a walk that failed to advance would otherwise loop until the run is killed"
                );
        }

        /// <summary>
        /// Requests the partitions of a collection and remembers the tokens it handed out.
        /// </summary>
        [When("the partitions of {string} are requested")]
        public async Task WhenThePartitionsAreRequested(string url) =>
            await RequestPartitionsAsync(url, string.Empty);

        [When("the partitions of {string} are requested with {string}")]
        public async Task WhenThePartitionsAreRequestedWith(string url, string query) =>
            await RequestPartitionsAsync(url, query);

        private async Task RequestPartitionsAsync(string url, string query)
        {
            _partitionsCollectionUrl = AddDataPrefixIfNecessary(url);
            _partitionTokens.Clear();

            string requestUri = string.IsNullOrEmpty(query)
                ? $"{_partitionsCollectionUrl}/partitions"
                : $"{_partitionsCollectionUrl}/partitions?{query}";

            var response = await GetAsync(requestUri);
            string body = await response.TextAsync();

            response.Status.Should().Be(200, body);

            _partitionTokens.AddRange(
                JsonNode.Parse(body)!["pageTokens"]!.AsArray().Select(token => token!.GetValue<string>())
            );
        }

        /// <summary>
        /// Walks every partition the last partitions response handed out, to its terminal empty page,
        /// repeating the supplied query on every page request.
        /// </summary>
        /// <remarks>
        /// The repeated query is the page query, not the partitions query: the token stores no filter, so
        /// a walk that dropped it would widen its own candidate set. It is deliberately separate from the
        /// count the partitions request carried, which is not a collection query field at all.
        /// </remarks>
        [When("every returned partition is walked with page size {int}")]
        public async Task WhenEveryReturnedPartitionIsWalked(int pageSize) =>
            await WalkEveryPartitionAsync(pageSize, string.Empty);

        [When("every returned partition is walked with page size {int} repeating the query {string}")]
        public async Task WhenEveryReturnedPartitionIsWalkedRepeating(int pageSize, string repeatedQuery) =>
            await WalkEveryPartitionAsync(pageSize, repeatedQuery);

        private async Task WalkEveryPartitionAsync(int pageSize, string repeatedQuery)
        {
            string separator = string.IsNullOrEmpty(repeatedQuery) ? string.Empty : "&" + repeatedQuery;

            _walkedDocumentIds.Clear();

            List<List<string>> perPartition = [];

            foreach (string partitionToken in _partitionTokens)
            {
                List<string> partitionIds = [];
                string? continuation = partitionToken;
                var pages = 0;

                while (continuation is not null && pages < MaximumWalkedPages)
                {
                    var page = await GetAsync(
                        $"{_partitionsCollectionUrl}?pageToken={Uri.EscapeDataString(continuation)}"
                            + $"&pageSize={pageSize}{separator}"
                    );
                    (List<string> ids, continuation) = await ReadPageAsync(page, pageSize);

                    pages++;
                    partitionIds.AddRange(ids);

                    if (continuation is null)
                    {
                        ids.Should().BeEmpty("a partition ends on the request that selected nothing");
                    }
                }

                pages.Should().BeLessThan(MaximumWalkedPages);
                partitionIds.Should().OnlyHaveUniqueItems("a partition must not return a document twice");
                perPartition.Add(partitionIds);
                _walkedDocumentIds.AddRange(partitionIds);
            }

            for (var partition = 0; partition < perPartition.Count; partition++)
            {
                for (var other = partition + 1; other < perPartition.Count; other++)
                {
                    perPartition[partition]
                        .Should()
                        .NotIntersectWith(
                            perPartition[other],
                            $"partitions {partition} and {other} must cover disjoint ranges"
                        );
                }
            }
        }

        [Then("the walk returned {int} documents with no duplicates")]
        public void ThenTheWalkReturnedDocumentsWithNoDuplicates(int expectedCount)
        {
            _walkedDocumentIds.Should().OnlyHaveUniqueItems("a walk must not return a document twice");
            _walkedDocumentIds
                .Should()
                .HaveCount(
                    expectedCount,
                    "the walk covers exactly the documents this scenario seeded, because the suite "
                        + "resets the data before each scenario"
                );
        }

        [Then("the walk ended with an empty page and no continuation")]
        public void ThenTheWalkEndedWithAnEmptyPageAndNoContinuation() =>
            _walkEndedEmptyWithoutContinuation
                .Should()
                .BeTrue(
                    "the implementation does not fetch an extra row to predict the terminal page, so "
                        + "the last useful page is followed by exactly one empty request"
                );

        [Then("at most {int} partition tokens were returned")]
        public void ThenAtMostPartitionTokensWereReturned(int maximum) =>
            _partitionTokens
                .Should()
                .HaveCountLessThanOrEqualTo(
                    maximum,
                    "the requested count is an upper bound the response never exceeds"
                );

        [Then("at least one partition token was returned")]
        public void ThenAtLeastOnePartitionTokenWasReturned() =>
            _partitionTokens.Should().NotBeEmpty("a non-empty collection yields at least one partition");

        /// <summary>
        /// Captures a response header into the variable dictionary the request steps substitute from.
        /// </summary>
        /// <remarks>
        /// Deliberately not the existing "is stored as variable" step: that one lives in the profile step
        /// definitions, which construct their own <c>ScenarioVariables</c>, so a value it stores is
        /// invisible to the request steps in this class and the placeholder would reach the server
        /// unsubstituted. This one shares the dictionary the substitution actually reads.
        /// </remarks>
        [Then("the response header {string} is captured as {string}")]
        public void ThenTheResponseHeaderIsCapturedAs(string header, string variableName)
        {
            string key = header.ToLowerInvariant();

            _apiResponse.Headers.Should().ContainKey(key, $"'{header}' must be present to be captured");

            _scenarioVariables.Add(variableName, _apiResponse.Headers[key]);
        }

        [Then("the response header {string} is present")]
        public void ThenTheResponseHeaderIsPresent(string header) =>
            _apiResponse
                .Headers.Should()
                .ContainKey(header.ToLowerInvariant(), $"'{header}' must be present");

        [Then("the response header {string} is absent")]
        public void ThenTheResponseHeaderIsAbsent(string header) =>
            _apiResponse
                .Headers.Should()
                .NotContainKey(header.ToLowerInvariant(), $"'{header}' must be absent");

        [Then("the response content type is {string}")]
        public void ThenTheResponseContentTypeIs(string expected)
        {
            _apiResponse.Headers.Should().ContainKey("content-type");
            _apiResponse
                .Headers["content-type"]
                .Should()
                .StartWith(expected, "the response media type is part of the public contract");
        }

        [Then("the response body has exactly one error {string}")]
        public async Task ThenTheResponseBodyHasExactlyOneError(string expectedError)
        {
            string body = await _apiResponse.TextAsync();

            JsonNode.Parse(body)!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal([expectedError], $"a rejected cursor request reports exactly one error. Body: {body}");
        }

        [Then("the response body errors are")]
        public async Task ThenTheResponseBodyErrorsAre(DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            string body = await _apiResponse.TextAsync();

            JsonNode.Parse(body)!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(
                    [.. table.Rows.Select(row => row[0])],
                    $"the errors are reported in the contract's canonical order. Body: {body}"
                );
        }

        [Then("the response body is the parameter validation shell")]
        public async Task ThenTheResponseBodyIsTheParameterValidationShell() =>
            await AssertProblemShellAsync(
                "Parameters supplied to the request were invalid.",
                "urn:ed-fi:api:bad-request:parameter-validation-failed",
                "Parameter Validation Failed"
            );

        [Then("the response body is the bad request shell")]
        public async Task ThenTheResponseBodyIsTheBadRequestShell() =>
            await AssertProblemShellAsync(
                "The request could not be processed. See 'errors' for details.",
                "urn:ed-fi:api:bad-request",
                "Bad Request"
            );

        private async Task AssertProblemShellAsync(string detail, string type, string title)
        {
            string body = await _apiResponse.TextAsync();
            JsonNode problem = JsonNode.Parse(body)!;

            problem["detail"]!.GetValue<string>().Should().Be(detail, body);
            problem["type"]!.GetValue<string>().Should().Be(type, body);
            problem["title"]!.GetValue<string>().Should().Be(title, body);
            problem["status"]!.GetValue<int>().Should().Be(400, body);
            problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace(body);
            problem["validationErrors"]!.AsObject().Should().BeEmpty(body);
        }

        [Then("the served OpenAPI document contains path {string}")]
        public async Task ThenTheServedOpenApiDocumentContainsPath(string path) =>
            (await ServedPathsAsync())
                .Should()
                .ContainKey(path, $"the served document must publish '{path}'");

        [Then("the served OpenAPI document does not contain path {string}")]
        public async Task ThenTheServedOpenApiDocumentDoesNotContainPath(string path) =>
            (await ServedPathsAsync())
                .Should()
                .NotContainKey(path, $"the served document must not publish '{path}'");

        [Then("the served OpenAPI document publishes at least one path")]
        public async Task ThenTheServedOpenApiDocumentPublishesAtLeastOnePath() =>
            (await ServedPathsAsync())
                .Should()
                .NotBeEmpty(
                    "a document with no paths at all would satisfy every absence assertion vacuously"
                );

        [Then("the served OpenAPI operation {string} on path {string} references parameter {string}")]
        public async Task ThenTheServedOpenApiOperationReferencesParameter(
            string operation,
            string path,
            string parameterComponent
        )
        {
            JsonObject paths = await ServedPathsAsync();

            paths.Should().ContainKey(path);

            JsonArray parameters = paths[path]![operation.ToLowerInvariant()]!["parameters"]!.AsArray();

            parameters
                .Select(parameter => parameter?["$ref"]?.GetValue<string>())
                .Should()
                .Contain(
                    $"#/components/parameters/{parameterComponent}",
                    $"'{operation} {path}' must reference the '{parameterComponent}' parameter"
                );
        }

        [Then("the served OpenAPI operation {string} on path {string} declares response header {string}")]
        public async Task ThenTheServedOpenApiOperationDeclaresResponseHeader(
            string operation,
            string path,
            string header
        )
        {
            JsonObject paths = await ServedPathsAsync();

            paths.Should().ContainKey(path);

            JsonNode? headers = paths[path]![operation.ToLowerInvariant()]!["responses"]!["200"]!["headers"];

            headers.Should().NotBeNull($"'{operation} {path}' must declare HTTP 200 response headers");
            headers!.AsObject().Should().ContainKey(header);
        }

        private async Task<JsonObject> ServedPathsAsync()
        {
            string body = await _apiResponse.TextAsync();
            JsonNode? document = JsonNode.Parse(body);

            document.Should().NotBeNull("a served OpenAPI document must be JSON");

            return document!["paths"]?.AsObject() ?? [];
        }

        private async Task<IAPIResponse> GetAsync(string url)
        {
            _logger.log.Information(url);

            IAPIResponse response = await _playwrightContext.ApiRequestContext?.GetAsync(
                url,
                new() { Headers = GetHeaders() }
            )!;

            SetCurrentApiResponse(response);

            return response;
        }

        /// <summary>
        /// Reads one page: the document ids it carries and the continuation it offers.
        /// </summary>
        private static async Task<(List<string> Ids, string? Continuation)> ReadPageAsync(
            IAPIResponse response,
            int pageSize
        )
        {
            string body = await response.TextAsync();

            response.Status.Should().Be(200, body);

            List<string> ids =
            [
                .. JsonNode.Parse(body)!.AsArray().Select(document => document!["id"]!.GetValue<string>()),
            ];

            ids.Should().HaveCountLessThanOrEqualTo(pageSize, "a page cannot exceed its page size");

            return response.Headers.TryGetValue(NextPageTokenHeader, out string? continuation)
                ? (ids, continuation)
                : (ids, null);
        }
    }
}
