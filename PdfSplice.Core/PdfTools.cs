using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.AcroForms;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.Annotations;
using PdfSharpCore.Pdf.IO;

namespace PdfSplice.Core
{
    /// <summary>
    /// 拼版参数：把多页按「每页 N 行」竖向拼接时的页边距裁切量与每页行数。
    /// 单位均为 PDF point（磅）。
    /// </summary>
    public sealed class PdfSpliceOptions
    {
        /// <summary>上边距裁切量。</summary>
        public double Top { get; set; }
        /// <summary>下边距裁切量。</summary>
        public double Bottom { get; set; }
        /// <summary>左边距裁切量。</summary>
        public double Left { get; set; }
        /// <summary>右边距裁切量。</summary>
        public double Right { get; set; }
        /// <summary>每张输出页竖向拼接的原始页数（N 合一）。</summary>
        public int MaxRow { get; set; } = 2;
    }

    /// <summary>
    /// PDF 工具：合并、拆分（每页一个文件）、信息读取、竖向拼版（N 合 1）。
    /// 基于 PdfSharpCore，所有入口接收/返回 Stream 或字节数组，方便 Web 端直接处理上传下载。
    /// 拼版时同步平移拷贝源页注解（签章/表单域）并保留 AcroForm SigFlags。
    /// </summary>
    public static class PdfTools
    {
        // PdfSharp 读取部分老 PDF 需要代码页编码提供程序，注册一次即可。
        private static readonly EncodingProvider CodePages = CodePagesEncodingProvider.Instance;

        /// <summary>
        /// 合并多个 PDF。
        /// </summary>
        /// <param name="sources">按顺序的 PDF 流集合（调用方负责释放）。</param>
        /// <returns>合并后的 PDF 字节。</returns>
        public static byte[] Combine(IEnumerable<Stream> sources)
        {
            Encoding.RegisterProvider(CodePages);
            using var output = new PdfDocument();
            foreach (var src in sources)
            {
                src.Position = 0;
                using var input = PdfReader.Open(src, PdfDocumentOpenMode.Import);
                for (int i = 0; i < input.PageCount; i++)
                {
                    output.AddPage(input.Pages[i]);
                }
            }
            return SaveToBytes(output);
        }

        /// <summary>
        /// 拆分：把源 PDF 的每一页拆成单独的 PDF。
        /// </summary>
        /// <returns>按页顺序的字节数组列表（每项是一页 PDF）。</returns>
        public static List<byte[]> Split(Stream source)
        {
            Encoding.RegisterProvider(CodePages);
            source.Position = 0;
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            var pages = new List<byte[]>(input.PageCount);
            for (int i = 0; i < input.PageCount; i++)
            {
                using var single = new PdfDocument();
                single.AddPage(input.Pages[i]);
                pages.Add(SaveToBytes(single));
            }
            return pages;
        }

        /// <summary>读取每页的序号、宽、高（point）。</summary>
        public static List<(int PageNo, double Width, double Height)> GetPageInfo(Stream source)
        {
            Encoding.RegisterProvider(CodePages);
            source.Position = 0;
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            var list = new List<(int, double, double)>(input.PageCount);
            for (int i = 0; i < input.PageCount; i++)
            {
                var p = input.Pages[i];
                list.Add((i + 1, p.Width, p.Height));
            }
            return list;
        }

