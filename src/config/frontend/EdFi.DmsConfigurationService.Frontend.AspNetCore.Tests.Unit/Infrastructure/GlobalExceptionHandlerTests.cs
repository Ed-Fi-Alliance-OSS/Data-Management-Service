// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Verifies the CMS exception handler classifies each reachable exception into the Ed-Fi error
/// contract without exposing exception details. This is a non-fixture container; the runnable
/// fixtures are the nested <c>Given_…</c> classes.
/// </summary>
public class GlobalExceptionHandlerTests
{
    private sealed class FakeAcceptsMetadata : IAcceptsMetadata
    {
        public IReadOnlyList<string> ContentTypes => ["application/json"];
        public Type? RequestType => null;
        public bool IsOptional => false;
    }

    private static async Task<(
        bool Handled,
        DefaultHttpContext Context,
        string Content,
        JsonObject Body
    )> HandleAsync(
        Exception exception,
        string traceId,
        Endpoint? endpoint = null,
        RouteValueDictionary? routeValues = null
    )
    {
        var context = new DefaultHttpContext { TraceIdentifier = traceId };
        context.Response.Body = new MemoryStream();
        if (endpoint is not null)
        {
            // Exception handling clears the active endpoint and route values; the middleware stashes
            // the originals on this feature, which is what the handler reads.
            context.Features.Set<IExceptionHandlerFeature>(
                new ExceptionHandlerFeature
                {
                    Error = exception,
                    Path = context.Request.Path,
                    Endpoint = endpoint,
                    RouteValues = routeValues,
                }
            );
        }

        bool handled = await new GlobalExceptionHandler().TryHandleAsync(
            context,
            exception,
            CancellationToken.None
        );

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string content = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (handled, context, content, JsonNode.Parse(content)!.AsObject());
    }

