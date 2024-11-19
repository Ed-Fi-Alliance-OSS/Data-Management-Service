// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Validation;

internal interface IDescriptorValidator
{
    /// <summary>
    /// Validates a descriptor document
    /// </summary>
    /// <returns></returns>
    bool Validate(PipelineContext context);
}

internal class DescriptorValidator : IDescriptorValidator
{
    public bool Validate(PipelineContext context)
    {
        var namespaceNode = context.ParsedBody["namespace"];
        if (namespaceNode == null)
        {
            throw new InvalidOperationException("Unexpected null namespace node");
        }

        var namespaceString = namespaceNode.GetValue<string>().ToLower();

        bool isValid =
            namespaceString.StartsWith("uri://")
            && namespaceString.EndsWith($"/{context.ResourceSchema.ResourceName.Value.ToLower()}");

        if (isValid)
        {
            isValid = Uri.IsWellFormedUriString(namespaceString, UriKind.Absolute);

        }

        return isValid;
    }
}
