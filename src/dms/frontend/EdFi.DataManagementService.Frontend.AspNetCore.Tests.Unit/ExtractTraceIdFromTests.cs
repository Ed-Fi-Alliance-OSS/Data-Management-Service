// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Trace_Id_Extraction
{
    private static IOptions<AppSettings> AppSettingsOptions(
        string correlationIdHeader = "correlationid",
        int correlationIdMaxLength = AppSettings.DefaultCorrelationIdMaxLength
    ) =>
        Options.Create(
            new AppSettings
            {
                AuthenticationService = "test",
                Datastore = "postgresql",
                CorrelationIdHeader = correlationIdHeader,
                CorrelationIdMaxLength = correlationIdMaxLength,
            }
        );

    private static DefaultHttpContext CreateHttpContext(
        string traceIdentifier = "host-trace-id",
        string? correlationId = null,
        string correlationIdHeader = "correlationid"
    )
    {
        DefaultHttpContext httpContext = new();
        httpContext.TraceIdentifier = traceIdentifier;

        if (correlationId is not null)
        {
            httpContext.Request.Headers[correlationIdHeader] = correlationId;
        }

        return httpContext;
    }

    [Test]
    public void It_preserves_a_well_formed_client_supplied_value()
    {
        DefaultHttpContext httpContext = CreateHttpContext(correlationId: "test-correlationId");

        AspNetCoreFrontend
            .ExtractTraceIdFrom(httpContext.Request, AppSettingsOptions())
            .Value.Should()
            .Be("test-correlationId");
    }

    [Test]
    public void It_preserves_printable_punctuation_and_non_ascii_characters()
    {
        const string correlationId = "trace+={}@|,#()[]<>\"'ß";
        DefaultHttpContext httpContext = CreateHttpContext(correlationId: correlationId);

        AspNetCoreFrontend
            .ExtractTraceIdFrom(httpContext.Request, AppSettingsOptions())
            .Value.Should()
            .Be(correlationId);
    }

    [Test]
    public void It_removes_control_characters_from_a_client_supplied_value()
    {
        DefaultHttpContext httpContext = CreateHttpContext(correlationId: "trace\r\nid\twith\0unsafe");

        AspNetCoreFrontend
            .ExtractTraceIdFrom(httpContext.Request, AppSettingsOptions())
            .Value.Should()
            .Be("traceidwithunsafe");
    }

    [Test]
    public void It_truncates_before_removing_control_characters()
    {
        DefaultHttpContext httpContext = CreateHttpContext(correlationId: "12\r{34}\t567890");

        AspNetCoreFrontend
            .ExtractTraceIdFrom(httpContext.Request, AppSettingsOptions(correlationIdMaxLength: 8))
            .Value.Should()
            .Be("12{34}");
    }

    [Test]
    public void It_is_idempotent()
    {
        TraceId normalized = AspNetCoreFrontend.NormalizeTraceId("12\r{34}\t567890", 8);

        AspNetCoreFrontend.NormalizeTraceId(normalized.Value, 8).Should().Be(normalized);
    }

    [Test]
    public void It_falls_back_to_the_default_maximum_when_the_configured_limit_is_not_positive()
    {
        string oversized = new('a', AppSettings.DefaultCorrelationIdMaxLength + 10);

        AspNetCoreFrontend
            .NormalizeTraceId(oversized, 0)
            .Value.Length.Should()
            .Be(AppSettings.DefaultCorrelationIdMaxLength);
    }

    [Test]
    public void It_returns_empty_when_normalizing_null_or_empty_values()
    {
        AspNetCoreFrontend.NormalizeTraceId(null, 8).Value.Should().BeEmpty();
        AspNetCoreFrontend.NormalizeTraceId(string.Empty, 8).Value.Should().BeEmpty();
    }

    [Test]
    public void It_uses_the_trace_identifier_when_the_correlation_header_is_disabled()
    {
        DefaultHttpContext httpContext = CreateHttpContext(
            traceIdentifier: "host-trace-id",
            correlationId: "client-value"
        );

        AspNetCoreFrontend
            .ExtractTraceIdFrom(httpContext.Request, AppSettingsOptions(correlationIdHeader: string.Empty))
            .Value.Should()
            .Be("host-trace-id");
    }
}
