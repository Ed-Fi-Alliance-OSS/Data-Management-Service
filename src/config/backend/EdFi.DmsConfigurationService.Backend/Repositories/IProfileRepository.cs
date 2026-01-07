// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IProfileRepository
{
    Task<ProfileInsertResult> InsertProfile(ProfileInsertCommand command);
    Task<ProfileUpdateResult> UpdateProfile(ProfileUpdateCommand command);
    Task<ProfileGetResult> GetProfile(long id);
    Task<IEnumerable<ProfileGetResult>> QueryProfiles(PagingQuery query);
    Task<ProfileDeleteResult> DeleteProfile(long id);
}

public abstract record ProfileGetResult
{
    public record Success(ProfileResponse Profile) : ProfileGetResult;
    public record FailureNotFound : ProfileGetResult;
    public record FailureUnknown(string Message) : ProfileGetResult;
}


// Result types for ProfileRepository operations
public abstract record ProfileInsertResult
{
    public record Success(long Id) : ProfileInsertResult;
    public record FailureDuplicateName(string ProfileName) : ProfileInsertResult;
    public record FailureUnknown(string Message) : ProfileInsertResult;
}

public abstract record ProfileUpdateResult
{
    public record Success : ProfileUpdateResult;
    public record FailureDuplicateName(string ProfileName) : ProfileUpdateResult;
    public record FailureNotExists(long Id) : ProfileUpdateResult;
    public record FailureUnknown(string Message) : ProfileUpdateResult;
}

public abstract record ProfileDeleteResult
{
    public record Success : ProfileDeleteResult;
    public record FailureInUse(long Id) : ProfileDeleteResult;
    public record FailureNotExists(long Id) : ProfileDeleteResult;
    public record FailureUnknown(string Message) : ProfileDeleteResult;
}
