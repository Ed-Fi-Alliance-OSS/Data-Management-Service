// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace Acme.IdentityFixture;

/// <summary>
/// Reports what the provider did to the control channel, so a test can show an operation was never
/// invoked, a person was issued exactly once or which token a poll received, and can wait until an
/// operation is waiting for its cancellation. Nothing is reported when no control address is
/// configured, and nothing is ever logged. A report carries the operation name and a kind, plus the
/// request token for a results invocation; it carries no person data.
/// </summary>
public sealed class FixtureControlEvents(FixtureControlChannel control)
{
    /// <summary>An operation was invoked, before its grant was checked.</summary>
    public const string Invocation = "invocation";

    /// <summary>
    /// An operation passed its namespace and grant checks and began its identity work (a store lookup,
    /// an issuance or a job read). Never reported for a denied call.
    /// </summary>
    public const string Lookup = "lookup";

    /// <summary>A UniqueId was issued.</summary>
    public const string Issuance = "issuance";

    /// <summary>An async job was created.</summary>
    public const string Job = "job";

    /// <summary>
    /// An operation is waiting for its cancellation token, which nothing but cancelling the request
    /// ends. Reported once, after the grant check passed and before the wait begins.
    /// </summary>
    public const string AwaitingCancellation = "awaiting-cancellation";

    public async Task ReportAsync(
        string operation,
        string kind,
        CancellationToken cancellationToken,
        string? token = null
    )
    {
        if (!control.IsEnabled)
        {
            return;
        }

        JsonObject body = new() { ["operation"] = operation, ["kind"] = kind };
        if (token is not null)
        {
            body["token"] = token;
        }

        await control.PostAsync("events", body, cancellationToken);
    }
}
