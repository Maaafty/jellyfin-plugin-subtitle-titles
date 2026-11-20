using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal abstract class DiscVolume : IDisposable
{
    internal abstract string[] List(string directory);
    internal abstract Stream? Open(string path);
    public abstract void Dispose();

    internal static bool IsIso(string? path) => string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDisc(string? path) => path is not null && (IsIso(path) || FolderRoot(path) is not null);

    internal sealed record Stamp(string Path, long Size, long Modified)
    {
        internal static Stamp? Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(path, file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
    }

    internal static string? FolderRoot(string path)
    {
        if (!Directory.Exists(path)) return null;
        var root = new DirectoryInfo(path);
        if (root.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)) root = root.Parent!;
        return root is not null && Directory.Exists(Path.Combine(root.FullName, "BDMV", "PLAYLIST"))
            && Directory.Exists(Path.Combine(root.FullName, "BDMV", "STREAM")) ? root.FullName : null;
    }

    internal static DiscVolume? OpenVolume(string path)
    {
        if (FolderRoot(path) is { } root) return new Folder(root);
        if (!IsIso(path) || !File.Exists(path) || !OperatingSystem.IsLinux()) return null;
        try
        {
            var volume = Udf.Init();
            if (!volume.IsInvalid && Udf.Open(volume, path) == 0) return new Image(volume);
            volume.Dispose();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException) { }
        return null;
    }

    private sealed class Folder(string root) : DiscVolume
    {
        private string? Resolve(string path)
        {
            var resolved = root;
            foreach (var part in path.Split('/'))
            {
                if (!Regex.IsMatch(part, @"\A[\w. -]+\z") || part is "." or "..") return null;
                resolved = Path.Combine(resolved, part);
                if (File.GetAttributes(resolved).HasFlag(FileAttributes.ReparsePoint)) return null;
            }
            return resolved;
        }

        internal override string[] List(string directory)
        {
            try { return Resolve(directory) is { } path ? Directory.EnumerateFileSystemEntries(path).Take(4097).Select(Path.GetFileName).OfType<string>().ToArray() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }

        internal override Stream? Open(string path)
        {
            try { return Resolve(path) is { } file && File.Exists(file) ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        public override void Dispose() { }
    }

    private sealed class Image(Udf.Volume volume) : DiscVolume
    {
        internal override string[] List(string directory)
        {
            var dir = Udf.OpenDirectory(volume, directory);
            if (dir == IntPtr.Zero) return [];
            try
            {
                var files = new List<string>();
                while (files.Count <= 4096 && Udf.ReadDirectory(dir, out var entry) != IntPtr.Zero)
                    if (Marshal.PtrToStringUTF8(entry.Name) is { } name && name is not ("." or "..")) files.Add(name);
                return files.ToArray();
            }
            finally { Udf.CloseDirectory(dir); }
        }

        internal override Stream? Open(string path)
        {
            var file = Udf.OpenFile(volume, path);
            if (!file.IsInvalid) return new ImageFile(file, Udf.Size(file));
            file.Dispose();
            return null;
        }

        public override void Dispose() => volume.Dispose();
    }

    private sealed class ImageFile(Udf.File file, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => Seek(0, SeekOrigin.Current); set => Seek(value, SeekOrigin.Begin); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = Udf.Seek(file, offset, (int)origin);
            return position >= 0 ? position : throw new IOException("Cannot seek disc file.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            if (count == 0) return 0;
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var bytes = Udf.Read(file, IntPtr.Add(pin.AddrOfPinnedObject(), offset), (nuint)count);
                return bytes >= 0 ? checked((int)bytes) : throw new IOException("Cannot read disc file.");
            }
            finally { pin.Free(); }
        }

        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static class Udf
    {
        private const string Library = "mreel_udfread";
        static Udf() => NativeLibrary.SetDllImportResolver(typeof(Udf).Assembly, Resolve);
        private static IntPtr Resolve(string library, Assembly assembly, DllImportSearchPath? searchPath) => library == Library
            ? NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "runtime", "linux-x64", "lib", "libudfread.so.0")) : IntPtr.Zero;

        internal sealed class Volume() : SafeHandleZeroOrMinusOneIsInvalid(true)
        { protected override bool ReleaseHandle() { Udf.Close(handle); return true; } }
        internal sealed class File() : SafeHandleZeroOrMinusOneIsInvalid(true)
        { protected override bool ReleaseHandle() { CloseFile(handle); return true; } }
        [StructLayout(LayoutKind.Sequential)] internal struct DirectoryEntry { internal uint Type; internal IntPtr Name; }

        [DllImport(Library, EntryPoint = "udfread_init")] internal static extern Volume Init();
        [DllImport(Library, EntryPoint = "udfread_open")] internal static extern int Open(Volume volume, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, EntryPoint = "udfread_close")] private static extern void Close(IntPtr volume);
        [DllImport(Library, EntryPoint = "udfread_opendir")] internal static extern IntPtr OpenDirectory(Volume volume, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, EntryPoint = "udfread_readdir")] internal static extern IntPtr ReadDirectory(IntPtr dir, out DirectoryEntry entry);
        [DllImport(Library, EntryPoint = "udfread_closedir")] internal static extern void CloseDirectory(IntPtr dir);
        [DllImport(Library, EntryPoint = "udfread_file_open")] internal static extern File OpenFile(Volume volume, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, EntryPoint = "udfread_file_close")] private static extern void CloseFile(IntPtr file);
        [DllImport(Library, EntryPoint = "udfread_file_size")] internal static extern long Size(File file);
        [DllImport(Library, EntryPoint = "udfread_file_seek")] internal static extern long Seek(File file, long offset, int origin);
        [DllImport(Library, EntryPoint = "udfread_file_read")] internal static extern nint Read(File file, IntPtr buffer, nuint count);
    }
}
