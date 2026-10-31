using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Tidybird
{
    // The Tidybird logo: an orange bird nesting inside a folder, on a teal tile.
    // Drawn on a 256x256 design grid and scaled to whatever size is asked for.
    public static class Logo
    {
        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.ScaleTransform(size / 256f, size / 256f);
                Draw(g);
            }
            return bmp;
        }

        // Writes a multi-size .ico with PNG-compressed entries (supported since Windows Vista).
        public static void SaveIco(string path, int[] sizes)
        {
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (var bmp = Render(s))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim);
                    w.Write(dim);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var png in pngs) w.Write(png);
            }
        }

        static void Draw(Graphics g)
        {
            // Tile
            using (var p = RoundRect(8, 8, 240, 240, 56))
            using (var b = new LinearGradientBrush(new PointF(8, 8), new PointF(248, 248), Hex("#2DD4BF"), Hex("#0F766E")))
                g.FillPath(b, p);

            // Folder back, with its tab
            using (var b = new SolidBrush(Hex("#CCFBF1")))
            {
                using (var p = RoundRect(40, 84, 84, 40, 12)) g.FillPath(b, p);
                using (var p = RoundRect(40, 100, 176, 112, 18)) g.FillPath(b, p);
            }

            // Bird: tail and crest first so the body overlaps them
            using (var b = new SolidBrush(Hex("#C2410C")))
                g.FillPolygon(b, new[] { new PointF(92, 112), new PointF(58, 84), new PointF(70, 130) });
            using (var b = new SolidBrush(Hex("#EA580C")))
            {
                g.FillEllipse(b, 114, 54, 14, 26);
                g.FillEllipse(b, 127, 52, 12, 24);
            }

            var body = new RectangleF(84, 68, 88, 88);
            using (var b = new LinearGradientBrush(body, Hex("#FDBA74"), Hex("#EA580C"), 60f))
                g.FillEllipse(b, body);

            var state = g.Save();
            g.TranslateTransform(112, 116);
            g.RotateTransform(-20);
            using (var b = new SolidBrush(Color.FromArgb(210, Hex("#C2410C"))))
                g.FillEllipse(b, -26, -13, 52, 26);
            g.Restore(state);

            using (var b = new SolidBrush(Hex("#FACC15")))
                g.FillPolygon(b, new[] { new PointF(164, 103), new PointF(196, 113), new PointF(164, 123) });
            using (var b = new SolidBrush(Hex("#1F2937")))
                g.FillEllipse(b, 140, 90, 15, 15);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, 147, 93, 5, 5);
            using (var b = new SolidBrush(Color.FromArgb(150, Hex("#FB7185"))))
                g.FillEllipse(b, 146, 111, 13, 9);

            // Folder front: soft shadow, then the face that tucks the bird in
            using (var p = RoundRect(32, 127, 192, 89, 18))
            using (var b = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
                g.FillPath(b, p);
            using (var p = RoundRect(32, 132, 192, 84, 18))
            using (var b = new LinearGradientBrush(new PointF(0, 132), new PointF(0, 216), Color.White, Hex("#E0F2F1")))
                g.FillPath(b, p);

            // Sorted rows on the folder face
            FillBar(g, 56, 154, 64, "#14B8A6");
            FillBar(g, 56, 172, 104, "#F97316");
            FillBar(g, 56, 190, 44, "#0EA5E9");
        }

        static void FillBar(Graphics g, float x, float y, float w, string color)
        {
            using (var p = RoundRect(x, y, w, 10, 5))
            using (var b = new SolidBrush(Hex(color)))
                g.FillPath(b, p);
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Hex(string html)
        {
            return ColorTranslator.FromHtml(html);
        }
    }
}
