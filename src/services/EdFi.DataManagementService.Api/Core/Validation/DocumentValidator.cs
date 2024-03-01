// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IDocumentValidator
{
    IEnumerable<string>? Validate(JsonNode? input);
}

public class ResourceDocumentValidator(ISchemaValidator schemaValidator) : IDocumentValidator
{
    public EvaluationOptions ValidatorEvaluationOptions
    { get => new() { OutputFormat = OutputFormat.List }; }

    public IEnumerable<string>? Validate(JsonNode? input)
    {
        var resourceSchemaValidator = schemaValidator.GetSchema();
        var results = resourceSchemaValidator.Evaluate(input, ValidatorEvaluationOptions);

        return PruneValidationErrors(results);

        List<string>? PruneValidationErrors(EvaluationResults results)
        {
            var allMessages = results.Details.Where(x => x.HasErrors).SelectMany(x => x.Errors == null ? [] : x.Errors.ToArray()).Select(x => x.Value).ToArray();

            var pruneMessages = results.Details.Where(x => x.HasErrors && x.EvaluationPath.Segments.Any() && x.EvaluationPath.Segments[^1] == "additionalProperties").Select(x => $"Overpost at {x.InstanceLocation}").ToArray();

            // Remove the unhelpful messages
            return allMessages.Where(x => x != "All values fail against the false schema").Concat(pruneMessages).ToList();
        }
    }
}

public class DescriptorDocumentValidator : IDocumentValidator
{
    public IEnumerable<string> Validate(JsonNode? input)
    {
        throw new NotImplementedException();
    }
}
