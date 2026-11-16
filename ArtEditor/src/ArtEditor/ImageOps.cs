using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Dds;

namespace ArtEditor;

/// <summary>Immagine in memoria: BGRA 8 bit, riga 0 in alto.</summary>
public sealed record Pic(int W, int H, byte[] Bgra)
{
    public double Aspect => (double)W / H;
}

/// <summary>
/// Operazioni sulle immagini dell'editor: lettura dei formati comuni (PNG, JPG, BMP, GIF, TIFF, TGA, DDS), ritaglio,
/// ridimensionamento di qualita' (media sull'area quando si rimpicciolisce, bilineare quando si ingrandisce), conversione
/// nel formato delle voci di BMS (RGB, magenta = trasparente) e salvataggio in PNG.
/// </summary>
public static class ImageOps
{
    public const string OpenFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.tga;*.dds|All files|*.*";

    public static Pic Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".tga")
        {
            var (rgba, w, h) = TerrainTool.Core.Models.TgaImage.Read(File.ReadAllBytes(path));
            var b = new byte[rgba.Length];
            for (int i = 0; i < w * h; i++) { b[4 * i] = rgba[4 * i + 2]; b[4 * i + 1] = rgba[4 * i + 1]; b[4 * i + 2] = rgba[4 * i]; b[4 * i + 3] = rgba[4 * i + 3]; }
            return new Pic(w, h, b);
        }
        if (ext == ".dds")
        {
            var d = TerrainTool.Core.Models.DdsImage.Load(path, 8192) ?? throw new InvalidDataException("DDS format not supported");
            return new Pic(d.Width, d.Height, d.Bgra);
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource f = dec.Frames[0];
        if (f.Format != PixelFormats.Bgra32) f = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
        var px = new byte[f.PixelWidth * f.PixelHeight * 4];
        f.CopyPixels(px, f.PixelWidth * 4, 0);
        return new Pic(f.PixelWidth, f.PixelHeight, px);
    }

    public static Pic FromBundle(ResourceBundle b, ResourceBundle.Entry e) => new(e.Width, e.Height, b.ToBgra(e));

    public static BitmapSource ToBitmap(Pic p)
    {
        var bmp = BitmapSource.Create(p.W, p.H, 96, 96, PixelFormats.Bgra32, null, p.Bgra, p.W * 4);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(Pic p, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToBitmap(p)));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static Pic Crop(Pic p, Int32Rect r)
    {
        r = Clamp(r, p.W, p.H);
        var b = new byte[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(p.Bgra, ((r.Y + y) * p.W + r.X) * 4, b, y * r.Width * 4, r.Width * 4);
        return new Pic(r.Width, r.Height, b);
    }

    private static Int32Rect Clamp(Int32Rect r, int w, int h)
    {
        int x = Math.Clamp(r.X, 0, w - 1), y = Math.Clamp(r.Y, 0, h - 1);
        return new Int32Rect(x, y, Math.Clamp(r.Width, 1, w - x), Math.Clamp(r.Height, 1, h - y));
    }

    /// <summary>Il riquadro piu' grande con le proporzioni 'aspect', centrato nell'immagine.</summary>
    public static Int32Rect CenterCrop(int w, int h, double aspect)
    {
        int cw = w, ch = (int)Math.Round(w / aspect);
        if (ch > h) { ch = h; cw = (int)Math.Round(h * aspect); }
        return new Int32Rect((w - cw) / 2, (h - ch) / 2, Math.Max(1, cw), Math.Max(1, ch));
    }

    /// <summary>
    /// Riquadro con proporzioni diverse ricavato da quello scelto (stesso centro): piu' stretto tiene l'altezza, piu' largo tiene la
    /// larghezza; se esce dall'immagine si sposta, e se e' ancora troppo grande si rimpicciolisce.
    /// </summary>
    public static Int32Rect Derive(Int32Rect r, int w, int h, double aspect)
    {
        double cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0;
        double rw = r.Width, rh = r.Height;
        if (aspect < rw / rh) rw = rh * aspect; else rh = rw / aspect;
        if (rw > w) { rw = w; rh = rw / aspect; }
        if (rh > h) { rh = h; rw = rh * aspect; }
        double x = Math.Clamp(cx - rw / 2, 0, w - rw), y = Math.Clamp(cy - rh / 2, 0, h - rh);
        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(rw)), Math.Max(1, (int)Math.Round(rh)));
    }

    /// <summary>Ridimensiona (colori pesati con l'alpha, cosi' i bordi trasparenti non diventano scuri).</summary>
    public static Pic Resize(Pic p, int nw, int nh)
    {
        if (nw == p.W && nh == p.H) return p;
        var res = new byte[nw * nh * 4];
        double sx = (double)p.W / nw, sy = (double)p.H / nh;
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                double b = 0, g = 0, r = 0, a = 0, wsum = 0;
                if (sx > 1 || sy > 1)
                {
                    // media sull'area del pixel di arrivo
                    double x0 = x * sx, x1 = x0 + sx, y0 = y * sy, y1 = y0 + sy;
                    for (int yy = (int)y0; yy < Math.Min(p.H, (int)Math.Ceiling(y1)); yy++)
                    {
                        double wy = Math.Min(y1, yy + 1) - Math.Max(y0, yy);
                        if (wy <= 0) continue;
                        for (int xx = (int)x0; xx < Math.Min(p.W, (int)Math.Ceiling(x1)); xx++)
                        {
                            double wx = Math.Min(x1, xx + 1) - Math.Max(x0, xx);
                            if (wx <= 0) continue;
                            Add(xx, yy, wx * wy);
                        }
                    }
                }
                else
                {
                    double fx = (x + 0.5) * sx - 0.5, fy = (y + 0.5) * sy - 0.5;
                    int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                    double tx = fx - ix, ty = fy - iy;
                    Add(ix, iy, (1 - tx) * (1 - ty)); Add(ix + 1, iy, tx * (1 - ty));
                    Add(ix, iy + 1, (1 - tx) * ty); Add(ix + 1, iy + 1, tx * ty);
                }
                int o = 4 * (y * nw + x);
                if (a > 0) { res[o] = (byte)Math.Round(b / a); res[o + 1] = (byte)Math.Round(g / a); res[o + 2] = (byte)Math.Round(r / a); }
                res[o + 3] = (byte)Math.Round(wsum > 0 ? a / wsum : 0);

                void Add(int xx, int yy, double wgt)
                {
                    xx = Math.Clamp(xx, 0, p.W - 1); yy = Math.Clamp(yy, 0, p.H - 1);
                    int i = 4 * (yy * p.W + xx);
                    double al = p.Bgra[i + 3];
                    b += p.Bgra[i] * al * wgt; g += p.Bgra[i + 1] * al * wgt; r += p.Bgra[i + 2] * al * wgt; a += al * wgt; wsum += wgt;
                }
            }
        return new Pic(nw, nh, res);
    }

    /// <summary>
    /// RGB per ResourceBundle.SetImageRgb: con 'key' i pixel trasparenti (alpha &lt; 128) diventano magenta, il colore chiave di BMS;
    /// senza, la trasparenza si appoggia sul nero.
    /// </summary>
    public static byte[] ToRgb(Pic p, bool key)
    {
        var rgb = new byte[p.W * p.H * 3];
        for (int i = 0; i < p.W * p.H; i++)
        {
            int a = p.Bgra[4 * i + 3];
            if (key && a < 128) { rgb[3 * i] = 255; rgb[3 * i + 1] = 0; rgb[3 * i + 2] = 255; continue; }
            double k = key ? 1 : a / 255.0;
            rgb[3 * i] = (byte)(p.Bgra[4 * i + 2] * k); rgb[3 * i + 1] = (byte)(p.Bgra[4 * i + 1] * k); rgb[3 * i + 2] = (byte)(p.Bgra[4 * i] * k);
            // un magenta vero (non trasparente) si sposta di poco, se no BMS lo renderebbe trasparente
            if (key && rgb[3 * i] >= 248 && rgb[3 * i + 1] < 8 && rgb[3 * i + 2] >= 248) rgb[3 * i + 1] = 8;
        }
        return rgb;
    }

    /// <summary>
    /// Bandiera verticale come le OOB_FLAGS di BMS dalla bandiera orizzontale: il lato dell'asta (sinistra) va in alto, il lato
    /// superiore va a destra (stessa regola di TheaterArtJob.FlagImage). La versione scura ha i colori al 55%.
    /// </summary>
    public static Pic Vertical(Pic hor, int w, int h, bool dark)
    {
        var src = Resize(hor, h, w);   // orizzontale larga h e alta w
        var res = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = 4 * ((w - 1 - x) * h + y), di = 4 * (y * w + x);
                double k = dark ? 0.55 : 1;
                res[di] = (byte)(src.Bgra[si] * k); res[di + 1] = (byte)(src.Bgra[si + 1] * k); res[di + 2] = (byte)(src.Bgra[si + 2] * k); res[di + 3] = src.Bgra[si + 3];
            }
        return new Pic(w, h, res);
    }

    /// <summary>Le voci con il flag 0x40000000 usano il colore chiave (magenta) come trasparenza.</summary>
    /// <summary>
    /// Versione "premuta" di un pulsante: piu' scura e spostata di un pixel in basso a destra (la riga e la colonna che restano libere
    /// prendono il bordo, piu' scuro ancora), come i pulsanti in rilievo dei vecchi BMS. I pixel trasparenti restano trasparenti.
    /// </summary>
    public static Pic Pressed(Pic p, double dark = 0.78, int shift = 1)
    {
        var o = new byte[p.Bgra.Length];
        for (int y = 0; y < p.H; y++)
            for (int x = 0; x < p.W; x++)
            {
                int sx = Math.Max(0, x - shift), sy = Math.Max(0, y - shift);
                bool edge = x < shift || y < shift;
                int si = 4 * (sy * p.W + sx), di = 4 * (y * p.W + x);
                double k = edge ? dark * 0.75 : dark;
                o[di] = (byte)(p.Bgra[si] * k); o[di + 1] = (byte)(p.Bgra[si + 1] * k); o[di + 2] = (byte)(p.Bgra[si + 2] * k);
                o[di + 3] = p.Bgra[si + 3];
            }
        return new Pic(p.W, p.H, o);
    }

    /// <summary>Versione "disattivata": in grigio e piu' chiara.</summary>
    public static Pic Disabled(Pic p)
    {
        var o = (byte[])p.Bgra.Clone();
        for (int i = 0; i < o.Length; i += 4)
        {
            int g = (o[i] * 11 + o[i + 1] * 59 + o[i + 2] * 30) / 100;
            byte v = (byte)Math.Min(255, g * 0.6 + 70);
            o[i] = o[i + 1] = o[i + 2] = v;
        }
        return new Pic(p.W, p.H, o);
    }

    public static bool UsesKey(ResourceBundle.Entry e) => (e.Flags & 0x40000000) != 0;
}
