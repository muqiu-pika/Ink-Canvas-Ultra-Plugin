using Ink_Canvas.Plugins.DocumentToImage.UI;
using NPOI.XWPF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;

namespace Ink_Canvas.Plugins.DocumentToImage.Converters
{
    /// <summary>
    /// 使用 NPOI 读取 Word 文档并通过 WPF 渲染为图片。
    /// 无需安装 Microsoft Office。
    /// </summary>
    public static class WordToImageConverter
    {
        /// <summary>
        /// 将 Word 文档逐页渲染为 PNG 文件写入磁盘，返回临时文件路径列表。
        /// 避免所有页面同时驻留内存导致 OOM。
        /// </summary>
        public static List<string> ConvertToFiles(string filePath, int dpi, string tempDir, IProgress<ConversionProgress> progress)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Word 文档不存在", filePath);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".docx")
                throw new NotSupportedException($"不支持的 Word 格式: {ext}，请使用 .docx");

            string fileName = Path.GetFileName(filePath);
            progress?.Report(new ConversionProgress
            {
                FileName = fileName,
                Message = "正在读取 Word 文档内容..."
            });

            const double pagePadding = 96;
            var flowDocument = new FlowDocument
            {
                PageWidth = 816,
                PageHeight = 1056,
                PagePadding = new Thickness(pagePadding),
                ColumnWidth = 816 - pagePadding * 2,
                FontFamily = new FontFamily("宋体"),
                FontSize = 16,
                Background = Brushes.White
            };

            ReadDocx(filePath, flowDocument);

            progress?.Report(new ConversionProgress
            {
                FileName = fileName,
                Message = "正在逐页渲染 Word 页面到磁盘..."
            });

            return RenderFlowDocumentToFiles(flowDocument, dpi, tempDir, fileName, progress);
        }

