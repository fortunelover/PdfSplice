using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSplice.Core;
using Xunit;

/// <summary>
/// PDF 工具单元测试：夹具全部用 PdfSharpCore 现场生成最小 PDF（不依赖外部文件），
/// 以「页数」为核心断言——合并页数=输入之和、拆分文件数=页数、拼版输出页数=ceil(总页/MaxRow)。
/// </summary>
public class PdfToolsTests
{
    // 生成 n 页最小 PDF 作夹具。
    private static byte[] MakePdf(int pages)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static byte[] CombinePdfs(params int[] pageCounts)
        => PdfTools.Combine(pageCounts.Select(c => new MemoryStream(MakePdf(c)) as Stream));

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    [Fact]
    public void Combine_OutputPageCount_EqualsSum()
    {
        byte[] combined = CombinePdfs(1, 2, 3);
        Assert.Equal(6, PageCount(combined));
    }

    [Fact]
    public void Split_EmitsOneFilePerPage()
    {
        byte[] merged = CombinePdfs(2, 1);
        var parts = PdfTools.Split(new MemoryStream(merged));
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.Equal(1, PageCount(p)));
    }

    [Fact]
    public void Splice_TwoUp_HalvesPageCount()
    {
        byte[] merged = CombinePdfs(2, 2, 2);
        byte[] spliced = PdfTools.Splice(
            new[] { new MemoryStream(merged) as Stream },
            new PdfSpliceOptions { Top = 0, Bottom = 0, Left = 0, Right = 0, MaxRow = 2 });
        Assert.Equal(3, PageCount(spliced));
    }

    [Fact]
    public void GetPageInfo_ReportsEveryPage()
    {
        byte[] pdf = MakePdf(3);
        var info = PdfTools.GetPageInfo(new MemoryStream(pdf));
        Assert.Equal(3, info.Count);
        Assert.Equal(1, info[0].PageNo);
        Assert.Equal(3, info[2].PageNo);
    }
}
