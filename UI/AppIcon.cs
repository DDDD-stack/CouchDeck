using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchDeck.UI
{
    /// <summary>
    /// The CouchDeck logo, drawn as vectors: a rounded gradient tile with a white couch whose backrest
    /// carries the four face buttons (A B X Y). Vectors keep it sharp at every size, from the 16 px
    /// tray icon to the 256 px .exe icon.
    ///
    /// Assets\CouchDeck.ico (the .exe / shortcut icon) and Assets\logo.png are generated from this:
    ///   CouchDeck.exe --export-icon Assets
    /// Inside the app the logo is drawn live in the current theme colours.
    /// </summary>
    public static class AppIcon
    {
        public static readonly Color BrandStart = Color.FromRgb(0x7C, 0x5C, 0xFF);
        public static readonly Color BrandEnd = Color.FromRgb(0x00, 0xD4, 0xFF);

        private const double Canvas = 256;

        /// <summary>The logo on a 256x256 canvas. <paramref name="simplified"/> drops details that turn to mush below 32 px.</summary>
        public static DrawingGroup CreateDrawing(Color start, Color end, bool simplified = false)
        {
            DrawingGroup group = new();
            using (DrawingContext dc = group.Open())
            {
                RectangleGeometry tile = new(new Rect(0, 0, Canvas, Canvas), 58, 58);
                LinearGradientBrush gradient = new(start, end, new Point(0, 0), new Point(1, 1));
                dc.DrawGeometry(gradient, null, tile);

                // Soft light from the top for a bit of depth
                LinearGradientBrush gloss = new(Color.FromArgb(64, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 0.55));
                dc.DrawGeometry(gloss, null, tile);

                Geometry couch = Union(
                    Rounded(62, 60, 132, 80, 28),   // backrest
                    Rounded(34, 108, 52, 84, 24),   // left arm
                    Rounded(170, 108, 52, 84, 24),  // right arm
                    Rounded(70, 136, 116, 48, 14),  // seat
                    Rounded(50, 178, 20, 24, 7),    // legs
                    Rounded(186, 178, 20, 24, 7));

                // Shadow under the couch, tinted from the gradient so it never looks grey
                dc.PushTransform(new TranslateTransform(0, simplified ? 5 : 7));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 10, 6, 40)), null, couch);
                dc.Pop();
                dc.DrawGeometry(Brushes.White, null, couch);

                if (!simplified)
                {
                    // Seam between backrest and seat cushion
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xE1, 0xE5, 0xF4)), null,
                        new Rect(86, 136, 84, 6), 3, 3);

                    // A B X Y diamond on the backrest, "cut out" in the gradient
                    const double cx = 128, cy = 99, spread = 16, radius = 8;
                    foreach ((double x, double y) in new[] { (cx, cy - spread), (cx + spread, cy), (cx, cy + spread), (cx - spread, cy) })
                    {
                        dc.DrawEllipse(gradient, null, new Point(x, y), radius, radius);
                    }
                }
            }

            group.Freeze();
            return group;
        }

        /// <summary>Vector image for WPF (header logo): crisp at any DPI.</summary>
        public static DrawingImage CreateImage(Color start, Color end)
        {
            DrawingImage image = new(CreateDrawing(start, end));
            image.Freeze();
            return image;
        }

        public static BitmapSource Render(int size) =>
            Render(size, ThemeManager.ParseColor(ThemeManager.Current.AccentColor, "#7C5CFF"),
                ThemeManager.ParseColor(ThemeManager.Current.SecondaryAccentColor, "#00D4FF"));

        public static BitmapSource Render(int size, Color start, Color end)
        {
            DrawingVisual visual = new();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / Canvas, size / Canvas));
                dc.DrawDrawing(CreateDrawing(start, end, simplified: size < 40));
            }

            RenderTargetBitmap bitmap = new(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        public static System.Drawing.Icon? ToTrayIcon(BitmapSource source)
        {
            try
            {
                using MemoryStream stream = new(Png(source));
                using System.Drawing.Bitmap bitmap = new(stream);
                return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray icon render failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Writes CouchDeck.ico (16-256 px, brand colours) and logo.png (1024 px) into <paramref name="folder"/>.</summary>
        public static void Export(string folder)
        {
            Directory.CreateDirectory(folder);

            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
            List<byte[]> images = new();
            foreach (int size in sizes)
            {
                images.Add(Png(Render(size, BrandStart, BrandEnd)));
            }

            // ICO container with PNG frames (supported by Windows since Vista).
            using (FileStream file = File.Create(Path.Combine(folder, "CouchDeck.ico")))
            using (BinaryWriter writer = new(file))
            {
                writer.Write((ushort)0);            // reserved
                writer.Write((ushort)1);            // type: icon
                writer.Write((ushort)sizes.Length); // count

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // 0 means 256
                    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    writer.Write((byte)0);   // palette colours
                    writer.Write((byte)0);   // reserved
                    writer.Write((ushort)1); // planes
                    writer.Write((ushort)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }

                foreach (byte[] image in images)
                {
                    writer.Write(image);
                }
            }

            File.WriteAllBytes(Path.Combine(folder, "logo.png"), Png(Render(1024, BrandStart, BrandEnd)));
        }

        private static byte[] Png(BitmapSource source)
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using MemoryStream stream = new();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static Geometry Rounded(double x, double y, double width, double height, double radius) =>
            new RectangleGeometry(new Rect(x, y, width, height), radius, radius);

        private static Geometry Union(params Geometry[] parts)
        {
            Geometry result = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                result = new CombinedGeometry(GeometryCombineMode.Union, result, parts[i]);
            }

            return result; // kept as curves (flattening faceted the corners at large sizes)
        }
    }
}
