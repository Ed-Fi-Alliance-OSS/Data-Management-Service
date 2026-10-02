// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using OpenIddictIdentityOptions = EdFi.DmsConfigurationService.Backend.OpenIddict.Models.IdentityOptions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// DMS-1327 D-12: the host starts only when an <see cref="ITokenRevocationManager"/> can be constructed
/// for its identity provider, and that check is construction only. Each deployment shape boots with its
/// <c>IdentitySettings:Authority</c> pointing at a recording endpoint that stands in for an unusable
/// identity provider: it counts every connection and drops it unanswered, so any provider request at
/// startup is observed, and any at request time fails.
/// </summary>
public class TokenRevocationStartupTests
{
    private const string Sentinel = "SECRET-STARTUP-SENTINEL";

    /// <summary>Every identity provider and engine shape the host supports.</summary>
    public static readonly object[] Shapes =
    [
        new object[] { "self-contained", "postgresql" },
        new object[] { "self-contained", "mssql" },
        new object[] { "keycloak", "postgresql" },
    ];

    private static FormUrlEncodedContent RevocationForm(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));

    private static async Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        string token = "opaque-token"
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/connect/revoke")
        {
            Content = token.Length == 0 ? RevocationForm() : RevocationForm(("token", token)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("revocation-caller:revocation-secret"))
        );
        return await client.SendAsync(request);
    }

    private static async Task<string?> OAuthErrorAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())?["error"]?.GetValue<string>();

    /// <summary>The exception a registration factory throws, with the sentinel everywhere it can carry text.</summary>
    public sealed class SentinelFactoryException(string message, Exception inner) : Exception(message, inner);

    private static Exception ThrowingFactoryFailure()
    {
        SentinelFactoryException failure = new(
            $"factory failed with {Sentinel}",
            new TimeoutException($"inner {Sentinel}")
        );
        failure.Data["connection"] = Sentinel;
        return failure;
    }

    private static readonly string _throwingFactoryTypeChain =
        $"{typeof(SentinelFactoryException).FullName} -> {typeof(TimeoutException).FullName}";

    [TestFixtureSource(typeof(TokenRevocationStartupTests), nameof(Shapes))]
    public class Given_a_boot_with_the_registration_its_provider_ships(string provider, string datastore)
    {
        private RevocationBoot _boot = null!;

        [OneTimeSetUp]
        public void OneTimeSetUp() => _boot = RevocationBoot.Run(provider, datastore);

        [OneTimeTearDown]
        public void OneTimeTearDown() => _boot.Dispose();

        [Test]
        public void It_starts() => _boot.StartupException.Should().BeNull();

        [Test]
        public void It_resolves_the_providers_manager()
        {
            using IServiceScope scope = _boot.Factory.Services.CreateScope();
            Type expected =
                provider == "keycloak"
                    ? typeof(KeycloakTokenRevocationManager)
                    : typeof(OpenIddictTokenManager);

            scope.ServiceProvider.GetRequiredService<ITokenRevocationManager>().Should().BeOfType(expected);
        }

        [Test]
        public void It_logs_nothing_critical() =>
            _boot.Logs.Records.Should().NotContain(record => record.Level == LogLevel.Critical);

        [Test]
        public void It_makes_no_identity_provider_request() =>
            _boot.ProviderConnectionsAtStartup.Should().Be(0);
    }

    [TestFixtureSource(typeof(TokenRevocationStartupTests), nameof(Shapes))]
    public class Given_a_boot_with_no_revocation_manager_registered(string provider, string datastore)
    {
        private static string NotRegisteredMessage(string provider) =>
            $"No token revocation manager is registered for AppSettings:IdentityProvider '{provider}'. "
            + "Register one for this provider or correct the setting, then restart.";

        private RevocationBoot _boot = null!;

        [OneTimeSetUp]
        public void OneTimeSetUp() =>
            _boot = RevocationBoot.Run(
                provider,
                datastore,
                services => services.RemoveAll<ITokenRevocationManager>()
            );

        [OneTimeTearDown]
        public void OneTimeTearDown() => _boot.Dispose();

        [Test]
        public void It_fails_to_start_with_the_not_registered_message() =>
            _boot
                .FindByMessage(NotRegisteredMessage(provider))
                .Should()
                .BeOfType<InvalidOperationException>();

        [Test]
        public void It_logs_the_same_message_as_critical_without_an_exception()
        {
            CapturedStartupLog critical = _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.Level == LogLevel.Critical)
                .Subject;
            critical.Message.Should().Be(NotRegisteredMessage(provider));
            critical.Exception.Should().BeNull();
        }

        [Test]
        public void It_makes_no_identity_provider_request() =>
            _boot.ProviderConnectionsAtStartup.Should().Be(0);
    }

    [TestFixtureSource(typeof(TokenRevocationStartupTests), nameof(Shapes))]
    public class Given_a_boot_whose_revocation_manager_factory_throws(string provider, string datastore)
    {
        private static string NotConstructedMessage(string provider, string exceptionTypes) =>
            $"The token revocation manager for AppSettings:IdentityProvider '{provider}' could not be "
            + $"constructed ({exceptionTypes}). Correct the registration or its dependencies, then restart.";

        private RevocationBoot _boot = null!;

        [OneTimeSetUp]
        public void OneTimeSetUp() =>
            _boot = RevocationBoot.Run(
                provider,
                datastore,
                services =>
                {
                    services.RemoveAll<ITokenRevocationManager>();
                    services.AddTransient<ITokenRevocationManager>(_ => throw ThrowingFactoryFailure());
                }
            );

        [OneTimeTearDown]
        public void OneTimeTearDown() => _boot.Dispose();

        [Test]
        public void It_fails_to_start_with_the_message_naming_the_exception_type_chain() =>
            _boot
                .FindByMessage(NotConstructedMessage(provider, _throwingFactoryTypeChain))
                .Should()
                .BeOfType<InvalidOperationException>();

        [Test]
        public void It_does_not_wrap_the_factory_exception() =>
            _boot
                .FindByMessage(NotConstructedMessage(provider, _throwingFactoryTypeChain))!
                .InnerException.Should()
                .BeNull();

        [Test]
        public void It_logs_the_same_message_as_critical_without_an_exception()
        {
            CapturedStartupLog critical = _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.Level == LogLevel.Critical)
                .Subject;
            critical.Message.Should().Be(NotConstructedMessage(provider, _throwingFactoryTypeChain));
            critical.Exception.Should().BeNull();
        }

        [Test]
        public void It_keeps_the_factory_exception_text_out_of_every_log_field_and_the_startup_failure() =>
            _boot.CapturedText().Should().NotContain(Sentinel);

        [Test]
        public void It_makes_no_identity_provider_request() =>
            _boot.ProviderConnectionsAtStartup.Should().Be(0);
    }

    /// <summary>
    /// A 503 is a per-request answer: the host that gave it keeps serving, and the manager is consulted
    /// again for the next request.
    /// </summary>
    [TestFixtureSource(typeof(TokenRevocationStartupTests), nameof(Shapes))]
    public class Given_a_running_host_whose_revocation_manager_answers_temporarily_unavailable(
        string provider,
        string datastore
    )
    {
        private readonly ScriptedRevocationManager _manager = new(
            new TokenRevocationResult.TemporarilyUnavailable("fake-outage"),
            new TokenRevocationResult.Completed()
        );
        private RevocationBoot _boot = null!;
        private HttpResponseMessage _first = null!;
        private HttpResponseMessage _second = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _boot = RevocationBoot.Run(
                provider,
                datastore,
                services =>
                {
                    services.RemoveAll<ITokenRevocationManager>();
                    services.AddSingleton<ITokenRevocationManager>(_manager);
                }
            );
            _first = await RevokeAsync(_boot.Client!);
            _second = await RevokeAsync(_boot.Client!);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _first.Dispose();
            _second.Dispose();
            _boot.Dispose();
        }

        [Test]
        public async Task It_answers_the_first_request_503_temporarily_unavailable()
        {
            _first.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await OAuthErrorAsync(_first)).Should().Be("temporarily_unavailable");
        }

        [Test]
        public void It_serves_the_next_request_through_the_manager_again()
        {
            _second.StatusCode.Should().Be(HttpStatusCode.OK);
            _manager.Calls.Should().Be(2);
        }

        [Test]
        public void It_does_not_stop_the_host() =>
            _boot
                .Factory.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.IsCancellationRequested.Should()
                .BeFalse();
    }

    /// <summary>
    /// A registration that constructs at startup and throws on a later resolution, which the startup
    /// check cannot see: the request it fails is answered 500 <c>server_error</c> in the OAuth format and
    /// logged once as a failed request by exception type names only, the factory's message, inner
    /// exception and <c>Data</c> reach neither the response nor any log field, scope or attached
    /// exception, and the host keeps serving (D-15, D-17).
    /// </summary>
    [TestFixture]
    public class Given_a_running_host_whose_revocation_manager_later_fails_to_construct
    {
        private int _resolutions;
        private RevocationBoot _boot = null!;
        private HttpResponseMessage _failed = null!;
        private string _failedContent = null!;
        private HttpResponseMessage _next = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _boot = RevocationBoot.Run(
                "self-contained",
                "postgresql",
                services =>
                {
                    services.RemoveAll<ITokenRevocationManager>();
                    services.AddTransient<ITokenRevocationManager>(_ =>
                        Interlocked.Increment(ref _resolutions) == 2
                            ? throw ThrowingFactoryFailure()
                            : new ScriptedRevocationManager(new TokenRevocationResult.Completed())
                    );
                }
            );
            _failed = await RevokeAsync(_boot.Client!);
            _failedContent = await _failed.Content.ReadAsStringAsync();
            _next = await RevokeAsync(_boot.Client!);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _failed.Dispose();
            _next.Dispose();
            _boot.Dispose();
        }

        [Test]
        public void It_starts() => _boot.StartupException.Should().BeNull();

        [Test]
        public void It_answers_500_server_error_in_the_oauth_format()
        {
            _failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            JsonNode.Parse(_failedContent)?["error"]?.GetValue<string>().Should().Be("server_error");
        }

        [Test]
        public void It_does_not_return_the_factory_exception_text() =>
            _failedContent.Should().NotContain(Sentinel);

        [Test]
        public void It_logs_one_failed_request_naming_the_exception_types_without_the_exception()
        {
            CapturedStartupLog failed = FailedRequestLog();
            failed.Exception.Should().BeNull();
            failed
                .State.Should()
                .Contain(new KeyValuePair<string, object?>("ExceptionTypes", _throwingFactoryTypeChain));
        }

        [Test]
        public void It_keeps_the_correlation_fields_on_the_failed_request()
        {
            string traceId = _failed.Headers.GetValues("TraceId").Single();
            CapturedStartupLog failed = FailedRequestLog();
            failed.State.Should().Contain(new KeyValuePair<string, object?>("TraceId", traceId));
            failed.State.Should().Contain(new KeyValuePair<string, object?>("StatusCode", 500));
            failed
                .Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>()
                .SelectMany(scope => scope)
                .Where(pair => pair.Key == "TraceId")
                .Select(pair => pair.Value)
                .Should()
                .Contain(traceId, "the request logging scope carries the same TraceId");
        }

        [Test]
        public void It_keeps_the_factory_exception_content_out_of_every_log_field_scope_and_exception() =>
            _boot.CapturedText().Should().NotContain(Sentinel);

        [Test]
        public void It_serves_the_next_request() => _next.StatusCode.Should().Be(HttpStatusCode.OK);

        private CapturedStartupLog FailedRequestLog() =>
            _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.EventId.Name == "HttpRequestFailed")
                .Subject;
    }

    // ----- Exceptions the framework's exception middleware cannot answer (D-15, D-17) -----

    /// <summary>The request header that makes the outermost test middleware fail every response write.</summary>
    private const string FailResponseWriteHeader = "X-Test-Fail-Response-Write";

    /// <summary>
    /// A boot whose <paramref name="route"/> fails at request time while binding its manager parameter,
    /// with the sentinel-bearing exception: after starting the response, or before it so the error
    /// handler runs and its response write fails. The revocation route carries
    /// <c>ExceptionTypeOnlyLoggingMetadata</c>; <c>/connect/token</c> is the unmarked control. The
    /// framework's exception middleware logs an exception it cannot answer with the exception attached,
    /// so these are the paths where only withholding the content before that middleware keeps it out
    /// of the logs.
    /// </summary>
    private static async Task<(RevocationBoot Boot, Exception? ClientFailure)> RunRequestTimeFailureAsync(
        string route,
        bool startResponseFirst
    )
    {
        RevocationBoot boot = RevocationBoot.Run(
            "self-contained",
            "postgresql",
            services =>
            {
                services.AddHttpContextAccessor();
                services.AddTransient<IStartupFilter, FailingResponseWriteStartupFilter>();
                services.RemoveAll<ITokenRevocationManager>();
                services.AddTransient<ITokenRevocationManager>(provider =>
                    FailRequestTime(provider, "/connect/revoke", startResponseFirst)
                    ?? new ScriptedRevocationManager(new TokenRevocationResult.Completed())
                );
                services.RemoveAll<ITokenManager>();
                services.AddTransient<ITokenManager>(provider =>
                    FailRequestTime<ITokenManager>(provider, "/connect/token", startResponseFirst)
                    ?? A.Fake<ITokenManager>()
                );
            }
        );

        using HttpRequestMessage request = new(HttpMethod.Post, route)
        {
            Content = RevocationForm(("token", "opaque-token"), ("grant_type", "client_credentials")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("revocation-caller:revocation-secret"))
        );
        if (!startResponseFirst)
        {
            request.Headers.Add(FailResponseWriteHeader, "1");
        }

        try
        {
            using HttpResponseMessage response = await boot.Client!.SendAsync(request);
            _ = await response.Content.ReadAsStringAsync();
            return (boot, null);
        }
        catch (Exception exception)
        {
            return (boot, exception);
        }
    }

    /// <summary>
    /// Throws the sentinel-bearing exception when resolved for a request to <paramref name="path"/>,
    /// after starting the response when asked; returns null outside such a request, startup included.
    /// </summary>
    private static T? FailRequestTime<T>(IServiceProvider provider, string path, bool startResponseFirst)
        where T : class
    {
        HttpContext? context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (context is null || !context.Request.Path.StartsWithSegments(path))
        {
            return null;
        }

        if (startResponseFirst)
        {
            context.Response.StartAsync().GetAwaiter().GetResult();
        }

        throw ThrowingFactoryFailure();
    }

    private static ITokenRevocationManager? FailRequestTime(
        IServiceProvider provider,
        string path,
        bool startResponseFirst
    ) => FailRequestTime<ITokenRevocationManager>(provider, path, startResponseFirst);

    /// <summary>
    /// Outermost test middleware: a request carrying <see cref="FailResponseWriteHeader"/> gets a response
    /// body that refuses every write, so the error handler's response write fails.
    /// </summary>
    private sealed class FailingResponseWriteStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        if (context.Request.Headers.ContainsKey(FailResponseWriteHeader))
                        {
                            context.Response.Body = new WriteRefusingStream();
                        }
                        await nextMiddleware(context);
                    }
                );
                next(app);
            };
    }

    private sealed class WriteRefusingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("The test response body refused the write.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => throw new IOException("The test response body refused the write.");

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => throw new IOException("The test response body refused the write.");
    }

    public static readonly object[] UnanswerableFailures =
    [
        new object[] { "after-the-response-started", true },
        new object[] { "while-writing-the-error-response", false },
    ];

    /// <summary>
    /// The revocation route: the framework's exception middleware does log the failure with an exception
    /// attached on both paths, and nothing of the withheld exception reaches any category, field, scope
    /// or attached exception chain.
    /// </summary>
    [TestFixtureSource(typeof(TokenRevocationStartupTests), nameof(UnanswerableFailures))]
    public class Given_a_revocation_request_that_fails_where_the_exception_handler_cannot_answer(
        string failure,
        bool startResponseFirst
    )
    {
        private RevocationBoot _boot = null!;

        private static IEnumerable<CapturedStartupLog> FrameworkRecordsWithAnException(RevocationBoot boot) =>
            boot.Logs.Records.Where(record =>
                record.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                && record.Exception is not null
            );

        private static IEnumerable<Exception> ChainOf(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                yield return current;
            }
        }

        [OneTimeSetUp]
        public async Task OneTimeSetUp() =>
            (_boot, _) = await RunRequestTimeFailureAsync("/connect/revoke", startResponseFirst);

        [OneTimeTearDown]
        public void OneTimeTearDown() => _boot.Dispose();

        [Test]
        public void It_reaches_the_framework_exception_logging() =>
            FrameworkRecordsWithAnException(_boot).Should().NotBeEmpty(failure);

        [Test]
        public void It_keeps_the_withheld_exception_out_of_every_logger_category() =>
            _boot.CapturedText().Should().NotContain(Sentinel);

        [Test]
        public void It_attaches_no_exception_chain_carrying_the_original() =>
            _boot
                .Logs.Records.Where(record => record.Exception is not null)
                .SelectMany(record => ChainOf(record.Exception!))
                .Should()
                .NotContain(exception =>
                    exception is SentinelFactoryException || exception is TimeoutException
                );

        [Test]
        public void It_logs_one_failed_request_naming_the_original_exception_types()
        {
            CapturedStartupLog failed = _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.EventId.Name == "HttpRequestFailed")
                .Subject;
            failed.Exception.Should().BeNull();
            failed
                .State.Should()
                .Contain(new KeyValuePair<string, object?>("ExceptionTypes", _throwingFactoryTypeChain));
        }
    }

    /// <summary>
    /// The unmarked control: the same failures on <c>/connect/token</c> keep their existing logging, so
    /// the original exception, sentinel included, still reaches the logs there.
    /// </summary>
    [TestFixtureSource(typeof(TokenRevocationStartupTests), nameof(UnanswerableFailures))]
    public class Given_an_unmarked_request_that_fails_where_the_exception_handler_cannot_answer(
        string failure,
        bool startResponseFirst
    )
    {
        private RevocationBoot _boot = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() =>
            (_boot, _) = await RunRequestTimeFailureAsync("/connect/token", startResponseFirst);

        [OneTimeTearDown]
        public void OneTimeTearDown() => _boot.Dispose();

        [Test]
        public void It_still_logs_the_original_exception() =>
            _boot.CapturedText().Should().Contain(Sentinel, failure);

        [Test]
        public void It_still_attaches_the_original_to_the_failed_request() =>
            _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.EventId.Name == "HttpRequestFailed")
                .Which.Exception.Should()
                .BeOfType<SentinelFactoryException>();
    }

    // ----- An I/O failure on the revocation route, with and without an aborted request (D-17) -----

    /// <summary>Delegates to the host's handler and records each exception it was asked to answer.</summary>
    private sealed class RecordingExceptionHandler(IExceptionHandler inner) : IExceptionHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _calls);
            return inner.TryHandleAsync(httpContext, exception, cancellationToken);
        }
    }

    /// <summary>
    /// A revocation whose manager binding throws an <see cref="IOException"/> carrying the sentinel in its
    /// message, inner exception and <c>Data</c>, after aborting the request when asked. The host's
    /// exception handler is wrapped so the test sees whether the error writer was invoked.
    /// </summary>
    private static async Task<(
        RevocationBoot Boot,
        RecordingExceptionHandler Handler,
        HttpResponseMessage? Response
    )> RunIoFailureAsync(bool abortRequest)
    {
        RecordingExceptionHandler handler = new(new GlobalExceptionHandler());
        RevocationBoot boot = RevocationBoot.Run(
            "self-contained",
            "postgresql",
            services =>
            {
                services.AddHttpContextAccessor();
                services.RemoveAll<IExceptionHandler>();
                services.AddSingleton<IExceptionHandler>(handler);
                services.RemoveAll<ITokenRevocationManager>();
                services.AddTransient<ITokenRevocationManager>(provider =>
                {
                    HttpContext? context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;
                    if (context is null)
                    {
                        return new ScriptedRevocationManager(new TokenRevocationResult.Completed());
                    }
                    if (abortRequest)
                    {
                        context.Abort();
                    }
                    IOException failure = new($"io {Sentinel}", new TimeoutException($"inner {Sentinel}"));
                    failure.Data["connection"] = Sentinel;
                    throw failure;
                });
            }
        );

        try
        {
            return (boot, handler, await RevokeAsync(boot.Client!));
        }
        catch (Exception) when (abortRequest)
        {
            // The client sees the aborted request as a failed send.
            return (boot, handler, null);
        }
    }

    private static readonly string _ioFailureTypeChain =
        $"{typeof(IOException).FullName} -> {typeof(TimeoutException).FullName}";

    /// <summary>
    /// The aborted request: the framework's exception middleware takes its aborted-request path (logs the
    /// abort, answers 499, invokes no error handler), and nothing of the exception reaches a log.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_aborted_while_an_io_failure_is_raised
    {
        private RevocationBoot _boot = null!;
        private RecordingExceptionHandler _handler = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() =>
            (_boot, _handler, _) = await RunIoFailureAsync(abortRequest: true);

        [OneTimeTearDown]
        public void OneTimeTearDown() => _boot.Dispose();

        [Test]
        public void It_does_not_invoke_the_error_handler() => _handler.Calls.Should().Be(0);

        [Test]
        public void It_takes_the_aborted_request_path() =>
            _boot
                .Logs.Records.Should()
                .Contain(record =>
                    record.Category.StartsWith("Microsoft.AspNetCore.Diagnostics", StringComparison.Ordinal)
                    && record.Message == "The request was aborted by the client."
                );

        [Test]
        public void It_logs_the_request_as_completed_with_499_and_not_as_failed()
        {
            _boot.Logs.Records.Should().NotContain(record => record.EventId.Name == "HttpRequestFailed");
            _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.EventId.Name == "HttpRequestCompleted")
                .Which.State.Should()
                .Contain(new KeyValuePair<string, object?>("StatusCode", 499));
        }

        [Test]
        public void It_keeps_the_exception_content_out_of_every_logger_category() =>
            _boot.CapturedText().Should().NotContain(Sentinel);
    }

    /// <summary>
    /// The control: the same failure on a request the caller did not abort stays a server fault, answered
    /// 500 <c>server_error</c> by the error handler and logged once as a failed request by type names.
    /// </summary>
    [TestFixture]
    public class Given_a_revocation_request_that_raises_an_io_failure_without_an_abort
    {
        private RevocationBoot _boot = null!;
        private RecordingExceptionHandler _handler = null!;
        private HttpResponseMessage _response = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            HttpResponseMessage? response;
            (_boot, _handler, response) = await RunIoFailureAsync(abortRequest: false);
            _response = response!;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _response.Dispose();
            _boot.Dispose();
        }

        [Test]
        public void It_invokes_the_error_handler_once() => _handler.Calls.Should().Be(1);

        [Test]
        public async Task It_answers_500_server_error()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await OAuthErrorAsync(_response)).Should().Be("server_error");
        }

        [Test]
        public void It_logs_one_failed_request_naming_the_original_exception_types()
        {
            CapturedStartupLog failed = _boot
                .Logs.Records.Should()
                .ContainSingle(record => record.EventId.Name == "HttpRequestFailed")
                .Subject;
            failed.Exception.Should().BeNull();
            failed
                .State.Should()
                .Contain(new KeyValuePair<string, object?>("ExceptionTypes", _ioFailureTypeChain));
        }

        [Test]
        public void It_keeps_the_exception_content_out_of_every_logger_category() =>
            _boot.CapturedText().Should().NotContain(Sentinel);
    }

    /// <summary>
    /// The shipped Keycloak manager against the recording endpoint: startup made no provider request,
    /// the first revocation reaches the endpoint (so the startup count of zero is an observation, not a
    /// blind spot), is answered 503 because the provider is unusable, and the host keeps serving.
    /// </summary>
    [TestFixture]
    public class Given_a_keycloak_host_whose_provider_is_unusable
    {
        private RevocationBoot _boot = null!;
        private HttpResponseMessage _first = null!;
        private int _connectionsAfterFirst;
        private HttpResponseMessage _malformed = null!;
        private HttpResponseMessage _retry = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _boot = RevocationBoot.Run("keycloak", "postgresql");
            _first = await RevokeAsync(_boot.Client!);
            _connectionsAfterFirst = _boot.Endpoint.Connections;
            _malformed = await RevokeAsync(_boot.Client!, token: "");
            _retry = await RevokeAsync(_boot.Client!);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _first.Dispose();
            _malformed.Dispose();
            _retry.Dispose();
            _boot.Dispose();
        }

        [Test]
        public void It_makes_no_identity_provider_request_at_startup() =>
            _boot.ProviderConnectionsAtStartup.Should().Be(0);

        [Test]
        public void It_contacts_the_identity_provider_for_the_first_revocation() =>
            _connectionsAfterFirst.Should().BeGreaterThan(0);

        [Test]
        public async Task It_answers_the_first_revocation_503_temporarily_unavailable()
        {
            _first.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await OAuthErrorAsync(_first)).Should().Be("temporarily_unavailable");
        }

        [Test]
        public async Task It_serves_the_next_request()
        {
            _malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await OAuthErrorAsync(_malformed)).Should().Be("invalid_request");
        }

        [Test]
        public async Task It_answers_a_retry_from_the_provider_again()
        {
            _retry.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await OAuthErrorAsync(_retry)).Should().Be("temporarily_unavailable");
            _boot.Endpoint.Connections.Should().BeGreaterThan(_connectionsAfterFirst);
        }

        [Test]
        public void It_does_not_stop_the_host() =>
            _boot
                .Factory.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.IsCancellationRequested.Should()
                .BeFalse();
    }

    /// <summary>
    /// A self-contained host signing with a development certificate, booted through the real pipeline:
    /// authentication middleware, the shipped <see cref="OpenIddictTokenManager"/>, its real secret
    /// hasher and certificate loaders. Only the token store is a fake, so every token write is
    /// observable. The token is issued by <c>/connect/token</c>, so it is a JWT signed by the
    /// certificate's key with the issuer and audience the host verifies.
    /// </summary>
    private sealed class DevelopmentCertificateHost : IDisposable
    {
        private const string ClientId = "certificate-owner";
        private const string ClientSecret = "certificate-owner-secret-0123456789";
        private const string CertificatePassword = "test-password";

        private readonly string _directory;
        private string _storedSecret = string.Empty;

        private DevelopmentCertificateHost()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"dms-1327-certs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            CertificatePath = Path.Combine(_directory, "devcert.pfx");
            WriteDevelopmentCertificate(CertificatePath);

            A.CallTo(() => Repository.GetApplicationByClientIdAsync(ClientId))
                .ReturnsLazily(() =>
                    new ApplicationInfo
                    {
                        Id = ApplicationId,
                        ClientId = ClientId,
                        ClientSecret = _storedSecret,
                        Permissions = ["edfi_admin_api/full_access"],
                    }
                );
            A.CallTo(() => Repository.GetClientRolesAsync(ApplicationId)).Returns(Array.Empty<string>());
            A.CallTo(() =>
                    Repository.StoreTokenAsync(
                        A<Guid>._,
                        ApplicationId,
                        A<string>._,
                        A<DateTimeOffset>._,
                        A<int>._
                    )
                )
                .Returns(TokenStoreOutcome.Stored);
            A.CallTo(() => Repository.GetTokenStatusAsync(A<Guid>._)).Returns("valid");
            A.CallTo(() => Repository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).Returns(true);

            Boot = RevocationBoot.Run(
                "self-contained",
                "postgresql",
                services =>
                {
                    services.RemoveAll<IOpenIddictTokenRepository>();
                    services.AddSingleton(Repository);
                    services.PostConfigure<OpenIddictIdentityOptions>(options =>
                    {
                        options.UseCertificates = true;
                        options.UseDevelopmentCertificates = true;
                        options.DevCertificatePath = CertificatePath;
                        options.DevCertificatePassword = CertificatePassword;
                    });
                }
            );
        }

        internal Guid ApplicationId { get; } = Guid.NewGuid();

        internal IOpenIddictTokenRepository Repository { get; } = A.Fake<IOpenIddictTokenRepository>();

        internal string CertificatePath { get; }

        internal RevocationBoot Boot { get; }

        internal static async Task<DevelopmentCertificateHost> StartAsync()
        {
            DevelopmentCertificateHost host = new();
            host._storedSecret = await host
                .Boot.Factory.Services.GetRequiredService<IClientSecretHasher>()
                .HashSecretAsync(ClientSecret);
            return host;
        }

        /// <summary>A token issued by the host's own token endpoint.</summary>
        internal async Task<string> IssueTokenAsync()
        {
            using HttpResponseMessage response = await Boot.Client!.PostAsync(
                "/connect/token",
                RevocationForm(
                    ("grant_type", "client_credentials"),
                    ("client_id", ClientId),
                    ("client_secret", ClientSecret)
                )
            );
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the precondition is an issued token");
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())![
                "access_token"
            ]!.GetValue<string>();
        }

        /// <summary>
        /// Revokes with the owner's form credentials and, when asked, the same token as a bearer
        /// header, which revocation ignores.
        /// </summary>
        internal async Task<HttpResponseMessage> RevokeWithFormCredentialsAsync(
            string token,
            bool withBearerHeader
        )
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/connect/revoke")
            {
                Content = RevocationForm(
                    ("client_id", ClientId),
                    ("client_secret", ClientSecret),
                    ("token", token)
                ),
            };
            if (withBearerHeader)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return await Boot.Client!.SendAsync(request);
        }

        private static void WriteDevelopmentCertificate(string path)
        {
            using RSA rsa = RSA.Create(2048);
            CertificateRequest request = new(
                "CN=DevCert",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(1)
            );
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        }

        public void Dispose()
        {
            Boot.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// D-07.4 through the real pipeline: the development certificate that signed the owner's token is
    /// gone, so revocation cannot verify the token and must answer 503 without creating a replacement.
    /// With the bearer header, the request must not reach the bearer authentication handler either: its
    /// key resolver loads keys through the JWKS helper, which creates a replacement certificate, and
    /// revocation would then verify against that unrelated key and answer an empty 200 revoking nothing.
    /// </summary>
    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_a_revocation_whose_development_signing_certificate_has_gone_missing(
        bool withBearerHeader
    )
    {
        private DevelopmentCertificateHost _host = null!;
        private HttpResponseMessage _response = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _host = await DevelopmentCertificateHost.StartAsync();
            string token = await _host.IssueTokenAsync();
            File.Delete(_host.CertificatePath);
            _response = await _host.RevokeWithFormCredentialsAsync(token, withBearerHeader);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public async Task It_answers_503_temporarily_unavailable()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await OAuthErrorAsync(_response)).Should().Be("temporarily_unavailable");
        }

        [Test]
        public void It_does_not_recreate_the_missing_certificate() =>
            File.Exists(_host.CertificatePath).Should().BeFalse();

        [Test]
        public void It_does_not_update_any_token() =>
            A.CallTo(() => _host.Repository.RevokeTokenAsync(A<Guid>._, A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// The control for the fixture above: with the certificate in place, the same request revokes the
    /// owner's token, so a bearer header leaves form-credential authentication and revocation working.
    /// </summary>
    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_a_revocation_whose_development_signing_certificate_is_present(bool withBearerHeader)
    {
        private DevelopmentCertificateHost _host = null!;
        private byte[] _certificateBytes = null!;
        private HttpResponseMessage _response = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _host = await DevelopmentCertificateHost.StartAsync();
            string token = await _host.IssueTokenAsync();
            _certificateBytes = await File.ReadAllBytesAsync(_host.CertificatePath);
            _response = await _host.RevokeWithFormCredentialsAsync(token, withBearerHeader);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public async Task It_answers_an_empty_200()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await _response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }

        [Test]
        public void It_revokes_the_owners_token_once() =>
            A.CallTo(() => _host.Repository.RevokeTokenAsync(A<Guid>._, _host.ApplicationId))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_leaves_the_certificate_unchanged() =>
            File.ReadAllBytes(_host.CertificatePath).Should().Equal(_certificateBytes);

        [Test]
        public void It_does_not_authenticate_the_bearer_header() =>
            A.CallTo(() => _host.Repository.GetTokenStatusAsync(A<Guid>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// The exemption is the revocation route's alone: a protected route still authenticates the same
    /// token through the bearer handler, its per-request status check included. The token's client holds
    /// no role, so the authenticated request is refused by authorization (403), not authentication (401).
    /// </summary>
    public class Given_a_protected_request_carrying_the_issued_token
    {
        private DevelopmentCertificateHost _host = null!;
        private HttpResponseMessage _response = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _host = await DevelopmentCertificateHost.StartAsync();
            string token = await _host.IssueTokenAsync();
            using HttpRequestMessage request = new(HttpMethod.Get, "/v3/vendors/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _response = await _host.Boot.Client!.SendAsync(request);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _response.Dispose();
            _host.Dispose();
        }

        [Test]
        public void It_authenticates_the_bearer_token() =>
            _response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        [Test]
        public void It_checks_the_tokens_stored_status_once() =>
            A.CallTo(() => _host.Repository.GetTokenStatusAsync(A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Under Keycloak the bearer handler would fetch the realm's metadata from the identity provider for
    /// a bearer header on the revocation route. A request the endpoint rejects for its shape never calls
    /// the manager, so any provider connection it causes comes from bearer authentication.
    /// </summary>
    public class Given_a_keycloak_revocation_request_carrying_a_bearer_header
    {
        private RevocationBoot _boot = null!;
        private int _connectionsBefore;
        private HttpResponseMessage _response = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _boot = RevocationBoot.Run("keycloak", "postgresql");
            _connectionsBefore = _boot.Endpoint.Connections;

            using RSA rsa = RSA.Create(2048);
            string bearer = new JsonWebTokenHandler().CreateToken(
                new SecurityTokenDescriptor
                {
                    Issuer = _boot.Endpoint.Authority,
                    Audience = "account",
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(
                        new RsaSecurityKey(rsa) { KeyId = "unrelated" },
                        SecurityAlgorithms.RsaSha256
                    ),
                }
            );
            using HttpRequestMessage request = new(HttpMethod.Post, "/connect/revoke")
            {
                Content = RevocationForm(("client_id", "revocation-caller"), ("client_secret", "secret")),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            _response = await _boot.Client!.SendAsync(request);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _response.Dispose();
            _boot.Dispose();
        }

        [Test]
        public async Task It_answers_the_missing_token_400_invalid_request()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await OAuthErrorAsync(_response)).Should().Be("invalid_request");
        }

        [Test]
        public void It_makes_no_identity_provider_request() =>
            _boot.Endpoint.Connections.Should().Be(_connectionsBefore);
    }

    /// <summary>Answers each call with the next scripted result and counts the calls.</summary>
    private sealed class ScriptedRevocationManager(params TokenRevocationResult[] results)
        : ITokenRevocationManager
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<TokenRevocationResult> RevokeTokenAsync(
            TokenRevocationRequest request,
            CancellationToken cancellationToken
        )
        {
            int call = Interlocked.Increment(ref _calls);
            return Task.FromResult(results[Math.Min(call, results.Length) - 1]);
        }
    }

    /// <summary>
    /// One Configuration Service boot of a deployment shape, against a fresh recording endpoint, with
    /// every log record it wrote and how host creation ended.
    /// </summary>
    private sealed class RevocationBoot : IDisposable
    {
        private RevocationBoot(
            WebApplicationFactory<Program> factory,
            RecordingProviderEndpoint endpoint,
            StartupLogCapture logs,
            HttpClient? client,
            Exception? startupException,
            int providerConnectionsAtStartup
        )
        {
            Factory = factory;
            Endpoint = endpoint;
            Logs = logs;
            Client = client;
            StartupException = startupException;
            ProviderConnectionsAtStartup = providerConnectionsAtStartup;
        }

        internal WebApplicationFactory<Program> Factory { get; }

        internal RecordingProviderEndpoint Endpoint { get; }

        internal StartupLogCapture Logs { get; }

        /// <summary>The client, or null when host creation failed.</summary>
        internal HttpClient? Client { get; }

        /// <summary>What host creation threw, or null when the host started.</summary>
        internal Exception? StartupException { get; }

        /// <summary>Connections the recording endpoint had accepted when host creation ended.</summary>
        internal int ProviderConnectionsAtStartup { get; }

        internal static RevocationBoot Run(
            string provider,
            string datastore,
            Action<IServiceCollection>? configureTestServices = null
        )
        {
            RecordingProviderEndpoint endpoint = new();
            StartupLogCapture logs = new();

            // UseSetting, because AddServices reads the provider, the engine and the authority before
            // the host is built, where configuration added later is not yet visible.
            WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                builder =>
                {
                    builder.UseEnvironment("Test");
                    // As in Development, so resolving the Keycloak manager outside a scope fails here too.
                    builder.UseDefaultServiceProvider(options => options.ValidateScopes = true);
                    builder.UseSetting("AppSettings:IdentityProvider", provider);
                    builder.UseSetting("AppSettings:Datastore", datastore);
                    builder.UseSetting("IdentitySettings:Authority", endpoint.Authority);
                    builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
                    // Every category at every level, the framework's included: a provider-specific rule
                    // outranks the configured category levels.
                    builder.ConfigureLogging(logging =>
                        logging.AddFilter<StartupLogCapture>(category: null, LogLevel.Trace)
                    );
                    if (configureTestServices is not null)
                    {
                        // Applied after Program's own registrations, so it sees what AddServices left.
                        builder.ConfigureTestServices(configureTestServices);
                    }
                }
            );

            HttpClient? client = null;
            Exception? startupException = null;
            try
            {
                // WebApplicationFactory defers the entry point until the server is first needed.
                client = factory.CreateClient();
            }
            catch (Exception exception)
            {
                startupException = exception;
            }

            return new RevocationBoot(
                factory,
                endpoint,
                logs,
                client,
                startupException,
                endpoint.Connections
            );
        }

        /// <summary>
        /// The first exception in the startup chain with exactly this message. The test host wraps what
        /// the entry point throws before RunAsync, so the chain is walked.
        /// </summary>
        internal Exception? FindByMessage(string message) => FindByMessage(StartupException, message);

        private static Exception? FindByMessage(Exception? exception, string message) =>
            exception switch
            {
                null => null,
                _ when exception.Message == message => exception,
                AggregateException aggregate => aggregate
                    .InnerExceptions.Select(inner => FindByMessage(inner, message))
                    .FirstOrDefault(found => found is not null),
                _ => FindByMessage(exception.InnerException, message),
            };

        /// <summary>
        /// Everything this boot put where an operator can read it: each log record's rendered message,
        /// every structured value, every scope and the attached exception with its full chain, and the
        /// startup exception chain.
        /// </summary>
        internal string CapturedText()
        {
            StringBuilder text = new();
            foreach (CapturedStartupLog record in Logs.Records)
            {
                text.AppendLine($"{record.Category} {record.EventId} {record.Message}");
                foreach ((string key, object? value) in record.State)
                {
                    text.AppendLine($"{key}={value}");
                }
                foreach (object? scope in record.Scopes)
                {
                    text.AppendLine(scope?.ToString());
                    if (scope is System.Collections.IEnumerable values and not string)
                    {
                        foreach (object? value in values)
                        {
                            text.AppendLine(value?.ToString());
                        }
                    }
                }
                AppendChain(text, record.Exception);
            }
            AppendChain(text, StartupException);
            return text.ToString();
        }

        private static void AppendChain(StringBuilder text, Exception? exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                text.AppendLine(current.ToString());
                foreach (System.Collections.DictionaryEntry entry in current.Data)
                {
                    text.AppendLine($"{entry.Key}={entry.Value}");
                }
            }
        }

        public void Dispose()
        {
            Client?.Dispose();
            Factory.Dispose();
            Endpoint.Dispose();
        }
    }

    /// <summary>
    /// A loopback listener standing in for the identity provider. It counts each connection before
    /// closing it unanswered, so a request that reached it has been counted by the time its caller
    /// observes the failure.
    /// </summary>
    private sealed class RecordingProviderEndpoint : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private int _connections;

        internal RecordingProviderEndpoint()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        internal string Authority =>
            $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/realms/edfi";

        internal int Connections => Volatile.Read(ref _connections);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient connection = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    Interlocked.Increment(ref _connections);
                    // Reset rather than a graceful close, so the caller fails at once.
                    connection.LingerState = new LingerOption(true, 0);
                }
            }
            catch (Exception exception)
                when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The listener was stopped.
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }
    }

    internal sealed record CapturedStartupLog(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        IReadOnlyList<object?> Scopes,
        Exception? Exception
    );

    /// <summary>
    /// Records, per log call, the level, the rendered message, every structured value, every active
    /// scope and the attached exception.
    /// </summary>
    internal sealed class StartupLogCapture : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<CapturedStartupLog> _records = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        internal IReadOnlyList<CapturedStartupLog> Records => [.. _records];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose() { }

        private sealed class CapturingLogger(string category, StartupLogCapture capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => capture._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                List<KeyValuePair<string, object?>> values = state
                    is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? [.. pairs]
                    : [];
                List<object?> scopes = [];
                capture._scopes.ForEachScope((scope, list) => list.Add(scope), scopes);
                capture._records.Enqueue(
                    new CapturedStartupLog(
                        category,
                        logLevel,
                        eventId,
                        formatter(state, exception),
                        values,
                        scopes,
                        exception
                    )
                );
            }
        }
    }
}
