// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.Exceptions
{
    public abstract class BaseDetailedException : Exception, IDetailedException
    {
        public const string BaseTypePrefix = "urn:dms";

        private string? _type;

        protected BaseDetailedException(string detail, string message)
            : base(message)
        {
            Detail = detail;
        }

        protected BaseDetailedException(string detail, string message, Exception innerException)
            : base(message, innerException)
        {
            Detail = detail;
        }

        public string Detail { get; }

        public string? Type
        {
            get => _type ??= string.Join(':', GetTypeParts());
        }

        public abstract string Title { get; }

        public abstract int Status { get; }

        public string? CorrelationId { get; set; }

        Dictionary<string, string[]>? IDetailedException.ValidationErrors { get; set; }

        string[]? IDetailedException.Errors { get; set; }

        protected virtual IEnumerable<string> GetTypeParts()
        {
            yield return BaseTypePrefix;
        }
    }

}
