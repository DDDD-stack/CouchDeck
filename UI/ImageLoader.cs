using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchDeck.UI
{
    /// <summary>
    /// Decodes images off the UI thread at exactly the pixel size they're shown at.
    ///
    /// Sharpness: the old converter always decoded covers to 300 px wide, which a 4K TV at 200%
    /// then stretched to ~480 px (blurry). Now the decode width is tile size x DPI x focus zoom, and
    /// tiles render with high-quality scaling, so art is crisp on any screen.
    /// </summary>
    public static class ImageLoader
    {
        private static readonly SemaphoreSlim Gate = new(4);
        private static readonly ConcurrentDictionary<(string path, int width, int height), WeakReference<BitmapSource>> Cache = new();

        /// <summary>Decodes to at most <paramref name="decodeWidth"/> wide, or (when given) <paramref name="decodeHeight"/> tall.</summary>
        public static async Task<BitmapSource?> LoadAsync(string? path, int decodeWidth, int decodeHeight = 0)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Cache.TryGetValue((path, decodeWidth, decodeHeight), out WeakReference<BitmapSource>? weak) && weak.TryGetTarget(out BitmapSource? cached))
            {
                return cached;
            }

            await Gate.WaitAsync();
            try
            {
                BitmapSource? image = await Task.Run(() => Decode(path, decodeWidth, decodeHeight));
                if (image != null)
                {
                    Cache[(path, decodeWidth, decodeHeight)] = new WeakReference<BitmapSource>(image);
                }

                return image;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static BitmapSource? Decode(string path, int decodeWidth, int decodeHeight)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path); // read once: no file lock, works for files being replaced
                int pixelWidth;
                int pixelHeight;
                using (MemoryStream probe = new(bytes))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    pixelWidth = decoder.Frames[0].PixelWidth;
                    pixelHeight = decoder.Frames[0].PixelHeight;
                }

                BitmapImage image = new();
                image.BeginInit();
                image.StreamSource = new MemoryStream(bytes);
                image.CacheOption = BitmapCacheOption.OnLoad;
                // Not IgnoreImageCache: with a StreamSource (no Uri key) WPF throws ArgumentNullException.
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeHeight > 0)
                {
                    if (pixelHeight > decodeHeight)
                    {
                        image.DecodePixelHeight = decodeHeight;
                    }
                }
                else if (decodeWidth > 0 && pixelWidth > decodeWidth)
                {
                    image.DecodePixelWidth = decodeWidth;
                }

                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Image decode failed {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Physical pixels per WPF unit for the monitor the visual is on.</summary>
        public static double DpiScale(Visual visual)
        {
            try
            {
                return VisualTreeHelper.GetDpi(visual).DpiScaleX;
            }
            catch
            {
                return 1;
            }
        }
    }

    /// <summary>
    /// Big, sharp icons for .exe files (up to 256 px) via the shell's IShellItemImageFactory.
    /// Used for games without cover art yet and for the open-windows list.
    /// </summary>
    public static class ShellIcons
    {
        private const int SIIGBF_BIGGERSIZEOK = 0x1;
        private const int SIIGBF_ICONONLY = 0x4;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static readonly ConcurrentDictionary<(string, int), BitmapSource?> Cache = new(StringComparerTuple.Instance);

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBitmap
        {
            public int Type;
            public int Width;
            public int Height;
            public int WidthBytes;
            public ushort Planes;
            public ushort BitsPixel;
            public IntPtr Bits;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

        /// <summary>Call on the UI thread (shell COM objects want STA). Cached per path and size.</summary>
        public static BitmapSource? Get(string? path, int size)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return Cache.GetOrAdd((path, size), key => Extract(key.Item1, key.Item2));
        }

        private static BitmapSource? Extract(string path, int size)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out IShellItemImageFactory factory);
                int hr = factory.GetImage(new NativeSize { Width = size, Height = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hBitmap);
                Marshal.ReleaseComObject(factory);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                // Copy the 32-bit premultiplied DIB ourselves: CreateBitmapSourceFromHBitmap drops alpha.
                GetObject(hBitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info);
                if (info.Bits == IntPtr.Zero || info.BitsPixel != 32)
                {
                    BitmapSource fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    fallback.Freeze();
                    return fallback;
                }

                int width = info.Width;
                int height = Math.Abs(info.Height);
                int stride = info.WidthBytes;
                byte[] pixels = new byte[stride * height];
                Marshal.Copy(info.Bits, pixels, 0, pixels.Length);

                // Bottom-up DIB (positive height): flip rows.
                if (info.Height > 0)
                {
                    byte[] flipped = new byte[pixels.Length];
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
                    }

                    pixels = flipped;
                }

                BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                result.Freeze();
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Icon extraction failed for {path}: {ex.Message}");
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
            }
        }

        /// <summary>Works for elevated processes too, where Process.MainModule throws.</summary>
        public static string? ProcessPath(int processId)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                StringBuilder builder = new(1024);
                int size = builder.Capacity;
                return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private sealed class StringComparerTuple : System.Collections.Generic.IEqualityComparer<(string, int)>
        {
            public static readonly StringComparerTuple Instance = new();

            public bool Equals((string, int) x, (string, int) y) =>
                x.Item2 == y.Item2 && string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string, int) obj) =>
                HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1), obj.Item2);
        }
    }
}
