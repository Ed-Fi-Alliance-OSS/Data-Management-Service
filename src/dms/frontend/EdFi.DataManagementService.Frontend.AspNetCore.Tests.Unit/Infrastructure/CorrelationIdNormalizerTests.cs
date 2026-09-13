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
    public class Given_A_Correlation_Id_Exactly_At_The_Maximum_Length : CorrelationIdNormalizerTests
    {
        private const int MaxLength = 16;

        [Test]
        public void It_does_not_truncate_at_the_boundary()
        {
            // The truncation test is `value.Length > effectiveMaxLength`, so a value of exactly
            // the maximum length passes through whole. An off-by-one that made it `>=` would
            // silently shorten every conforming identifier by one character, and no other test
            // in this file would notice: the over-length cases all overshoot by many characters.
            string atTheBound = new('c', MaxLength);

            CorrelationIdNormalizer.Normalize(atTheBound, MaxLength).Should().Be(atTheBound);
        }

        [Test]
        public void It_truncates_one_character_past_the_boundary()
        {
            // The other side of the same boundary, so the pair pins the comparison rather than
            // just one of its two outcomes.
            CorrelationIdNormalizer
                .Normalize(new string('c', MaxLength + 1), MaxLength)
                .Should()
                .Be(new string('c', MaxLength));
        }
    }

    [TestFixture]
    public class Given_A_Single_Astral_Character_And_A_Maximum_Length_Of_One : CorrelationIdNormalizerTests
    {
        [Test]
        public void It_yields_an_empty_string_rather_than_an_orphaned_high_surrogate()
        {
            // Two UTF-16 code units cut to one lands on the high half, the surrogate guard drops
            // it, and the retained prefix is empty. That empty result is documented behavior, and
            // it is what the ingestion point's fallback to the server-generated identifier then
            // depends on: the alternative - emitting a lone surrogate - would break the
            // byte-identical log/body parity FR-LOG-6 guarantees.
            CorrelationIdNormalizer.Normalize("\U0001F600", 1).Should().Be(string.Empty);
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_That_Already_Contains_A_Lone_Surrogate : CorrelationIdNormalizerTests
    {
        // A high surrogate with nothing after it. An HTTP header value cannot carry this, so it
        // is unreachable from a request - but the documented contract says such a value is
        // preserved rather than repaired, and that promise is what is pinned here.
        private const string WithLoneSurrogate = "ab\ud83dcd";

        [Test]
        public void It_preserves_the_lone_surrogate_rather_than_repairing_it()
        {
            // char.IsControl is false for a surrogate, so the allowlist keeps it. The
            // surrogate-pair guard is deliberately scoped to the truncation cut and does not
            // scan the input, so nothing here removes or replaces it.
            CorrelationIdNormalizer
                .Normalize(WithLoneSurrogate, AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(WithLoneSurrogate);
        }

        [Test]
        public void It_still_removes_control_characters_around_it()
        {
            // The lone surrogate is preserved, not treated as a reason to give up on the rest of
            // the value: the allowlist still applies to every other character.
            CorrelationIdNormalizer
                .Normalize($"\r{WithLoneSurrogate}\n", AppSettings.DefaultCorrelationIdMaxLength)
                .Should()
                .Be(WithLoneSurrogate);
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
            // This single equality is the whole order assertion: it rules out the
            // filter-then-truncate result "abcdefghij" and pins the length at MaxLength - 1.
            // A result shorter than the maximum length is the intended consequence of
            // truncating first, not a defect to compensate for.
            _result.Should().Be("abcdefghi");
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Truncated_Inside_A_Surrogate_Pair : CorrelationIdNormalizerTests
    {
        // U+1F600 is a non-BMP code point, so it occupies two UTF-16 code units. Cutting at
        // seven units lands between them.
        private const string WithAstralCharacter = "abcdef\U0001F600ghij";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = CorrelationIdNormalizer.Normalize(WithAstralCharacter, 7);
        }

        [Test]
        public void It_drops_the_orphaned_high_surrogate_rather_than_emitting_it()
        {
            // char.IsControl is false for a surrogate, so the allowlist would keep an orphaned
            // high half. System.Text.Json would then write U+FFFD into the response body while
            // a log sink received the raw unpaired unit, and the two would no longer be
            // byte-identical - the parity FR-LOG-6 guarantees.
            _result.Should().Be("abcdef");
        }
    }

    [TestFixture]
    public class Given_A_Correlation_Id_Ending_Exactly_At_A_Surrogate_Pair_Boundary
        : CorrelationIdNormalizerTests
    {
        private const string WithAstralCharacter = "abcdef\U0001F600ghij";

        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            // Cutting at eight units keeps the pair whole: the last retained unit is the LOW
            // half. The guard must test only for a HIGH surrogate, so it leaves this alone.
            _result = CorrelationIdNormalizer.Normalize(WithAstralCharacter, 8);
        }

        [Test]
        public void It_preserves_a_complete_pair_that_ends_at_the_boundary()
        {
            // Pins the guard's predicate. Widening char.IsHighSurrogate to char.IsSurrogate
            // would drop the low half here and emit "abcdef\uD83D" - an orphaned high
            // surrogate, exactly the parity break the guard exists to prevent.
            _result.Should().Be("abcdef\U0001F600");
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
