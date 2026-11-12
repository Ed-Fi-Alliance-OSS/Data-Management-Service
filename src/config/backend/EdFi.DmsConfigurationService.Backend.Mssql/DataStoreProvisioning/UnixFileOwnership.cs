// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// D-18 on Linux: whether a path is owned by this process's effective user. .NET reads a file's mode but not its
/// owner, so the owner is read with <c>statx</c>, whose structure layout the Linux kernel ABI fixes on every
/// architecture (glibc 2.28 and musl 1.2.5 export it), and compared with <c>geteuid</c>. Symbolic links are
/// followed, as the mode read is. Linux only; never called on Windows.
/// </summary>
internal static class UnixFileOwnership
{
    private const int AtCurrentDirectory = -100;
    private const uint StatxUserId = 0x8;
    private const int StatxSize = 256;
    private const int StatxMaskOffset = 0;
    private const int StatxUserIdOffset = 20;

    /// <summary>
    /// True when <paramref name="path"/> is owned by the effective user. Throws <see cref="IOException"/> when the
    /// owner cannot be read, also on a platform without <c>statx</c>, so the caller refuses instead of accepting.
    /// </summary>
    public static bool IsOwnedByEffectiveUser(string path)
    {
        byte[] buffer = new byte[StatxSize];
        int result;
        uint effectiveUserId;
        try
        {
            result = Statx(AtCurrentDirectory, path, 0, StatxUserId, buffer);
            effectiveUserId = GetEffectiveUserId();
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new IOException("The owner of the path cannot be read on this platform.", exception);
        }

        if (result != 0)
        {
            throw new IOException(
                $"Reading the owner of the path failed with errno {Marshal.GetLastPInvokeError()}."
            );
        }

        if ((MemoryMarshal.Read<uint>(buffer.AsSpan(StatxMaskOffset)) & StatxUserId) == 0)
        {
            throw new IOException("The file system did not report the owner of the path.");
        }

        return MemoryMarshal.Read<uint>(buffer.AsSpan(StatxUserIdOffset)) == effectiveUserId;
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
