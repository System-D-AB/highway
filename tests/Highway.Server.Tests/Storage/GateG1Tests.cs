using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Highway.Server.Storage;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Gate G1 (038 T7 / R1.2, R4.1, R4.3): the seam carries no engine type; there is exactly
/// one commit point; no clock is read inside the store layer. These are asserted by test,
/// not by review — if any fails, the seam is wrong and 041 (the command port) must not start.
/// </summary>
public class GateG1Tests
{
    // -------------------------------------------------------------------------
    // R1.2 / R3.2 — no engine, Garnet or Tsavorite type on the seam surface
    // -------------------------------------------------------------------------

    [Fact]
    public void Seam_HasNoEngineTypeOnItsSurface()
    {
        // Every type referenced by IHighwayStore / IStoreBatch / IStoreSnapshot — parameters,
        // return types, generic arguments — must live in a non-engine namespace. The seam is
        // the boundary G1 protects: a RocksDbSharp/Garnet/Tsavorite type here means the port
        // leaked the engine through the abstraction.
        var seamTypes = new[] { typeof(IHighwayStore), typeof(IStoreBatch), typeof(IStoreSnapshot) };
        var forbidden = new Regex(@"^(RocksDbSharp|Garnet|Tsavorite)\b", RegexOptions.Compiled);

        var offenders = new List<string>();

        foreach (var seam in seamTypes)
        {
            foreach (var member in seam.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case MethodInfo m:
                        Check(m.ReturnType, $"{seam.Name}.{m.Name} return");
                        foreach (var p in m.GetParameters())
                            Check(p.ParameterType, $"{seam.Name}.{m.Name}({p.Name})");
                        break;
                    case PropertyInfo p:
                        Check(p.PropertyType, $"{seam.Name}.{p.Name}");
                        break;
                }
            }
        }

        offenders.Should().BeEmpty("no engine type may appear on the seam surface (G1)");

        void Check(Type t, string where)
        {
            foreach (var involved in Flatten(t))
            {
                var ns = involved.Namespace ?? "";
                if (forbidden.IsMatch(ns) || forbidden.IsMatch(involved.Name))
                    offenders.Add($"{where}: {involved.FullName}");
            }
        }

        static IEnumerable<Type> Flatten(Type t)
        {
            yield return t;
            if (t.IsGenericType)
                foreach (var arg in t.GetGenericArguments())
                    foreach (var inner in Flatten(arg))
                        yield return inner;
            if (t.HasElementType && t.GetElementType() is { } el)
                foreach (var inner in Flatten(el))
                    yield return inner;
        }
    }

    // -------------------------------------------------------------------------
    // R4.1 — exactly one commit point
    // -------------------------------------------------------------------------

    [Fact]
    public void ThereIsExactlyOneCommitMethodOnTheSeam()
    {
        // The single durable-state-change entry point is IStoreBatch.Commit. No other seam
        // member may commit. This asserts the seam shape; the store implementations route
        // every mutation through their batch, whose only exit is Commit.
        var commitMembers = typeof(IStoreBatch)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        commitMembers.Should().ContainSingle("Commit is the one place durable state changes (R4.1)");
        commitMembers[0].Name.Should().Be(nameof(IStoreBatch.Commit));

        // And no other seam interface exposes a commit-like method.
        foreach (var seam in new[] { typeof(IHighwayStore), typeof(IStoreSnapshot) })
        {
            seam.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty($"{seam.Name} must not expose a commit path");
        }
    }

    [Fact]
    public void StoreLayer_CommitsOnlyThroughDbWrite_SinglePoint()
    {
        // Source-level assertion (R4.1): the RocksDB store must call db.Write in exactly one
        // place. A second db.Write would be a second commit point, defeating the guarantee.
        var source = ReadStoreSource("Rocks", "RocksDbStore.cs");
        var writeCalls = Regex.Matches(source, @"_db\.Write\(").Count;
        writeCalls.Should().Be(1, "RocksDbStore must commit through exactly one db.Write call");
    }

    // -------------------------------------------------------------------------
    // R4.3 / R5.1 — no clock read inside the store layer
    // -------------------------------------------------------------------------

    [Fact]
    public void StoreLayer_ReadsNoClock()
    {
        // R5.1: the store never reads the wall clock — every time value is passed in as an
        // absolute tick count. A DateTime.UtcNow / DateTimeOffset.UtcNow / Now / Stopwatch in
        // the store layer would reintroduce the non-determinism the WAL-replay guarantee
        // (R5.3) depends on being absent. Enforced by source scan over the store files.
        var forbidden = new Regex(@"\b(DateTime\.UtcNow|DateTimeOffset\.UtcNow|DateTime\.Now|Stopwatch|Environment\.TickCount)\b");

        foreach (var file in StoreSourceFiles())
        {
            var source = File.ReadAllText(file);
            forbidden.IsMatch(source).Should().BeFalse(
                $"the store layer must not read a clock (R5.1); found one in {Path.GetFileName(file)}");
        }
    }

    // -------------------------------------------------------------------------
    // R5.1 — RocksDB native library resolves for the host RID
    // -------------------------------------------------------------------------

    [Fact]
    public void RocksDbNativeLibrary_IsPresentForHostRid()
    {
        // The full cross-RID (win-x64 + linux-x64) distribution assert is 041's packaging job
        // (the server distribution does not yet reference RocksDB). What 038 can prove now: the
        // RocksDB package's native asset reaches Highway.Server's build output for THIS RID, so
        // the store actually loads. If it did not, every RocksDbStoreContractTests case would
        // have failed to open a DB — but assert it explicitly so a packaging regression is loud.
        var baseDir = AppContext.BaseDirectory;
        var nativeNames = new[] { "rocksdb.dll", "librocksdb.so", "librocksdb.dylib" };

        bool Found(string dir) =>
            Directory.Exists(dir) &&
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f => nativeNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));

        // Either flat in the output or under runtimes/<rid>/native/.
        var present = Found(baseDir) || Found(Path.Combine(baseDir, "runtimes"));
        present.Should().BeTrue(
            "the RocksDB native library must resolve to the build output for the host RID");
    }

    // -------------------------------------------------------------------------
    // helpers — locate the store source under src/
    // -------------------------------------------------------------------------

    private static string ReadStoreSource(params string[] relative)
    {
        var path = Path.Combine(StorageSrcDir(), Path.Combine(relative));
        File.Exists(path).Should().BeTrue($"expected store source at {path}");
        return File.ReadAllText(path);
    }

    private static IEnumerable<string> StoreSourceFiles()
        => Directory.EnumerateFiles(StorageSrcDir(), "*.cs", SearchOption.AllDirectories)
            // Exclude the layout key-builders folder scan? No — they must also be clock-free.
            .ToList();

    private static string StorageSrcDir()
    {
        // From tests/Highway.Server.Tests/bin/<cfg>/<tfm>/ walk up to the repo root, then into src.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        dir.Should().NotBeNull("repo root with a src/ folder must be found");
        var storage = Path.Combine(dir!.FullName, "src", "Highway.Server", "Storage");
        Directory.Exists(storage).Should().BeTrue($"expected storage source at {storage}");
        return storage;
    }
}
