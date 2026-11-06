// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// In-place corruptions of a finished zip, each aimed at one boundary where the .NET zip reader reports a
/// malformed container after the archive was opened: the central directory (parsed when the entries are first
/// enumerated), an entry's local header and compression method (checked when the entry is opened) and an
/// entry's deflate stream (checked when it is read). Offsets follow the zip format's fixed header layouts.
/// </summary>
public static class ZipLayout
{
    private const int LocalHeaderCompressionMethodOffset = 8;
    private const int LocalHeaderNameLengthOffset = 26;
    private const int LocalHeaderExtraLengthOffset = 28;
    private const int LocalHeaderNameOffset = 30;
    private const int CentralHeaderCompressionMethodOffset = 10;
    private const int CentralHeaderNameLengthOffset = 28;
    private const int CentralHeaderNameOffset = 46;
    private const int EndRecordCentralDirectoryOffsetOffset = 16;
    private const ushort DeflateMethod = 8;
    private const ushort UnsupportedMethod = 99;

    private static ReadOnlySpan<byte> LocalHeaderSignature => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> CentralHeaderSignature => [0x50, 0x4B, 0x01, 0x02];
    private static ReadOnlySpan<byte> EndRecordSignature => [0x50, 0x4B, 0x05, 0x06];

    /// <summary>Overwrites the first central directory header's signature; enumerating the entries fails.</summary>
    public static void CorruptCentralDirectory(byte[] zip)
    {
        int endRecord = zip.AsSpan().LastIndexOf(EndRecordSignature);
        if (endRecord < 0)
        {
            throw new InvalidOperationException("The zip has no end of central directory record.");
        }

        int centralDirectory = BinaryPrimitives.ReadInt32LittleEndian(
            zip.AsSpan(endRecord + EndRecordCentralDirectoryOffsetOffset)
        );
        zip.AsSpan(centralDirectory, CentralHeaderSignature.Length).Fill(0xFF);
    }

    /// <summary>Overwrites the entry's local header signature; opening the entry fails.</summary>
    public static void CorruptLocalHeader(byte[] zip, string entryName) =>
        zip.AsSpan(LocalHeaderOffset(zip, entryName), LocalHeaderSignature.Length).Fill(0xFF);

    /// <summary>Declares a compression method the reader does not support in the entry's central directory header; opening the entry fails.</summary>
    public static void SetUnsupportedCompressionMethod(byte[] zip, string entryName) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            zip.AsSpan(CentralHeaderOffset(zip, entryName) + CentralHeaderCompressionMethodOffset),
            UnsupportedMethod
        );

    /// <summary>
    /// Replaces the first byte of the entry's deflate stream with a final block of the reserved type, which the
    /// decompressor refuses on the first read.
    /// </summary>
    public static void CorruptDeflateStream(byte[] zip, string entryName)
    {
        int header = LocalHeaderOffset(zip, entryName);
        if (ReadUInt16(zip, header + LocalHeaderCompressionMethodOffset) != DeflateMethod)
        {
            throw new InvalidOperationException(
                "The entry is not deflate-compressed; there is no stream to corrupt."
            );
        }

        int data =
            header
            + LocalHeaderNameOffset
            + ReadUInt16(zip, header + LocalHeaderNameLengthOffset)
            + ReadUInt16(zip, header + LocalHeaderExtraLengthOffset);
        zip[data] = 0b0000_0111;
    }

    private static int LocalHeaderOffset(byte[] zip, string entryName) =>
        HeaderOffset(
            zip,
            LocalHeaderSignature,
            entryName,
            LocalHeaderNameLengthOffset,
            LocalHeaderNameOffset
        );

    private static int CentralHeaderOffset(byte[] zip, string entryName) =>
        HeaderOffset(
            zip,
            CentralHeaderSignature,
            entryName,
            CentralHeaderNameLengthOffset,
            CentralHeaderNameOffset
        );

    private static int HeaderOffset(
        byte[] zip,
        ReadOnlySpan<byte> signature,
        string entryName,
        int nameLengthOffset,
        int nameOffset
    )
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        int searchFrom = 0;
        while (true)
        {
            int found = zip.AsSpan(searchFrom).IndexOf(signature);
            if (found < 0)
            {
                throw new InvalidOperationException("The zip has no header for the named entry.");
            }

            int header = searchFrom + found;
            int nameLength = ReadUInt16(zip, header + nameLengthOffset);
            if (nameLength == name.Length && zip.AsSpan(header + nameOffset, nameLength).SequenceEqual(name))
            {
                return header;
            }

            searchFrom = header + signature.Length;
        }
    }

    private static ushort ReadUInt16(byte[] zip, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset));
}
