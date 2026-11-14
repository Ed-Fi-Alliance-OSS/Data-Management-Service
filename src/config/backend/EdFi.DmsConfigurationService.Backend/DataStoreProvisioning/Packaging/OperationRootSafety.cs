// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// Why an operation root was refused (<see cref="UnsafeOperationRootException.Reason"/>); fixed names only, never a path.
/// </summary>
public enum UnsafeOperationRootReason
{
    /// <summary>The root or one of its ancestors is a symbolic link, which another identity may be able to repoint.</summary>
    SymbolicLink,

    /// <summary>The root or one of its ancestors is not a directory.</summary>
    NotDirectory,

    /// <summary>The root or one of its ancestors is owned by an identity other than <c>root</c> and this process's effective user.</summary>
    UntrustedOwner,

    /// <summary>The root or one of its ancestors is writable by its group or by others without the sticky bit.</summary>
    ReplaceableEntries,

    /// <summary>The owner, type, or mode of the root or one of its ancestors could not be read.</summary>
    Unverifiable,
}

/// <summary>
/// The configured operation root or SQL Server staging directory, or the path to it, lets an identity other than
/// <c>root</c> and this process replace what the provisioner wrote there. The message names the reason only, never
/// the path.
/// </summary>
public sealed class UnsafeOperationRootException(UnsafeOperationRootReason reason)
    : IOException($"The directory or one of its ancestors is not safe to use ({reason}).")
{
    public UnsafeOperationRootReason Reason { get; } = reason;
}

/// <summary>
/// The Unix condition under which the private operation directory (D-5) keeps its contents: its <c>0700</c> mode stops
/// another identity from writing inside it, but not from renaming it, or an ancestor, away and putting another tree under
/// the same path, which <c>psql</c> would then open by name. Renaming an entry needs write permission on the directory
/// holding it, so the root and every ancestor up to <c>/</c> must be a real directory (not a symbolic link) owned by
/// <c>root</c> or this process's effective user, and writable by its group or others only with the sticky bit set,
/// which then lets only an entry's owner, the directory's owner, or <c>root</c> rename it. A correctly owned <c>/tmp</c>
/// (<c>1777</c>, owned by <c>root</c>) qualifies. A group is not assumed trusted: any member of a group with write
/// permission on a directory may rename the entries in it, so a group-writable directory without the sticky bit (what a
/// <c>umask</c> of <c>0002</c> creates) is refused. Windows is not checked here: the claim carries a protected access
/// control list, and the operator configures share and folder permissions. The owner and mode are read with
/// <c>statx</c> without following links. The SQL Server adapter applies the same condition to its staging directory,
/// which <c>RESTORE</c> opens by path after the backup is staged and re-hashed there.
/// </summary>
public static class OperationRootSafety
{
    private const int AtCurrentDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxMode = 0x2;
    private const uint StatxUserId = 0x8;
    private const int StatxSize = 256;
    private const int StatxMaskOffset = 0;
    private const int StatxUserIdOffset = 20;
    private const int StatxModeOffset = 28;
    private const int FileTypeMask = 0xF000;
    private const int DirectoryType = 0x4000;
    private const int SymbolicLinkType = 0xA000;
    private const int GroupOrOtherWrite = 0x12; // 0o022
    private const int StickyBit = 0x200; // 0o1000

    /// <summary>
    /// Throws <see cref="UnsafeOperationRootException"/> unless <paramref name="fullyQualifiedRoot"/> and each of its
    /// ancestors meet the condition above. Unix only. The caller checks the lexical, fully qualified form it then
    /// uses, so no <c>.</c> or <c>..</c> segment can reach another directory.
    /// </summary>
    public static void RequireSafe(string fullyQualifiedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedRoot);
        uint effectiveUserId = EffectiveUserId();
        string? current = Path.TrimEndingDirectorySeparator(fullyQualifiedRoot);
        while (!string.IsNullOrEmpty(current))
        {
            Check(current, effectiveUserId);
            current = Path.GetDirectoryName(current);
        }
    }

    private static void Check(string path, uint effectiveUserId)
    {
        byte[] buffer = new byte[StatxSize];
        int result;
        try
        {
            result = Statx(
                AtCurrentDirectory,
                path,
                AtSymlinkNoFollow,
                StatxType | StatxMode | StatxUserId,
                buffer
            );
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new UnsafeOperationRootException(UnsafeOperationRootReason.Unverifiable);
        }

        uint reported = MemoryMarshal.Read<uint>(buffer.AsSpan(StatxMaskOffset));
        uint required = StatxType | StatxMode | StatxUserId;
        if (result != 0 || (reported & required) != required)
        {
            throw new UnsafeOperationRootException(UnsafeOperationRootReason.Unverifiable);
        }

        int mode = MemoryMarshal.Read<ushort>(buffer.AsSpan(StatxModeOffset));
        uint owner = MemoryMarshal.Read<uint>(buffer.AsSpan(StatxUserIdOffset));
        UnsafeOperationRootReason? reason = Judge(mode, owner, effectiveUserId);
        if (reason is { } refused)
        {
            throw new UnsafeOperationRootException(refused);
        }
    }

    /// <summary>The rule over one entry's raw <c>st_mode</c> and owner; null when the entry is safe.</summary>
    internal static UnsafeOperationRootReason? Judge(int mode, uint owner, uint effectiveUserId)
    {
        int type = mode & FileTypeMask;
        if (type == SymbolicLinkType)
        {
            return UnsafeOperationRootReason.SymbolicLink;
        }

        if (type != DirectoryType)
        {
            return UnsafeOperationRootReason.NotDirectory;
        }

        if (owner != 0 && owner != effectiveUserId)
        {
            return UnsafeOperationRootReason.UntrustedOwner;
        }

        return (mode & GroupOrOtherWrite) != 0 && (mode & StickyBit) == 0
            ? UnsafeOperationRootReason.ReplaceableEntries
            : null;
    }

    private static uint EffectiveUserId()
    {
        try
        {
            return GetEffectiveUserId();
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new UnsafeOperationRootException(UnsafeOperationRootReason.Unverifiable);
        }
    }

#pragma warning disable SYSLIB1054 // Two small calls; do not enable unsafe compilation for the library.
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        [Out] byte[] buffer
    );

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
#pragma warning restore SYSLIB1054
}
