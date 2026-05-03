using System.IO;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Core;
using Community.PowerToys.Run.Plugin.SemanticSearcher.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.UnitTests;

[TestClass]
public class DocumentReaderTests
{
    private static readonly IndexConfig DefaultConfig = new()
    {
        MaxFileSizeMB = 10,
        SupportedExtensions = [".pdf", ".txt", ".docx"],
    };

    private static DocumentReader Reader => new(DefaultConfig);

    // ── PDF parsing ───────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(@"C:\Users\adees\Desktop\1st year Modal paper A.pdf")]
    [DataRow(@"C:\Users\adees\Desktop\Rain_Words_and_Natural_Calamities.pdf")]
    [DataRow(@"C:\Users\adees\Desktop\Terrorism.pdf")]
    public void ReadText_TextPdf_ReturnsNonEmptyString(string path)
    {
        if (!File.Exists(path))
            Assert.Inconclusive($"Test file not present on this machine: {path}");

        string? result = Reader.ReadText(path);

        Assert.IsNotNull(result, $"Expected text from {Path.GetFileName(path)} but got null");
        Assert.IsTrue(result.Length > 0, $"Expected non-empty text from {Path.GetFileName(path)}");
    }

    [TestMethod]
    [DataRow(@"C:\Users\adees\Desktop\Hindi0001.pdf")]
    [DataRow(@"C:\Users\adees\Desktop\info\10th marksheet .pdf")]
    public void ReadText_ImageOnlyPdf_ReturnsNullOrEmpty(string path)
    {
        if (!File.Exists(path))
            Assert.Inconclusive($"Test file not present on this machine: {path}");

        string? result = Reader.ReadText(path);

        // Image-only PDFs have no text layer — either null or whitespace is acceptable
        bool isNullOrEmpty = result is null || result.Trim().Length == 0;
        Assert.IsTrue(isNullOrEmpty,
            $"Expected null/empty from image-only PDF {Path.GetFileName(path)}, but got {result?.Length} chars");
    }

    // ── Size guard ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ReadText_FileExceedsMaxSize_ReturnsNull()
    {
        // Use a config with a tiny 0-byte limit to force the size guard
        var tinyConfig = new IndexConfig { MaxFileSizeMB = 0, SupportedExtensions = [".pdf"] };
        var reader = new DocumentReader(tinyConfig);

        // Any existing PDF should be blocked by the size guard
        string path = @"C:\Users\adees\Desktop\Terrorism.pdf";
        if (!File.Exists(path))
            Assert.Inconclusive("Test PDF not present on this machine.");

        string? result = reader.ReadText(path);
        Assert.IsNull(result, "Expected null when file exceeds MaxFileSizeMB");
    }

    // ── Unsupported extension ─────────────────────────────────────────────────

    [TestMethod]
    public void ReadText_UnsupportedExtension_ReturnsNull()
    {
        // .exe is not in SupportedExtensions
        string fakePath = @"C:\Windows\System32\notepad.exe";
        if (!File.Exists(fakePath))
            Assert.Inconclusive("notepad.exe not found, skipping.");

        var result = Reader.ReadText(fakePath);
        Assert.IsNull(result, "Expected null for unsupported extension");
    }

    // ── Plain text ────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(@"C:\Users\adees\Desktop\Dell RemoteActions Terms and Conditions.txt")]
    public void ReadText_PlainTextFile_ReturnsContent(string path)
    {
        if (!File.Exists(path))
            Assert.Inconclusive($"Test file not present on this machine: {path}");

        string? result = Reader.ReadText(path);

        Assert.IsNotNull(result, $"Expected text from {Path.GetFileName(path)}");
        Assert.IsTrue(result.Length > 0, "Expected non-empty content");
    }
}
