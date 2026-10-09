// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>Which DMS call a request was.</summary>
public enum RequestKind
{
    Discovery,
    Token,
    Page,
}

/// <summary>
/// One request the reader sent. For a page, <see cref="Attempt"/> counts walks (a page sent without a cursor starts
/// one) and <see cref="AttemptPage"/> is the page's position in its walk.
/// </summary>
public sealed record RecordedRequest(RequestKind Kind, int Attempt, int AttemptPage);

/// <summary>
/// What a test injects: for a page request, a response to return instead of calling DMS, or <c>null</c> to let the
/// request through. The token is the request's own, so a handler that waits on it ends when the reader cancels.
/// </summary>
public delegate Task<HttpResponseMessage?> PageFault(
    RecordedRequest page,
    CancellationToken cancellationToken
);

/// <summary>Records every request the reader sends and applies the test's <see cref="PageFault"/>.</summary>
public sealed class FaultPlan
{
    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private int _attempt;
    private int _attemptPage;

    public PageFault? OnPage { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Pages =>
        [.. Requests.Where(request => request.Kind == RequestKind.Page)];

    internal RecordedRequest Record(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        lock (_gate)
        {
            RecordedRequest recorded;
            if (path.EndsWith("/management/education-organizations", StringComparison.Ordinal))
            {
                bool hasCursor = request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal);
                _attempt += hasCursor ? 0 : 1;
                _attemptPage = hasCursor ? _attemptPage + 1 : 1;
                recorded = new RecordedRequest(RequestKind.Page, _attempt, _attemptPage);
            }
            else
            {
                recorded = new RecordedRequest(
                    path.EndsWith("/oauth/token", StringComparison.Ordinal)
                        ? RequestKind.Token
                        : RequestKind.Discovery,
                    0,
                    0
                );
            }

            _requests.Add(recorded);
            return recorded;
        }
    }
}

/// <summary>
/// Added to the reader's named client after its production registration, so every reader request passes through it
/// on its way to the real DMS. A new instance per handler chain: the factory rebuilds chains when their lifetime ends.
/// </summary>
public sealed class FaultInjectingHandler(FaultPlan plan) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        RecordedRequest recorded = plan.Record(request);
        if (recorded.Kind == RequestKind.Page && plan.OnPage is { } onPage)
        {
            HttpResponseMessage? injected = await onPage(recorded, cancellationToken);
            if (injected is not null)
            {
                injected.RequestMessage = request;
                return injected;
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// The production reader registration (<see cref="ServiceCollectionExtensions.AddDmsEducationOrganizationProjectionReader"/>)
/// configured with the fixture's per-tenant projection credentials and the live DMS, plus the fault handler. One per
/// test fixture, so Discovery and token caches never carry over between cases.
/// </summary>
public sealed class ReaderHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public ReaderHost(ProjectionE2EFixture fixture, int pageSize = 2)
    {
        string section = DmsEducationOrganizationProjectionSettings.SectionName;
        Dictionary<string, string?> values = new()
        {
            [$"{section}:DmsBaseUrl"] = fixture.Environment.DmsBaseUrl.ToString().TrimEnd('/'),
            [$"{section}:PageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach ((string tenant, ClientCredentials client) in fixture.ProjectionClients)
        {
            values[$"{section}:TenantCredentials:{tenant}:ClientId"] = client.Key;
            values[$"{section}:TenantCredentials:{tenant}:ClientSecret"] = client.Secret;
        }

        ServiceCollection services = new();
        services.AddLogging();
        services.AddHttpClient();
        services.AddDmsEducationOrganizationProjectionReader(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()
        );
        services
            .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
            .AddHttpMessageHandler(() => new FaultInjectingHandler(Plan));
        _provider = services.BuildServiceProvider(validateScopes: true);

        // What ValidateOnStart does in a host: the settings must pass the production validator.
        _ = _provider.GetRequiredService<IOptions<DmsEducationOrganizationProjectionSettings>>().Value;
    }

    public FaultPlan Plan { get; } = new();

    public Task<EducationOrganizationProjectionReadResult> ReadAsync(
        string tenant,
        int dataStoreId,
        IReadOnlyDictionary<string, string> contexts,
        CancellationToken cancellationToken = default
    ) =>
        _provider
            .GetRequiredService<IEducationOrganizationProjectionReader>()
            .ReadAllAsync(
                new EducationOrganizationProjectionReadRequest(tenant, dataStoreId, contexts),
                cancellationToken
            );

    public void Dispose() => _provider.Dispose();

    /// <summary>
    /// A DMS problem response from the checked-in contract example, with the status the example states.
    /// </summary>
    public static HttpResponseMessage ContractProblem(string example)
    {
        string body = File.ReadAllText(Path.Combine(ContractExamplesDirectory(), $"problem-{example}.json"));
        using JsonDocument document = JsonDocument.Parse(body);
        HttpResponseMessage response = new(
            (System.Net.HttpStatusCode)document.RootElement.GetProperty("status").GetInt32()
        )
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
        return response;
    }

    private static string ContractExamplesDirectory()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            string candidate = Path.Combine(
                directory.FullName,
                "reference",
                "design",
                "edorg-projection-DMS-1440",
                "contract",
                "examples"
            );
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Setup failed: reference/design/edorg-projection-DMS-1440/contract/examples was not found above the "
                + "test output directory."
        );
    }
}
