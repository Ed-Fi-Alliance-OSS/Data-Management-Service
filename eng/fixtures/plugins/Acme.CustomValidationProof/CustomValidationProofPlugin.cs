// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.CustomValidationProof;

/// <summary>
/// The plugin that carries the rejecting validator the end-to-end proof drives over HTTP.
/// </summary>
/// <remarks>
/// It reads no configuration, so nothing the proof asserts can depend on a settings key. The
/// registration is the fan-in shape the host's startup guard accepts: a transient added through
/// TryAddEnumerable, which is what lets several plugins each contribute one validator.
/// </remarks>
public sealed class CustomValidationProofPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.CustomValidationProof";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, ProofResourceValidator>()
        );
    }
}

/// <summary>
/// Rejects a document that carries one of two reserved tokens as the value of a top-level string
/// property, and passes everything else.
/// </summary>
/// <remarks>
/// <para>
/// Document-only, no I/O, and no constructor dependencies, which is the shape the design's second
/// driving scenario needs from the contract. It is deliberately not that scenario's business rule:
/// a reserved token has no meaning to any data standard, so nothing here can be mistaken for a rule
/// DMS ships.
/// </para>
/// <para>
/// The rule is resource-agnostic on purpose, and that is what makes the applicability proof sharp.
/// <see cref="AppliesTo"/> is the only thing standing between a token and a rejection, so a request
/// carrying the token to a resource this validator does not declare must succeed; if fan-in
/// filtering ever regressed and ran every validator for every resource, that request would start
/// returning 400 and the test would fail. A rule keyed to a field only one resource has could not
/// express that, because the other resource's document would pass either way.
/// </para>
/// <para>
/// Both arms of the failure contract are reachable: the path token produces the
/// <c>validationErrors</c> arm and the resource token produces the <c>errors</c> arm, so the proof
/// covers both response factories over real HTTP rather than only the one the parity assertion
/// needs.
/// </para>
/// </remarks>
public sealed class ProofResourceValidator : ICustomResourceValidator
{
    /// <summary>The value that makes this validator report a path-level failure.</summary>
    public const string RejectOnPathToken = "custom-validation-proof-reject-path";

    /// <summary>The value that makes this validator report a resource-level failure.</summary>
    public const string RejectOnResourceToken = "custom-validation-proof-reject-resource";

    public IReadOnlyList<ValidatedResource> AppliesTo { get; } = [new ValidatedResource("Ed-Fi", "Student")];

    public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(document);

        List<CustomValidationFailure> failures = [];

        // Top-level only, and string values only. The document reaching a validator carries a
        // server-assigned "_lastModifiedDate" the client never sent, so a rule that inspected values
        // more broadly would have to reason about it; reading only for an exact match against two
        // reserved tokens means no injected value can ever trigger this.
        if (document is JsonObject root)
        {
            // Document order, so a document carrying both tokens reports them in the order it wrote
            // them rather than in whatever order a hash bucket happens to yield.
            foreach (KeyValuePair<string, JsonNode?> property in root)
            {
                if (property.Value is not JsonValue value || !value.TryGetValue(out string? text))
                {
                    continue;
                }

                if (string.Equals(text, RejectOnPathToken, StringComparison.Ordinal))
                {
                    failures.Add(
                        new CustomValidationFailure.OnPath(
                            $"$.{property.Key}",
                            "This value is the custom-validation proof fixture's reserved rejection token."
                        )
                    );
                }
                else if (string.Equals(text, RejectOnResourceToken, StringComparison.Ordinal))
                {
                    failures.Add(
                        new CustomValidationFailure.OnResource(
                            "This document carries the custom-validation proof fixture's reserved "
                                + "document-level rejection token."
                        )
                    );
                }
            }
        }

        return Task.FromResult<IReadOnlyList<CustomValidationFailure>>(failures);
    }
}
