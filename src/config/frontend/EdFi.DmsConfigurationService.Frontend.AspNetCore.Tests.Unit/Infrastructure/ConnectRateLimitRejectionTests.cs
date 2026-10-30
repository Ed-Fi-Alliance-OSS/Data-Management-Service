// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class ConnectRateLimitRejectionTests
{
    /// <summary>A refused lease whose limiter recommended the given delay, or none.</summary>
    private sealed class RefusedLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }
            metadata = null;
            return false;
        }
    }

    private static DefaultHttpContext RejectWith(TimeSpan? retryAfter)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-reject" };
        context.Response.Body = new MemoryStream();
        using var lease = new RefusedLease(retryAfter);
        _ = ConnectRateLimitRejection.Create(
            context,
            lease,
            ConnectRateLimitRejection.ClientGate,
            new TestLogger<ConnectRateLimitRejectionTests>()
        );
        return context;
    }

    [TestFixture]
    public class Given_a_lease_recommending_a_fractional_retry_delay
    {
        private DefaultHttpContext _context = null!;

        [SetUp]
        public void Setup() => _context = RejectWith(TimeSpan.FromSeconds(1.2));

        [Test]
        public void It_rounds_the_retry_after_up_to_whole_seconds() =>
            _context.Response.Headers.RetryAfter.ToString().Should().Be("2");
    }

    [TestFixture]
    public class Given_a_lease_recommending_a_delay_just_over_a_whole_second
    {
        private DefaultHttpContext _context = null!;

        [SetUp]
        public void Setup() => _context = RejectWith(TimeSpan.FromSeconds(3.01));

        [Test]
        public void It_does_not_round_down_or_to_nearest() =>
            _context.Response.Headers.RetryAfter.ToString().Should().Be("4");
    }

    [TestFixture]
    public class Given_a_lease_with_no_retry_delay
    {
        private DefaultHttpContext _context = null!;

        [SetUp]
        public void Setup() => _context = RejectWith(null);

        [Test]
        public void It_omits_the_retry_after_header() =>
            _context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }
}