        private static void ReadDocx(string filePath, FlowDocument flowDocument)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var doc = new XWPFDocument(fs);
                foreach (var element in doc.BodyElements)
                {
                    if (element is XWPFParagraph para)
                    {
                        var paragraph = new Paragraph();
                        foreach (var run in para.Runs)
                        {
                            var runText = new Run(run.Text)
                            {
                                FontWeight = run.IsBold ? FontWeights.Bold : FontWeights.Normal,
                                FontStyle = run.IsItalic ? FontStyles.Italic : FontStyles.Normal
                            };
                            paragraph.Inlines.Add(runText);
                        }
                        if (paragraph.Inlines.Count == 0)
                            paragraph.Inlines.Add(new Run(""));
                        flowDocument.Blocks.Add(paragraph);
                    }
                    else if (element is XWPFTable table)
                    {
                        var wpfTable = new Table();
                        int colCount = table.Rows.Count > 0 ? table.Rows.Max(r => r.GetTableCells().Count) : 1;
                        for (int i = 0; i < colCount; i++)
                            wpfTable.Columns.Add(new TableColumn());

                        var rowGroup = new TableRowGroup();
                        foreach (var row in table.Rows)
                        {
                            var tr = new TableRow();
                            foreach (var cell in row.GetTableCells())
                            {
                                var tc = new TableCell
                                {
                                    BorderBrush = Brushes.LightGray,
                                    BorderThickness = new Thickness(0.5),
                                    Padding = new Thickness(4)
                                };
                                var cellPara = new Paragraph();
                                foreach (var cellBlock in cell.Paragraphs)
                                {
                                    foreach (var run in cellBlock.Runs)
                                        cellPara.Inlines.Add(new Run(run.Text));
                                }
                                tc.Blocks.Add(cellPara);
                                tr.Cells.Add(tc);
                            }
                            rowGroup.Rows.Add(tr);
                        }
                        wpfTable.RowGroups.Add(rowGroup);
                        flowDocument.Blocks.Add(wpfTable);
                    }
                }
            }
        }

        private static List<string> RenderFlowDocumentToFiles(FlowDocument flowDocument, int dpi, string tempDir, string fileName, IProgress<ConversionProgress> progress)
        {
            var pageFiles = new List<string>();
            string tempXps = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".xps");

            try
            {
                // 将 FlowDocument 先转成临时 XPS 文件，避免内存流缺少 Package URI 的问题。
                using (var xpsDoc = new XpsDocument(tempXps, FileAccess.ReadWrite))
                {
                    var writer = XpsDocument.CreateXpsDocumentWriter(xpsDoc);
                    writer.Write(((IDocumentPaginatorSource)flowDocument).DocumentPaginator);
                }

                using (var xpsDoc = new XpsDocument(tempXps, FileAccess.Read))
                {
                    var sequence = xpsDoc.GetFixedDocumentSequence();
                    int pageCount = sequence.References.Sum(r => r.GetDocument(false).Pages.Count);

                    int current = 0;
                    foreach (var docRef in sequence.References)
                    {
                        var fixedDoc = docRef.GetDocument(true);
                        foreach (PageContent pageContent in fixedDoc.Pages)
                        {
                            current++;
                            progress?.Report(new ConversionProgress
                            {
                                FileName = fileName,
                                Current = current,
                                Total = pageCount,
                                Message = $"正在转换第 {current}/{pageCount} 页..."
                            });

                            FixedPage fixedPage = pageContent.GetPageRoot(true);
                            if (fixedPage == null) continue;

                            fixedPage.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            fixedPage.Arrange(new Rect(fixedPage.DesiredSize));
                            fixedPage.UpdateLayout();

                            int width = (int)Math.Ceiling(fixedPage.DesiredSize.Width * dpi / 96.0);
                            int height = (int)Math.Ceiling(fixedPage.DesiredSize.Height * dpi / 96.0);
                            if (width <= 0 || height <= 0) continue;

                            pageFiles.AddRange(RenderVisualToFiles(
                                fixedPage,
                                fixedPage.DesiredSize.Width,
                                fixedPage.DesiredSize.Height,
                                width,
                                height,
                                dpi,
                                tempDir,
                                $"page_{current:D04}"));
                        }
                    }
                }
            }
            finally
            {
                try { File.Delete(tempXps); } catch { }
            }

            return pageFiles;
        }

        private static void SaveBitmapToPngFile(BitmapSource source, string filePath)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }
        }

        private static List<string> RenderVisualToFiles(Visual visual, double visualWidthDip, double visualHeightDip, int pixelWidth, int pixelHeight, int dpi, string tempDir, string filePrefix)
        {
            var pageFiles = new List<string>();
            if (visual == null || visualWidthDip <= 0 || visualHeightDip <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
                return pageFiles;

            double scale = dpi / 96.0;
            int stripPixelHeight = CalculateStripPixelHeight(pixelWidth);
            int stripCount = Math.Max(1, (int)Math.Ceiling((double)pixelHeight / stripPixelHeight));

            for (int stripIndex = 0; stripIndex < stripCount; stripIndex++)
            {
                int offsetPixels = stripIndex * stripPixelHeight;
                int currentStripPixelHeight = Math.Min(stripPixelHeight, pixelHeight - offsetPixels);
                double offsetDip = offsetPixels / scale;
                double currentStripHeightDip = currentStripPixelHeight / scale;

                var drawingVisual = new DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, visualWidthDip, currentStripHeightDip));
                    var brush = new VisualBrush(visual)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, offsetDip, visualWidthDip, currentStripHeightDip),
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, visualWidthDip, currentStripHeightDip)
                    };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, visualWidthDip, currentStripHeightDip));
                }

                var rtb = new RenderTargetBitmap(pixelWidth, currentStripPixelHeight, dpi, dpi, PixelFormats.Pbgra32);
                rtb.Render(drawingVisual);

                string filePath = stripCount == 1
                    ? Path.Combine(tempDir, filePrefix + ".png")
                    : Path.Combine(tempDir, $"{filePrefix}_{stripIndex + 1:D04}.png");
                SaveBitmapToPngFile(rtb, filePath);
                pageFiles.Add(filePath);
            }

            return pageFiles;
        }

        private static int CalculateStripPixelHeight(int pixelWidth)
        {
            const long targetPixelsPerStrip = 16L * 1024 * 1024;
            const int minStripHeight = 512;
            const int maxStripHeight = 4096;

            if (pixelWidth <= 0)
                return maxStripHeight;

            long estimated = targetPixelsPerStrip / pixelWidth;
            if (estimated < minStripHeight) return minStripHeight;
            if (estimated > maxStripHeight) return maxStripHeight;
            return (int)estimated;
        }
    }
}
