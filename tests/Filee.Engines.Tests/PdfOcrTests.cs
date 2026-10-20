// OCR of scanned PDF pages (DocumentOptions.RecognizeText): Tesseract's TSV output becomes words and lines, a scanned
// page becomes editable text in DOCX and TXT, and a scan stays a picture when OCR is off. The tests that run Tesseract
// are skipped when it is not installed (pwsh build/fetch-engines.ps1 -Only tesseract); the scan is drawn in the test.

using System.Runtime.Versioning;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using static Filee.Engines.Tests.DocxAssert;

namespace Filee.Engines.Tests;

public class PdfOcrTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog";

    private static bool TesseractInstalled => TesseractOcr.Locate() is not null;

    /// <summary>One A4 page that is only a picture: a headline and two lines of black text on white, no text layer.</summary>
    private static string ScannedPdf(string folder)
    {
        using var bitmap = new SKBitmap(1654, 2339);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont(SKTypeface.Default, 52);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("Invoice 20260814", 200, 300, font, paint);
            canvas.DrawText(Sentence, 200, 480, font, paint);
            canvas.DrawText("Payment is due within thirty days", 200, 560, font, paint);
        }
        var jpeg = Path.Combine(folder, "scan.jpg");
        using (var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92))
            File.WriteAllBytes(jpeg, data.ToArray());
        var pdf = new PdfBuilder().NewPage();
        pdf.Picture(jpeg, 0, 0, 595, 842);
        return pdf.Save(Path.Combine(folder, "scan.pdf"));
    }

    /// <summary>Draws a page of text with <paramref name="draw"/> and wraps it in a PDF as one picture, with no text layer.</summary>
    private static string ScannedPage(string folder, string name, int width, int height, Action<SKCanvas, SKFont, SKPaint> draw,
        double pageWidth = 595, double pageHeight = 842)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont(SKTypeface.Default, 52);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            draw(canvas, font, paint);
        }
        var jpeg = Path.Combine(folder, name + ".jpg");
        using (var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92))
            File.WriteAllBytes(jpeg, data.ToArray());
        var pdf = new PdfBuilder().NewPage(pageWidth, pageHeight);
        pdf.Picture(jpeg, 0, 0, pageWidth, pageHeight);
        return pdf.Save(Path.Combine(folder, name + ".pdf"));
    }

    private async Task<string> ConvertAsync(string input, string target, bool recognize)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target, Document = { RecognizeText = recognize } });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Tesseract_output_becomes_words_with_their_lines()
    {
        const string tsv =
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\r\n" +
            "1\t1\t0\t0\t0\t0\t0\t0\t1654\t2339\t-1\t\r\n" +
            "5\t1\t1\t1\t1\t1\t200\t250\t120\t50\t96.5\tHello\r\n" +
            "5\t1\t1\t1\t1\t2\t340\t250\t140\t50\t91\tworld\r\n" +
            "5\t1\t1\t1\t2\t1\t200\t330\t90\t50\t12\tspeck\r\n" +
            "5\t1\t1\t1\t2\t2\t310\t330\t100\t50\t88\t \r\n" +
            "5\t1\t2\t1\t1\t1\t200\t900\t200\t50\t90\tSecond\r\n";

        var words = TesseractOcr.ParseTsv(tsv);

        Assert.Equal(["Hello", "world", "Second"], words.Select(w => w.Text));
        Assert.Equal([0, 0, 1], words.Select(w => w.Line));
        Assert.Equal(new OcrWord("world", 340, 250, 480, 300, 0), words[1]);
    }

    [Fact]
    public void Recognized_words_are_placed_on_the_page_in_points()
    {
        // 300 dpi: 100 px are 24 points. The page is 842 points high.
        var found = new List<OcrWord> { new("Hello", 300, 300, 600, 350, 0), new("world", 650, 310, 900, 360, 0) };

        var words = PdfDocumentReader.OcrTextWords(found, 72.0 / 300, 842);

        Assert.Equal(2, words.Count);
        Assert.Equal(72, words[0].Left, 3);
        Assert.Equal(144, words[0].Right, 3);
        // The words of a line share the line's top and bottom (the highest and lowest of them), so they are one line.
        Assert.All(words, w => Assert.Equal(842 - 300 * 0.24, w.Top, 3));
        Assert.All(words, w => Assert.Equal(842 - 360 * 0.24, w.Bottom, 3));
        Assert.Equal(words[0].Baseline, words[1].Baseline);
        Assert.Equal(words[0].Size, words[1].Size);
        // The line is 60 px (14.4 pt) from its highest to its lowest letter: about one em.
        Assert.Equal(14.4, words[0].Size, 3);
    }

    [Fact]
    public async Task A_scan_stays_a_picture_when_recognition_is_off()
    {
        var path = ScannedPdf(fx.NewFolder());

        var docx = await ConvertAsync(path, "docx", recognize: false);

        ValidPackage(docx);
        var paragraphs = Paragraphs(ReadBack(docx).Sections.SelectMany(s => s.Blocks)).ToList();
        Assert.Contains(paragraphs, p => p.Inlines.OfType<HImage>().Any());
        Assert.DoesNotContain(paragraphs, p => TextOf(p).Contains("quick", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_scan_becomes_editable_text_in_docx()
    {
        Assert.SkipUnless(TesseractInstalled, "Tesseract is not installed (pwsh build/fetch-engines.ps1 -Only tesseract).");
        var path = ScannedPdf(fx.NewFolder());

        var docx = await ConvertAsync(path, "docx", recognize: true);

        ValidPackage(docx);
        var paragraphs = Paragraphs(ReadBack(docx).Sections.SelectMany(s => s.Blocks)).ToList();
        var text = string.Join("\n", paragraphs.Select(TextOf));
        Assert.Contains("quick brown fox", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thirty days", text, StringComparison.OrdinalIgnoreCase);
        // Text only: the page is no longer a picture behind empty text.
        Assert.DoesNotContain(paragraphs, p => p.Inlines.OfType<HImage>().Any());
    }

    [Fact]
    public async Task A_scan_becomes_text_in_txt_and_the_error_for_a_scan_goes_away()
    {
        Assert.SkipUnless(TesseractInstalled, "Tesseract is not installed (pwsh build/fetch-engines.ps1 -Only tesseract).");
        var path = ScannedPdf(fx.NewFolder());

        var without = await fx.ConvertAsync([path], new Preset { TargetFormat = "txt" });
        Assert.NotEqual(JobState.Completed, without.State);

        var txt = await File.ReadAllTextAsync(await ConvertAsync(path, "txt", recognize: true), TestContext.Current.CancellationToken);
        Assert.Contains("quick brown fox", txt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Columns_of_a_scan_are_read_one_after_the_other()
    {
        Assert.SkipUnless(TesseractInstalled, "Tesseract is not installed (pwsh build/fetch-engines.ps1 -Only tesseract).");
        var path = ScannedPage(fx.NewFolder(), "columns", 1654, 2339, (canvas, font, paint) =>
        {
            // Each line is far shorter than the gutter between the columns, so the page has two real columns.
            string[] left = ["Alpha opens the report", "Bravo follows the plan", "Charlie ends this side"];
            string[] right = ["Delta starts the right", "Echo continues the story", "Foxtrot ends the page"];
            for (var i = 0; i < 3; i++)
            {
                // Ordinary line spacing (about 1.2 times the type size), so each column is one paragraph.
                canvas.DrawText(left[i], 120, 300 + i * 64, font, paint);
                canvas.DrawText(right[i], 900, 300 + i * 64, font, paint);
            }
        });

        var txt = await File.ReadAllTextAsync(await ConvertAsync(path, "txt", recognize: true), TestContext.Current.CancellationToken);

        var order = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" }.Select(w => txt.IndexOf(w, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.All(order, i => Assert.True(i >= 0, txt));
        if (!order.SequenceEqual(order.Order()))
            Assert.Fail("Read out of column order:\n" + txt);
    }

    [Fact]
    public async Task A_scan_stored_sideways_on_a_turned_page_is_read_upright()
    {
        Assert.SkipUnless(TesseractInstalled, "Tesseract is not installed (pwsh build/fetch-engines.ps1 -Only tesseract).");
        var folder = fx.NewFolder();
        // The picture is stored turned 90° counter-clockwise on a landscape page, and the page is turned 90° clockwise to
        // show it upright (what a scanner feeding paper sideways produces).
        var path = ScannedPage(folder, "turned", 2339, 1654, (canvas, font, paint) =>
        {
            canvas.Translate(0, 1654);
            canvas.RotateDegrees(-90);
            canvas.DrawText("Turned pages are read upright", 200, 300, font, paint);
            canvas.DrawText("Second line of the turned page", 200, 400, font, paint);
        }, pageWidth: 842, pageHeight: 595);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Rotate = 90;
            document.Save(path);
        }

        var txt = await File.ReadAllTextAsync(await ConvertAsync(path, "txt", recognize: true), TestContext.Current.CancellationToken);
        Assert.Contains("read upright", txt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Second line", txt, StringComparison.OrdinalIgnoreCase);

        // The page keeps its upright, portrait shape in the document.
        var docx = await ConvertAsync(path, "docx", recognize: true);
        ValidPackage(docx);
        var text = string.Join("\n", Paragraphs(ReadBack(docx).Sections.SelectMany(s => s.Blocks)).Select(TextOf));
        Assert.Contains("read upright", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failing_tesseract_leaves_the_scan_as_a_picture()
    {
        // The stand-in for a broken Tesseract is a shell script, so this runs on Linux and macOS only.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The failing stand-in is a shell script.");
        var folder = fx.NewFolder();
        var path = ScannedPdf(folder);
        var fake = Path.Combine(folder, "tesseract-fake");
        Directory.CreateDirectory(fake);
        var program = Path.Combine(fake, Filee.Engines.Infrastructure.EngineEnvironment.ProgramName("tesseract.exe"));
        File.WriteAllText(program, "#!/bin/sh\necho broken >&2\nexit 3\n");
        if (!OperatingSystem.IsWindows())
            MakeExecutable(program);

        // Pointing the user's own copy at it: the page must fall back to its picture, and the job still completes.
        var before = Filee.Engines.Infrastructure.EngineEnvironment.OwnCopies;
        Filee.Engines.Infrastructure.EngineEnvironment.OwnCopies = new Dictionary<string, string>(before) { ["tesseract"] = fake };
        try
        {
            var docx = await ConvertAsync(path, "docx", recognize: true);

            ValidPackage(docx);
            Assert.Contains(Paragraphs(ReadBack(docx).Sections.SelectMany(s => s.Blocks)), p => p.Inlines.OfType<HImage>().Any());
        }
        finally
        {
            Filee.Engines.Infrastructure.EngineEnvironment.OwnCopies = before;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void MakeExecutable(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
