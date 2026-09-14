using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace Stow.Storage.Engine;

/// <summary>
/// Custom merge operator for uint64 counters (design § 4, R4.6).
/// Each operand is a signed int64 delta. FullMerge starts from the existing value
/// (or 0 if absent) and applies all deltas. PartialMerge combines two deltas by addition.
///
/// Key format: cnt|{collection_name} in the catalog CF.
///
/// RocksDbSharp does not have a built-in uint64-add merge operator, so we implement
/// it using MergeOperators.Create.
/// </summary>
public static class CounterMergeOperator
{
    /// <summary>Key prefix for counter keys in the catalog CF.</summary>
    public static readonly byte[] CounterPrefix = System.Text.Encoding.UTF8.GetBytes("cnt|");

    /// <summary>The name persisted to SST metadata — treat as a versioning string.</summary>
    public const string OperatorName = "StowI64Add";

    /// <summary>
    /// Builds the counter key for a collection.
    /// </summary>
    public static byte[] BuildCounterKey(string collectionName)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(collectionName);
        var key = new byte[CounterPrefix.Length + nameBytes.Length];
        CounterPrefix.CopyTo(key, 0);
        nameBytes.CopyTo(key, CounterPrefix.Length);
        return key;
    }

    /// <summary>
    /// Encodes a delta as an 8-byte big-endian int64 operand.
    /// +1 for insert/save(new), -1 for delete.
    /// </summary>
    public static byte[] EncodeDelta(long delta)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, delta);
        return bytes;
    }

    /// <summary>
    /// Decodes a counter value from the stored bytes.
    /// </summary>
    public static long DecodeCounter(byte[] value)
    {
        if (value == null || value.Length < 8)
            return 0;
        return BinaryPrimitives.ReadInt64BigEndian(value);
    }

    /// <summary>
    /// Decodes a counter value from a span.
    /// </summary>
    public static long DecodeCounter(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8)
            return 0;
        return BinaryPrimitives.ReadInt64BigEndian(value);
    }

    private static readonly MergeOperator SharedOperator = MergeOperators.Create(
        OperatorName,
        partialMerge: (ReadOnlySpan<byte> key, MergeOperators.OperandsEnumerator operands, out bool success) =>
        {
            long sum = 0;
            for (int i = 0; i < operands.Count; i++)
            {
                var op = operands.Get(i);
                if (op.Length >= 8)
                    sum += BinaryPrimitives.ReadInt64BigEndian(op);
            }
            success = true;
            var result = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(result, sum);
            return result;
        },
        fullMerge: (ReadOnlySpan<byte> key, bool hasExisting, ReadOnlySpan<byte> existing,
                    MergeOperators.OperandsEnumerator operands, out bool success) =>
        {
            long current = 0;
            if (hasExisting && existing.Length >= 8)
                current = BinaryPrimitives.ReadInt64BigEndian(existing);

            for (int i = 0; i < operands.Count; i++)
            {
                var op = operands.Get(i);
                if (op.Length >= 8)
                    current += BinaryPrimitives.ReadInt64BigEndian(op);
            }
            success = true;
            var result = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(result, current);
            return result;
        });

    /// <summary>
    /// Creates the RocksDbSharp MergeOperator for use on the catalog column family.
    /// Register this when creating/opening the database.
    /// </summary>
    public static MergeOperator CreateMergeOperator() => SharedOperator;


}
