// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;

internal static class FrontendDataStoreManageQuery
{
    public static (DataStoreManageQuery Query, List<ValidationFailure> Errors) Parse(IQueryCollection input)
    {
        List<ValidationFailure> errors = [];
        var query = new DataStoreManageQuery
        {
            Offset = Integer("offset") ?? 0,
            Limit = Integer("limit") ?? 25,
            Id = Integer("id"),
            Name = Text("name"),
            OrderBy = Text("orderBy") ?? "id",
            Direction = Text("direction") ?? "Descending",
        };
        errors.AddRange(new DataStoreManageQuery.Validator().Validate(query).Errors);
        return (query, errors);

        string? Text(string field)
        {
            if (!input.TryGetValue(field, out var values))
            {
                return null;
            }
            if (values.Count != 1)
            {
                errors.Add(new(field, $"'{field}' must have exactly one value."));
                return null;
            }
            return values[0] ?? "";
        }

        int? Integer(string field)
        {
            string? text = Text(field);
            if (text is null)
            {
                return null;
            }
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
            errors.Add(new(field, $"'{field}' must be an int32 value."));
            return null;
        }
    }
}
