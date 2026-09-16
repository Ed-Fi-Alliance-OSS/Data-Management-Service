// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// What a third party writes: an implementation of the packaged validator contract, compiled
// against the packed nupkg rather than against anything in this repository.
//
// The region below is mirrored verbatim into the packed readme and a check compares the two, so
// the sample an implementer copies is one that has been compiled. It therefore carries its own
// usings, its own namespace, and every type it names except StudentIdentityOptions, which the
// readme embeds as its own region beside this one.
//
// The rule is deliberately a real one that needs nothing outside the process. A sample that
// pretended to call an external identity service would have to fake the call, and a faked call is
// the one thing a compiling sample cannot prove. The cost rules a validator doing real I/O has to
// design around are stated in the readme's prose instead, where they can be stated accurately.

// embed-region: validator
using System.Text.Json.Nodes;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Options;

namespace Acme.Dms.StudentIdentity;

public sealed class StudentIdentityValidator : ICustomResourceValidator
{
    private readonly StudentIdentityOptions _options;

    // Trivial by obligation, not by taste: DMS resolves every registered validator on every write
    // request before it reads any AppliesTo, so this constructor runs for writes to resources this
    // validator has nothing to say about. Reading IOptions<T>.Value is the whole of it.
    public StudentIdentityValidator(IOptions<StudentIdentityOptions> options)
    {
        _options = options.Value;
    }

    // Built once and handed back by reference. This getter is read on every write request for
    // every registered validator, before any filtering, so it must stay this cheap.
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
        // Observed and allowed to propagate. A validator must not catch this and report a
        // validation failure instead: on a request the client has already aborted, DMS rethrows it
        // rather than turning it into a 500, and swallowing it would answer 400 for a request that
        // no longer has a caller.
        cancellationToken.ThrowIfCancellationRequested();

        // The document is read, never written. The parameter is a JsonNode and nothing in the type
        // system stops a validator mutating it; not doing so is a contract rule.
        string? studentUniqueId = document["studentUniqueId"]?.GetValue<string>();

        // Absent rather than wrong. This is the profile-effective body, so a writable profile that
        // does not name studentUniqueId leaves it out of what this method receives, and a rule that
        // reported a failure here would reject every write made through such a profile. There is
        // nothing to check, so nothing is reported.
        if (string.IsNullOrEmpty(studentUniqueId))
        {
            return NoFailures;
        }

        if (studentUniqueId.StartsWith(_options.RequiredPrefix, StringComparison.Ordinal))
        {
            return NoFailures;
        }

        // The submitted value is deliberately not quoted back. A failure message reaches the 400
        // body, and keeping submitted data out of it is what lets a deployment log these messages
        // if it chooses to.
        return Task.FromResult<IReadOnlyList<CustomValidationFailure>>([
            new CustomValidationFailure.OnPath(
                "$.studentUniqueId",
                $"A {resource.ResourceName} unique id must begin with "
                    + $"'{_options.RequiredPrefix}' in this deployment."
            ),
        ]);
    }

    // An empty list, never null. A null return is not a substitute for one and DMS treats it as a
    // hard failure.
    private static Task<IReadOnlyList<CustomValidationFailure>> NoFailures { get; } =
        Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
}
// embed-region-end: validator
