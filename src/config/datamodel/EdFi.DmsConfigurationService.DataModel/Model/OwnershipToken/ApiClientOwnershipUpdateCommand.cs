// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;

public class ApiClientOwnershipUpdateCommand
{
    private const int MinimumOwnershipTokenId = 1;
    private const int MaximumOwnershipTokenId = 32767;
    private const int MaximumOwnershipTokenCount = 1999;

    public int ApiClientId { get; set; }
    public int? CreatorOwnershipTokenId { get; set; }
    public required int[] OwnershipTokenIds { get; set; } = [];

    public class Validator : AbstractValidator<ApiClientOwnershipUpdateCommand>
    {
        public Validator()
        {
            RuleFor(a => a.ApiClientId).GreaterThan(0);
            RuleFor(a => a.CreatorOwnershipTokenId)
                .InclusiveBetween(MinimumOwnershipTokenId, MaximumOwnershipTokenId)
                .When(a => a.CreatorOwnershipTokenId.HasValue);
            RuleFor(a => a.OwnershipTokenIds)
                .Cascade(CascadeMode.Stop)
                .NotNull()
                .Must(ids => ids.Length <= MaximumOwnershipTokenCount)
                .WithMessage(
                    $"OwnershipTokenIds cannot contain more than {MaximumOwnershipTokenCount} values."
                )
                .Must(ids => ids.Distinct().Count() == ids.Length)
                .WithMessage("OwnershipTokenIds cannot contain duplicate values.")
                .Must(ids => Array.TrueForAll(ids, IsValidOwnershipTokenId))
                .WithMessage(
                    $"OwnershipTokenIds values must be between {MinimumOwnershipTokenId} and {MaximumOwnershipTokenId}."
                );
        }

        private static bool IsValidOwnershipTokenId(int id) =>
            id >= MinimumOwnershipTokenId && id <= MaximumOwnershipTokenId;
    }
}
