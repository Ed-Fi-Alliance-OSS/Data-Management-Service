// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;

/// <summary>Managed paging deliberately does not inherit the ordinary catalog's PagingQuery.</summary>
public sealed class DataStoreManageQuery
{
    public int Offset { get; set; }
    public int Limit { get; set; } = 25;
    public int? Id { get; set; }
    public string? Name { get; set; }
    private string _orderBy = "id";
    public string OrderBy
    {
        get => _orderBy;
        set => _orderBy = value == "" ? "id" : value;
    }
    public string Direction { get; set; } = "Descending";

    public static bool IsSortField(string? value) =>
        value
            is "id"
                or "name"
                or "dataStoreId"
                or "dataStoreName"
                or "status"
                or "databaseTemplate"
                or "databaseName"
                or "lastRefreshed"
                or "lastModifiedDate";

    public sealed class Validator : AbstractValidator<DataStoreManageQuery>
    {
        public Validator()
        {
            RuleFor(x => x.Offset).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Limit).GreaterThan(0);
            RuleFor(x => x.OrderBy)
                .Must(IsSortField)
                .WithMessage("OrderBy must be a managed data store field.");
            RuleFor(x => x.Direction)
                .Must(value => value is "Ascending" or "Descending")
                .WithMessage("Direction must be 'Ascending' or 'Descending'.");
        }
    }
}
