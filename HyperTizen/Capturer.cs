using System;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using Tizen.Applications.Notifications;
using Tizen.System;

namespace HyperTizen
{
    public static class Capturer
    {
        private const int MaxColorValue = 1023;   // 10-Bit-Werte vom TV
        private const int MaxReadAttempts = 50;   // statt Endlosschleife bei ungueltigen Werten

        private const int ImgWidth = 64;
        private const int ImgHeight = 48;
        private const int BorderThick = 4;

        // Einmal beim Start ermitteln, nicht bei jedem Native-Aufruf neu.
        private static readonly bool UseTizen7Api = DetectTizen7();

        private static Condition _condition;
        private static CapturePoint[] _capturedPoints = DefaultPoints();

        // Vorberechnete Messpositionen + letzter gueltiger Wert pro Punkt.
        private static volatile CaptureLayout _layout;

        // Wiederverwendete Skia-Objekte fuer ToImage.
        private static readonly object ImageLock = new object();
        private static SKBitmap _bitmap;
        private static SKCanvas _canvas;

        #region Native

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition(out Condition unknown);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition(int i, int x, int y);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel(int i, out Color color);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition7(out Condition unknown);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition7(int i, int x, int y);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel7(int i, out Color color);

        private static int SetPosition(int slot, int x, int y)
        {
            return UseTizen7Api ? MeasurePosition7(slot, x, y) : MeasurePosition(slot, x, y);
        }

        private static int ReadPixelNative(int slot, out Color color)
        {
            return UseTizen7Api ? MeasurePixel7(slot, out color) : MeasurePixel(slot, out color);
        }

        #endregion

        #region Setup

        private static bool DetectTizen7()
        {
            try
            {
                string version;
                if (Information.TryGetValue("http://tizen.org/feature/platform.version", out version)
                    && !string.IsNullOrEmpty(version))
                {
                    int major;
                    return int.TryParse(version.Split('.')[0], out major) && major >= 7;
                }
            }
            catch
            {
                // Im Zweifel die aeltere API verwenden.
            }
            return false;
        }

        private static CapturePoint[] DefaultPoints()
        {
            return new CapturePoint[]
            {
                new CapturePoint(0.05, 0.05),
                new CapturePoint(0.275, 0.05),
                new CapturePoint(0.5, 0.05),
                new CapturePoint(0.725, 0.05),
                new CapturePoint(0.95, 0.05),
                new CapturePoint(0.95, 0.275),
                new CapturePoint(0.95, 0.5),
                new CapturePoint(0.95, 0.725),
                new CapturePoint(0.95, 0.95),
                new CapturePoint(0.725, 0.95),
                new CapturePoint(0.5, 0.95),
                new CapturePoint(0.275, 0.95),
                new CapturePoint(0.05, 0.95),
                new CapturePoint(0.05, 0.725),
                new CapturePoint(0.05, 0.5),
                new CapturePoint(0.05, 0.275)
            };
        }

        public static bool GetCondition()
        {
            int res = -1;
            try
            {
                res = UseTizen7Api ? MeasureCondition7(out _condition) : MeasureCondition(out _condition);
            }
            catch
            {
                Notification notification = new Notification
                {
                    Title = "HyperTizen",
                    Content = "Your TV does not support the required functions for HyperTizen.",
                    Count = 1
                };

                NotificationManager.Post(notification);
            }

            if (res < 0)
            {
                return false;
            }

            RebuildLayout();
            return true;
        }

        public static void SetCapturePoints(CapturePoint[] capturePoints)
        {
            _capturedPoints = capturePoints;
            RebuildLayout();
        }

        // Positionen hangen nur von Capture-Punkten und Condition ab,
        // also einmal berechnen statt pro Frame.
        private static void RebuildLayout()
        {
            CapturePoint[] points = _capturedPoints;
            Condition c = _condition;

            if (points == null || c.Width <= 0 || c.Height <= 0)
            {
                _layout = null;
                return;
            }

            int[] xs = new int[points.Length];
            int[] ys = new int[points.Length];

            for (int i = 0; i < points.Length; i++)
            {
                int x = (int)(points[i].X * (double)c.Width) - c.PixelDensityX / 2;
                int y = (int)(points[i].Y * (double)c.Height) - c.PixelDensityY / 2;

                // Originalverhalten unveraendert uebernommen (inkl. der Asymmetrie bei y).
                x = (x >= c.Width - c.PixelDensityX) ? c.Width - (c.PixelDensityX + 1) : x;
                y = (y >= c.Height - c.PixelDensityY) ? (c.Height - c.PixelDensityY + 1) : y;

                xs[i] = x;
                ys[i] = y;
            }

            _layout = new CaptureLayout(xs, ys);
        }

        #endregion

        #region Capture

