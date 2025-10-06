// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;

public class DmsInstanceRouteContextUpdateCommand
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public string ContextKey { get; set; } = "";
    public string ContextValue { get; set; } = "";

    public class Validator : AbstractValidator<DmsInstanceRouteContextUpdateCommand>
    {
        public Validator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.InstanceId).NotEmpty().GreaterThan(0);
            RuleFor(x => x.ContextKey).NotEmpty().MaximumLength(256);
            RuleFor(x => x.ContextValue).NotEmpty().MaximumLength(256);
        }
    }
}
