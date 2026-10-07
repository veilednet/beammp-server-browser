// Colour emoji. The text drawing the rest of the app uses (GDI) can only draw emoji as black
// and white outlines, so emoji are found in the text, drawn once each with Direct2D, which can
// draw them in colour, and kept as small pictures to place between the runs of ordinary text.
// Nothing here is needed for the app to work: if Direct2D is unavailable, text is drawn as before.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerBrowser
{
    struct TextRun
    {
        public bool IsEmoji;
        public string Text;
    }

    static class Emoji
    {
        // ------------------------------------------------------------ finding emoji in text

        // Symbols that are emoji on their own. Others in these blocks (arrows, card suits, ticks)
        // only count when followed by the "show as emoji" mark U+FE0F, and are left as text otherwise.
        static bool IsBmpEmoji(char c)
        {
            if (c < '⌚' || c > '⭕') return false;
            switch (c)
            {
                case '⌚': case '⌛': case '⏩': case '⏪': case '⏫': case '⏬': case '⏰': case '⏳':
                case '◽': case '◾': case '☔': case '☕': case '♿': case '⚓': case '⚡': case '⚪':
                case '⚫': case '⚽': case '⚾': case '⛄': case '⛅': case '⛎': case '⛔': case '⛪':
                case '⛲': case '⛳': case '⛵': case '⛺': case '⛽': case '✅': case '✊': case '✋':
                case '✨': case '❌': case '❎': case '❓': case '❔': case '❕': case '❗': case '➕':
                case '➖': case '➗': case '➰': case '➿': case '⬛': case '⬜': case '⭐': case '⭕':
                    return true;
            }
            return c >= '♈' && c <= '♓'; // zodiac
        }

        static bool CanTakeEmojiMark(char c)
        {
            return (c >= '←' && c <= '⯿') || c == '©' || c == '®' || c == '‼' || c == '⁉' || c == '™'
                || c == 'ℹ' || c == '〰' || c == '〽' || c == '㊗' || c == '㊙';
        }

        static bool IsAstralEmoji(int cp) { return cp >= 0x1F000 && cp <= 0x1FAFF; }
        static bool IsRegional(int cp) { return cp >= 0x1F1E6 && cp <= 0x1F1FF; }
        static bool IsSkinTone(int cp) { return cp >= 0x1F3FB && cp <= 0x1F3FF; }

        public static bool Has(string s)
        {
            if (s == null) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '‍') continue; // nearly all text stops here
                if (c == '‍' || c == '️' || IsBmpEmoji(c)) return true;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && IsAstralEmoji(char.ConvertToUtf32(c, s[i + 1]))) return true;
            }
            return false;
        }

        static int CodePointAt(string s, int i, out int length)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { length = 2; return char.ConvertToUtf32(s[i], s[i + 1]); }
            length = 1;
            return s[i];
        }

        static readonly Dictionary<string, TextRun[]> splits = new Dictionary<string, TextRun[]>();

        // Breaks text into runs of ordinary text and single emoji. An emoji can be several
        // characters: a base, a skin tone, parts joined by U+200D, two letters making a flag.
        public static TextRun[] Split(string s)
        {
            TextRun[] cached;
            if (splits.TryGetValue(s, out cached)) return cached;

            List<TextRun> runs = new List<TextRun>();
            StringBuilder text = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                int len, cp = CodePointAt(s, i, out len);
                int start = i;
                bool emoji = false;

                if (IsRegional(cp))
                {
                    int len2 = 0;
                    if (i + len < s.Length && IsRegional(CodePointAt(s, i + len, out len2))) { i += len + len2; emoji = true; }
                }
                else if ((cp >= '0' && cp <= '9') || cp == '#' || cp == '*')
                {
                    // keycaps: a digit, the emoji mark, then the enclosing key
                    if (i + 2 < s.Length && s[i + 1] == '️' && s[i + 2] == '⃣') { i += 3; emoji = true; }
                }
                else if (IsAstralEmoji(cp) || (len == 1 && (IsBmpEmoji((char)cp) || (CanTakeEmojiMark((char)cp) && i + 1 < s.Length && s[i + 1] == '️'))))
                {
                    i += len;
                    emoji = true;
                    while (i < s.Length)
                    {
                        int l2, next = CodePointAt(s, i, out l2);
                        if (next == 0xFE0F || next == 0x20E3 || IsSkinTone(next)) { i += l2; continue; }
                        if (next == 0x200D && i + l2 < s.Length) // joined to another emoji
                        {
                            int l3, after = CodePointAt(s, i + l2, out l3);
                            if (IsAstralEmoji(after) || (l3 == 1 && (IsBmpEmoji((char)after) || CanTakeEmojiMark((char)after)))) { i += l2 + l3; continue; }
                        }
                        break;
                    }
                }

                if (emoji)
                {
                    if (text.Length > 0) { runs.Add(new TextRun { Text = text.ToString() }); text.Length = 0; }
                    runs.Add(new TextRun { IsEmoji = true, Text = s.Substring(start, i - start) });
                }
                else
                {
                    // stray joiners and emoji marks would otherwise show as boxes
                    if (cp != 0xFE0F && cp != 0x200D) text.Append(s, i, len);
                    i += len;
                }
            }
            if (text.Length > 0) runs.Add(new TextRun { Text = text.ToString() });

            if (splits.Count > 4000) splits.Clear();
            return splits[s] = runs.ToArray();
        }

        // ------------------------------------------------------------ drawing one emoji

        static readonly Dictionary<string, Bitmap> pictures = new Dictionary<string, Bitmap>();

        // The picture for one emoji at a text size (the font's height in pixels), or null if it
        // cannot be drawn, in which case the caller falls back to plain text.
        public static Bitmap Get(string emoji, int px)
        {
            string key = px + ":" + emoji;
            Bitmap b;
            if (pictures.TryGetValue(key, out b)) return b;
            if (pictures.Count > 3000)
            {
                foreach (Bitmap old in pictures.Values) if (old != null) old.Dispose();
                pictures.Clear();
            }

            int l1, l2 = 0;
            int first = CodePointAt(emoji, 0, out l1);
            if (IsRegional(first) && l1 < emoji.Length && IsRegional(CodePointAt(emoji, l1, out l2)))
            {
                // Windows' emoji font has no country flags; use the app's own
                string code = "" + (char)('A' + first - 0x1F1E6) + (char)('A' + CodePointAt(emoji, l1, out l2) - 0x1F1E6);
                int h = Math.Max(8, (int)Math.Round(px * 0.92)), w = (int)Math.Round(h * 1.45);
                b = new Bitmap(w + 2, h, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b)) Flags.Draw(g, code, new Rectangle(1, 0, w, h));
            }
            else b = Render(emoji, px);
            return pictures[key] = b;
        }

        public static bool Available { get { return Init(); } }

        // ---- Direct2D and DirectWrite, called through their COM tables directly so the app
        // ---- needs no extra libraries. The numbers are each method's position in its interface.

        const int ID2D1Factory_CreateDCRenderTarget = 16;
        const int RT_CreateSolidColorBrush = 8, RT_DrawText = 27, RT_SetTextAntialiasMode = 34, RT_Clear = 47, RT_BeginDraw = 48, RT_EndDraw = 49, RT_BindDC = 57;
        const int IDWriteFactory_CreateTextFormat = 15, IDWriteTextFormat_SetWordWrapping = 5;
        const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
        const int D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 4, D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct TargetProperties { public int Type, Format, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }
        [StructLayout(LayoutKind.Sequential)]
        struct ColorF { public float R, G, B, A; }
        [StructLayout(LayoutKind.Sequential)]
        struct RectF { public float Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct RectI { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateDCRenderTargetFn(IntPtr self, ref TargetProperties properties, out IntPtr target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int BindDCFn(IntPtr self, IntPtr hdc, ref RectI rect);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void BeginDrawFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EndDrawFn(IntPtr self, IntPtr tag1, IntPtr tag2);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void ClearFn(IntPtr self, ref ColorF color);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void SetIntFn(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetIntResultFn(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateSolidColorBrushFn(IntPtr self, ref ColorF color, IntPtr brushProperties, out IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void DrawTextFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, int length, IntPtr format, ref RectF layout, IntPtr brush, int options, int measuringMode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateTextFormatFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style, int stretch, float size,
            [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);

        [DllImport("d2d1.dll")]
        static extern int D2D1CreateFactory(int factoryType, ref Guid iid, IntPtr options, out IntPtr factory);
        [DllImport("dwrite.dll")]
        static extern int DWriteCreateFactory(int factoryType, ref Guid iid, out IntPtr factory);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapHeader info, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T));
        }

        static bool tried, ready;
        static IntPtr d2d, dwrite, target, brush;
        static readonly Dictionary<int, IntPtr> formats = new Dictionary<int, IntPtr>();

        static bool Init()
        {
            if (tried) return ready;
            tried = true;
            try
            {
                Guid d2dId = new Guid("06152247-6f50-465a-9245-118bfd3b6007"), dwriteId = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
                if (D2D1CreateFactory(0, ref d2dId, IntPtr.Zero, out d2d) < 0) return false;
                if (DWriteCreateFactory(0, ref dwriteId, out dwrite) < 0) return false;
                ready = CreateTarget();
            }
            catch (Exception) { ready = false; } // no Direct2D here: emoji stay as plain text
            return ready;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ReleaseFn(IntPtr self);

        static void Release(ref IntPtr obj)
        {
            if (obj != IntPtr.Zero) try { Method<ReleaseFn>(obj, 2)(obj); } catch (Exception) { }
            obj = IntPtr.Zero;
        }

        static bool CreateTarget()
        {
            Release(ref brush);
            Release(ref target);
            TargetProperties p = new TargetProperties { Format = DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED, DpiX = 96, DpiY = 96 };
            if (Method<CreateDCRenderTargetFn>(d2d, ID2D1Factory_CreateDCRenderTarget)(d2d, ref p, out target) < 0) return false;
            Method<SetIntFn>(target, RT_SetTextAntialiasMode)(target, D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            ColorF white = new ColorF { R = 1, G = 1, B = 1, A = 1 };
            return Method<CreateSolidColorBrushFn>(target, RT_CreateSolidColorBrush)(target, ref white, IntPtr.Zero, out brush) >= 0;
        }

        static IntPtr FormatFor(int px)
        {
            IntPtr f;
            if (formats.TryGetValue(px, out f)) return f;
            if (Method<CreateTextFormatFn>(dwrite, IDWriteFactory_CreateTextFormat)(dwrite, "Segoe UI Emoji", IntPtr.Zero, 400, 0, 5, px, "en-us", out f) < 0) return IntPtr.Zero;
            Method<SetIntResultFn>(f, IDWriteTextFormat_SetWordWrapping)(f, 1); // one line
            return formats[px] = f;
        }

        static Bitmap Render(string emoji, int px)
        {
            if (!Init()) return null;
            try
            {
                IntPtr format = FormatFor(px);
                if (format == IntPtr.Zero) return null;

                // drawn into a strip several emoji wide, then trimmed to what was actually inked
                int w = px * 8 + 8, h = (int)Math.Ceiling(px * 1.4) + 2;
                BitmapHeader header = new BitmapHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
                IntPtr bits, dc = CreateCompatibleDC(IntPtr.Zero);
                IntPtr dib = CreateDIBSection(dc, ref header, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) { DeleteDC(dc); return null; }
                IntPtr old = SelectObject(dc, dib);
                try
                {
                    RectI area = new RectI { Right = w, Bottom = h };
                    if (Method<BindDCFn>(target, RT_BindDC)(target, dc, ref area) < 0) return null;
                    ColorF clear = new ColorF();
                    RectF layout = new RectF { Left = 2, Top = 0, Right = w, Bottom = h };
                    Method<BeginDrawFn>(target, RT_BeginDraw)(target);
                    Method<ClearFn>(target, RT_Clear)(target, ref clear);
                    Method<DrawTextFn>(target, RT_DrawText)(target, emoji, emoji.Length, format, ref layout, brush, D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT, 0);
                    if (Method<EndDrawFn>(target, RT_EndDraw)(target, IntPtr.Zero, IntPtr.Zero) < 0)
                    {
                        ready = CreateTarget(); // the display driver reset; start again with a fresh target
                        return null;
                    }

                    byte[] data = new byte[w * h * 4];
                    Marshal.Copy(bits, data, 0, data.Length);
                    int minX = w, maxX = -1;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            if (data[(y * w + x) * 4 + 3] != 0) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
                    if (maxX < 0) return null;

                    int cw = maxX - minX + 1;
                    Bitmap result = new Bitmap(cw + 2, h, PixelFormat.Format32bppPArgb);
                    BitmapData locked = result.LockBits(new Rectangle(0, 0, result.Width, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(data, (y * w + minX) * 4, new IntPtr(locked.Scan0.ToInt64() + (long)y * locked.Stride + 4), cw * 4);
                    result.UnlockBits(locked);
                    return result;
                }
                finally { SelectObject(dc, old); DeleteObject(dib); DeleteDC(dc); }
            }
            catch (Exception) { return null; }
        }
    }
}
