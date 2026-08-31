// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStore;

public class DataStoreInsertCommand
{
    public string DataStoreType { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ConnectionString { get; set; }
    public string? Provider { get; set; }

    public class Validator : AbstractValidator<DataStoreInsertCommand>
    {
        public Validator(IDataStoreConnectionStringValidator connectionStringValidator)
        {
            RuleFor(x => x.DataStoreType).NotEmpty().MaximumLength(50);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
            RuleFor(x => x.ConnectionString).ApplyDataStoreConnectionStringRules(connectionStringValidator);
            RuleFor(x => x.Provider)
                .MaximumLength(50)
                .Must(IsSupportedProvider)
                .WithMessage("Provider must be 'postgresql' or 'sqlserver'.");
        }

        private static bool IsSupportedProvider(string? provider) =>
            string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase);
    }
}
