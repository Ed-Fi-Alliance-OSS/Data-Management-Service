// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The reference UniqueId validator: it rejects a write to a person resource whose UniqueId does not
// exist in an external unique-id system, which is the migration path for an implementer of ODS/API's
// own UniqueIdValidation feature. The region below is mirrored verbatim onto the how-to page, and the
// same published bytes are loaded by this repository's own integration suite over real HTTP against a
// stub of that external system, so this is a sample that has been proven rather than one that only
// looks right.

// embed-region: validator
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.CustomValidation;

namespace Acme.UniqueIdValidation;

public sealed class UniqueIdValidator : ICustomResourceValidator
{
    /// <summary>
    /// The name this validator asks <see cref="IHttpClientFactory"/> for, and the same name the
    /// plugin below registers it under, so the two sides of that agreement are one string rather
    /// than two that can drift apart.
    /// </summary>
    public const string HttpClientName = "Acme.UniqueIdValidation";

    private readonly IHttpClientFactory _httpClientFactory;

    // Trivial by obligation, not by taste: DMS resolves every registered validator on every write
    // request before it reads any AppliesTo, so this constructor runs for writes to resources this
    // validator has nothing to say about. Storing the factory is the whole of it; the HttpClient
    // itself is built per call in ValidateAsync below, which is exactly as cheap and keeps this
    // validator from holding a client past the request that created it.
    public UniqueIdValidator(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    // Built once and handed back by reference. This getter is read on every write request for
    // every registered validator, before any filtering, so it must stay this cheap.
    public IReadOnlyList<ValidatedResource> AppliesTo { get; } =
    [
        new ValidatedResource("Ed-Fi", "Student"),
        new ValidatedResource("Ed-Fi", "Staff"),
        new ValidatedResource("Ed-Fi", "Contact"),
    ];

    public async Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    )
    {
        // Observed and allowed to propagate rather than caught and turned into a validation
        // failure: on a request the client has already abandoned, DMS rethrows this instead of
        // answering a 500, and reporting a failure here would answer 400 to a caller who is gone.
        cancellationToken.ThrowIfCancellationRequested();

        // This validator does not special-case operation: it checks on both Upsert (POST) and
        // Update (PUT), because an upstream unique-id system can retire an id after the document
        // was first created, so a write that was valid once is not proof it still is. ODS/API's own
        // UniqueIdValidation feature does not re-check on PUT; this is a deliberate difference.
        string member = MemberFor(resource.ResourceName);

        // Read as a JSON string rather than through GetValue<string>(), which throws when the
        // member holds a number, and treated as nothing to check when it is absent, not a string,
        // or empty: a validator reads a document it did not construct, so it defends against its
        // own rule's input being missing rather than assuming that input is there.
        if (
            document[member] is not JsonValue submitted
            || !submitted.TryGetValue(out string? uniqueId)
            || uniqueId.Length == 0
        )
        {
            return NoFailures;
        }

        // "." and ".." cannot be sent as one path segment: EscapeDataString leaves dots alone, and
        // URI resolution removes dot segments even when they are percent-encoded, so the lookup
        // would land on the upstream's collection or root instead. No upstream can hold such an
        // id under this contract, so it is answered as not found without a call.
        if (uniqueId is "." or "..")
        {
            return NotFound(resource.ResourceName, member);
        }

        HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
        string requestUri = $"{resource.ResourceName}/{Uri.EscapeDataString(uniqueId)}";

        // Only the status code is read, so the call completes once the headers arrive rather than
        // after buffering a body this validator never looks at.
        using HttpResponseMessage response = await httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return NoFailures;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(resource.ResourceName, member);
        }

        // Any other answer, a different 2xx included, is outside the contract this validator
        // speaks, so it is treated as a fault in the dependency rather than a fact about the
        // document: reporting it as a validation failure would tell a client something false about
        // their own data. Thrown instead, it reaches the host's catch-all and becomes a logged 500
        // that persists nothing; the only timeout on this call is the one this validator's own
        // HttpClient was configured with.
        throw new HttpRequestException(
            $"The external unique id system answered {(int)response.StatusCode} for a {resource.ResourceName} lookup.",
            inner: null,
            statusCode: response.StatusCode
        );
    }

    // The submitted value is deliberately not quoted back: a failure message reaches the 400
    // body, and keeping submitted data out of it is what lets a deployment log these messages if
    // it chooses to.
    private static IReadOnlyList<CustomValidationFailure> NotFound(
        string resourceName,
        string member
    ) =>
        [
            new CustomValidationFailure.OnPath(
                $"$.{member}",
                $"The {resourceName} unique id was not found in the external unique id system."
            ),
        ];

    private static string MemberFor(string resourceName) =>
        $"{char.ToLowerInvariant(resourceName[0])}{resourceName[1..]}UniqueId";

    // An empty list, never null. A null return is not a substitute for one and DMS treats it as a
    // hard failure.
    private static readonly IReadOnlyList<CustomValidationFailure> NoFailures = [];
}
// embed-region-end: validator
