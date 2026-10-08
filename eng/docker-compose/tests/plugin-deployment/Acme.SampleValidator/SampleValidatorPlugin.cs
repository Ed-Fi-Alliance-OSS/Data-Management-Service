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

namespace Acme.SampleValidator;

/// <summary>
/// The smallest plugin a real Data Management Service will accept.
/// </summary>
/// <remarks>
/// It has to contribute something the host's contract registry declares: under the production
/// registry a plugin that registers no declared contract fails the surviving-contribution check and
/// aborts startup. <see cref="ICustomResourceValidator"/> is the one contract that registry declares,
/// and it is fan-in, so contributing an implementation is all this needs to do.
/// </remarks>
public sealed class SampleValidatorPlugin : EdFiApiPlugin
{
    public override string Name => "Acme.SampleValidator";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, SampleResourceValidator>()
        );
    }
}

/// <summary>
/// A validator that passes everything.
/// </summary>
/// <remarks>
/// The deployment proof is about loading and composition: that the inventory event names this plugin
/// with its version and the digests of the files it shipped, and that the registration resolves.
/// Making a request fail is a different story's proof and needs a validator that rejects something.
/// </remarks>
public sealed class SampleResourceValidator : ICustomResourceValidator
{
    public IReadOnlyList<ValidatedResource> AppliesTo { get; } = [new ValidatedResource("Ed-Fi", "School")];

    public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
}
