// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The implementer-owned options type the two samples share. It is embedded in
// CUSTOM-VALIDATION.md as its own region rather than folded into either sample, because a reader
// copying the validator needs this declaration too and a guide that showed two classes depending
// on an undeclared third would not compile where it is read.
//
// It names no Ed-Fi type and needs no package: an options type is ordinary implementer code, which
// is the point worth showing.

// embed-region: options
namespace Acme.Dms.StudentIdentity;

public sealed class StudentIdentityOptions
{
    /// <summary>
    /// The prefix every student unique id in this deployment is required to carry.
    /// </summary>
    public string RequiredPrefix { get; set; } = string.Empty;
}
// embed-region-end: options
