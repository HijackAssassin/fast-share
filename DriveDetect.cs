using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FastShare;

/// <summary>
/// What kind of disk a path lives on. Shown in the Activity log as "SSD → HDD" so a slow
/// transfer can be blamed on the right thing: when the link does 900 Mbit but a folder of
/// thousands of small files crawls, a spinning disk on either end is the usual culprit, and
/// the number alone never says which end.
/// </summary>
public enum DriveKind { Unknown, Hdd, Ssd, Nvme }

/// <summary>
/// Detects whether a path is on an HDD, an SSD, or an NVMe drive.
///
/// Done with <c>DeviceIoControl</c> rather than WMI so it adds no NuGet dependency to the
/// single-file build and matches the P/Invoke already used elsewhere (the debug console, the
/// shell drag-out). The volume is opened with zero access, which is enough for a property
/// query and - unlike a read/write handle - needs no administrator rights.
///
/// Everything here is best-effort: a disk it cannot classify (a network share, a RAID volume,
/// a USB enclosure that hides the media type) reports <see cref="DriveKind.Unknown"/> rather
/// than throwing. A drive letter's kind never changes while the app runs, so results are cached.
/// </summary>
public static class DriveDetect
{
    private static readonly ConcurrentDictionary<char, DriveKind> Cache = new();

    /// <summary>The short label shown in the UI. Unknown becomes "?" so a row still lines up.</summary>
    public static string Label(DriveKind kind) => kind switch
    {
        DriveKind.Nvme => "NVMe",
        DriveKind.Ssd => "SSD",
        DriveKind.Hdd => "HDD",
        _ => "?",
    };

    /// <summary>The kind of drive a single path sits on, or Unknown if it cannot be told.</summary>
    public static DriveKind ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DriveKind.Unknown;

        string? root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
        catch { return DriveKind.Unknown; }

        // Only a plain drive letter can be probed this way. UNC paths ("\\server\share") and
        // anything without a letter are left as Unknown rather than guessed at.
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return DriveKind.Unknown;

        var letter = char.ToUpperInvariant(root[0]);
        if (letter is < 'A' or > 'Z') return DriveKind.Unknown;

        return Cache.GetOrAdd(letter, Probe);
    }

    /// <summary>
    /// The kind shared by a group of paths - the transfer engine works on many files at once,
    /// and the log wants one label per end. The most common known kind wins; "Unknown" only
    /// results when nothing could be classified at all.
    /// </summary>
    public static DriveKind ForPaths(IEnumerable<string> paths)
    {
        var tally = new Dictionary<DriveKind, int>();
        foreach (var path in paths)
        {
            var kind = ForPath(path);
            if (kind == DriveKind.Unknown) continue;
            tally[kind] = tally.GetValueOrDefault(kind) + 1;
        }

        return tally.Count == 0
            ? DriveKind.Unknown
            : tally.MaxBy(p => p.Value).Key;
    }

    /// <summary>Parses a label sent by a peer ("SSD", "HDD", "NVMe") back into a kind.</summary>
    public static DriveKind Parse(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        "NVME" => DriveKind.Nvme,
        "SSD" => DriveKind.Ssd,
        "HDD" => DriveKind.Hdd,
        _ => DriveKind.Unknown,
    };

    // =======================================================================
    // Win32 storage property query
    // =======================================================================

    private static DriveKind Probe(char letter)
    {
        SafeFileHandle? handle = null;
        try
        {
            // "\\.\C:" opens the volume itself. Zero desired access is deliberate: it grants a
            // handle usable for IOCTL_STORAGE_QUERY_PROPERTY without the administrator rights a
            // read/write handle to a raw volume would demand.
            handle = CreateFileW($@"\\.\{letter}:", 0,
                FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid) return DriveKind.Unknown;

            // NVMe is reported by bus type, not seek penalty (an NVMe drive has no seek penalty
            // either, so without this it would just read as "SSD"). Check it first.
            if (BusType(handle) == BusTypeNvme) return DriveKind.Nvme;

            return SeekPenalty(handle) switch
            {
                true => DriveKind.Hdd,
                false => DriveKind.Ssd,
                null => DriveKind.Unknown,
            };
        }
        catch
        {
            return DriveKind.Unknown;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>True if the disk incurs a seek penalty (a spinning HDD), false for solid state.</summary>
    private static bool? SeekPenalty(SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = PropertyStandardQuery,
        };

        var output = new DEVICE_SEEK_PENALTY_DESCRIPTOR();
        if (!Query(handle, query, ref output, Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>()))
            return null;

        return output.IncursSeekPenalty;
    }

    /// <summary>The bus the disk sits on (SATA, USB, NVMe...), from the device descriptor.</summary>
    private static int BusType(SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceProperty,
            QueryType = PropertyStandardQuery,
        };

        // The descriptor is variable-length (it trails vendor/product strings), so a fixed
        // buffer is read and only the fixed header - which holds BusType - is inspected.
        var buffer = new byte[512];
        var input = Marshal.AllocHGlobal(Marshal.SizeOf<STORAGE_PROPERTY_QUERY>());
        try
        {
            Marshal.StructureToPtr(query, input, false);
            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    input, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                    buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 32)
                return -1;

            // STORAGE_DEVICE_DESCRIPTOR lays BusType at byte offset 28 (after the two DWORD
            // header fields, four bytes of flags, and four DWORD string offsets).
            return BitConverter.ToInt32(buffer, 28);
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    private static bool Query<T>(SafeFileHandle handle, STORAGE_PROPERTY_QUERY query,
        ref T output, int outputSize) where T : struct
    {
        var input = Marshal.AllocHGlobal(Marshal.SizeOf<STORAGE_PROPERTY_QUERY>());
        var result = Marshal.AllocHGlobal(outputSize);
        try
        {
            Marshal.StructureToPtr(query, input, false);
            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    input, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                    result, outputSize, out _, IntPtr.Zero))
                return false;

            output = Marshal.PtrToStructure<T>(result);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(result);
        }
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const int BusTypeNvme = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);
}
