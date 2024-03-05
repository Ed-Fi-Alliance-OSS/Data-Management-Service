// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.Exceptions
{
    /// <summary>
    /// Bad request exception type for data validation
    /// </summary>
    public class BadRequestDataException : BadRequestException
    {
        private const string TypePart = "data";
        private const string TitleText = "Data Validation Failed";

        public BadRequestDataException(string detail)
            : base(detail) { }

        public BadRequestDataException(string detail, Dictionary<string, string[]> validationErrors)
            : base(detail)
        {
            ((IDetailedException)this).ValidationErrors = validationErrors;
        }

        public BadRequestDataException(string detail, string[] errors)
            : base(detail)
        {
            ((IDetailedException)this).Errors = errors;
        }

        public override string Title { get => TitleText; }

        protected override IEnumerable<string> GetTypeParts()
        {
            foreach (var part in base.GetTypeParts())
            {
                yield return part;
            }

            yield return TypePart;
        }
    }
}
