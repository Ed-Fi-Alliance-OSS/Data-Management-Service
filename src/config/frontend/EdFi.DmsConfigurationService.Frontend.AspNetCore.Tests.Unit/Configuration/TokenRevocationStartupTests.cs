// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

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
    /// check cannot see: the request it fails is answered 500 <c>server_error</c> in the OAuth format,
    /// with nothing of the factory's exception in the body, and the host keeps serving.
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
                            ? throw new InvalidOperationException($"factory failed with {Sentinel}")
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
        public void It_serves_the_next_request() => _next.StatusCode.Should().Be(HttpStatusCode.OK);
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
                text.AppendLine(record.Message);
                foreach ((string key, object? value) in record.State)
                {
                    text.AppendLine($"{key}={value}");
                }
                foreach (object? scope in record.Scopes)
                {
                    text.AppendLine(scope?.ToString());
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
        LogLevel Level,
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

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose() { }

        private sealed class CapturingLogger(StartupLogCapture capture) : ILogger
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
                    new CapturedStartupLog(logLevel, formatter(state, exception), values, scopes, exception)
                );
            }
        }
    }
}
