using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace HrOs.Ai.Cv;

/// <summary>
/// Reads the embedded text of a CV: PDF text layer, Word (.docx) or plain text.
/// There is no OCR here; a scanned PDF has no text layer and is refused by the caller
/// with a message that sends the candidate to the manual form.
/// </summary>
public static class CvTextReader
{
    /// <exception cref="CvReadException">Unsupported type, corrupt or password-protected file.</exception>
    public static string Read(byte[] contents, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".pdf" => ReadPdf(contents),
                ".docx" => ReadDocx(contents),
                ".txt" => ReadText(contents),
                ".doc" => throw CvReadException.Legacy(),
                _ => throw CvReadException.UnsupportedType(),
            };
        }
        catch (CvReadException)
        {
            throw;
        }
        catch (Exception)
        {
            // A public, unauthenticated route must never leak a 500 for a bad file.
            throw CvReadException.Corrupt();
        }
    }

    private static string ReadPdf(byte[] contents)
    {
        using var document = PdfDocument.Open(contents);
        var pages = document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page));
        return string.Join("\n", pages).Trim();
    }

    /// <summary>Paragraphs and table cells in the order they appear in the document.</summary>
    private static string ReadDocx(byte[] contents)
    {
        using var stream = new MemoryStream(contents, writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body
                   ?? throw CvReadException.Corrupt();

        var lines = new List<string>();
        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    lines.Add(paragraph.InnerText);
                    break;
                case Table table:
                    foreach (var cell in table.Descendants<TableCell>())
                        lines.Add(string.Join("\n", cell.Elements<Paragraph>().Select(p => p.InnerText)));
                    break;
            }
        }

        return string.Join("\n", lines).Trim();
    }

    private static string ReadText(byte[] contents) =>
        new UTF8Encoding(false).GetString(contents).Replace("﻿", "").Replace("�", "").Trim();
}
