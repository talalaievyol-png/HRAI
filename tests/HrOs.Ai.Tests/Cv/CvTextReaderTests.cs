using HrOs.Ai.Cv;
using HrOs.Ai.Tests.Support;

namespace HrOs.Ai.Tests.Cv;

public class CvTextReaderTests
{
    [Fact]
    public void Plain_text_is_read_and_a_byte_order_mark_is_dropped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(SampleFiles.Text("Alice Smith\nMultan")).ToArray();

        Assert.Equal("Alice Smith\nMultan", CvTextReader.Read(bytes, "cv.txt"));
    }

    [Fact]
    public void A_pdf_text_layer_is_read()
    {
        var text = CvTextReader.Read(SampleFiles.Pdf(SampleFiles.AliceCv), "cv.pdf");

        Assert.Contains("Alice Smith", text);
        Assert.Contains("alice@example.com", text);
        Assert.Contains("Python, SQL, FastAPI", text);
    }

    [Fact]
    public void A_pdf_with_no_text_layer_reads_as_empty()
    {
        Assert.Equal("", CvTextReader.Read(SampleFiles.EmptyPdf(), "scan.pdf"));
    }

    [Fact]
    public void Word_paragraphs_and_table_cells_are_read_in_document_order()
    {
        var bytes = SampleFiles.Docx(
            ["Sana Malik", "Education"],
            [["BS Accounting", "University of Sargodha"], ["CGPA", "3.4"]]);

        var text = CvTextReader.Read(bytes, "cv.docx");

        Assert.Equal(
            ["Sana Malik", "Education", "BS Accounting", "University of Sargodha", "CGPA", "3.4"],
            text.Split('\n'));
    }

    [Fact]
    public void The_file_type_comes_from_the_name_in_any_case()
    {
        Assert.Contains("Alice", CvTextReader.Read(SampleFiles.Text("Alice"), "CV.TXT"));
    }

    [Fact]
    public void A_legacy_doc_file_tells_the_candidate_what_to_upload_instead()
    {
        var error = Assert.Throws<CvReadException>(() => CvTextReader.Read([1, 2, 3], "cv.doc"));

        Assert.Equal(CvReadProblem.Unsupported, error.Problem);
        Assert.Contains("upload .docx or PDF", error.Message);
    }

    [Theory]
    [InlineData("cv.rtf")]
    [InlineData("cv.png")]
    [InlineData("cv")]
    public void An_unsupported_type_is_refused(string name)
    {
        var error = Assert.Throws<CvReadException>(() => CvTextReader.Read([1, 2, 3], name));

        Assert.Equal(CvReadProblem.Unsupported, error.Problem);
    }

    [Theory]
    [InlineData("cv.pdf")]
    [InlineData("cv.docx")]
    public void A_corrupt_file_is_a_clean_error_never_a_crash(string name)
    {
        var error = Assert.Throws<CvReadException>(() => CvTextReader.Read(SampleFiles.Text("not a real file"), name));

        Assert.Equal(CvReadProblem.Unreadable, error.Problem);
        Assert.Contains("corrupt or password-protected", error.Message);
    }
}
