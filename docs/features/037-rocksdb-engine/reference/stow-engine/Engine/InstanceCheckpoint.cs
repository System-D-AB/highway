using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace Stow.Storage.Engine;

/// <summary>
/// Physical instance checkpointing using native RocksDB hardlink snapshots (R1.1, R1.2).
/// Creates an atomic, non-blocking disk image in &lt;10ms.
/// </summary>
public static class InstanceCheckpoint
{
    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rocksdb_checkpoint_object_create(IntPtr db, out IntPtr errptr);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_checkpoint_create(IntPtr checkpoint, string checkpoint_dir, ulong log_size_for_flush, out IntPtr errptr);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_checkpoint_object_destroy(IntPtr checkpoint);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_free(IntPtr ptr);

    /// <summary>
    /// Creates a consistent physical checkpoint of the RocksDB instance in <paramref name="targetDir"/>.
    /// </summary>
    /// <param name="db">The active RocksDb database handle.</param>
    /// <param name="targetDir">The destination directory to populate with hardlinks and metadata.</param>
    /// <param name="logSizeForFlush">Log size threshold to trigger flush (0 = default).</param>
    public static void Create(RocksDb db, string targetDir, ulong logSizeForFlush = 0)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new ArgumentException("Target directory cannot be empty.", nameof(targetDir));

        if (Directory.Exists(targetDir))
        {
            if (Directory.EnumerateFileSystemEntries(targetDir).Any())
                throw new IOException($"Target directory '{targetDir}' already exists and is not empty.");

            Directory.Delete(targetDir);
        }


        IntPtr errPtr;
        IntPtr checkpoint = rocksdb_checkpoint_object_create(db.Handle, out errPtr);
        if (errPtr != IntPtr.Zero)
        {
            string err = Marshal.PtrToStringAnsi(errPtr) ?? "Failed to create checkpoint object.";
            rocksdb_free(errPtr);
            throw new InvalidOperationException($"RocksDB Checkpoint object creation failed: {err}");
        }

        try
        {
            rocksdb_checkpoint_create(checkpoint, targetDir, logSizeForFlush, out errPtr);
            if (errPtr != IntPtr.Zero)
            {
                string err = Marshal.PtrToStringAnsi(errPtr) ?? "Failed to create checkpoint.";
                rocksdb_free(errPtr);
                throw new InvalidOperationException($"RocksDB Checkpoint creation failed: {err}");
            }
        }
        finally
        {
            rocksdb_checkpoint_object_destroy(checkpoint);
        }
    }
}
