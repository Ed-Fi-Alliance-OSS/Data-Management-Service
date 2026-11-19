// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class ProjectionHttpContentTests
{
    private const int Cap = 40_000;

    [TestFixture]
    public class Given_bodies_around_the_cap
    {
        private static byte[] Bytes(int length) => Enumerable.Repeat((byte)'a', length).ToArray();

        [TestCase(0, true)]
        [TestCase(Cap, true)]
        [TestCase(Cap + 1, false)]
        public async Task It_reads_a_declared_length_body_only_up_to_the_cap(int length, bool read)
        {
            byte[]? body = await ProjectionHttpContent.ReadBoundedAsync(
                new ByteArrayContent(Bytes(length)),
                Cap,
                CancellationToken.None
            );
            (body?.Length).Should().Be(read ? length : null);
        }

        [TestCase(0, true)]
        [TestCase(Cap, true)]
        [TestCase(Cap + 1, false)]
        public async Task It_reads_an_undeclared_length_body_only_up_to_the_cap(int length, bool read)
        {
            StreamContent content = new(new UnknownLengthStream(Bytes(length)));
            content.Headers.ContentLength.Should().BeNull();

            byte[]? body = await ProjectionHttpContent.ReadBoundedAsync(content, Cap, CancellationToken.None);

            (body?.Length).Should().Be(read ? length : null);
        }

        [Test]
        public async Task It_refuses_a_declared_length_over_the_cap_without_reading()
        {
            StalledStream stream = new(new IOException("must not be read"));
            StreamContent content = new(stream);
            content.Headers.ContentLength = Cap + 1;

            byte[]? body = await ProjectionHttpContent.ReadBoundedAsync(content, Cap, CancellationToken.None);

            body.Should().BeNull();
            stream.ReadStarted.Task.IsCompleted.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_error_bodies
    {
        private static Task<ProblemFields> Read(HttpResponseMessage response) =>
            ProjectionHttpContent.ReadProblemAsync(response, CancellationToken.None);

        [Test]
        public async Task It_reads_the_type_and_correlation_id_of_a_problem_document() =>
            (await Read(DmsResponses.Problem(HttpStatusCode.NotFound, "urn:x:y", "c-1")))
                .Should()
                .Be(new ProblemFields("urn:x:y", "c-1"));

        [Test]
        public async Task It_matches_the_media_type_ignoring_case_and_parameters()
        {
            HttpResponseMessage response = DmsResponses.Text(
                HttpStatusCode.NotFound,
                """{"type":"urn:x:y"}""",
                "Application/Problem+JSON"
            );
            (await Read(response)).Type.Should().Be("urn:x:y");
        }

        [TestCase("application/json")]
        [TestCase("text/plain")]
        public async Task It_reads_no_type_from_another_media_type(string mediaType) =>
            (await Read(DmsResponses.Text(HttpStatusCode.NotFound, """{"type":"urn:x:y"}""", mediaType)))
                .Should()
                .Be(ProblemFields.None);

        [Test]
        public async Task It_reads_no_type_without_content() =>
            (await Read(new HttpResponseMessage(HttpStatusCode.InternalServerError)))
                .Should()
                .Be(ProblemFields.None);

        [TestCase("not json")]
        [TestCase("""["urn:x:y"]""")]
        [TestCase("""{"type":"urn:x:y" """)]
        public async Task It_reads_no_type_from_an_unparsable_body(string body) =>
            (await Read(DmsResponses.Text(HttpStatusCode.NotFound, body, "application/problem+json")))
                .Should()
                .Be(ProblemFields.None);

        [Test]
        public async Task It_reads_no_type_from_invalid_utf8()
        {
            HttpResponseMessage response = new(HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent([
                    .. Encoding.UTF8.GetBytes("""{"type":"urn:"""),
                    0xFF,
                    .. "\"}"u8,
                ]),
            };
            response.Content.Headers.ContentType = new("application/problem+json");

            (await Read(response)).Should().Be(ProblemFields.None);
        }

        [Test]
        public async Task It_drops_members_that_are_not_strings() =>
            (
                await Read(
                    DmsResponses.Text(
                        HttpStatusCode.NotFound,
                        """{"type":7,"correlationId":"c-1"}""",
                        "application/problem+json"
                    )
                )
            )
                .Should()
                .Be(new ProblemFields(null, "c-1"));

        [TestCase(ProjectionHttpContent.MaxProblemBodyBytes, "urn:x:y")]
        [TestCase(ProjectionHttpContent.MaxProblemBodyBytes + 1, null)]
        public async Task It_reads_the_type_only_from_a_body_up_to_64_kilobytes(
            int length,
            string? expectedType
        )
        {
            string prefix = """{"type":"urn:x:y","pad":" """;
            string body = prefix + new string('a', length - prefix.Length - 2) + "\"}";
            Encoding.UTF8.GetByteCount(body).Should().Be(length);

            (await Read(DmsResponses.Text(HttpStatusCode.NotFound, body, "application/problem+json")))
                .Type.Should()
                .Be(expectedType);
        }
    }

    [TestFixture]
    public class Given_x_correlation_id_headers
    {
        private static string? Read(params string[] values)
        {
            HttpResponseMessage response = new(HttpStatusCode.Unauthorized);
            foreach (string value in values)
            {
                response.Headers.TryAddWithoutValidation("X-Correlation-ID", value);
            }
            return ProjectionHttpContent.ReadCorrelationIdHeader(response);
        }

        [TestCase("0HNABCDEF%3A00000001", "0HNABCDEF:00000001")]
        [TestCase("a%20b", "a b")]
        [TestCase("a%E2%82%ACb", "a€b")]
        [TestCase("plain-id_1.2", "plain-id_1.2")]
        public void It_percent_decodes_the_value_as_utf8(string header, string expected) =>
            Read(header).Should().Be(expected);

        [Test]
        public void It_decodes_only_once() => Read("abc%253A").Should().Be("abc%3A");

        [TestCase("abc%")]
        [TestCase("abc%3")]
        [TestCase("abc%zz")]
        [TestCase("abc%+1")]
        [TestCase("%C3%28")]
        [TestCase("%FF")]
        [TestCase("a b")]
        [TestCase("abé")]
        public void It_reads_nothing_from_a_malformed_value(string header) => Read(header).Should().BeNull();

        [Test]
        public void It_reads_nothing_without_the_header() =>
            ProjectionHttpContent
                .ReadCorrelationIdHeader(new HttpResponseMessage(HttpStatusCode.Unauthorized))
                .Should()
                .BeNull();

        [Test]
        public void It_reads_nothing_from_a_repeated_header() => Read("c-1", "c-2").Should().BeNull();

        [Test]
        public void It_reads_a_value_that_decodes_to_the_kept_length() =>
            Read(new string('a', EducationOrganizationProjectionFailure.MaxCorrelationIdLength))
                .Should()
                .Be(new string('a', EducationOrganizationProjectionFailure.MaxCorrelationIdLength));

        [Test]
        public void It_reads_nothing_from_a_value_that_decodes_past_the_kept_length() =>
            Read(new string('a', EducationOrganizationProjectionFailure.MaxCorrelationIdLength + 1))
                .Should()
                .BeNull();

        [Test]
        public void It_reads_a_raw_value_of_the_raw_bound()
        {
            string header = string.Concat(
                Enumerable.Repeat("%41", EducationOrganizationProjectionFailure.MaxCorrelationIdLength)
            );
            header.Length.Should().Be(ProjectionHttpContent.MaxCorrelationIdHeaderLength);

            Read(header)
                .Should()
                .Be(new string('A', EducationOrganizationProjectionFailure.MaxCorrelationIdLength));
        }

        [Test]
        public void It_reads_nothing_from_a_raw_value_past_the_raw_bound_however_short_it_decodes()
        {
            // Each "%C3%A9" is six raw characters decoding to one: 65 of them decode to 65 characters.
            string header = string.Concat(Enumerable.Repeat("%C3%A9", 65));
            header.Length.Should().BeGreaterThan(ProjectionHttpContent.MaxCorrelationIdHeaderLength);
            Read(string.Concat(Enumerable.Repeat("%C3%A9", 64))).Should().Be(new string('é', 64));

            Read(header).Should().BeNull();
        }
    }
}
