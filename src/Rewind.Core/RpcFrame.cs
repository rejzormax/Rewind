using System.Buffers.Binary;
using System.Text.Json;

namespace Rewind.Core;

public sealed record RpcMessage(int Opcode, byte[] Payload);

public static class RpcFrame
{
    public const int MaxPayloadBytes = 1024 * 1024;

    public static byte[] Encode(int opcode, object payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }

    public static async Task<RpcMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (opcode is < 0 or > 4 || size is < 0 or > MaxPayloadBytes)
            throw new InvalidDataException("Invalid Discord IPC frame.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token);
        return new RpcMessage(opcode, body);
    }

    public static byte[] EchoPong(byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }
}
