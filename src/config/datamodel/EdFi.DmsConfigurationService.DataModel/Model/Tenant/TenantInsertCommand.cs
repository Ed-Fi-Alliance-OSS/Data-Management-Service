// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.Tenant;

public class TenantInsertCommand
{
    public string Name { get; set; } = "";

    public class Validator : AbstractValidator<TenantInsertCommand>
    {
        public Validator()
        {
            RuleFor(t => t.Name).NotEmpty().MaximumLength(256);
        }
    }
}
