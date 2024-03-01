// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.Validation;

public interface IDocumentValidatorResolver
{
    IDocumentValidator Resolve(ValidatorContext? validatorContext);
}

public class DocumentValidatorResolver(ISchemaValidatorResolver schemaValidatorResolver) : IDocumentValidatorResolver
{
    public IDocumentValidator Resolve(ValidatorContext? validatorContext)
    {
        if (validatorContext != null)
        {
            if (!validatorContext.IsDescriptor)
            {
                var schemaValidator = schemaValidatorResolver.Resolve(validatorContext);
                var resourceValidator = new ResourceDocumentValidator(schemaValidator);
                return resourceValidator;
            }
            else
            {
                throw new InvalidOperationException();
            }
        }
        else
        {
            throw new NotImplementedException();
        }
    }
}
