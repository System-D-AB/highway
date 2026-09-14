using System;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace Stow.Storage.Engine;

/// <summary>
/// Native binding interop for RocksDB WAL flushing and recovery mode options (Spec 017 R1.6, R1.8).
/// </summary>
public static class WalFlusher
{
    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_flush_wal(IntPtr db, byte sync, out IntPtr errptr);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_options_set_wal_recovery_mode(IntPtr options, int mode);

    [DllImport("rocksdb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rocksdb_free(IntPtr ptr);

    /// <summary>
    /// RocksDB WAL recovery mode 1: kTolerateCorruptedTailRecords.
    /// Drops any unacknowledged torn final WAL record on crash/restart rather than refusing to open.
    /// </summary>
    public const int TolerateCorruptedTailRecords = 1;

    /// <summary>
    /// Configures the explicit WAL recovery mode on the DbOptions handle.
    /// </summary>
    public static void SetWalRecoveryMode(DbOptions options, int mode = TolerateCorruptedTailRecords)
    {
        if (options != null)
        {
            rocksdb_options_set_wal_recovery_mode(options.Handle, mode);
        }
    }

    /// <summary>
    /// Flushes the WAL buffer to disk using fsync (when sync = true).
    /// </summary>
    public static void FlushWal(RocksDb db, bool sync = true)
    {
        if (db == null) return;
        rocksdb_flush_wal(db.Handle, (byte)(sync ? 1 : 0), out var errPtr);
        if (errPtr != IntPtr.Zero)
        {
            string msg = Marshal.PtrToStringAnsi(errPtr);
            rocksdb_free(errPtr);
            throw new InvalidOperationException($"rocksdb_flush_wal failed: {msg}");
        }
    }
}
