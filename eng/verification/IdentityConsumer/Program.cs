// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The assertions. Compiling the sample proves the packed contract carries the closure its
// signatures need; running these proves the sample keeps the payload shapes the guide documents.
//
// Everything below goes through the real CreateAsync, GetByIdAsync and SearchAsync calls. Nothing
// asserts about the source text.

using System.Text.Json.Nodes;
using Acme.Dms.Identity;
using EdFi.DataManagementService.Identity;

namespace IdentityConsumer;

internal static class Program
{
    private static readonly IdentityRequestContext Context = new()
    {
        ClientId = "acme-sample-client",
        TraceId = "identity-consumer-assertions",
    };

    private static readonly string[] StandardProperties =
    [
        "UniqueId",
        "LastSurname",
        "FirstName",
        "MiddleName",
        "GenerationCodeSuffix",
        "SexType",
        "BirthDate",
        "BirthOrder",
        "BirthLocation",
        "Score",
    ];

    internal static async Task<int> Main()
    {
        await AssertMistypedAttributesAreRefused();

        AcmeIdentityService provider = NewProvider();
        string uniqueId = await AssertTheStoredIdentityKeepsTheResponseShape(provider);
        await AssertSearchMatchesTheNormalizedBirthDate(provider, uniqueId);

        Console.WriteLine(
            "Verified the sample provider refuses a standard property of the wrong type or a birth date "
                + "that is not a date-time with an offset, returns every standard property in the type "
                + "the served schema gives it, and matches a search on the normalized birth date."
        );

        return 0;
    }

