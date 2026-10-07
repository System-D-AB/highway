using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;
using Highway.Abstractions;

namespace Highway.Client.Wire;

/// <summary>
/// Decodes dead-letter entry wire framing into strongly-typed <see cref="DeadLetter{T}"/> (feature 073).
/// </summary>
internal static class DeadLetterWire
{
    private const byte FormatVersion = 0xFF;
    private const int HeaderSize = 8 + 2 + 2;
    private const uint LineageMagic = 0xFE11EA9E;   // [u16 requeues][u32 magic] — see the protocol file
    private const int LineageTrailerLen = 6;
    private const uint FailureMagic = 0xFE15FA11;
    private const int FailureTrailer = 8;
    private const int FailureBlockHeader = 8;
    private const int RpcHeader = 1 + 2 + 2;

    private static readonly ConcurrentDictionary<Type, Func<object?, ReadOnlyMemory<byte>, string, string, DeadLetterTarget, int, int, DateTimeOffset, string, FailureInfo?, FailureInfo?, object>> Factories = new();

    public static object DecodeDeadLetter(
        Type contractType,
        ReadOnlySpan<byte> rawRecord,
        string deadLetterId,
        DeadLetterTargetKind targetKind,
        string targetName,
        string? group)
    {
        if (rawRecord.Length < HeaderSize)
            throw new InvalidDataException($"Dead-letter entry too short ({rawRecord.Length} bytes); minimum is {HeaderSize}.");

        var deadLetteredTicks = BinaryPrimitives.ReadInt64BigEndian(rawRecord[..8]);
        var attempts = BinaryPrimitives.ReadUInt16BigEndian(rawRecord.Slice(8, 2));
        var reasonLen = BinaryPrimitives.ReadUInt16BigEndian(rawRecord.Slice(10, 2));

        if (rawRecord.Length < HeaderSize + reasonLen)
            throw new InvalidDataException($"Dead-letter entry truncated: reason length {reasonLen} but only {rawRecord.Length - HeaderSize} bytes remain.");

        var reason = Encoding.UTF8.GetString(rawRecord.Slice(HeaderSize, reasonLen));
        var originalEntry = rawRecord.Slice(HeaderSize + reasonLen);

        ushort requeues = 0;
        var withoutLineage = originalEntry;
        if (originalEntry.Length >= LineageTrailerLen
            && BinaryPrimitives.ReadUInt32BigEndian(originalEntry.Slice(originalEntry.Length - 4)) == LineageMagic)
        {
            requeues = BinaryPrimitives.ReadUInt16BigEndian(originalEntry.Slice(originalEntry.Length - LineageTrailerLen));
            withoutLineage = originalEntry[..^LineageTrailerLen];
        }

        FailureInfo? lastFailure = null;
        FailureInfo? firstFailure = null;
        var withoutFailureBlock = withoutLineage;
        if (withoutLineage.Length >= FailureTrailer && BinaryPrimitives.ReadUInt32BigEndian(withoutLineage.Slice(withoutLineage.Length - 4)) == FailureMagic)
        {
            var blockLen = (int)BinaryPrimitives.ReadUInt32BigEndian(withoutLineage.Slice(withoutLineage.Length - 8));
            if (blockLen <= withoutLineage.Length - FailureTrailer)
            {
                var blockStart = withoutLineage.Length - FailureTrailer - blockLen;
                var block = withoutLineage.Slice(blockStart, blockLen);
                withoutFailureBlock = withoutLineage[..blockStart];
                if (block.Length >= FailureBlockHeader)
                {
                    var typeLen = BinaryPrimitives.ReadUInt16BigEndian(block[..2]);
                    var firstLen = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(2, 2));
                    var detailLen = (int)BinaryPrimitives.ReadUInt32BigEndian(block.Slice(4, 4));
                    if (block.Length >= FailureBlockHeader + typeLen + firstLen + detailLen)
                    {
                        var typeStr = Encoding.UTF8.GetString(block.Slice(FailureBlockHeader, typeLen));
                        var firstStr = firstLen > 0 ? Encoding.UTF8.GetString(block.Slice(FailureBlockHeader + typeLen, firstLen)) : null;
                        var detailStr = Encoding.UTF8.GetString(block.Slice(FailureBlockHeader + typeLen + firstLen, detailLen));
                        lastFailure = new FailureInfo(typeStr, detailStr);
                        if (firstStr is not null)
                            firstFailure = new FailureInfo(firstStr, detailStr);
                    }
                }
            }
        }

