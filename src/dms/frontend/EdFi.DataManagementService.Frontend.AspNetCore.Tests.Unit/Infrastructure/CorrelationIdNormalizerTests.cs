// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
[Parallelizable]
public class CorrelationIdNormalizerTests
{
    [TestFixture]
    public class Given_A_Well_Formed_Correlation_Id : CorrelationIdNormalizerTests
    {
        private const string WellFormed = "test-correlationId";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(
                WellFormed,
                AppSettings.DefaultCorrelationIdMaxLength
            );
        }

        [Test]
        public void It_passes_the_value_through_unchanged()
        {
            _result.Should().Be(WellFormed);
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_From_An_Upstream_Identifier_Scheme : CorrelationIdNormalizerTests
    {
        // FR-LOG-3 forbids narrowing the allowlist to alphanumerics: these characters are
        // ordinary in upstream identifier schemes and the stricter Method/Path allowlist
        // would strip every one of them.
        private const string UpstreamId = "a+b=c{d}e@f|g,h#i(j)k[l]m<n>o\"p'q";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(
                UpstreamId,
                AppSettings.DefaultCorrelationIdMaxLength
            );
        }

        [Test]
        public void It_preserves_printable_punctuation()
        {
            _result.Should().Be(UpstreamId);
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Containing_Control_Characters : CorrelationIdNormalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(
                "trace\r\nid\twith\0control",
                AppSettings.DefaultCorrelationIdMaxLength
            );
        }

        [Test]
        public void It_removes_every_control_character()
        {
            _result.Should().Be("traceidwithcontrol");
        }

        [Test]
        public void It_cannot_forge_an_additional_log_line()
        {
            _result.Should().NotContain("\r").And.NotContain("\n");
        }
    }

    [TestFixture]
    public class Given_An_Over_Length_Correlation_Id : CorrelationIdNormalizerTests
    {
        private string _resultAtDefaultBound = string.Empty;
        private string _resultAtConfiguredBound = string.Empty;

        [SetUp]
        public void Setup()
        {
            _resultAtDefaultBound = CorrelationIdNormalizer.Normalize(
                new string('a', AppSettings.DefaultCorrelationIdMaxLength + 45),
                AppSettings.DefaultCorrelationIdMaxLength
            );
            _resultAtConfiguredBound = CorrelationIdNormalizer.Normalize(new string('b', 100), 16);
        }

        [Test]
        public void It_truncates_to_the_default_maximum_length()
        {
            _resultAtDefaultBound.Should().Be(new string('a', AppSettings.DefaultCorrelationIdMaxLength));
        }

        [Test]
        public void It_truncates_to_the_host_configured_maximum_length()
        {
            _resultAtConfiguredBound.Should().Be(new string('b', 16));
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_That_Is_Both_Over_Length_And_Hostile : CorrelationIdNormalizerTests
    {
        // The first MaxLength characters contain a control character, which is what makes the
        // two possible orders observably different. Truncate-then-filter yields "abcdefghi"
        // (nine characters); filter-then-truncate would yield "abcdefghij" (ten). This test
        // fails if the order in CorrelationIdNormalizer.Normalize is ever swapped.
        private const int MaxLength = 10;
        private const string Hostile = "ab\ncdefghijklmnopqrstuvwxyz";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(Hostile, MaxLength);
        }

        [Test]
        public void It_truncates_before_it_filters()
        {
            _result.Should().Be("abcdefghi");
        }

        [Test]
        public void It_does_not_produce_the_filter_then_truncate_result()
        {
            _result.Should().NotBe("abcdefghij");
        }

        [Test]
        public void It_may_be_shorter_than_the_maximum_length()
        {
            // Intended consequence of truncating first, not a defect to compensate for.
            _result.Length.Should().Be(MaxLength - 1);
        }
    }

    [TestFixture]
    public class Given_An_Already_Normalized_Correlation_Id : CorrelationIdNormalizerTests
    {
        private const int MaxLength = 10;

        [TestCase("ab\ncdefghijklmnopqrstuvwxyz")]
        [TestCase("clean-id")]
        [TestCase("\r\n\t\0")]
        [TestCase("0HNCTN1IRQMDG:00000001")]
        [TestCase("")]
        public void It_is_idempotent(string input)
        {
            string once = CorrelationIdNormalizer.Normalize(input, MaxLength);

            CorrelationIdNormalizer.Normalize(once, MaxLength).Should().Be(once);
        }
    }

    [TestFixture]
    public class Given_An_Empty_Or_Whitespace_Correlation_Id : CorrelationIdNormalizerTests
    {
        [Test]
        public void It_returns_empty_for_null_without_throwing()
        {
            CorrelationIdNormalizer
                .Normalize(null, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_for_an_empty_string_without_throwing()
        {
            CorrelationIdNormalizer
                .Normalize(string.Empty, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_when_every_character_is_a_control_character()
        {
            CorrelationIdNormalizer
                .Normalize("\r\n\t\0", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(string.Empty);
        }

        [Test]
        public void It_preserves_a_whitespace_only_value()
        {
            CorrelationIdNormalizer
                .Normalize("   ", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be("   ");
        }
    }

    [TestFixture]
    public class Given_A_Non_Positive_Maximum_Length : CorrelationIdNormalizerTests
    {
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void It_falls_back_to_the_documented_default_rather_than_throwing(int maxLength)
        {
            string oversized = new('c', AppSettings.DefaultCorrelationIdMaxLength + 10);

            CorrelationIdNormalizer
                .Normalize(oversized, maxLength)
                .Should()
                .Be(new string('c', AppSettings.DefaultCorrelationIdMaxLength));
        }
    }

    [TestFixture]
    public class Given_A_Server_Generated_Trace_Identifier : CorrelationIdNormalizerTests
    {
        // The shape ASP.NET Core produces for HttpContext.TraceIdentifier. FR-LOG-5 requires
        // the behavior for system-generated IDs to be unchanged.
        private const string TraceIdentifier = "0HNCTN1IRQMDG:00000001";

        [Test]
        public void It_passes_the_value_through_unchanged()
        {
            CorrelationIdNormalizer
                .Normalize(TraceIdentifier, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(TraceIdentifier);
        }
    }
}
