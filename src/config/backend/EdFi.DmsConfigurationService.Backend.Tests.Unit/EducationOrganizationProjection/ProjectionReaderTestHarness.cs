// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>Responses and requests of a fake DMS serving the projection.</summary>
public static class ProjectionPages
{
    public const string ContractVersionV1 = "educationOrganizationProjection.v1";
    public const int DataStoreId = 3788;
    public const string School = "edfi.School";
    public const string Lea = "edfi.LocalEducationAgency";
    public const string Esc = "edfi.EducationServiceCenter";
    public const string Sea = "edfi.StateEducationAgency";

    public static JsonObject Item(
        long id,
        string discriminator = School,
        long? parentId = null,
        string? name = null,
        string? shortName = null
    ) =>
        new()
        {
            ["educationOrganizationId"] = id,
            ["nameOfInstitution"] = name ?? "Name " + id,
            ["shortNameOfInstitution"] = shortName,
            ["discriminator"] = discriminator,
            ["parentId"] = parentId,
        };

    public static string PageJson(
        string? nextCursor,
        IEnumerable<JsonObject> items,
        string contractVersion = ContractVersionV1,
        int dataStoreId = DataStoreId
    ) =>
        new JsonObject
        {
            ["contractVersion"] = contractVersion,
            ["dataStoreId"] = dataStoreId,
            ["nextCursor"] = nextCursor,
            ["items"] = new JsonArray([.. items]),
        }.ToJsonString();

    public static HttpResponseMessage Page(string? nextCursor, params JsonObject[] items) =>
        DmsResponses.Text(HttpStatusCode.OK, PageJson(nextCursor, items));

    /// <summary>Items of the given ids, all schools without a parent.</summary>
    public static JsonObject[] Schools(params long[] ids) => [.. ids.Select(id => Item(id))];

    /// <summary>The value of a query parameter of a recorded page request, or <c>null</c>.</summary>
    public static string? Query(RecordedRequest request, string name) => Query(request.Uri, name);

    public static string? Query(Uri uri, string name) => HttpUtility.ParseQueryString(uri.Query)[name];

    /// <summary>Whether a request is for a projection page, rather than Discovery or a token.</summary>
    public static bool IsPageRequest(Uri uri) =>
        uri.AbsolutePath.EndsWith("/management/education-organizations", StringComparison.Ordinal);
}

/// <summary>
/// A reader over the real Discovery client and token provider, all on one fake DMS and one fake clock. The fake DMS
/// answers every POST with a token (<c>token-1</c>, <c>token-2</c>, ...), a GET of a
/// <c>.../management/education-organizations</c> path with the given pages, and any other GET with the Discovery
/// document.
/// </summary>
public sealed class ProjectionReaderHarness
{
    public const string BaseUrl = "https://dms.example.org/api";

    public const string DiscoveryDocument = """
        {"urls":{"oauth":"https://dms.example.org/api/{districtId}/oauth/token",
        "educationOrganizationProjection":"https://dms.example.org/api/{districtId}/management/education-organizations"},
        "educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}
        """;

    public static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private int _tokensIssued;

    /// <param name="pages">Answers page requests; the argument is the page request's number, from 1.</param>
    /// <param name="configure">Changes the settings, which start as the defaults with a base URL and credentials.</param>
    /// <param name="discovery">Answers Discovery requests; the harness document by default.</param>
    /// <param name="token">Answers token requests; a new bearer token each time by default.</param>
    /// <param name="tokenProvider">Replaces the real token provider.</param>
    public ProjectionReaderHarness(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> pages,
        Action<DmsEducationOrganizationProjectionSettings>? configure = null,
        Func<HttpResponseMessage>? discovery = null,
        Func<HttpResponseMessage>? token = null,
        Func<ProjectionReaderHarness, IProjectionServiceTokenProvider>? tokenProvider = null
    )
    {
        Settings = new DmsEducationOrganizationProjectionSettings
        {
            DmsBaseUrl = BaseUrl,
            Credentials = new() { ClientId = "client", ClientSecret = "secret" },
        };
        configure?.Invoke(Settings);

        int pageCalls = 0;
        Handler = new FakeDmsHandler(
            (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(token?.Invoke() ?? IssueToken());
                }
                return ProjectionPages.IsPageRequest(request.RequestUri!)
                    ? pages(Interlocked.Increment(ref pageCalls), request, cancellationToken)
                    : Task.FromResult(
                        discovery?.Invoke() ?? DmsResponses.Text(HttpStatusCode.OK, DiscoveryDocument)
                    );
            }
        );

        SingleHandlerHttpClientFactory factory = new(
            Handler,
            DmsEducationOrganizationProjectionHttpClient.Name
        );
        IOptions<DmsEducationOrganizationProjectionSettings> options = Options.Create(Settings);
        Discovery = new DmsDiscoveryClient(factory, options, Time);
        Tokens = tokenProvider?.Invoke(this) ?? new ProjectionServiceTokenProvider(factory, options, Time);
        Reader = new EducationOrganizationProjectionReader(
            Discovery,
            Tokens,
            factory,
            options,
            Time,
            LoggerFactory
                .Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Recorder))
                .CreateLogger<EducationOrganizationProjectionReader>()
        );
    }

    /// <summary>A harness whose pages come from <paramref name="responses"/> in order; the last one repeats.</summary>
    public static ProjectionReaderHarness Serving(
        Func<HttpResponseMessage>[] responses,
        Action<DmsEducationOrganizationProjectionSettings>? configure = null
    ) => new((call, _, _) => Task.FromResult(responses[Math.Min(call, responses.Length) - 1]()), configure);

    public FakeTimeProvider Time { get; } = new(Start);

    public DmsEducationOrganizationProjectionSettings Settings { get; }

    public FakeDmsHandler Handler { get; }

    public DmsDiscoveryClient Discovery { get; }

    public IProjectionServiceTokenProvider Tokens { get; }

    public EducationOrganizationProjectionReader Reader { get; }

    public RecordingLoggerProvider Recorder { get; } = new();

    public EducationOrganizationProjectionReadRequest Request { get; set; } =
        new(null, ProjectionPages.DataStoreId, new Dictionary<string, string> { ["districtId"] = "255901" });

    public IReadOnlyList<RecordedRequest> PageRequests =>
        [.. Handler.Requests.Where(request => ProjectionPages.IsPageRequest(request.Uri))];

    public IReadOnlyList<RecordedRequest> TokenRequests =>
        [.. Handler.Requests.Where(request => request.Method == HttpMethod.Post)];

    public IReadOnlyList<RecordedRequest> DiscoveryRequests =>
        [
            .. Handler.Requests.Where(request =>
                request.Method == HttpMethod.Get && !ProjectionPages.IsPageRequest(request.Uri)
            ),
        ];

    public Task<EducationOrganizationProjectionReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    ) => Reader.ReadAllAsync(Request, cancellationToken);

    private HttpResponseMessage IssueToken() =>
        DmsResponses.Text(
            HttpStatusCode.OK,
            $$"""{"access_token":"token-{{Interlocked.Increment(ref _tokensIssued)}}","token_type":"bearer","expires_in":3600}"""
        );
}
