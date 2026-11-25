using System.Drawing.Drawing2D;
using System.Drawing.Printing;

namespace Kpos;

internal static class BarcodeSheet
{
    // ISO/IEC 15417 run lengths, alternating bars and spaces by symbol value.
    private static readonly string[] Patterns = """
        212222 222122 222221 121223 121322 131222 122213 122312 132212 221213 221312 231212
        112232 122132 122231 113222 123122 123221 223211 221132 221231 213212 223112 312131
        311222 321122 321221 312212 322112 322211 212123 212321 232121 111323 131123 131321
        112313 132113 132311 211313 231113 231311 112133 112331 132131 113123 113321 133121
        313121 211331 231131 213113 213311 213131 311123 311321 331121 312113 312311 332111
        314111 221411 431111 111224 111422 121124 121421 141122 141221 112214 112412 122114
        122411 142112 142211 241211 221114 413111 241112 134111 111242 121142 121241 114212
        124112 124211 411212 421112 421211 212141 214121 412121 111143 111341 131141 114113
        114311 411113 411311 113141 114131 311141 411131 211412 211214 211232 2331112
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(ch => ch < ' ' || ch > '~'))
            throw new ArgumentException($"Barcode '{value}' requires printable ASCII characters.");
        bool pairs = value.Length % 2 == 0 && value.All(char.IsAsciiDigit);
        var codes = new List<int> { pairs ? 105 : 104 };
        if (pairs)
            for (int i = 0; i < value.Length; i += 2) codes.Add((value[i] - '0') * 10 + value[i + 1] - '0');
        else codes.AddRange(value.Select(ch => ch - 32));
        int check = codes[0];
        for (int i = 1; i < codes.Count; i++) check += codes[i] * i;
        codes.Add(check % 103); codes.Add(106);
        return string.Concat(codes.Select(code => Patterns[code]));
    }

    internal static void Preview(IWin32Window owner, IEnumerable<Product> selection, int copies)
    {
        var items = selection.SelectMany(p => Enumerable.Repeat((Product:p, Bars:Encode(p.Barcode)),copies)).ToArray();
        // Graphics coordinates are hundredths of an inch. Reserve ten modules of quiet zone on each side.
        if (items.Any(i => (i.Bars.Sum(ch => ch - '0') + 20) * .75f > 246))
            throw new ArgumentException("One of the selected barcodes is too long to print legibly on this label.");
        using var doc = new PrintDocument { DocumentName = "kPOS barcode labels" };
        doc.DefaultPageSettings.PaperSize = new PaperSize("Letter", 850, 1100);
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.DefaultPageSettings.Landscape = false;
        using var font = new Font("Arial", 9);
        using var digits = new Font("Arial", 8);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        int index = 0;
        doc.BeginPrint += (_, _) => index = 0;
        doc.PrintPage += (_, e) =>
        {
            Graphics g = e.Graphics!;
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            g.SmoothingMode = SmoothingMode.None;
            for (int slot = 0; slot < 30 && index < items.Length; slot++, index++)
            {
                var (p, bars) = items[index];
                float x = 18.75f + slot % 3 * 275, y = 50 + slot / 3 * 100;
                g.DrawString(p.Description, font, Brushes.Black, new RectangleF(x + 5, y + 4, 252.5f, 24), center);
                int modules = bars.Sum(ch => ch - '0');
                float width = Math.Min(1.4f, 246f / (modules + 20));
                float cursor = x + (262.5f - modules * width) / 2;
                bool ink = true;
                foreach (char ch in bars)
                {
                    float run = (ch - '0') * width;
                    if (ink) g.FillRectangle(Brushes.Black, cursor, y + 30, run, 42);
                    cursor += run; ink = !ink;
                }
                g.DrawString(p.Barcode, digits, Brushes.Black, new RectangleF(x, y + 73, 262.5f, 20), center);
            }
            e.HasMorePages = index < items.Length;
        };
        using var preview = new PrintPreviewDialog { Document = doc, Width = 1050, Height = 800 };
        preview.ShowDialog(owner);
    }
}
