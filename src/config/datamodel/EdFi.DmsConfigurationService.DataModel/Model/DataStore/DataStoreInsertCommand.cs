// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStore;

public class DataStoreInsertCommand
{
    public string DataStoreType { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ConnectionString { get; set; }

    public class Validator : AbstractValidator<DataStoreInsertCommand>
    {
        public Validator()
        {
            RuleFor(x => x.DataStoreType).NotEmpty().MaximumLength(50);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
            RuleFor(x => x.ConnectionString).MaximumLength(1000);
        }
    }
}
