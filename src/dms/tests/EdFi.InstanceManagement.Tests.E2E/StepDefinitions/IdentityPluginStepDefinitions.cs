// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// Step definitions for the identity plugin slice: the DMS container runs with Acme.IdentityFixture
/// mounted and allowlisted, so identity requests reach the fixture through the plugin path. These steps
/// complement <see cref="IdentityStepDefinitions"/>, whose application and token steps the slice reuses.
/// Requests are sent from here, rather than through <see cref="DmsApiClient"/>, because the redaction
/// proof must send its own <c>correlationid</c> header.
/// </summary>
[Binding]
public partial class IdentityPluginStepDefinitions(InstanceManagementContext context)
{
    private const string FixturePluginName = "Acme.IdentityFixture";
    private const string EnvironmentFileHelper = "New-IdentityPluginEnvironmentFile.ps1";

    // CORRELATION_ID_HEADER in .env.routeContext.e2e; the request's TraceId equals the value sent.
    private const string CorrelationIdHeader = "correlationid";
    private const string UniqueIdPlaceholder = "<UniqueId>";
    private const int MaximumPollAttempts = 10;
    private static readonly TimeSpan LogDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogPollInterval = TimeSpan.FromSeconds(1);

    private string? _uniqueId;
    private string? _resultsLocation;
    private string? _currentCorrelationId;
    private DmsLogPosition? _logPosition;
    private readonly List<string> _correlationIds = [];
    private readonly List<string> _requestTokens = [];
    private readonly List<string> _pollStatuses = [];

    // The name is rendered quoted, and in a JSON log line the quote is escaped, so a backslash may precede it.
    [GeneratedRegex("""Plugin inventory for \\?"?Acme\.IdentityFixture""")]
    private static partial Regex FixtureInventoryLine();

    [GeneratedRegex("^[A-Za-z0-9]{32}$")]
    private static partial Regex IssuedToken();

    [Given("the identity fixture plugin is loaded")]
    public async Task GivenTheIdentityFixturePluginIsLoaded()
    {
        DmsLogSnapshot logs = await DmsContainerControl.GetLogsAsync();

        if (!logs.AllLines.Any(line => FixtureInventoryLine().IsMatch(line)))
        {
            throw new InvalidOperationException(
                $"The DMS container '{TestConfiguration.DmsContainerName}' did not log a plugin inventory for "
                    + $"{FixturePluginName}, so the identity fixture plugin is not loaded. This slice needs the plugin "
                    + $"mounted and allowlisted: generate an environment file with {EnvironmentFileHelper} "
                    + "(src/dms/tests/EdFi.InstanceManagement.Tests.E2E) and pass it to "
                    + "./build-dms.ps1 InstanceE2ETest -EnvironmentFile."
            );
        }
    }

    [Given("a new unique correlation id is used for identity requests")]
    public void GivenANewUniqueCorrelationIdIsUsedForIdentityRequests()
    {
        _currentCorrelationId = $"idplugin-{Guid.NewGuid():N}";
        _correlationIds.Add(_currentCorrelationId);
    }

    [Given("the DMS container log position is recorded")]
    public async Task GivenTheDmsContainerLogPositionIsRecorded()
    {
        _logPosition = (await DmsContainerControl.GetLogsAsync()).Position;
    }

    [When("a person is created through identity route {string} with body:")]
    public async Task WhenAPersonIsCreatedThroughIdentityRouteWithBody(string route, string jsonBody)
    {
        await SendAsync(HttpMethod.Post, $"/{route}", jsonBody);

        string body = await context.LastResponse!.Content.ReadAsStringAsync();
        if (context.LastResponse.IsSuccessStatusCode)
        {
            _uniqueId = JsonSerializer.Deserialize<string>(body);
        }
    }

    [When("a GET request is made to identity route {string} for the issued UniqueId")]
    public async Task WhenAGetRequestIsMadeToIdentityRouteForTheIssuedUniqueId(string route)
    {
        _uniqueId.Should().NotBeNullOrEmpty("a person must be created first");
        await SendAsync(HttpMethod.Get, $"/{route}/{_uniqueId}");
    }

    [When(
        "a POST request is made to identity route {string} with the issued UniqueId substituted into body:"
    )]
    public async Task WhenAPostRequestIsMadeToIdentityRouteWithTheIssuedUniqueIdSubstitutedIntoBody(
        string route,
        string jsonBody
    )
    {
        if (jsonBody.Contains(UniqueIdPlaceholder, StringComparison.Ordinal))
        {
            _uniqueId.Should().NotBeNullOrEmpty("a person must be created first");
            jsonBody = jsonBody.Replace(UniqueIdPlaceholder, _uniqueId, StringComparison.Ordinal);
        }

        await SendAsync(HttpMethod.Post, $"/{route}", jsonBody);

        if (context.LastResponse!.Headers.Location is { } location)
        {
            _resultsLocation = location.ToString();
            _pollStatuses.Clear();
        }
    }

