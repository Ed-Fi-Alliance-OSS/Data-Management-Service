// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;

/// <summary>
/// <c>GET /v3/jobs/{jobId}</c> through the HTTP pipeline with a faked repository (spec §5, §2 rows 3, 4 and 15):
/// authorization, the five-property body, the not-found and failure contracts, safe failure logging, and the
/// multi-tenant header check.
/// </summary>
public class JobModuleTests
{
    private const string JobId = "Job-Report_7f3a9c21-Tenant";
    private const string TenantName = "tenant-a";

    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();
    protected IJobRepository Repository { get; private set; } = null!;
    protected ITenantRepository TenantRepository { get; private set; } = null!;
    protected CapturingLoggerProvider Logs { get; private set; } = null!;
    protected ITenantContextProvider? TenantContextAtRead { get; private set; }
    protected HttpResponseMessage Response { get; private set; } = null!;
    protected string Content { get; private set; } = "";

    [SetUp]
    public void CreateFakes()
    {
        Repository = A.Fake<IJobRepository>();
        TenantRepository = A.Fake<ITenantRepository>();
        Logs = new CapturingLoggerProvider();
        TenantContextAtRead = null;
    }

    [TearDown]
    public void DisposeWebApplicationFactories()
    {
        Response?.Dispose();
        _factoryTracker.DisposeTrackedFactories();
        Logs.Dispose();
    }

    protected void ReturnsFromRepository(JobStatusQueryResult result) =>
        A.CallTo(() => Repository.GetJobStatus(A<string>._, A<CancellationToken>._)).Returns(result);

