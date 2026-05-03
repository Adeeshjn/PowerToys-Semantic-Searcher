using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Models;
using DocumentFormat.OpenXml.Packaging;
using PdfPigDocument  = UglyToad.PdfPig.PdfDocument;
using Windows.Data.Pdf;
using WinPdfDocument  = Windows.Data.Pdf.PdfDocument;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.Core;

/// <summary>
/// Reads text from plain text, PDF, Office documents, and images (via OCR).
/// </summary>
public sealed class DocumentReader
{
    private readonly IndexConfig _config;

    public DocumentReader(IndexConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Returns the full text content of the file at <paramref name="filePath"/>,
    /// or <c>null</c> if the file is unsupported, too large, or unreadable.
    /// </summary>
    public string? ReadText(string filePath)
    {
        int maxRetries = 3;
        int delayMs = 200;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var info = new FileInfo(filePath);

                // Size guard
                if (info.Length > _config.MaxFileSizeMB * 1024L * 1024L)
                    return null;

                string ext = info.Extension.ToLowerInvariant();

                if (!_config.SupportedExtensions.Contains(ext))
                    return null;

                return ext switch
                {
                    ".pdf"  => ReadPdf(filePath),
                    ".docx" => ReadDocx(filePath),
                    ".pptx" => ReadPptx(filePath),
                    ".xlsx" => ReadXlsx(filePath),
                    ".png" or ".jpg" or ".jpeg"
                    or ".bmp" or ".tiff" or ".tif"
                    or ".gif" or ".webp"            => OcrImageAsync(filePath).GetAwaiter().GetResult() ?? string.Empty,
                    _       => ReadPlainText(filePath),
                };
            }
            catch (IOException)
            {
                // File locked — wait and retry
                if (attempt == maxRetries) return null;
                System.Threading.Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                // Log parse failures so we can see why (e.g. image-only PDFs, corrupt files)
                AppendLog($"ReadText failed [{ex.GetType().Name}]: {ex.Message} | File: {filePath}");
                return null;
            }
        }
        return null;
    }

    private static void AppendLog(string msg)
    {
        IndexManager.Log("READER", msg);
    }

    private static string ReadPlainText(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadPdf(string filePath)
    {
        // First try PdfPig for text-based PDFs (fast, no GPU needed)
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = PdfPigDocument.Open(stream);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
            sb.AppendLine(page.Text);

        string extracted = sb.ToString().Trim();
        if (extracted.Length > 0)
            return extracted;

        // Fallback: image-only / scanned PDF — use Windows OCR
        AppendLog($"PdfPig extracted no text, attempting OCR fallback: {filePath}");
        return OcrPdfAsync(filePath).GetAwaiter().GetResult() ?? string.Empty;
    }

    /// <summary>
    /// Uses Windows.Media.Ocr to extract text directly from an image file
    /// (PNG, JPG, BMP, TIFF, GIF, WEBP).
    /// </summary>
    private static async Task<string?> OcrImageAsync(string filePath)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                AppendLog("OCR engine unavailable (no language pack installed).");
                return null;
            }

            using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(ms.GetOutputStreamAt(0));

            // Copy the file bytes into a WinRT stream
            byte[] bytes = new byte[fileStream.Length];
            _ = await fileStream.ReadAsync(bytes, 0, bytes.Length);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();

            var decoder = await BitmapDecoder.CreateAsync(ms);
            var bitmap  = await decoder.GetSoftwareBitmapAsync();
            var result  = await engine.RecognizeAsync(bitmap);
            return result.Text.Trim();
        }
        catch (Exception ex)
        {
            AppendLog($"OCR (image) failed [{ex.GetType().Name}]: {ex.Message} | File: {filePath}");
            return null;
        }
    }

    /// <summary>
    /// Uses Windows.Data.Pdf to render each page and Windows.Media.Ocr to
    /// extract text. Only called for scanned / image-only PDFs.
    /// </summary>
    private static async Task<string?> OcrPdfAsync(string filePath)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                AppendLog("OCR engine unavailable (no language pack installed).");
                return null;
            }

            // Load the PDF via the WinRT API
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
            var pdfDoc = await WinPdfDocument.LoadFromFileAsync(file);

            var sb = new StringBuilder();

            for (uint i = 0; i < pdfDoc.PageCount; i++)
            {
                using var page = pdfDoc.GetPage(i);

                // Render the page to an in-memory PNG stream
                using var ms = new InMemoryRandomAccessStream();
                var renderOptions = new PdfPageRenderOptions
                {
                    // 150 DPI equivalent — good balance of accuracy vs. speed
                    DestinationWidth = (uint)(page.Size.Width * 2),
                };
                await page.RenderToStreamAsync(ms, renderOptions);

                // Decode the rendered image into a SoftwareBitmap
                var decoder = await BitmapDecoder.CreateAsync(ms);
                var bitmap  = await decoder.GetSoftwareBitmapAsync();

                // Run OCR on the bitmap
                var result = await engine.RecognizeAsync(bitmap);
                sb.AppendLine(result.Text);
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            AppendLog($"OCR failed [{ex.GetType().Name}]: {ex.Message} | File: {filePath}");
            return null;
        }
    }

    private static string ReadDocx(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wordDoc = WordprocessingDocument.Open(stream, false);
        return wordDoc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }

    private static string ReadPptx(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var pptDoc = PresentationDocument.Open(stream, false);
        var slideParts = pptDoc.PresentationPart?.SlideParts;
        if (slideParts == null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var slide in slideParts)
        {
            if (slide.Slide != null)
            {
                sb.AppendLine(slide.Slide.InnerText);
            }
        }
        return sb.ToString();
    }

    private static string ReadXlsx(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sheetDoc = SpreadsheetDocument.Open(stream, false);
        var sheets = sheetDoc.WorkbookPart?.WorksheetParts;
        if (sheets == null) return string.Empty;

        var sb = new StringBuilder();
        var sharedStringTable = sheetDoc.WorkbookPart?.SharedStringTablePart?.SharedStringTable;

        foreach (var sheet in sheets)
        {
            var rows = sheet.Worksheet?.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.SheetData>()?.Elements<DocumentFormat.OpenXml.Spreadsheet.Row>();
            if (rows == null) continue;

            foreach (var row in rows)
            {
                foreach (var cell in row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                {
                    string val = cell.CellValue?.Text ?? string.Empty;
                    if (cell.DataType != null && cell.DataType.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString)
                    {
                        if (sharedStringTable != null && int.TryParse(val, out int idx))
                        {
                            val = sharedStringTable.ElementAt(idx).InnerText;
                        }
                    }
                    sb.Append(val + " ");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
