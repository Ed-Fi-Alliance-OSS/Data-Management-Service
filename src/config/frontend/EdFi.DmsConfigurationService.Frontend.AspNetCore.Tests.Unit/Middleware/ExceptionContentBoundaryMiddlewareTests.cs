// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// DMS-1327 D-15, D-17: on a route marked with <see cref="ExceptionTypeOnlyLoggingMetadata"/>, every
/// exception is replaced before it leaves the boundary by one of the same category with fixed text and no
/// inner exception or <c>Data</c>, and the original type names are kept on
/// <see cref="WithheldExceptionFeature"/>. Unmarked routes are untouched.
/// </summary>
public class ExceptionContentBoundaryMiddlewareTests
{
    private const string Sentinel = "SECRET-BOUNDARY-SENTINEL";

    private static Endpoint Endpoint(params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "route");

    private static T WithSentinels<T>(T exception)
        where T : Exception
    {
        exception.Data["connection"] = Sentinel;
        return exception;
    }

    private static (Exception? Thrown, DefaultHttpContext Context) Run(
        Exception exception,
        Endpoint? endpoint,
        CancellationToken requestAborted = default
    )
    {
        DefaultHttpContext context = new() { RequestAborted = requestAborted };
        if (endpoint is not null)
        {
            context.SetEndpoint(endpoint);
        }
        ExceptionContentBoundaryMiddleware middleware = new(_ => throw exception);
        try
        {
            middleware.InvokeAsync(context).GetAwaiter().GetResult();
            return (null, context);
        }
        catch (Exception thrown)
        {
            return (thrown, context);
        }
    }

    private static void AssertWithheld(Exception thrown, Exception original)
    {
        thrown.Should().NotBeSameAs(original);
        thrown.InnerException.Should().BeNull();
        thrown.Data.Count.Should().Be(0);
        thrown.Message.Should().NotContain(Sentinel);
    }

    public static IEnumerable<TestFixtureData> Replacements()
    {
        yield return new TestFixtureData(
            "a fault",
            (Func<Exception>)(
                () =>
                    WithSentinels(
                        new InvalidOperationException($"outer {Sentinel}", new TimeoutException(Sentinel))
                    )
            ),
            typeof(InvalidOperationException),
            $"{typeof(InvalidOperationException).FullName} -> {typeof(TimeoutException).FullName}"
        );
        yield return new TestFixtureData(
            "a malformed form",
            (Func<Exception>)(() => WithSentinels(new InvalidDataException($"form {Sentinel}"))),
            typeof(InvalidDataException),
            typeof(InvalidDataException).FullName
        );
        yield return new TestFixtureData(
            "an I/O failure on a request the caller did not abort",
            (Func<Exception>)(
                () => WithSentinels(new IOException($"io {Sentinel}", new TimeoutException(Sentinel)))
            ),
            typeof(InvalidOperationException),
            $"{typeof(IOException).FullName} -> {typeof(TimeoutException).FullName}"
        );
        yield return new TestFixtureData(
            "a cancellation the caller did not cause",
            (Func<Exception>)(() => WithSentinels(new OperationCanceledException($"timeout {Sentinel}"))),
            typeof(InvalidOperationException),
            typeof(OperationCanceledException).FullName
        );
    }

    [TestFixtureSource(typeof(ExceptionContentBoundaryMiddlewareTests), nameof(Replacements))]
    public class Given_an_exception_on_a_marked_route(
        string description,
        Func<Exception> create,
        Type expectedType,
        string expectedTypes
    )
    {
        private Exception _original = null!;
        private Exception? _thrown;
        private DefaultHttpContext _context = null!;

        [SetUp]
        public void Setup()
        {
            _original = create();
            (_thrown, _context) = Run(_original, Endpoint(ExceptionTypeOnlyLoggingMetadata.Instance));
        }

        [Test]
        public void It_throws_a_replacement_of_the_same_category() =>
            _thrown.Should().BeOfType(expectedType, description);

        [Test]
        public void It_withholds_the_original_content() => AssertWithheld(_thrown!, _original);

        [Test]
        public void It_names_the_original_types_in_the_replacement() =>
            _thrown!.Message.Should().Contain(expectedTypes);

        [Test]
        public void It_keeps_the_original_types_for_the_failed_request_event() =>
            _context
                .Features.Get<WithheldExceptionFeature>()
                .Should()
                .Be(new WithheldExceptionFeature(expectedTypes));
    }

    [TestFixture]
    public class Given_an_unreadable_request_on_a_marked_route
    {
        [Test]
        public void It_keeps_the_status_code()
        {
            BadHttpRequestException original = WithSentinels(
                new BadHttpRequestException($"too large {Sentinel}", StatusCodes.Status413PayloadTooLarge)
            );

            (Exception? thrown, _) = Run(original, Endpoint(ExceptionTypeOnlyLoggingMetadata.Instance));

            thrown
                .Should()
                .BeOfType<BadHttpRequestException>()
                .Which.StatusCode.Should()
                .Be(StatusCodes.Status413PayloadTooLarge);
            AssertWithheld(thrown!, original);
        }
    }

    public static IEnumerable<TestFixtureData> AbortedRequestFailures()
    {
        yield return new TestFixtureData(
            (Func<Exception>)(
                () =>
                    WithSentinels(
                        new OperationCanceledException(
                            $"cancelled {Sentinel}",
                            new TimeoutException(Sentinel)
                        )
                    )
            )
        ).SetArgDisplayNames("OperationCanceledException");
        yield return new TestFixtureData(
            (Func<Exception>)(
                () => WithSentinels(new IOException($"io {Sentinel}", new TimeoutException(Sentinel)))
            )
        ).SetArgDisplayNames("IOException");
        yield return new TestFixtureData(
            (Func<Exception>)(
                () =>
                    WithSentinels(
                        new BadHttpRequestException($"unreadable {Sentinel}", StatusCodes.Status400BadRequest)
                    )
            )
        ).SetArgDisplayNames("BadHttpRequestException");
    }

    /// <summary>
    /// Every type the framework's exception middleware treats as an aborted request when the caller has
    /// aborted stays a cancellation, so that middleware still takes its aborted-request path.
    /// </summary>
    [TestFixtureSource(typeof(ExceptionContentBoundaryMiddlewareTests), nameof(AbortedRequestFailures))]
    public class Given_a_failure_on_an_aborted_request_on_a_marked_route(Func<Exception> create)
    {
        [Test]
        public void It_stays_a_cancellation_for_the_aborted_request()
        {
            using CancellationTokenSource aborted = new();
            aborted.Cancel();
            Exception original = create();

            (Exception? thrown, _) = Run(
                original,
                Endpoint(ExceptionTypeOnlyLoggingMetadata.Instance),
                aborted.Token
            );

            thrown
                .Should()
                .BeOfType<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(aborted.Token);
            AssertWithheld(thrown!, original);
        }
    }

    [TestFixture]
    public class Given_an_exception_on_an_unmarked_route
    {
        [Test]
        public void It_rethrows_the_original_untouched()
        {
            InvalidOperationException original = new($"outer {Sentinel}");

            (Exception? thrown, DefaultHttpContext context) = Run(
                original,
                Endpoint(OAuthErrorContractMetadata.Instance)
            );

            thrown.Should().BeSameAs(original);
            context.Features.Get<WithheldExceptionFeature>().Should().BeNull();
        }

        [Test]
        public void It_rethrows_the_original_when_no_route_matched()
        {
            InvalidOperationException original = new($"outer {Sentinel}");

            (Exception? thrown, _) = Run(original, endpoint: null);

            thrown.Should().BeSameAs(original);
        }
    }
}