    [When("the results Location is polled once")]
    public async Task WhenTheResultsLocationIsPolledOnce()
    {
        await PollAsync();
    }

    [When("the results Location is polled until it completes")]
    public async Task WhenTheResultsLocationIsPolledUntilItCompletes()
    {
        for (int attempt = 0; attempt < MaximumPollAttempts; attempt++)
        {
            string? status = await PollAsync();
            if (status == "Complete")
            {
                return;
            }

            context.LastResponse!.IsSuccessStatusCode.Should().BeTrue("a results poll must answer 200");
        }

        throw new InvalidOperationException(
            $"The job did not complete within {MaximumPollAttempts} polls; the statuses were {string.Join(", ", _pollStatuses)}."
        );
    }

    [When("the DMS container is restarted")]
    public async Task WhenTheDmsContainerIsRestarted()
    {
        await DmsContainerControl.RestartAsync();
    }

    [Then("the DMS log names {string} in a plugin inventory line")]
    public async Task ThenTheDmsLogNamesInAPluginInventoryLine(string pluginName)
    {
        pluginName.Should().Be(FixturePluginName);
        DmsLogSnapshot logs = await DmsContainerControl.GetLogsAsync();
        logs.AllLines.Should().Contain(line => FixtureInventoryLine().IsMatch(line));
    }

    [Then("the create response should be a bare issued UniqueId")]
    public async Task ThenTheCreateResponseShouldBeABareIssuedUniqueId()
    {
        string body = await context.LastResponse!.Content.ReadAsStringAsync();
        string? uniqueId = JsonSerializer.Deserialize<string>(body);

        uniqueId.Should().MatchRegex(IssuedToken().ToString(), $"the create answer was: {body}");
    }

    [Then("the response property {string} should be the issued UniqueId")]
    public async Task ThenTheResponsePropertyShouldBeTheIssuedUniqueId(string propertyName)
    {
        using JsonDocument document = await ReadResponseAsync();

        document
            .RootElement.GetProperty(propertyName)
            .GetString()
            .Should()
            .Be(_uniqueId, "the person was read back under the id it was issued");
    }

    [Then("search group {int} of the response should list the issued UniqueId")]
    public async Task ThenSearchGroupOfTheResponseShouldListTheIssuedUniqueId(int group)
    {
        using JsonDocument document = await ReadResponseAsync();

        SearchGroup(document, group)
            .EnumerateArray()
            .Select(match => match.GetProperty("UniqueId").GetString())
            .Should()
            .Contain(_uniqueId);
    }

    [Then("search group {int} of the response should be empty")]
    public async Task ThenSearchGroupOfTheResponseShouldBeEmpty(int group)
    {
        using JsonDocument document = await ReadResponseAsync();

        SearchGroup(document, group).GetArrayLength().Should().Be(0);
    }

    [Then("the response should carry a Location to an identity results route")]
    public void ThenTheResponseShouldCarryALocationToAnIdentityResultsRoute()
    {
        Uri? location = context.LastResponse!.Headers.Location;

        location.Should().NotBeNull("an accepted async request carries a Location");
        location!.AbsolutePath.Should().Contain("/identity/v2/identities/results/");
        _requestTokens.Add(Uri.UnescapeDataString(location.AbsolutePath.Split('/')[^1]));
    }

    [Then("the results polls should have answered {string} then {string}")]
    public void ThenTheResultsPollsShouldHaveAnsweredThen(string first, string last)
    {
        _pollStatuses.Should().Equal(first, last);
    }

    [Then(
        "the DMS container log shows the frontend and core completion events for every correlation id used"
    )]
    public async Task ThenTheDmsContainerLogShowsTheCompletionEventsForEveryCorrelationIdUsed()
    {
        _logPosition.Should().NotBeNull("the log position must be recorded first");
        _correlationIds.Should().NotBeEmpty("a correlation id must be used first");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<string> missing;

        do
        {
            DmsLogSnapshot logs = await DmsContainerControl.GetLogsAsync();
            List<string> window = logs.LinesSince(_logPosition!.Value).ToList();

            missing = _correlationIds
                .SelectMany(id =>
                    new[] { "DMS request completed", "DMS core request completed" }
                        .Where(marker => !window.Exists(line => IsCompletionEvent(line, id, marker)))
                        .Select(marker => $"'{marker}' for {id}")
                )
                .ToList();

            if (missing.Count == 0)
            {
                return;
            }

            await Task.Delay(LogPollInterval);
        } while (stopwatch.Elapsed < LogDeadline);

        throw new InvalidOperationException(
            $"The completion events never appeared in the DMS container log: {string.Join("; ", missing)}."
        );
    }

