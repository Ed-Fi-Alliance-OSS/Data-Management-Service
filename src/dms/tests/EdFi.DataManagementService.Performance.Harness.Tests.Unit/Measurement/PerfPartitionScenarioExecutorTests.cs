// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_Partition_Response_Validation
{
    private static string Body(params string[] tokens) =>
        JsonSerializer.Serialize(new Dictionary<string, string[]> { ["pageTokens"] = tokens });

    private static string Token(long inclusiveMinimum) =>
        PerfCursorTokens.DocumentIdRangeFrom(inclusiveMinimum);

    [Test]
    public void It_accepts_tokens_up_to_the_requested_number()
    {
        IReadOnlyList<string> tokens = PerfPartitionScenarioExecutor.ParsePageTokens(
            Body(Token(1), Token(2_501), Token(5_001)),
            requestedNumber: 10
        );

        tokens.Should().HaveCount(3);
    }

    [Test]
    public void It_accepts_exactly_the_requested_number()
    {
        PerfPartitionScenarioExecutor
            .ParsePageTokens(Body(Token(1), Token(2)), requestedNumber: 2)
            .Should()
            .HaveCount(2);
    }

    [Test]
    public void It_rejects_more_tokens_than_requested()
    {
        Action act = () =>
            PerfPartitionScenarioExecutor.ParsePageTokens(
                Body(Token(1), Token(2), Token(3), Token(4)),
                requestedNumber: 3
            );

        act.Should().Throw<PerfObservationException>();
    }

    [Test]
    public void It_rejects_an_empty_token_list()
    {
        Action act = () => PerfPartitionScenarioExecutor.ParsePageTokens(Body(), requestedNumber: 10);

        act.Should().Throw<PerfObservationException>();
    }

    [Test]
    public void It_rejects_a_body_that_is_not_a_lone_page_tokens_object()
    {
        foreach (
            string body in (string[])
                [
                    "[]",
                    """{"pageTokens": [], "totalCount": 5}""",
                    """{"tokens": []}""",
                    """{"pageTokens": "not-an-array"}""",
                ]
        )
        {
            Action act = () => PerfPartitionScenarioExecutor.ParsePageTokens(body, requestedNumber: 10);

            act.Should().Throw<PerfObservationException>(body);
        }
    }

    [Test]
    public void It_rejects_undecodable_or_wrongly_anchored_tokens()
    {
        string changeVersionToken = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("c,1,5"));

        foreach (string body in (string[])[Body("garbage-token!"), Body(Token(1), changeVersionToken)])
        {
            Action act = () => PerfPartitionScenarioExecutor.ParsePageTokens(body, requestedNumber: 10);

            act.Should().Throw<PerfObservationException>(body);
        }
    }
}