    protected async Task GetAsync(
        string path,
        string? scope,
        bool multiTenancy = false,
        string? tenantHeader = null
    )
    {
        var factory = _factoryTracker.Track(
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:MultiTenancy", multiTenancy.ToString());
                builder.ConfigureServices(collection =>
                {
                    collection.AddTestAuthentication();
                    collection.AddTransient(_ => TenantRepository);
                    collection.AddSingleton<ILoggerProvider>(Logs);
                    collection.AddTransient(provider =>
                    {
                        // The repository reads the tenant from this scoped provider, so capture it per request.
                        TenantContextAtRead = provider.GetRequiredService<ITenantContextProvider>();
                        return Repository;
                    });
                });
            })
        );

        using var client = factory.CreateClient();
        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope);
        }
        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", tenantHeader);
        }

        Response = await client.GetAsync(path);
        Content = await Response.Content.ReadAsStringAsync();
    }

    protected JsonObject Body => JsonNode.Parse(Content)!.AsObject();

    protected void RepositoryWasNotCalled() =>
        A.CallTo(() => Repository.GetJobStatus(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();

    [TestFixture]
    public class Given_a_pending_job_read_with_the_admin_scope : JobModuleTests
    {
        [SetUp]
        public async Task Setup()
        {
            ReturnsFromRepository(
                new JobStatusQueryResult.Success(
                    new JobStatusResponse
                    {
                        JobId = JobId,
                        Status = JobStatuses.Pending,
                        CreatedAt = new DateTime(2026, 9, 24, 12, 30, 45, DateTimeKind.Utc),
                        FinishedAt = null,
                        ErrorMessage = null,
                    }
                )
            );
            await GetAsync($"/v3/jobs/{JobId}", AuthorizationScopes.AdminScope.Name);
        }

        [Test]
        public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_returns_json() =>
            Response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        [Test]
        public void It_exposes_exactly_the_five_contract_properties() =>
            Body.Select(property => property.Key)
                .Should()
                .BeEquivalentTo("jobId", "status", "createdAt", "finishedAt", "errorMessage");

        [Test]
        public void It_returns_the_job_id_and_status()
        {
            Body["jobId"]!.GetValue<string>().Should().Be(JobId);
            Body["status"]!.GetValue<string>().Should().Be("Pending");
        }

        [Test]
        public void It_serializes_utc_timestamps_with_Z_and_nulls_present()
        {
            Content.Should().Contain("\"createdAt\":\"2026-09-24T12:30:45Z\"");
            Content.Should().Contain("\"finishedAt\":null");
            Content.Should().Contain("\"errorMessage\":null");
        }

        [Test]
        public void It_passes_the_route_identifier_unchanged() =>
            A.CallTo(() => Repository.GetJobStatus(JobId, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_a_failed_job : JobModuleTests
    {
        [SetUp]
        public async Task Setup()
        {
            ReturnsFromRepository(
                new JobStatusQueryResult.Success(
                    new JobStatusResponse
                    {
                        JobId = JobId,
                        Status = JobStatuses.Error,
                        CreatedAt = new DateTime(2026, 9, 24, 12, 30, 45, DateTimeKind.Utc),
                        FinishedAt = new DateTime(2026, 9, 24, 12, 31, 0, 250, DateTimeKind.Utc),
                        ErrorMessage = "The job type is not registered.",
                    }
                )
            );
            await GetAsync($"/v3/jobs/{JobId}", AuthorizationScopes.AdminScope.Name);
        }

        [Test]
        public void It_returns_the_status_finish_time_and_error_message()
        {
            Body["status"]!.GetValue<string>().Should().Be("Error");
            Content.Should().Contain("\"finishedAt\":\"2026-09-24T12:31:00.25Z\"");
            Body["errorMessage"]!.GetValue<string>().Should().Be("The job type is not registered.");
        }
    }

    [TestFixture]
    public class Given_a_job_read_with_the_read_only_scope : JobModuleTests
    {
        [SetUp]
        public async Task Setup()
        {
            ReturnsFromRepository(
                new JobStatusQueryResult.Success(
                    new JobStatusResponse
                    {
                        JobId = JobId,
                        Status = JobStatuses.Completed,
                        CreatedAt = new DateTime(2026, 9, 24, 12, 30, 45, DateTimeKind.Utc),
                        FinishedAt = new DateTime(2026, 9, 24, 12, 31, 0, DateTimeKind.Utc),
                    }
                )
            );
            await GetAsync($"/v3/jobs/{JobId}", AuthorizationScopes.ReadOnlyScope.Name);
        }

        [Test]
        public void It_returns_200() => Response.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_returns_the_job() => Body["status"]!.GetValue<string>().Should().Be("Completed");
    }

    [TestFixture]
    public class Given_a_job_read_with_only_the_auth_metadata_read_only_scope : JobModuleTests
    {
        [SetUp]
        public async Task Setup() =>
            await GetAsync($"/v3/jobs/{JobId}", AuthorizationScopes.AuthMetadataReadOnlyAccessScope.Name);

        [Test]
        public void It_returns_403() => Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        [Test]
        public void It_does_not_read_the_job() => RepositoryWasNotCalled();
    }

    [TestFixture]
    public class Given_an_anonymous_job_read : JobModuleTests
    {
        [SetUp]
        public async Task Setup() => await GetAsync($"/v3/jobs/{JobId}", scope: null);

        [Test]
        public void It_returns_401() => Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        [Test]
        public void It_does_not_read_the_job() => RepositoryWasNotCalled();
    }

    [TestFixture]
    public class Given_an_absent_job : JobModuleTests
    {
        [SetUp]
        public async Task Setup()
        {
            ReturnsFromRepository(new JobStatusQueryResult.FailureNotFound());
            await GetAsync($"/v3/jobs/{JobId}", AuthorizationScopes.AdminScope.Name);
        }

        [Test]
        public void It_returns_404() => Response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_returns_problem_json() =>
            Response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_returns_the_cms_not_found_problem_details()
        {
            Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
            Body["title"]!.GetValue<string>().Should().Be("Not Found");
            Body["status"]!.GetValue<int>().Should().Be(404);
            Body["detail"]!.GetValue<string>().Should().Be("Job not found.");
            Body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        }
    }

    [TestFixture]
    public class Given_a_repository_failure : JobModuleTests
    {
        private static readonly JobFailureDiagnostic _diagnostic = new(
            "Npgsql.NpgsqlException/System.TimeoutException",
            "57014",
            "GetJobStatus"
        );

        [SetUp]
        public async Task Setup()
        {
            ReturnsFromRepository(new JobStatusQueryResult.FailureUnknown(_diagnostic));
            // %0A decodes to a line feed in the route value, which must not reach the log unsanitized.
            await GetAsync("/v3/jobs/forged%0Aline", AuthorizationScopes.AdminScope.Name);
        }

        private CapturedLog FailureLog => Logs.Entries.Single(entry => entry.EventId.Id == 1437_40);

        [Test]
        public void It_returns_500() => Response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_returns_the_unknown_failure_contract() =>
            Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");

        [Test]
        public void It_keeps_the_diagnostic_out_of_the_response()
        {
            Content.Should().NotContain("Npgsql");
            Content.Should().NotContain("57014");
        }

        [Test]
        public void It_logs_repository_failure_diagnostics_without_messages()
        {
            FailureLog.Level.Should().Be(LogLevel.Error);
            FailureLog.Exception.Should().BeNull();
            FailureLog.State["Event"].Should().Be(nameof(JobModule.JobStatusReadFailed));
            FailureLog.State["ExceptionTypeChain"].Should().Be(_diagnostic.ExceptionTypeChain);
            FailureLog.State["ProviderErrorCode"].Should().Be("57014");
            FailureLog.State["Operation"].Should().Be("GetJobStatus");
        }

        [Test]
        public void It_logs_the_sanitized_job_id() => FailureLog.State["JobId"].Should().Be("forgedline");
    }

    [TestFixture]
    public class Given_multi_tenancy_without_a_tenant_header : JobModuleTests
    {
        [SetUp]
        public async Task Setup() =>
            await GetAsync($"/v3/jobs/{JobId}", AuthorizationScopes.AdminScope.Name, multiTenancy: true);

        [Test]
        public void It_returns_400() => Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_returns_the_tenant_header_problem_details() =>
            Body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The 'Tenant' header is required when multi-tenancy is enabled");

        [Test]
        public void It_does_not_read_the_job() => RepositoryWasNotCalled();
    }

    [TestFixture]
    public class Given_multi_tenancy_with_an_unknown_tenant : JobModuleTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => TenantRepository.GetTenantByName("unknown"))
                .Returns(new TenantGetByNameResult.FailureNotFound());
            await GetAsync(
                $"/v3/jobs/{JobId}",
                AuthorizationScopes.AdminScope.Name,
                multiTenancy: true,
                tenantHeader: "unknown"
            );
        }

        [Test]
        public void It_returns_400() => Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        [Test]
        public void It_does_not_read_the_job() => RepositoryWasNotCalled();
    }

    [TestFixture]
    public class Given_multi_tenancy_with_a_valid_tenant : JobModuleTests
    {
        private TenantContext? _tenantContext;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => TenantRepository.GetTenantByName(TenantName))
                .Returns(
                    new TenantGetByNameResult.Success(new TenantResponse { Id = 42, Name = TenantName })
                );
            A.CallTo(() => Repository.GetJobStatus(A<string>._, A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    _tenantContext = TenantContextAtRead?.Context;
                    return new JobStatusQueryResult.FailureNotFound();
                });
            await GetAsync(
                $"/v3/jobs/{JobId}",
                AuthorizationScopes.AdminScope.Name,
                multiTenancy: true,
                tenantHeader: TenantName
            );
        }

        [Test]
        public void It_reads_the_job_within_the_resolved_tenant() =>
            _tenantContext.Should().BeOfType<TenantContext.Multitenant>().Which.TenantId.Should().Be(42);

        [Test]
        public void It_returns_the_repository_result() =>
            Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>A log record captured with its structured state.</summary>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    IReadOnlyDictionary<string, object?> State,
    Exception? Exception
);

/// <summary>Captures every log record the host writes, with its structured state.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Dictionary<string, object?> values = [];
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach ((string key, object? value) in pairs)
                {
                    values[key] = value;
                }
            }
            entries.Enqueue(new CapturedLog(category, logLevel, eventId, values, exception));
        }
    }
}
