// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceDerivative;

/// <summary>
/// Command to update an existing DMS instance derivative
/// </summary>
public class DmsInstanceDerivativeUpdateCommand
{
    /// <summary>
    /// The unique identifier of the derivative instance to update
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The parent DMS instance ID
    /// </summary>
    public long InstanceId { get; set; }

    /// <summary>
    /// The type of derivative: "ReadReplica" or "Snapshot"
    /// </summary>
    public string DerivativeType { get; set; } = "";

    /// <summary>
    /// The connection string for the derivative instance
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Validator for DmsInstanceDerivativeUpdateCommand
    /// </summary>
    public class Validator : AbstractValidator<DmsInstanceDerivativeUpdateCommand>
    {
        private static readonly string[] ValidDerivativeTypes = ["ReadReplica", "Snapshot"];

        public Validator()
        {
            RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than 0.");

            RuleFor(x => x.InstanceId).GreaterThan(0).WithMessage("InstanceId must be greater than 0.");

            RuleFor(x => x.DerivativeType)
                .NotEmpty()
                .WithMessage("DerivativeType is required.")
                .MaximumLength(50)
                .WithMessage("DerivativeType must be 50 characters or fewer.")
                .Must(type => ValidDerivativeTypes.Contains(type))
                .WithMessage("DerivativeType must be either 'ReadReplica' or 'Snapshot'.");

            RuleFor(x => x.ConnectionString)
                .MaximumLength(1000)
                .WithMessage("ConnectionString must be 1000 characters or fewer.");
        }
    }
}
