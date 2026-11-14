// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DataStoreManageInsertCommand
{
    private string? _name;
    public string? Name
    {
        get => _name;
        set => _name = value is null ? null : CatalogName.Trim(value);
    }
    public string? DatabaseTemplate { get; set; }

    public sealed class Validator : AbstractValidator<DataStoreManageInsertCommand>
    {
        public Validator()
        {
            RuleFor(x => x.Name)
                .Must(CatalogName.IsValidManagedName)
                .WithMessage(
                    "Name must contain 1–46 ASCII letters, digits, spaces or underscores after trimming."
                );
            RuleFor(x => x.DatabaseTemplate)
                .Must(value => value is "Minimal" or "Sample")
                .WithMessage("DatabaseTemplate must be 'Minimal' or 'Sample'.");
        }
    }
}