        if (withoutFailureBlock.Length < RpcHeader || withoutFailureBlock[0] != FormatVersion)
            throw new InvalidDataException("Entry is in an unsupported or corrupted storage format.");

        var idLen = BinaryPrimitives.ReadUInt16BigEndian(withoutFailureBlock.Slice(3, 2));
        if (withoutFailureBlock.Length < RpcHeader + idLen)
            throw new InvalidDataException($"Entry truncated: id length {idLen} exceeds available bytes {withoutFailureBlock.Length - RpcHeader}.");

        var idStr = Encoding.UTF8.GetString(withoutFailureBlock.Slice(RpcHeader, idLen));
        var userPayload = withoutFailureBlock.Slice(RpcHeader + idLen);

        object? message = null;
        try
        {
            var env = HighwayJson.DecodeEnvelope(userPayload);
            message = HighwayJson.DeserializeBody(env, contractType);
        }
        catch
        {
            // Deserialization failure leaves message = null (R1 AC2, R4 AC4)
            message = null;
        }

        var target = new DeadLetterTarget(targetKind, targetName, targetKind == DeadLetterTargetKind.Group ? group : null);
        var factory = GetFactory(contractType);

        return factory(
            message,
            userPayload.ToArray(),
            idStr,
            deadLetterId,
            target,
            attempts,
            requeues,
            new DateTimeOffset(deadLetteredTicks, TimeSpan.Zero),
            reason,
            lastFailure,
            firstFailure);
    }

    private static Func<object?, ReadOnlyMemory<byte>, string, string, DeadLetterTarget, int, int, DateTimeOffset, string, FailureInfo?, FailureInfo?, object> GetFactory(Type contractType)
    {
        return Factories.GetOrAdd(contractType, static type =>
        {
            var deadLetterType = typeof(DeadLetter<>).MakeGenericType(type);
            var ctor = deadLetterType.GetConstructors()[0];

            var msgParam = Expression.Parameter(typeof(object), "msg");
            var rawParam = Expression.Parameter(typeof(ReadOnlyMemory<byte>), "raw");
            var idParam = Expression.Parameter(typeof(string), "id");
            var deadIdParam = Expression.Parameter(typeof(string), "deadId");
            var targetParam = Expression.Parameter(typeof(DeadLetterTarget), "target");
            var attemptsParam = Expression.Parameter(typeof(int), "attempts");
            var requeuesParam = Expression.Parameter(typeof(int), "requeues");
            var dtParam = Expression.Parameter(typeof(DateTimeOffset), "dt");
            var reasonParam = Expression.Parameter(typeof(string), "reason");
            var lastFailParam = Expression.Parameter(typeof(FailureInfo), "lastFail");
            var firstFailParam = Expression.Parameter(typeof(FailureInfo), "firstFail");

            var castMsg = Expression.Convert(msgParam, type);

            var newExpr = Expression.New(ctor,
                castMsg, rawParam, idParam, deadIdParam, targetParam,
                attemptsParam, requeuesParam, dtParam, reasonParam,
                lastFailParam, firstFailParam);

            return Expression.Lambda<Func<object?, ReadOnlyMemory<byte>, string, string, DeadLetterTarget, int, int, DateTimeOffset, string, FailureInfo?, FailureInfo?, object>>(
                newExpr, msgParam, rawParam, idParam, deadIdParam, targetParam,
                attemptsParam, requeuesParam, dtParam, reasonParam,
                lastFailParam, firstFailParam).Compile();
        });
    }
}