    /// <summary>
    /// Each request carries exactly one standard property in a shape the served schema does not
    /// allow. The sample must answer InvalidProperties with one error at that property's path, and
    /// the error must not repeat the value, which is person data.
    /// </summary>
    private static async Task AssertMistypedAttributesAreRefused()
    {
        (string Description, string Json, string Path)[] cases =
        [
            ("a date with no time", """ "BirthDate": "2012-05-14" """, "$.BirthDate"),
            ("a date-time with no offset", """ "BirthDate": "2012-05-14T00:00:00" """, "$.BirthDate"),
            ("a birth date in another format", """ "BirthDate": "May 14 2012" """, "$.BirthDate"),
            ("a numeric birth date", """ "BirthDate": 20120514 """, "$.BirthDate"),
            ("a numeric middle name", """ "MiddleName": 5 """, "$.MiddleName"),
            (
                "a boolean generation code suffix",
                """ "GenerationCodeSuffix": true """,
                "$.GenerationCodeSuffix"
            ),
            ("an object sex type", """ "SexType": {} """, "$.SexType"),
            ("a fractional birth order", """ "BirthOrder": 1.5 """, "$.BirthOrder"),
            ("a string birth order", """ "BirthOrder": "1" """, "$.BirthOrder"),
            ("a string birth location", """ "BirthLocation": "Austin" """, "$.BirthLocation"),
            ("a numeric birth city", """ "BirthLocation": { "City": 78701 } """, "$.BirthLocation.City"),
        ];

        foreach ((string description, string json, string path) in cases)
        {
            JsonObject request = JsonNode
                .Parse($$"""{ "LastSurname": "Lovelace", "FirstName": "Ada", {{json}} }""")!
                .AsObject();

            if (!request.ContainsKey("BirthDate"))
            {
                request["BirthDate"] = "2012-05-14T00:00:00Z";
            }

            IdentityResult result = await NewProvider().CreateAsync(request, Context, CancellationToken.None);

            Assert(
                result.Status == IdentityResultStatus.InvalidProperties,
                $"A create with {description} answered {result.Status}, not InvalidProperties."
            );
            Assert(
                result.Errors.Count == 1 && result.Errors[0].Path == path,
                $"A create with {description} reported errors at "
                    + $"[{string.Join(", ", result.Errors.Select(error => error.Path))}], not exactly one at {path}."
            );

            string suppliedValue = json[(json.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Trim('"');
            Assert(
                !result.Errors[0].Message.Contains(suppliedValue, StringComparison.Ordinal),
                $"The error for {description} repeats the supplied value."
            );
        }
    }

    /// <summary>
    /// A get after a create returns every standard property, in the type the served schema gives it,
    /// with the birth date in one normalized UTC form.
    /// </summary>
    private static async Task<string> AssertTheStoredIdentityKeepsTheResponseShape(
        AcmeIdentityService provider
    )
    {
        JsonObject request = JsonNode
            .Parse(
                """
                {
                  "LastSurname": "Lovelace",
                  "FirstName": "Ada",
                  "BirthDate": "2012-05-14T00:00:00-05:00",
                  "BirthOrder": 2,
                  "BirthLocation": { "City": "Austin" }
                }
                """
            )!
            .AsObject();

        IdentityResult created = await provider.CreateAsync(request, Context, CancellationToken.None);

        Assert(
            created.Status == IdentityResultStatus.Success
                && created.Payload is JsonValue issued
                && issued.TryGetValue(out string? _),
            $"A valid create answered {created.Status} without a bare JSON string UniqueId."
        );

        string uniqueId = created.Payload!.GetValue<string>();
        IdentityResult found = await provider.GetByIdAsync(uniqueId, Context, CancellationToken.None);

        Assert(
            found.Status == IdentityResultStatus.Success && found.Payload is JsonObject,
            $"A get of the created identity answered {found.Status} without an IdentityResponse object."
        );

        JsonObject identity = found.Payload!.AsObject();

        foreach (string property in StandardProperties)
        {
            Assert(
                identity.ContainsKey(property),
                $"The returned identity omits {property}; it must be present."
            );
        }

        Assert(
            identity["UniqueId"]!.GetValue<string>() == uniqueId,
            "The returned identity has another UniqueId."
        );
        Assert(identity["MiddleName"] is null, "A middle name the request omitted is not returned as null.");
        Assert(
            identity["BirthDate"]!.GetValue<string>() == "2012-05-14T05:00:00Z",
            $"The birth date is returned as '{identity["BirthDate"]}', not in the normalized UTC form."
        );
        Assert(
            identity["BirthOrder"]!.GetValue<int>() == 2,
            "The birth order is not returned as the integer 2."
        );
        Assert(identity["Score"] is null, "An identity returned outside a search carries a Score.");

        JsonObject location = identity["BirthLocation"]!.AsObject();
        Assert(location["City"]!.GetValue<string>() == "Austin", "The birth city is not returned as sent.");

        foreach (string property in new[] { "StateAbbreviation", "InternationalProvince", "Country" })
        {
            Assert(
                location.ContainsKey(property) && location[property] is null,
                $"BirthLocation.{property}, which the request omitted, is not returned as null."
            );
        }

        return uniqueId;
    }

    /// <summary>
    /// A search that names the same instant in another offset matches the stored identity, and a
    /// search birth date that is not a date-time is refused at its item's path.
    /// </summary>
    private static async Task AssertSearchMatchesTheNormalizedBirthDate(
        AcmeIdentityService provider,
        string uniqueId
    )
    {
        JsonObject sameInstant = JsonNode
            .Parse(
                """{ "LastSurname": "Lovelace", "FirstName": "Ada", "BirthDate": "2012-05-14T05:00:00+00:00" }"""
            )!
            .AsObject();

        IdentityAsyncResult matched = await provider.SearchAsync(
            [sameInstant],
            Context,
            CancellationToken.None
        );

        JsonArray? responses = matched.Payload?["SearchResponses"]?[0]?["Responses"]?.AsArray();
        Assert(
            matched.Status == IdentityResultStatus.Success
                && responses is { Count: 1 }
                && responses[0]!["UniqueId"]!.GetValue<string>() == uniqueId
                && responses[0]!["Score"]!.GetValue<int>() == 100,
            "A search naming the stored birth date in another offset did not return the identity with Score 100."
        );

        JsonObject dateOnly = JsonNode
            .Parse("""{ "LastSurname": "Lovelace", "FirstName": "Ada", "BirthDate": "2012-05-14" }""")!
            .AsObject();

        IdentityAsyncResult refused = await provider.SearchAsync(
            [sameInstant, dateOnly],
            Context,
            CancellationToken.None
        );

        Assert(
            refused.Status == IdentityResultStatus.InvalidProperties
                && refused.Errors.Count == 1
                && refused.Errors[0].Path == "$[1].BirthDate",
            $"A search whose second item has a date-only birth date answered {refused.Status} with errors at "
                + $"[{string.Join(", ", refused.Errors.Select(error => error.Path))}], not one at $[1].BirthDate."
        );
    }

    private static AcmeIdentityService NewProvider() => new(new AcmeIdentityStore([Context.ClientId]));

    private static void Assert(bool condition, string failureMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }
}
