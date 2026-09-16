// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcControllerFixtureOffsets
{
    [TestCase(
        "{\"commit_lsn\":\"00000001:00000002:0003\",\"change_lsn\":\"00000001:00000002:0003\",\"event_serial_no\":1}",
        true,
        false,
        true
    )]
    [TestCase(
        "{\"commit_lsn\":\"00000001:00000002:0003\",\"change_lsn\":\"NULL\",\"event_serial_no\":1}",
        true,
        true,
        true
    )]
    [TestCase("{\"commit_lsn\":\"private-invalid-lsn\",\"event_serial_no\":-1}", false, false, false)]
    public async Task It_preserves_consumed_bytes_and_records_only_offset_shape(
        string offset,
        bool validCommit,
        bool nullChange,
        bool nonNegativeSerial
    )
    {
        string body =
            "{\"offsets\":[{\"partition\":{\"server\":\"private-source\",\"database\":\"private-db\"},\"offset\":"
            + offset
            + "}]}";
        using var evidence = new CdcControllerFixtureOffsets(new Response(new StringContent(body)));
        using var client = new HttpClient(evidence);
        using var response = await client.GetAsync(
            "http://fixture/connectors/private-name/offsets",
            HttpCompletionOption.ResponseHeadersRead
        );
        evidence.Observations.Should().BeEmpty("observations must follow consumption, without pre-reading");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await response.Content.ReadAsStringAsync()).Should().Be(body);
        string text = JsonSerializer.Serialize(evidence.Observations);
        text.Should().NotContain("private").And.NotContain("00000001");
        using var document = JsonDocument.Parse(text);
        var entry = document.RootElement[0].GetProperty("Entries")[0];
        entry.GetProperty("Commit").GetProperty("Valid").GetBoolean().Should().Be(validCommit);
        entry.GetProperty("Change").GetProperty("NullMarker").GetBoolean().Should().Be(nullChange);
        entry.GetProperty("SerialNonNegative").GetBoolean().Should().Be(nonNegativeSerial);
    }

    [Test]
    public void It_distinguishes_the_idle_serial_zero_without_publishing_offset_values()
    {
        using var evidence = new CdcControllerFixtureOffsets();
        evidence.Record(
            Encoding.UTF8.GetBytes(
                """{"offsets":[{"offset":{"commit_lsn":"00000001:00000002:0003","change_lsn":"NULL","event_serial_no":0}}]}"""
            ),
            "Complete"
        );
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(evidence.Observations));
        var entry = document.RootElement[0].GetProperty("Entries")[0];
        entry.GetProperty("SerialZero").GetBoolean().Should().BeTrue();
        entry.GetProperty("SerialOne").GetBoolean().Should().BeFalse();
        entry.GetProperty("Change").GetProperty("NullMarker").GetBoolean().Should().BeTrue();
        document.RootElement.GetRawText().Should().NotContain("00000001");
    }

    [Test]
    public async Task It_preserves_malformed_json_for_the_adapter_to_reject()
    {
        const string body = "{private-malformed";
        using var evidence = new CdcControllerFixtureOffsets(new Response(new StringContent(body)));
        using var client = new HttpClient(evidence);
        (await client.GetStringAsync("http://fixture/connectors/name/offsets")).Should().Be(body);
        JsonSerializer
            .Serialize(evidence.Observations)
            .Should()
            .Contain("InvalidJson")
            .And.NotContain("private");
    }

    [TestCase("GET", "/config", HttpStatusCode.OK)]
    [TestCase("DELETE", "/offsets", HttpStatusCode.OK)]
    [TestCase("GET", "/offsets", HttpStatusCode.NotFound)]
    public async Task It_ignores_other_routes_mutations_and_failed_responses(
        string method,
        string route,
        HttpStatusCode status
    )
    {
        using var evidence = new CdcControllerFixtureOffsets(
            new Response(new StringContent("private"), status)
        );
        using var client = new HttpClient(evidence);
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            "http://fixture/connectors/name" + route
        );
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(status);
        (await response.Content.ReadAsStringAsync()).Should().Be("private");
        evidence.Observations.Should().BeEmpty();
    }

    [Test]
    public void It_bounds_observation_and_entry_counts_without_retaining_extra_fields()
    {
        using var evidence = new CdcControllerFixtureOffsets();
        byte[] bytes = Encoding.UTF8.GetBytes(
            "{\"offsets\":[" + string.Join(",", Enumerable.Repeat("{\"private\":\"secret\"}", 9)) + "]}"
        );
        for (int index = 0; index < 129; index++)
        {
            evidence.Record(bytes, "Complete");
        }
        evidence.Observations.Should().HaveCount(128);
        string text = JsonSerializer.Serialize(evidence.Observations);
        text.Should().NotContain("private").And.NotContain("secret");
        using var document = JsonDocument.Parse(text);
        document.RootElement[0].GetProperty("Count").GetInt32().Should().Be(9);
        document.RootElement[0].GetProperty("Entries").GetArrayLength().Should().Be(8);
    }

    [Test]
    public async Task It_leaves_oversized_response_enforcement_to_the_adapter()
    {
        string body = new('x', CdcConnectRestAdapter.MaximumResponseBytes + 1);
        using var evidence = new CdcControllerFixtureOffsets(new Response(new StringContent(body)));
        using var client = new HttpClient(evidence);
        (await client.GetStringAsync("http://fixture/connectors/name/offsets")).Should().Be(body);
        JsonSerializer
            .Serialize(evidence.Observations)
            .Should()
            .Contain("TooLarge")
            .And.NotContain("Entries");
    }

    [Test]
    public async Task It_preserves_read_failures_and_records_incomplete_consumption()
    {
        var failure = new IOException("private-stream-failure");
        using var evidence = new CdcControllerFixtureOffsets(
            new Response(new StreamContent(new FailedStream(failure)))
        );
        using var client = new HttpClient(evidence);
        using (
            var response = await client.GetAsync(
                "http://fixture/connectors/name/offsets",
                HttpCompletionOption.ResponseHeadersRead
            )
        )
        {
            var stream = await response.Content.ReadAsStreamAsync();
            Func<Task> read = async () => await stream.ReadExactlyAsync(new byte[8]);
            (await read.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        }
        JsonSerializer
            .Serialize(evidence.Observations)
            .Should()
            .Contain("Incomplete")
            .And.NotContain("private");
    }

    [Test]
    public async Task It_preserves_caller_cancellation_without_recording_complete_evidence()
    {
        using var evidence = new CdcControllerFixtureOffsets(new Response(new StringContent("{}")));
        using var client = new HttpClient(evidence);
        using var cancellation = new CancellationTokenSource();
        using (
            var response = await client.GetAsync(
                "http://fixture/connectors/name/offsets",
                HttpCompletionOption.ResponseHeadersRead
            )
        )
        {
            var stream = await response.Content.ReadAsStreamAsync();
            await cancellation.CancelAsync();
            Func<Task> read = async () => await stream.ReadExactlyAsync(new byte[8], cancellation.Token);
            (await read.Should().ThrowAsync<OperationCanceledException>())
                .Which.CancellationToken.Should()
                .Be(cancellation.Token);
        }
        JsonSerializer
            .Serialize(evidence.Observations)
            .Should()
            .Contain("Incomplete")
            .And.NotContain("Complete");
    }

    [Test]
    public async Task It_does_not_treat_a_zero_length_read_as_end_of_response()
    {
        using var evidence = new CdcControllerFixtureOffsets(new Response(new StringContent("{}")));
        using var client = new HttpClient(evidence);
        using var response = await client.GetAsync(
            "http://fixture/connectors/name/offsets",
            HttpCompletionOption.ResponseHeadersRead
        );
        var stream = await response.Content.ReadAsStreamAsync();
        (await stream.ReadAsync(Memory<byte>.Empty)).Should().Be(0);
        evidence.Observations.Should().BeEmpty();
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be("{}");
        JsonSerializer.Serialize(evidence.Observations).Should().Contain("Complete");
    }

    private sealed class Response(HttpContent content, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private sealed class FailedStream(IOException failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromException<int>(failure);
    }
}
