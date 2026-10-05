// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class ProjectionHttpClientLoggerTests
{
    private const string Cursor = "CURSOR-VALUE-1440";
    private const string Token = "TOKEN-VALUE-1440";
    private const string HostileMessage = "connect failed\r\nFORGED " + Token;

    private const string RequestUrl =
        "https://dms.example.org/api/Tenant1/255901/management/education-organizations?dataStoreId=7&limit=2&cursor="
        + Cursor;

    /// <summary>A primary handler that answers every request with <paramref name="respond"/>.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    /// <summary>
    /// Sends one request through the production registration, with every category recorded at Trace and only the
    /// primary handler replaced, and returns what was logged and how the call ended.
    /// </summary>
    private static async Task<(
        IReadOnlyList<RecordedLog> Records,
        Exception? Exception
    )> SendThroughClientAsync(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        RecordingLoggerProvider recorder = new();
        ServiceCollection services = new();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder));
        services.AddDmsEducationOrganizationProjectionReader(new ConfigurationBuilder().Build());
        services
            .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(DmsEducationOrganizationProjectionHttpClient.Name);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Exception? thrown = null;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }
        return (recorder.Records, thrown);
    }

    private static object? Field(RecordedLog record, string name) =>
        record.State.Single(pair => pair.Key == name).Value;

    [TestFixture]
    public class Given_a_successful_request
    {
        private IReadOnlyList<RecordedLog> _records = null!;
        private RecordedLog _projectionRecord = null!;

        [SetUp]
        public async Task Setup()
        {
            (_records, _) = await SendThroughClientAsync(
                RequestUrl,
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Cursor) }
            );
            _projectionRecord = _records.Single(record =>
                record.Category == typeof(ProjectionHttpClientLogger).FullName
            );
        }

        [Test]
        public void It_logs_at_information() => _projectionRecord.Level.Should().Be(LogLevel.Information);

        [Test]
        public void It_logs_the_method() => Field(_projectionRecord, "Method").Should().Be("GET");

        [Test]
        public void It_logs_the_path_without_the_query() =>
            Field(_projectionRecord, "Path")
                .Should()
                .Be("/api/Tenant1/255901/management/education-organizations");

        [Test]
        public void It_logs_the_status() => Field(_projectionRecord, "StatusCode").Should().Be(200);

        [Test]
        public void It_logs_the_elapsed_time() =>
            Field(_projectionRecord, "ElapsedMilliseconds").Should().BeOfType<long>();

        [Test]
        public void It_logs_nothing_from_the_default_http_client_loggers() =>
            _records.Should().NotContain(record => record.Category.StartsWith("System.Net.Http.HttpClient"));

        [Test]
        public void It_logs_no_cursor_query_or_token_at_any_level() =>
            _records
                .Should()
                .NotContain(record =>
                    record.AllText.Contains(Cursor)
                    || record.AllText.Contains(Token)
                    || record.AllText.Contains("dataStoreId=")
                    || record.AllText.Contains("limit=")
                );
    }

    [TestFixture]
    public class Given_a_failed_request
    {
        private IReadOnlyList<RecordedLog> _records = null!;
        private RecordedLog _projectionRecord = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            (_records, _exception) = await SendThroughClientAsync(
                RequestUrl,
                _ => throw new HttpRequestException(HostileMessage, new SocketException(10061))
            );
            _projectionRecord = _records.Single(record =>
                record.Category == typeof(ProjectionHttpClientLogger).FullName
            );
        }

        [Test]
        public void It_lets_the_failure_reach_the_caller() =>
            _exception.Should().BeOfType<HttpRequestException>();

        [Test]
        public void It_logs_a_warning() => _projectionRecord.Level.Should().Be(LogLevel.Warning);

        [Test]
        public void It_logs_the_exception_type_chain() =>
            Field(_projectionRecord, "ExceptionTypes")
                .Should()
                .Be("System.Net.Http.HttpRequestException/System.Net.Sockets.SocketException");

        [Test]
        public void It_logs_the_path_without_the_query() =>
            Field(_projectionRecord, "Path")
                .Should()
                .Be("/api/Tenant1/255901/management/education-organizations");

        [Test]
        public void It_does_not_attach_the_exception() => _projectionRecord.Exception.Should().BeNull();

        [Test]
        public void It_logs_no_exception_message_cursor_or_token_at_any_level() =>
            _records
                .Should()
                .NotContain(record =>
                    record.AllText.Contains("FORGED")
                    || record.AllText.Contains("connect failed")
                    || record.AllText.Contains(Cursor)
                    || record.AllText.Contains(Token)
                );
    }

    [TestFixture]
    public class Given_a_path_with_encoded_line_breaks
    {
        private string _path = null!;

        [SetUp]
        public async Task Setup()
        {
            (IReadOnlyList<RecordedLog> records, _) = await SendThroughClientAsync(
                "https://dms.example.org/api/x%0D%0AFORGED/management/education-organizations?cursor="
                    + Cursor,
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            );
            _path = (string)
                Field(
                    records.Single(record => record.Category == typeof(ProjectionHttpClientLogger).FullName),
                    "Path"
                )!;
        }

        [Test]
        public void It_logs_the_sanitized_path() =>
            _path.Should().Be("/api/x0D0AFORGED/management/education-organizations");
    }
}
