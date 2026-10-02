// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Identity;

namespace Acme.IdentityFixture;

/// <summary>
/// The deliberate result shapes the fixture's triggers select: contract misuse, projected errors and
/// deliberate exceptions. A variant is named by the text after the trigger's prefix, compared
/// <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// </summary>
public static class FixtureVariants
{
    public const string Incomplete = "incomplete";
    public const string JobFailed = "jobfailed";
    public const string JobFailedWithPayload = "jobfailed-payload";
    public const string SuccessWithoutPayload = "success-nopayload";
    public const string SuccessWithPayloadAndToken = "success-both";
    public const string SuccessWithNeither = "success-neither";
    public const string ThrowPersonText = "throw-person";
    public const string LostCreate = "lost-create";
    public const string InvalidPrefix = "invalid-";

    /// <summary>The set of <c>InvalidProperties</c> shapes, named after the served document's pinned examples.</summary>
    public const string CreateFieldError = "createFieldError";
    public const string SearchItemError = "searchItemError";
    public const string PathlessError = "pathlessError";
    public const string PathlessNullError = "pathlessNullError";
    public const string TwoMessagesOneKey = "twoMessagesOneKey";

    /// <summary>The result a sync-operation or results variant forces, or throws for the throwing ones.</summary>
    public static IdentityResult Result(string variant)
    {
        if (TryInvalid(variant, out IReadOnlyList<IdentityError>? errors))
        {
            return new IdentityResult { Status = IdentityResultStatus.InvalidProperties, Errors = errors };
        }

        return Is(variant, Incomplete)
                ? new IdentityResult
                {
                    Status = IdentityResultStatus.Incomplete,
                    Payload = new JsonObject
                    {
                        ["Status"] = "Incomplete",
                        ["SearchResponses"] = new JsonArray(),
                    },
                }
            : Is(variant, JobFailed) ? new IdentityResult { Status = IdentityResultStatus.JobFailed }
            : Is(variant, JobFailedWithPayload)
                ? new IdentityResult
                {
                    Status = IdentityResultStatus.JobFailed,
                    Payload = new JsonObject
                    {
                        ["Status"] = "Complete",
                        ["SearchResponses"] = new JsonArray(),
                    },
                    Errors = [new IdentityError { Message = "The job failed.", Path = "$.firstName" }],
                }
            : Is(variant, SuccessWithoutPayload) || Is(variant, SuccessWithNeither)
                ? new IdentityResult { Status = IdentityResultStatus.Success }
            : Is(variant, ThrowPersonText) ? throw FixtureFailures.PersonText()
            : throw Unknown();
    }

    /// <summary>The result a find or search variant forces, or throws for the throwing ones.</summary>
    public static IdentityAsyncResult AsyncResult(string variant)
    {
        if (TryInvalid(variant, out IReadOnlyList<IdentityError>? errors))
        {
            return new IdentityAsyncResult
            {
                Status = IdentityResultStatus.InvalidProperties,
                Errors = errors,
            };
        }

        return Is(variant, Incomplete)
                ? new IdentityAsyncResult
                {
                    Status = IdentityResultStatus.Incomplete,
                    Payload = new JsonObject
                    {
                        ["Status"] = "Incomplete",
                        ["SearchResponses"] = new JsonArray(),
                    },
                }
            : Is(variant, JobFailed) ? new IdentityAsyncResult { Status = IdentityResultStatus.JobFailed }
            : Is(variant, JobFailedWithPayload)
                ? new IdentityAsyncResult
                {
                    Status = IdentityResultStatus.JobFailed,
                    Payload = new JsonObject
                    {
                        ["Status"] = "Complete",
                        ["SearchResponses"] = new JsonArray(),
                    },
                    Errors = [new IdentityError { Message = "The job failed.", Path = "$.firstName" }],
                    RequestToken = Guid.NewGuid().ToString("N"),
                }
            : Is(variant, SuccessWithoutPayload) || Is(variant, SuccessWithNeither)
                ? new IdentityAsyncResult { Status = IdentityResultStatus.Success }
            : Is(variant, SuccessWithPayloadAndToken)
                ? new IdentityAsyncResult
                {
                    Status = IdentityResultStatus.Success,
                    Payload = new JsonObject
                    {
                        ["Status"] = "Complete",
                        ["SearchResponses"] = new JsonArray(),
                    },
                    RequestToken = Guid.NewGuid().ToString("N"),
                }
            : Is(variant, ThrowPersonText) ? throw FixtureFailures.PersonText()
            : throw Unknown();
    }

    public static bool Is(string variant, string expected) =>
        string.Equals(variant, expected, StringComparison.OrdinalIgnoreCase);

    // Each shape has exactly the messages and paths of the pinned example of the same name in the
    // served identity document. A blank path and a null path project the same way.
    private static bool TryInvalid(string variant, out IReadOnlyList<IdentityError> errors)
    {
        errors = [];
        if (!variant.StartsWith(InvalidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string shape = variant[InvalidPrefix.Length..];
        errors = true switch
        {
            _ when Is(shape, CreateFieldError) =>
            [
                new IdentityError { Path = "$.firstName", Message = "First name is required." },
            ],
            _ when Is(shape, SearchItemError) =>
            [
                new IdentityError { Path = "$[2].firstName", Message = "First name is required for item 2." },
            ],
            _ when Is(shape, PathlessError) =>
            [
                new IdentityError { Path = "", Message = "The request could not be evaluated." },
            ],
            _ when Is(shape, PathlessNullError) =>
            [
                new IdentityError { Path = null, Message = "The request could not be evaluated." },
            ],
            _ when Is(shape, TwoMessagesOneKey) =>
            [
                new IdentityError { Path = "$.firstName", Message = "First name is required." },
                new IdentityError
                {
                    Path = "$.firstName",
                    Message = "First name must not exceed 75 characters.",
                },
            ],
            _ => throw Unknown(),
        };

        return true;
    }

    private static InvalidOperationException Unknown() => new("The fixture does not know that variant.");
}