    [Then(
        "no line of the DMS container log since the recorded position contains an issued UniqueId or request token"
    )]
    public async Task ThenNoLineOfTheDmsContainerLogContainsAnIssuedUniqueIdOrRequestToken()
    {
        _logPosition.Should().NotBeNull("the log position must be recorded first");
        _uniqueId.Should().NotBeNullOrEmpty("a person must be created first");
        _requestTokens.Should().NotBeEmpty("an async request must be accepted first");

        string[] secrets = [_uniqueId!, .. _requestTokens];
        List<string> window = (await DmsContainerControl.GetLogsAsync())
            .LinesSince(_logPosition!.Value)
            .ToList();

        window.Should().NotBeEmpty("the window must hold the requests' log lines");

        for (int index = 0; index < window.Count; index++)
        {
            string line = window[index];

            // Raw text first, which also covers plain-text lines and escaped JSON.
            foreach (string secret in secrets)
            {
                line.Contains(secret, StringComparison.OrdinalIgnoreCase)
                    .Should()
                    .BeFalse($"window line {index} must not carry an issued UniqueId or request token");
            }

            // Structured lines, parsed, so the Path property and the rendered message are checked as
            // the strings the logger recorded rather than as JSON text.
            if (TryParseJsonLine(line, out JsonDocument? document))
            {
                using (document)
                {
                    foreach (string value in StringValues(document.RootElement))
                    {
                        foreach (string secret in secrets)
                        {
                            value
                                .Contains(secret, StringComparison.OrdinalIgnoreCase)
                                .Should()
                                .BeFalse(
                                    $"a property of structured window line {index} must not carry an issued UniqueId or request token"
                                );
                        }
                    }
                }
            }
        }
    }

    private static JsonElement SearchGroup(JsonDocument document, int group) =>
        document.RootElement.GetProperty("SearchResponses")[group].GetProperty("Responses");

    private async Task<JsonDocument> ReadResponseAsync()
    {
        context.LastResponse.Should().NotBeNull();
        return JsonDocument.Parse(await context.LastResponse!.Content.ReadAsStringAsync());
    }

    private async Task SendAsync(HttpMethod method, string path, string? jsonBody = null)
    {
        context.DmsToken.Should().NotBeNullOrEmpty("must be authenticated to DMS first");

        using var http = new HttpClient { BaseAddress = new Uri(TestConfiguration.DmsApiUrl) };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.DmsToken);

        if (_currentCorrelationId is not null)
        {
            request.Headers.Add(CorrelationIdHeader, _currentCorrelationId);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        // Buffered, so the response outlives the request and client that produced it.
        context.LastResponse = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
    }

    /// <summary>
    /// Polls the stored Location once and returns the payload's wire status, or null when the answer was
    /// not a 200.
    /// </summary>
    private async Task<string?> PollAsync()
    {
        _resultsLocation.Should().NotBeNullOrEmpty("an async request must be accepted first");

        var location = new Uri(new Uri(TestConfiguration.DmsApiUrl), _resultsLocation);
        await SendAsync(HttpMethod.Get, location.PathAndQuery);

        if (!context.LastResponse!.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument document = await ReadResponseAsync();
        string? status = document.RootElement.GetProperty("Status").GetString();
        _pollStatuses.Add(status ?? string.Empty);
        return status;
    }

    private static bool IsCompletionEvent(string line, string correlationId, string marker)
    {
        if (!TryParseJsonLine(line, out JsonDocument? document))
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            return root.TryGetProperty("Properties", out JsonElement properties)
                && properties.TryGetProperty("TraceId", out JsonElement traceId)
                && traceId.ValueKind == JsonValueKind.String
                && traceId.GetString() == correlationId
                && root.TryGetProperty("RenderedMessage", out JsonElement message)
                && message.GetString()?.Contains(marker, StringComparison.Ordinal) == true;
        }
    }

    private static bool TryParseJsonLine(string line, out JsonDocument document)
    {
        document = null!;

        if (!line.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (string value in StringValues(property.Value))
                    {
                        yield return value;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string value in StringValues(item))
                    {
                        yield return value;
                    }
                }
                break;
        }
    }
}
