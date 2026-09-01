using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Lumio.Server.Account;

internal sealed class LumioBinWriter
{
    private readonly List<byte> bytes = new(128);

    public byte[] ToArray() => bytes.ToArray();

    public void WriteU8(byte value) => bytes.Add(value);

    public void WriteU16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        Add(buffer);
    }

    public void WriteU64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Add(buffer);
    }

    public void WriteAscii(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var payload = Encoding.ASCII.GetBytes(value);
        WriteU32((uint)payload.Length);
        bytes.AddRange(payload);
    }

    public void WriteFixedBytes(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            bytes.Add(b);
        }
    }

    private void WriteU32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Add(buffer);
    }

    private void Add(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            bytes.Add(b);
        }
    }
}

internal sealed class LumioBinReader
{
    private readonly byte[] bytes;
    private int offset;

    public LumioBinReader(byte[] bytes)
    {
        this.bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }

    public int Remaining => bytes.Length - offset;

    public bool TryReadU8(out byte value)
    {
        value = 0;
        if (Remaining < 1)
        {
            return false;
        }

        value = bytes[offset++];
        return true;
    }

    public bool TryReadU16(out ushort value)
    {
        value = 0;
        if (Remaining < 2)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        offset += 2;
        return true;
    }

    public bool TryReadU64(out ulong value)
    {
        value = 0;
        if (Remaining < 8)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        offset += 8;
        return true;
    }

    public bool TryReadAscii(out string value)
    {
        value = string.Empty;
        if (Remaining < 4)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        if (length > (uint)Remaining)
        {
            return false;
        }

        var slice = bytes.AsSpan(offset, (int)length);
        for (var i = 0; i < slice.Length; i++)
        {
            if (slice[i] > 0x7F)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(slice);
        offset += (int)length;
        return true;
    }

    public bool TryReadFixedBytes(int length, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (length < 0 || Remaining < length)
        {
            return false;
        }

        value = bytes.AsSpan(offset, length).ToArray();
        offset += length;
        return true;
    }
}
