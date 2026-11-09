using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Proteus.Core.Metadata;

/// <summary>
/// Reads Windows Property System values (the same data Explorer shows in its Details pane):
/// video length and resolution, audio bitrate, document authors and page counts, music tags for
/// MP3/FLAC/M4A/WMA, camera data and hundreds more, for any file type Windows has a handler for.
/// All COM work happens on one dedicated STA thread, because some property handlers require it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellProperties
{
    private static readonly ConcurrentDictionary<string, PropertyKey?> KeyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WorkerGate = new();
    private static BlockingCollection<Action>? _queue;

    /// <summary>
    /// The queue of the current STA worker. If a property handler hangs, that worker is abandoned
    /// (it is a background thread) and the next request starts a fresh one, so one bad file can't
    /// stall every later preview.
    /// </summary>
    private static BlockingCollection<Action> Queue()
    {
        lock (WorkerGate)
        {
            if (_queue != null) return _queue;
            var queue = new BlockingCollection<Action>();
            var t = new Thread(() =>
            {
                foreach (var work in queue.GetConsumingEnumerable())
                {
                    try { work(); } catch { /* each work item reports its own result */ }
                }
            })
            { IsBackground = true, Name = "Proteus shell properties" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _queue = queue;
            return queue;
        }
    }

    private static void Abandon(BlockingCollection<Action> stuck)
    {
        lock (WorkerGate)
        {
            if (!ReferenceEquals(_queue, stuck)) return;
            _queue = null;
            stuck.CompleteAdding();
        }
    }

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Reads the named properties (e.g. "System.Media.Duration") of one file.</summary>
    /// <returns>Only the properties that have a value. Never throws.</returns>
    public static Dictionary<string, object> Read(string path, IEnumerable<string> canonicalNames, int timeoutMs = 4000)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!IsSupported) return result;
        var names = canonicalNames.ToList();
        var done = new ManualResetEventSlim();
        var queue = Queue();
        try
        {
            queue.Add(() =>
            {
                try { ReadOnSta(path, names, result); }
                finally { done.Set(); }
            });
        }
        catch (InvalidOperationException)
        {
            return new Dictionary<string, object>(); // worker was just abandoned; the next call gets a new one
        }
        if (!done.Wait(timeoutMs))
        {
            Abandon(queue);
            return new Dictionary<string, object>();
        }
        done.Dispose();
        return result;
    }

    private static void ReadOnSta(string path, List<string> names, Dictionary<string, object> into)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_BESTEFFORT, ref iid, out var store) != 0 || store == null)
            return;
        try
        {
            foreach (var name in names)
            {
                var key = KeyCache.GetOrAdd(name, n => PSGetPropertyKeyFromName(n, out var k) == 0 ? k : null);
                if (key is not { } pk) continue;
                var pv = new PropVariant();
                try
                {
                    if (store.GetValue(ref pk, out pv) != 0) continue;
                    var value = pv.ToObject();
                    if (value != null) into[name] = value;
                }
                finally { PropVariantClear(ref pv); }
            }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // ---------------- interop ----------------

    private const int GPS_BESTEFFORT = 0x40;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] public long int64;
        [FieldOffset(8)] public ulong uint64;
        [FieldOffset(8)] public int int32;
        [FieldOffset(8)] public uint uint32;
        [FieldOffset(8)] public short int16;
        [FieldOffset(8)] public ushort uint16;
        [FieldOffset(8)] public double r8;
        [FieldOffset(8)] public float r4;
        [FieldOffset(8)] public uint vectorCount;
        [FieldOffset(16)] public IntPtr vectorPointer; // valid on 64-bit only; we target x64

        public object? ToObject()
        {
            switch (vt)
            {
                case 0: case 1: return null; // empty, null
                case 2: return (int)int16;
                case 3: return int32;
                case 4: return (double)r4;
                case 5: return r8;
                case 8: return pointer == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(pointer);
                case 11: return int16 != 0;
                case 16: return (int)(sbyte)(int32 & 0xFF);
                case 17: return (int)(byte)(uint32 & 0xFF);
                case 18: return (int)uint16;
                case 19: return (long)uint32;
                case 20: return int64;
                case 21: return uint64;
                case 22: return int32;
                case 23: return (long)uint32;
                case 30: return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
                case 31: return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
                case 64: // FILETIME
                    return int64 <= 0 ? null : DateTime.FromFileTimeUtc(int64).ToLocalTime();
                case 0x1000 | 31: // vector of strings, e.g. authors
                {
                    if (IntPtr.Size != 8 || vectorCount == 0 || vectorPointer == IntPtr.Zero) return null;
                    var list = new List<string>();
                    for (int i = 0; i < Math.Min(vectorCount, 64u); i++)
                    {
                        var p = Marshal.ReadIntPtr(vectorPointer, i * IntPtr.Size);
                        var s = Marshal.PtrToStringUni(p);
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                    return list.Count == 0 ? null : list.ToArray();
                }
                default:
                    return null;
            }
        }
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindCtx, int flags,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
