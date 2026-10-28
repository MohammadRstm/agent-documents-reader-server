using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>Synthetic PDF files generated in code, so tests never depend on real documents.</summary>
public static class PdfFixtures
{
    // A valid 1x1 pixel PNG, used to simulate scanned (image-only) pages.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>Creates a PDF with one page per entry; each page holds the given lines of text.</summary>
    public static byte[] WithPages(params string[][] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 800.0;
            foreach (var line in lines)
            {
                page.AddText(line, 12, new PdfPoint(50, y), font);
                y -= 20;
            }
        }

        return builder.Build();
    }

    /// <summary>Creates a PDF with one single-line page per text.</summary>
    public static byte[] WithPageTexts(params string[] pageTexts) =>
        WithPages(pageTexts.Select(text => new[] { text }).ToArray());

    /// <summary>Pages that contain only an image and no text, like a scanned document.</summary>
    public static byte[] ImageOnly(int pageCount)
    {
        using var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            builder.AddPage(PageSize.A4).AddPng(OnePixelPng, new PdfRectangle(50, 50, 250, 250));
        }

        return builder.Build();
    }

    /// <summary>Page 1 has text; page 2 contains only an image.</summary>
    public static byte[] TextThenImageOnlyPage(string text)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(50, 800), font);
        builder.AddPage(PageSize.A4).AddPng(OnePixelPng, new PdfRectangle(50, 50, 250, 250));
        return builder.Build();
    }

    /// <summary>Pages with no content at all.</summary>
    public static byte[] Blank(int pageCount)
    {
        using var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            builder.AddPage(PageSize.A4);
        }

        return builder.Build();
    }

    /// <summary>Starts with a valid PDF header but is otherwise garbage.</summary>
    public static byte[] Corrupt() =>
        Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 400) + "\nthis is not a pdf body\n");

    /// <summary>A file of the given size that starts with a PDF header (content is padding).</summary>
    public static byte[] PdfHeaderPadded(int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Array.Fill(content, (byte)' ');
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(content, 0);
        return content;
    }

    /// <summary>
    /// A hand-built PDF whose /Encrypt dictionary uses the standard security handler with a user
    /// password that is not empty, so it cannot be opened without the password.
    /// </summary>
    public static byte[] PasswordProtected()
    {
        var randomHash = string.Concat(Enumerable.Repeat("8F3A", 16));
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            $"<< /Filter /Standard /V 1 /R 2 /O <{randomHash}> /U <{randomHash}> /P -4 >>",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefOffset = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        const string fileId = "0123456789ABCDEF0123456789ABCDEF";
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{fileId}><{fileId}>] >>\n");
        pdf.Append(CultureInfo.InvariantCulture, $"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
