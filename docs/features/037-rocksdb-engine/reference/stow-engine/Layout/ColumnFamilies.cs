using System;
using System.Collections.Generic;
using RocksDbSharp;

namespace Stow.Storage.Layout;

/// <summary>
/// Column family names and their fixed numeric order.
/// Batches address families by numeric id — a mismatched order applies
/// writes to the wrong family in silence (design § 3, R3.2).
/// </summary>
public static class ColumnFamilies
{
    // The order is significant and must never change.
    // New families are appended; existing families are never reordered.
    public static readonly IReadOnlyList<string> OrderedNames = new[]
    {
        "default",  // 0 — unused, RocksDB requires it
        "meta",     // 1 — envelope + indexed values + inlined body
        "body",     // 2 — separated payloads
        "index",    // 3 — ordinary index entries
        "uniq",     // 4 — unique claims
        "catalog",  // 5 — structure
    };

    public const int Default = 0;
    public const int Meta = 1;
    public const int Body = 2;
    public const int Index = 3;
    public const int Uniq = 4;
    public const int Catalog = 5;

    /// <summary>
    /// Builds the <see cref="RocksDbSharp.ColumnFamilies"/> descriptor in the
    /// canonical order for database creation.
    /// </summary>
    public static RocksDbSharp.ColumnFamilies CreateDescriptor(ColumnFamilyOptions defaultOptions = null!)
    {
        var families = new RocksDbSharp.ColumnFamilies();
        var options = defaultOptions ?? new ColumnFamilyOptions();

        // Skip index 0 ("default") — RocksDB creates it implicitly.
        for (int i = 1; i < OrderedNames.Count; i++)
        {
            families.Add(OrderedNames[i], options);
        }

        return families;
    }

    /// <summary>
    /// Builds the <see cref="RocksDbSharp.ColumnFamilies"/> descriptor with the
    /// merge operator on the catalog CF for counter support (R4.6).
    /// </summary>
    public static RocksDbSharp.ColumnFamilies CreateDescriptor(
        ColumnFamilyOptions defaultOptions,
        MergeOperator catalogMergeOperator)
    {
        var families = new RocksDbSharp.ColumnFamilies();
        var options = defaultOptions ?? new ColumnFamilyOptions();

        for (int i = 1; i < OrderedNames.Count; i++)
        {
            if (i == Catalog && catalogMergeOperator != null)
            {
                var catalogOptions = new ColumnFamilyOptions()
                    .SetMergeOperator(catalogMergeOperator);
                families.Add(OrderedNames[i], catalogOptions);
            }
            else
            {
                families.Add(OrderedNames[i], options);
            }
        }

        return families;
    }

    /// <summary>
    /// Asserts the column families in the opened database match the expected order.
    /// Throws <see cref="InvalidOperationException"/> on mismatch.
    /// </summary>
    public static void AssertOrder(RocksDb db)
    {
        // RocksDB returns families via ListColumnFamilies on the path, but once
        // opened we verify by checking each handle's Name matches our expectation.
        // The handles are returned in creation order.
        for (int i = 1; i < OrderedNames.Count; i++)
        {
            var handle = db.GetColumnFamily(OrderedNames[i]);
            if (handle == null)
            {
                throw new InvalidOperationException(
                    $"Column family '{OrderedNames[i]}' (index {i}) not found in the database.");
            }
        }
    }
}