        /// <summary>
        /// 竖向拼版：把多个源 PDF 的页面按顺序拼接，每 <see cref="PdfSpliceOptions.MaxRow"/> 页
        /// 竖向叠到一张输出页上，并按给定边距裁掉四周。
        /// </summary>
        public static byte[] Splice(IEnumerable<Stream> sources, PdfSpliceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            Encoding.RegisterProvider(CodePages);

            int maxRow = options.MaxRow <= 0 ? 2 : options.MaxRow;
            double left = options.Left, right = options.Right, top = options.Top, bottom = options.Bottom;

            using var output = new PdfDocument { PageLayout = PdfPageLayout.SinglePage };

            PdfPage? currentPage = null;
            XGraphics? gfx = null;
            int rowInPage = 0;
            double contentHeight = 0;
            double contentWidth = 0;

            foreach (var src in sources)
            {
                src.Position = 0;
                using var input = PdfReader.Open(src, PdfDocumentOpenMode.Import);
                foreach (PdfPage page in input.Pages)
                {
                    contentWidth = page.Width - left - right;
                    contentHeight = page.Height - top - bottom;

                    // 需要新开一张输出页：第一次或已叠满 MaxRow 行。
                    if (currentPage == null || rowInPage >= maxRow)
                    {
                        currentPage = output.AddPage();
                        currentPage.Width = contentWidth;
                        currentPage.Height = contentHeight * maxRow;
                        gfx = XGraphics.FromPdfPage(currentPage);
                        rowInPage = 0;
                    }

                    // 把源页作为可绘制表单，按 -left/-top 偏移绘制，从而裁掉左右/上边距。
                    double y = contentHeight * rowInPage;
                    using var form = XPdfForm.FromStream(ToTempBytes(page, src));
                    gfx!.DrawImage(form, -left, y - top);

                    // 把源页的注解（如签章/表单域）平移后复制到输出页。
                    CopyAnnotations(page, currentPage, left, top, rowInPage, maxRow, contentHeight, bottom);

                    rowInPage++;
                }
            }

            PreserveSignatureFlags(output);
            return SaveToBytes(output);
        }

        // PdfSharpCore 的 XPdfForm 需要一个可寻址的流/字节，这里把单页渲染成字节供其读取。
        private static MemoryStream ToTempBytes(PdfPage page, Stream originalSource)
        {
            using var tmp = new PdfDocument();
            tmp.AddPage(page);
            var ms = new MemoryStream();
            tmp.Save(ms);
            ms.Position = 0;
            return ms;
        }

        // 拷贝源页注解到目标页，并按拼版偏移修正其矩形坐标。
        private static void CopyAnnotations(
            PdfPage srcPage, PdfPage dstPage,
            double left, double top, int rowInPage, int maxRow, double contentHeight, double bottom)
        {
            foreach (PdfAnnotation item in srcPage.Annotations)
            {
                var clone = (PdfAnnotation)item.Clone();
                double x1 = item.Rectangle.X1 - left;
                double x2 = item.Rectangle.X2 - left;
                // Y 方向：第 rowInPage 行在输出页上的纵向起点是 (maxRow - 1 - rowInPage) * contentHeight，
                // 这里沿用原 Tooler 的换算：y 随 row 从上到下叠加。
                double y1 = item.Rectangle.Y1 + ((maxRow - rowInPage) * contentHeight - bottom);
                double y2 = item.Rectangle.Y2 + ((maxRow - rowInPage) * contentHeight - bottom);
                clone.Elements.SetRectangle("/Rect", new PdfRectangle(new XRect(x1, y1, x2 - x1, y2 - y1)));
                dstPage.Annotations.Add(clone);
            }
        }

        // 若源 PDF 含签章域，在输出里保留 AcroForm 的 SigFlags=3（保留签章）。
        private static void PreserveSignatureFlags(PdfDocument output)
        {
            var fields = new PdfArray();
            foreach (var obj in output.Internals.GetAllObjects())
            {
                if (obj is PdfDictionary dict)
                {
                    var ft = dict.Elements[PdfAcroField.Keys.FT];
                    if (ft is PdfName name && name.Value != null && name.Value.IndexOf("/Sig", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fields.Elements.Add(PdfInternals.GetReference(obj));
                    }
                }
            }

            if (fields.Elements.Count == 0) return;

            var acroForm = new PdfDictionary(output);
            acroForm.Elements[PdfAcroForm.Keys.Fields] = fields;
            acroForm.Elements[PdfAcroForm.Keys.SigFlags] = new PdfInteger(3);
            output.Internals.AddObject(acroForm);
            output.Internals.Catalog.Elements["/AcroForm"] = PdfInternals.GetReference(acroForm);
        }

        // 把 PdfDocument 存成字节数组。
        private static byte[] SaveToBytes(PdfDocument doc)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }
    }
}
