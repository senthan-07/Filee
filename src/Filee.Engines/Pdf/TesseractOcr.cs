// Text recognition for scanned PDF pages with Tesseract (Apache-2.0, a separate program downloaded on demand into
// engines/tesseract). A page image goes in, words with their boxes come out (Tesseract's TSV output), which
// PdfDocumentReader turns into the same lines and paragraphs as the text of any other PDF page.

using System.Globalization;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Pdf;

/// <summary>A word found by OCR: its box in pixels of the page image (y downwards) and the line it belongs to.</summary>
/// <param name="Line">Number of the line on the page (blocks and paragraphs in reading order), the same for all its words.</param>
internal sealed record OcrWord(string Text, int Left, int Top, int Right, int Bottom, int Line);

/// <summary>Runs Tesseract on page images.</summary>
internal static class TesseractOcr
{
    /// <summary>Resolution the pages are rendered at for recognition; Tesseract reads best at about 300 dpi.</summary>
    public const int Dpi = 300;

    /// <summary>The language Filee recognizes (Tesseract's three-letter code); English first.</summary>
    public const string Language = "eng";

    /// <summary>Words Tesseract is less sure of than this (percent) are noise from specks and borders.</summary>
    private const double MinimumConfidence = 20;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Filee's own Tesseract (engines/tesseract, downloaded on demand), the user's copy, or one on the PC.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("tesseract", "tesseract.exe") ?? EngineEnvironment.BundledProgram("tesseract", "tesseract.exe")
        ?? EngineEnvironment.SystemProgram("tesseract", "tesseract.exe");

    /// <summary>Recognizes the words of the picture <paramref name="imagePath"/> with <paramref name="tesseract"/>.</summary>
    public static async Task<IReadOnlyList<OcrWord>> RecognizeAsync(string tesseract, string imagePath, CancellationToken cancellationToken)
    {
        // Filee's own copy reads Filee's English model; a copy of the user's or the PC's finds its data itself.
        var environment = new Dictionary<string, string>();
        if (DataFolder(tesseract) is { } data)
            environment["TESSDATA_PREFIX"] = data;

        // "stdout" writes the result to standard output; --psm 3 finds the columns and blocks of a whole page; the image
        // has no resolution of its own (PDFium's PNG), so it is told. The tessedit_create_tsv parameter asks for the
        // TSV output without needing the "tsv" config file in the data folder.
        var result = await ProcessRunner.RunAsync(tesseract,
            [imagePath, "stdout", "-l", Language, "--psm", "3", "--dpi", Dpi.ToString(CultureInfo.InvariantCulture), "-c", "tessedit_create_tsv=1"],
            Timeout, cancellationToken, environment: environment);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Tesseract failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        return ParseTsv(result.StandardOutput);
    }

    /// <summary>
    /// The folder with Filee's English model (engines/tessdata-eng), when <paramref name="tesseract"/> is Filee's own
    /// copy; null for a copy of the user's or the PC's, which has its own data.
    /// </summary>
    private static string? DataFolder(string tesseract)
    {
        if (EngineEnvironment.FindBundled("tesseract") is not { } program || EngineEnvironment.FindBundled("tessdata-eng") is not { } data)
            return null;
        var own = Path.GetFullPath(tesseract).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(program)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return own && File.Exists(Path.Combine(data, Language + ".traineddata")) ? data : null;
    }

    /// <summary>
    /// The words of Tesseract's TSV output (<c>level page block par line word left top width height conf text</c>).
    /// Rows that are not words, empty words and words below <see cref="MinimumConfidence"/> are left out.
    /// </summary>
    internal static IReadOnlyList<OcrWord> ParseTsv(string tsv)
    {
        var words = new List<OcrWord>();
        var lines = new Dictionary<(int Block, int Paragraph, int Line), int>();
        foreach (var row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5")
                continue;
            if (!TryInt(cells[2], out var block) || !TryInt(cells[3], out var paragraph) || !TryInt(cells[4], out var line)
                || !TryInt(cells[6], out var left) || !TryInt(cells[7], out var top)
                || !TryInt(cells[8], out var width) || !TryInt(cells[9], out var height)
                || !double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                continue;
            var text = cells[11].Trim();
            if (text.Length == 0 || confidence < MinimumConfidence)
                continue;
            var key = (block, paragraph, line);
            if (!lines.TryGetValue(key, out var number))
                lines[key] = number = lines.Count;
            words.Add(new OcrWord(text, left, top, left + width, top + height, number));
        }
        return words;

        static bool TryInt(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
