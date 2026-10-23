// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class ConnectRateLimiterTests
{
    private static ConnectRateLimiter CreateLimiter(int sourceLimit, int clientLimit, bool enabled = true) =>
        new(
            Options.Create(
                new ConnectRateLimitSettings
                {
                    Enabled = enabled,
                    SourcePermitLimit = sourceLimit,
                    ClientPermitLimit = clientLimit,
                    WindowSeconds = 60,
                }
            )
        );

    private static HttpContext ContextFrom(string? address) =>
        new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = address is null ? null : IPAddress.Parse(address) },
        };

    private static bool Admits(RateLimitLease lease)
    {
        using (lease)
        {
            return lease.IsAcquired;
        }
    }

    [TestFixture]
    public class Given_source_addresses
    {
        [TestCase(null, "unknown")]
        [TestCase("198.51.100.7", "198.51.100.7")]
        [TestCase("::ffff:198.51.100.7", "198.51.100.7")]
        [TestCase("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
        [TestCase("2001:db8:1:2::1", "2001:db8:1:2::/64")]
        [TestCase("::1", "::/64")]
        public void It_keys_each_address(string? address, string expectedKey) =>
            ConnectRateLimiter
                .SourcePartitionKey(address is null ? null : IPAddress.Parse(address))
                .Should()
                .Be(expectedKey);
    }

    [TestFixture]
    public class Given_client_ids
    {
        [Test]
        public void It_keeps_a_short_id() =>
            ConnectRateLimiter.ClientPartitionKey("ClientA").Should().Be("ClientA");

        [TestCase("ClientA ", "ClientA")]
        [TestCase("ClientA  ", "ClientA")]
        [TestCase(" ClientA", " ClientA")]
        [TestCase("ClientA\t", "ClientA\t")]
        public void It_ignores_only_trailing_ascii_spaces(string clientId, string expectedKey) =>
            ConnectRateLimiter.ClientPartitionKey(clientId).Should().Be(expectedKey);

        [Test]
        public void It_removes_trailing_spaces_before_truncating() =>
            ConnectRateLimiter
                .ClientPartitionKey(new string('c', 255) + new string(' ', 30))
                .Should()
                .Be(new string('c', 255));

        [Test]
        public void It_truncates_a_long_id_to_256_characters() =>
            ConnectRateLimiter.ClientPartitionKey(new string('x', 300)).Should().Be(new string('x', 256));
    }

    [TestFixture]
    public class Given_a_client_that_exhausts_its_budget
    {
        private List<bool> _sameClient = [];
        private bool _caseVariant;
        private bool _otherClient;
        private bool _sourceAfterClientExhausted;
        private bool _retryAfterPresent;
        private TimeSpan _retryAfter;

        [SetUp]
        public void Setup()
        {
            _sameClient = [];
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 2, clientLimit: 2);
            _sameClient.Add(Admits(limiter.AttemptClient("ClientA")));
            _sameClient.Add(Admits(limiter.AttemptClient("ClientA")));
            using (RateLimitLease rejected = limiter.AttemptClient("ClientA"))
            {
                _sameClient.Add(rejected.IsAcquired);
                _retryAfterPresent = rejected.TryGetMetadata(MetadataName.RetryAfter, out _retryAfter);
            }
            _caseVariant = Admits(limiter.AttemptClient("CLIENTA"));
            _otherClient = Admits(limiter.AttemptClient("ClientB"));
            _sourceAfterClientExhausted = Admits(limiter.AttemptSource(ContextFrom("198.51.100.7")));
        }

        [Test]
        public void It_admits_up_to_the_limit_then_rejects() => _sameClient.Should().Equal(true, true, false);

        [Test]
        public void It_shares_the_budget_across_case_variants() => _caseVariant.Should().BeFalse();

        [Test]
        public void It_keeps_another_client_independent() => _otherClient.Should().BeTrue();

        [Test]
        public void It_keeps_the_source_budget_independent() => _sourceAfterClientExhausted.Should().BeTrue();

        [Test]
        public void It_supplies_a_retry_after_within_the_window()
        {
            _retryAfterPresent.Should().BeTrue();
            _retryAfter
                .Should()
                .BeGreaterThan(TimeSpan.Zero)
                .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        }
    }

    [TestFixture]
    public class Given_long_client_ids_sharing_a_256_character_prefix
    {
        private bool _second;

        [SetUp]
        public void Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 10, clientLimit: 1);
            string prefix = new('c', 256);
            _ = Admits(limiter.AttemptClient(prefix + "-one"));
            _second = Admits(limiter.AttemptClient(prefix + "-two"));
        }

        [Test]
        public void It_shares_one_budget() => _second.Should().BeFalse();
    }

    [TestFixture]
    public class Given_sources_in_and_outside_one_ipv6_64
    {
        private bool _sameSlash64;
        private bool _otherSlash64;
        private bool _unknownSecond;

        [SetUp]
        public void Setup()
        {
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1, clientLimit: 10);
            _ = Admits(limiter.AttemptSource(ContextFrom("2001:db8::1")));
            _sameSlash64 = Admits(limiter.AttemptSource(ContextFrom("2001:db8::2")));
            _otherSlash64 = Admits(limiter.AttemptSource(ContextFrom("2001:db8:0:1::1")));
            _ = Admits(limiter.AttemptSource(ContextFrom(null)));
            _unknownSecond = Admits(limiter.AttemptSource(ContextFrom(null)));
        }

        [Test]
        public void It_shares_a_budget_within_a_64() => _sameSlash64.Should().BeFalse();

        [Test]
        public void It_separates_another_64() => _otherSlash64.Should().BeTrue();

        [Test]
        public void It_limits_unknown_sources_as_one_partition() => _unknownSecond.Should().BeFalse();
    }

    [TestFixture]
    public class Given_limiting_is_disabled
    {
        private List<bool> _results = [];

        [SetUp]
        public void Setup()
        {
            _results = [];
            using ConnectRateLimiter limiter = CreateLimiter(sourceLimit: 1, clientLimit: 1, enabled: false);
            for (int i = 0; i < 256; i++)
            {
                _results.Add(Admits(limiter.AttemptClient($"Client{i}")));
                _results.Add(Admits(limiter.AttemptSource(ContextFrom($"198.51.100.{i}"))));
            }
        }

        [Test]
        public void It_admits_every_request() => _results.Should().OnlyContain(admitted => admitted);
    }

    [TestFixture]
    public class Given_disabled_limiting_with_many_distinct_keys
    {
        private long _sourceSuccessfulLeases;
        private long _clientSuccessfulLeases;

        [SetUp]
        public void Setup()
        {
            ConnectRateLimitSettings settings = new() { Enabled = false };
            using PartitionedRateLimiter<string> sources = ConnectRateLimiter.CreateLimiter(
                settings,
                1,
                StringComparer.Ordinal
            );
            using PartitionedRateLimiter<string> clients = ConnectRateLimiter.CreateLimiter(
                settings,
                1,
                StringComparer.OrdinalIgnoreCase
            );
            for (int i = 0; i < 256; i++)
            {
                using RateLimitLease sourceLease = sources.AttemptAcquire($"198.51.100.{i}");
                using RateLimitLease clientLease = clients.AttemptAcquire($"Client{i}");
            }

            // An unseen key must report all prior admissions: every resource used the same cached
            // no-op limiter, rather than retaining a new partition for each caller-supplied key.
            _sourceSuccessfulLeases = sources.GetStatistics("203.0.113.1")!.TotalSuccessfulLeases;
            _clientSuccessfulLeases = clients.GetStatistics("UnseenClient")!.TotalSuccessfulLeases;
        }

        [Test]
        public void It_reuses_one_source_partition() => _sourceSuccessfulLeases.Should().Be(256);

        [Test]
        public void It_reuses_one_client_partition() => _clientSuccessfulLeases.Should().Be(256);
    }

    [TestFixture]
    public class Given_a_disposed_limiter
    {
        private ConnectRateLimiter _limiter = null!;

        [SetUp]
        public void Setup()
        {
            _limiter = CreateLimiter(sourceLimit: 1, clientLimit: 1);
            _limiter.Dispose();
        }

        [TearDown]
        public void TearDown() => _limiter.Dispose();

        [Test]
        public void It_refuses_further_use() =>
            FluentActions
                .Invoking(() => _limiter.AttemptClient("ClientA"))
                .Should()
                .Throw<ObjectDisposedException>();
    }
}
