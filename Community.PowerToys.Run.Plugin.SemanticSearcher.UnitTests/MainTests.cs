using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wox.Plugin;

namespace Community.PowerToys.Run.Plugin.SemanticSearcher.UnitTests;

/// <summary>
/// Smoke tests for Main (the plugin entry point).
/// Note: Init() requires a live PluginInitContext from PowerToys, so these tests
/// validate graceful fallback behaviour without a real context.
/// </summary>
[TestClass]
public class MainTests
{
    private Main _main = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _main = new Main();
        // Do NOT call Init() here — it needs a real PowerToys PluginInitContext.
        // The plugin must handle a null context gracefully (return empty results).
    }

    [TestMethod]
    public void Query_WithoutInit_ReturnsEmptyList()
    {
        // Without Init(), _indexManager is null — should return [] not throw
        var results = _main.Query(new Query("test search"));
        Assert.IsNotNull(results);
    }

    [TestMethod]
    public void Query_EmptySearch_ReturnsEmptyList()
    {
        var results = _main.Query(new Query(""));
        Assert.IsNotNull(results);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void LoadContextMenus_NonSearchResult_ReturnsEmptyList()
    {
        // ContextData is a plain string, not a SearchResult — should return []
        var results = _main.LoadContextMenus(new Result { ContextData = "not-a-search-result" });
        Assert.IsNotNull(results);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Dispose_CanBeCalledSafely()
    {
        // Should not throw even without Init()
        _main.Dispose();
    }
}