    private static void AssertHandledContract(
        DefaultHttpContext context,
        JsonObject body,
        int status,
        string type,
        string title,
        string detail
    )
    {
        context.Response.StatusCode.Should().Be(status);
        context.Response.ContentType.Should().Be("application/problem+json");
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["detail"]!.GetValue<string>().Should().Be(detail);
        body["status"]!.GetValue<int>().Should().Be(status);

        string correlationId = body["correlationId"]!.GetValue<string>();
        correlationId.Should().Be(context.TraceIdentifier);
        context.Response.Headers["TraceId"].ToString().Should().Be(correlationId);
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_status_400_and_no_inner_exception
    {
        private const string Sentinel = "SENTINEL_BADREQ_400_3a1c_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            // No inner exception → classified as parameter validation, not the generic bad-request
            // taxonomy this branch used previously.
            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(Sentinel, StatusCodes.Status400BadRequest),
                "trace-400"
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_parameter_validation_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "Parameter validation failed. See 'errors' for details."
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_has_empty_validation_errors_and_a_fixed_value_free_errors_entry()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(1);
            _body["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The request contains one or more invalid parameters.");
        }
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_status_400_on_a_body_accepting_endpoint
    {
        private const string Sentinel = "SENTINEL_BADREQ_BODY_2f7a_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(Sentinel, StatusCodes.Status400BadRequest),
                "trace-400-body",
                new Endpoint(null, new EndpointMetadataCollection(new FakeAcceptsMetadata()), "test-endpoint")
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_generic_bad_request_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request",
                "Bad Request",
                "The request could not be processed. See 'errors' for details."
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_has_the_kb_exact_missing_body_message_with_empty_validation_errors()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("A non-empty request body is required.");
        }
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_an_unbindable_route_value_on_a_body_accepting_endpoint
    {
        private const string Sentinel = "SENTINEL_BADREQ_ROUTE_7c3e_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        private static Task RouteHandler(long id, object command)
        {
            _ = id;
            _ = command;
            return Task.CompletedTask;
        }

        [SetUp]
        public async Task Setup()
        {
            var endpoint = new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/v3/applications/{id}"),
                0,
                new EndpointMetadataCollection(
                    new FakeAcceptsMetadata(),
                    ((Func<long, object, Task>)RouteHandler).Method
                ),
                "unbindable-route-endpoint"
            );

            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(Sentinel, StatusCodes.Status400BadRequest),
                "trace-route-unbindable",
                endpoint,
                new RouteValueDictionary { ["id"] = "not-a-long" }
            );
        }

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_parameter_validation_contract_despite_the_endpoint_accepting_a_body() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "Parameter validation failed. See 'errors' for details."
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_has_the_fixed_parameter_message_and_empty_validation_errors()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("The request contains one or more invalid parameters.");
        }
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_a_bindable_route_value_on_a_body_accepting_endpoint
    {
        private const string Sentinel = "SENTINEL_BADREQ_ROUTE_OK_5d1b_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        private static Task RouteHandler(long id, object command)
        {
            _ = id;
            _ = command;
            return Task.CompletedTask;
        }

        [SetUp]
        public async Task Setup()
        {
            var endpoint = new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/v3/applications/{id}"),
                0,
                new EndpointMetadataCollection(
                    new FakeAcceptsMetadata(),
                    ((Func<long, object, Task>)RouteHandler).Method
                ),
                "bindable-route-endpoint"
            );

            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(Sentinel, StatusCodes.Status400BadRequest),
                "trace-route-bindable",
                endpoint,
                new RouteValueDictionary { ["id"] = "123" }
            );
        }

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_still_returns_the_generic_missing_body_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request",
                "Bad Request",
                "The request could not be processed. See 'errors' for details."
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_has_the_missing_body_message_and_empty_validation_errors()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("A non-empty request body is required.");
        }
    }

    [TestFixture]
    public class Given_an_invalid_data_exception
    {
        private const string Sentinel = "SENTINEL_FORM_6b4d_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            (_handled, _context, _content, _body) = await HandleAsync(
                new InvalidDataException(Sentinel),
                "trace-form"
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_generic_bad_request_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request",
                "Bad Request",
                "The request could not be processed. See 'errors' for details."
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_has_the_fixed_malformed_form_message_and_empty_validation_errors()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("The request form payload is malformed.");
        }
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_status_400_and_a_json_inner_exception
    {
        private const string Sentinel = "SENTINEL_BADREQ_JSON_9e2f_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            // A JsonException inner exception is the framework's signal (not message text) that this 400
            // came from request-body JSON deserialization.
            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(
                    "Failed to read parameter from the request body as JSON.",
                    StatusCodes.Status400BadRequest,
                    new JsonException(Sentinel)
                ),
                "trace-400-json"
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_data_validation_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request:data",
                "Data Validation Failed",
                "Data validation failed. See 'validationErrors' for details."
            );

        [Test]
        public void It_does_not_leak_the_parser_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_populates_validation_errors_at_the_document_root_with_empty_errors()
        {
            var validationErrors = _body["validationErrors"]!.AsObject();
            validationErrors.ContainsKey("$").Should().BeTrue();
            validationErrors["$"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("The request body contains invalid JSON.");
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_parameter_validation_exception
    {
        private bool _handled;
        private DefaultHttpContext _context = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var failures = new List<ValidationFailure> { new("Limit", "'limit' must be greater than 0.") };
            (_handled, _context, _, _body) = await HandleAsync(
                new ParameterValidationException(failures),
                "trace-parameter"
            );
        }

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_parameter_validation_contract_not_the_data_validation_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request:parameter",
                "Parameter Validation Failed",
                "Parameter validation failed. See 'errors' for details."
            );

        [Test]
        public void It_carries_the_validator_message_in_errors_with_empty_validation_errors()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal("'limit' must be greater than 0.");
        }
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_status_415
    {
        private const string Sentinel = "SENTINEL_BADREQ_415_5b2d_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(Sentinel, StatusCodes.Status415UnsupportedMediaType),
                "trace-415"
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_unsupported_media_type_contract() =>
            AssertHandledContract(
                _context,
                _body,
                415,
                "urn:ed-fi:api:unsupported-media-type",
                "Unsupported Media Type",
                "The value specified in the 'Content-Type' header is not supported by this host."
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);
    }

    [TestFixture]
    public class Given_a_bad_http_request_with_an_unclassified_status
    {
        private const string Sentinel = "SENTINEL_BADREQ_413_7c3e_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            // 413 (Kestrel request-body-size limit) has no documented Ed-Fi taxonomy URI → about:blank.
            (_handled, _context, _content, _body) = await HandleAsync(
                new BadHttpRequestException(Sentinel, StatusCodes.Status413PayloadTooLarge),
                "trace-413"
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_about_blank_contract() =>
            AssertHandledContract(
                _context,
                _body,
                413,
                "about:blank",
                ReasonPhrases.GetReasonPhrase(413),
                ""
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_includes_empty_extension_members()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_validation_exception
    {
        private bool _handled;
        private DefaultHttpContext _context = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            var failures = new List<ValidationFailure> { new("PropertyName", "Property is required") };
            (_handled, _context, _, _body) = await HandleAsync(
                new ValidationException(failures),
                "trace-validation"
            );
        }

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_data_validation_contract() =>
            AssertHandledContract(
                _context,
                _body,
                400,
                "urn:ed-fi:api:bad-request:data",
                "Data Validation Failed",
                "Data validation failed. See 'validationErrors' for details."
            );

        [Test]
        public void It_populates_grouped_validation_errors()
        {
            var validationErrors = _body["validationErrors"]!.AsObject();
            validationErrors.ContainsKey("PropertyName").Should().BeTrue();
            validationErrors["PropertyName"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Contain("Property is required");
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_generic_exception
    {
        private const string Sentinel = "SENTINEL_GENERIC_500_9d4f_must_not_leak";

        private bool _handled;
        private DefaultHttpContext _context = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup() =>
            (_handled, _context, _content, _body) = await HandleAsync(
                new InvalidOperationException(Sentinel),
                "trace-500"
            );

        [Test]
        public void It_reports_the_exception_as_handled() => _handled.Should().BeTrue();

        [Test]
        public void It_returns_the_internal_server_error_contract() =>
            AssertHandledContract(
                _context,
                _body,
                500,
                "urn:ed-fi:api:internal-server-error",
                "Internal Server Error",
                ""
            );

        [Test]
        public void It_does_not_leak_the_exception_message() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_includes_empty_extension_members()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }
    }
}