        public static Color[] GetColors()
        {
            CaptureLayout layout = _layout;
            if (layout == null)
            {
                RebuildLayout();
                layout = _layout;
            }

            if (layout == null)
            {
                return new Color[_capturedPoints != null ? _capturedPoints.Length : 0];
            }

            int total = layout.X.Length;
            int batchSize = _condition.ScreenCapturePoints;
            int sleepMs = _condition.SleepMS;

            Color[] colorData = new Color[total];
            if (batchSize <= 0)
            {
                return colorData;
            }

            // Der TV kann nur 'batchSize' Punkte gleichzeitig messen.
            for (int start = 0; start < total; start += batchSize)
            {
                int count = Math.Min(batchSize, total - start);

                for (int j = 0; j < count; j++)
                {
                    SetPosition(j, layout.X[start + j], layout.Y[start + j]);
                }

                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }

                for (int k = 0; k < count; k++)
                {
                    int index = start + k;
                    Color color;
                    if (TryReadPixel(k, out color))
                    {
                        layout.LastGood[index] = color;
                    }

                    // Bei Fehlschlag den letzten guten Wert behalten (kein schwarzes Flackern).
                    colorData[index] = layout.LastGood[index];
                }
            }

            return colorData;
        }

        private static bool TryReadPixel(int slot, out Color color)
        {
            for (int attempt = 0; attempt < MaxReadAttempts; attempt++)
            {
                int res = ReadPixelNative(slot, out color);
                if (res >= 0 && color.R <= MaxColorValue && color.G <= MaxColorValue && color.B <= MaxColorValue)
                {
                    return true;
                }

                // Messung noch nicht fertig: kurz warten statt im Busy-Loop zu drehen.
                Thread.Sleep(1);
            }

            color = default(Color);
            return false;
        }

        #endregion

        #region Image

        public static string ToImage(Color[] colors)
        {
            // Erwartet mindestens 16 Farben (Reihenfolge wie in DefaultPoints).
            SKColor[] sk = new SKColor[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                sk[i] = ClampColor(colors[i]);
            }

            lock (ImageLock)
            {
                if (_bitmap == null)
                {
                    _bitmap = new SKBitmap(ImgWidth, ImgHeight);
                    _canvas = new SKCanvas(_bitmap);
                }

                _canvas.Clear(SKColors.Transparent);

                DrawBorder(_canvas, new SKRect(0, 0, ImgWidth, BorderThick),
                    new SKPoint(ImgWidth, 0), sk[0], sk[1], sk[2], sk[3], sk[4]);

                DrawBorder(_canvas, new SKRect(0, ImgHeight - BorderThick, ImgWidth, ImgHeight),
                    new SKPoint(ImgWidth, 0), sk[12], sk[11], sk[10], sk[9], sk[8]);

                DrawBorder(_canvas, new SKRect(0, 0, BorderThick, ImgHeight),
                    new SKPoint(0, ImgHeight), sk[0], sk[15], sk[14], sk[13], sk[12]);

                DrawBorder(_canvas, new SKRect(ImgWidth - BorderThick, 0, ImgWidth, ImgHeight),
                    new SKPoint(0, ImgHeight), sk[4], sk[5], sk[6], sk[7], sk[8]);

                using (SKImage image = SKImage.FromBitmap(_bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
                {
                    return Convert.ToBase64String(data.ToArray());
                }
            }
        }

        // Shader und Paint sind native Objekte und muessen freigegeben werden.
        private static void DrawBorder(SKCanvas canvas, SKRect rect, SKPoint end,
            SKColor c0, SKColor c1, SKColor c2, SKColor c3, SKColor c4)
        {
            using (SKShader shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), end, new SKColor[] { c0, c1, c2, c3, c4 }, SKShaderTileMode.Clamp))
            using (SKPaint paint = new SKPaint { Style = SKPaintStyle.Fill, Shader = shader })
            {
                canvas.DrawRect(rect, paint);
            }
        }

        private static SKColor ClampColor(Color color)
        {
            return new SKColor(
                Convert10BitTo8Bit(color.R),
                Convert10BitTo8Bit(color.G),
                Convert10BitTo8Bit(color.B));
        }

        private static byte Convert10BitTo8Bit(int value)
        {
            return (byte)((Math.Clamp(value, 0, MaxColorValue) * 255) / MaxColorValue);
        }

        #endregion

        private sealed class CaptureLayout
        {
            public readonly int[] X;
            public readonly int[] Y;
            public readonly Color[] LastGood;

            public CaptureLayout(int[] x, int[] y)
            {
                X = x;
                Y = y;
                LastGood = new Color[x.Length];
            }
        }
    }

    public struct Color
    {
        public int R;
        public int G;
        public int B;
    }

    public struct Condition
    {
        public int ScreenCapturePoints;

        public int PixelDensityX;

        public int PixelDensityY;

        public int SleepMS;

        public int Width;

        public int Height;
    }

    public struct CapturePoint
    {
        public CapturePoint(double x, double y)
        {
            this.X = x;
            this.Y = y;
        }

        public double X;
        public double Y;
    }
}
