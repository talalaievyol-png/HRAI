using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace HrOs.Ai.Tests.Support;

/// <summary>Real PDF, Word and text files built in code, so no sample files are needed.</summary>
public static class SampleFiles
{
    public const string AliceCv =
        "Alice Smith\n" +
        "Email: alice@example.com\n" +
        "Phone: 03001234567\n" +
        "Address: Multan\n" +
        "Skills: Python, SQL, FastAPI\n" +
        "Education: B.S. Computer Science - COMSATS\n" +
        "Experience: Developer at XY Corp (2021-present)\n";

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var y = 800.0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            page.AddText(line, 11, new PdfPoint(40, y), font);
            y -= 16;
        }

        return builder.Build();
    }

    /// <summary>A PDF page with no text at all, like a scan.</summary>
    public static byte[] EmptyPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        return builder.Build();
    }

    public static byte[] Docx(IEnumerable<string> paragraphs, IEnumerable<string[]>? tableRows = null)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            foreach (var text in paragraphs)
                body.Append(new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));

            if (tableRows is not null)
            {
                var table = new Table();
                foreach (var row in tableRows)
                {
                    var tableRow = new TableRow();
                    foreach (var cell in row)
                        tableRow.Append(new TableCell(new Paragraph(new Run(new Text(cell)))));
                    table.Append(tableRow);
                }

                body.Append(table);
            }
        }

        return stream.ToArray();
    }
}
