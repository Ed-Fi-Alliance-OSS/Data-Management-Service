// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.IdentityFixture;

/// <summary>
/// The exceptions the fixture throws on purpose. Their messages carry person-shaped text, so a test
/// can prove the host neither logs nor serves provider exception text.
/// </summary>
public static class FixtureFailures
{
    /// <summary>Person-shaped text that appears in the message of every deliberate exception.</summary>
    public const string PersonSentinel = "SENTINEL-Jane-Doe-1999-01-01";

    /// <summary>An outer exception wrapping an inner one, both messages carrying the sentinel.</summary>
    public static InvalidOperationException Nested(string what) =>
        new(
            $"{what} {PersonSentinel} outer",
            new InvalidOperationException($"Inner failure for {PersonSentinel} inner")
        );

    /// <summary>An operation failure whose message reads like text about a person.</summary>
    public static InvalidOperationException PersonText() =>
        new($"Lookup failed for Jane Doe born 1999-01-01 ({PersonSentinel})");

    /// <summary>
    /// A cancellation of <paramref name="cancellationToken"/> whose message and inner exception both
    /// read like text about a person, as a provider quoting the request it was cancelled during would
    /// throw.
    /// </summary>
    public static OperationCanceledException PersonTextCancellation(CancellationToken cancellationToken) =>
        new(
            $"Lookup cancelled for Jane Doe born 1999-01-01 ({PersonSentinel})",
            PersonText(),
            cancellationToken
        );

    /// <summary>The simulated loss of an upstream response after the upstream acted on a create.</summary>
    public static InvalidOperationException LostResponse() =>
        new("The upstream response to the create was lost.");

    /// <summary>A transient failure to obtain a job's state.</summary>
    public static InvalidOperationException TransientPoll() =>
        new("The upstream could not report the job state.");
}
