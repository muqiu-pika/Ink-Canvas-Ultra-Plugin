using Ink_Canvas.Plugins.DocumentToImage.UI;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ink_Canvas.Plugins.DocumentToImage.Converters
{
    /// <summary>
    /// 使用 NPOI 读取 Excel 工作簿并通过 WPF 渲染为图片。
    /// 无需安装 Microsoft Office。
    /// </summary>
    public static class ExcelToImageConverter
    {
        /// <summary>
        /// 将 Excel 工作表逐表渲染为 PNG 文件写入磁盘，返回临时文件路径列表。
        /// 避免所有工作表同时驻留内存导致 OOM。
        /// </summary>
        public static List<string> ConvertToFiles(string filePath, int dpi, string tempDir, IProgress<ConversionProgress> progress)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Excel 文件不存在", filePath);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".xls" && ext != ".xlsx")
                throw new NotSupportedException($"不支持的 Excel 格式: {ext}");

            string fileName = Path.GetFileName(filePath);
            progress?.Report(new ConversionProgress
            {
                FileName = fileName,
                Message = "正在读取 Excel 工作簿..."
            });

            IWorkbook workbook = null;
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    workbook = ext == ".xlsx" ? (IWorkbook)new XSSFWorkbook(fs) : new HSSFWorkbook(fs);
                }

                int sheetCount = workbook.NumberOfSheets;
                var pageFiles = new List<string>();
                for (int i = 0; i < sheetCount; i++)
                {
                    ISheet sheet = workbook.GetSheetAt(i);
                    progress?.Report(new ConversionProgress
                    {
                        FileName = fileName,
                        Current = i + 1,
                        Total = sheetCount,
                        Message = $"正在转换工作表 {sheet.SheetName} ({i + 1}/{sheetCount})..."
                    });

                    pageFiles.AddRange(RenderSheetToFiles(sheet, dpi, tempDir, i + 1));
                }

                return pageFiles;
            }
            finally
            {
                if (workbook is IDisposable disposableWorkbook)
                    disposableWorkbook.Dispose();
            }
        }

        private static List<string> RenderSheetToFiles(ISheet sheet, int dpi, string tempDir, int sheetIndex)
        {
            var pageFiles = new List<string>();
            int firstRow = sheet.FirstRowNum;
            int lastRow = sheet.LastRowNum;
            if (firstRow < 0 || lastRow < firstRow)
                return pageFiles;

            int maxCol = 0;
            for (int r = firstRow; r <= lastRow; r++)
            {
                IRow row = sheet.GetRow(r);
                if (row != null && row.LastCellNum > maxCol)
                    maxCol = row.LastCellNum;
            }
            if (maxCol <= 0)
                return pageFiles;

            double[] columnWidthsDip = BuildColumnWidthsDip(sheet, maxCol);
            double[] rowHeightsDip = BuildRowHeightsDip(sheet, firstRow, lastRow);
            double renderWidthDip = columnWidthsDip.Sum();
            if (renderWidthDip <= 0)
                return pageFiles;

            double scale = dpi / 96.0;
            int pixelWidth = (int)Math.Ceiling(renderWidthDip * scale);
            if (pixelWidth <= 0)
                return pageFiles;

            double maxChunkHeightDip = CalculateLayoutChunkPixelHeight(pixelWidth) / scale;
            var rowChunks = BuildRowChunks(firstRow, lastRow, rowHeightsDip, maxChunkHeightDip);
            for (int chunkIndex = 0; chunkIndex < rowChunks.Count; chunkIndex++)
            {
                var chunk = rowChunks[chunkIndex];
                string filePrefix = rowChunks.Count == 1
                    ? $"page_{sheetIndex:D04}"
                    : $"page_{sheetIndex:D04}_{chunkIndex + 1:D04}";
                pageFiles.AddRange(RenderRowChunkToFiles(
                    sheet,
                    chunk.startRow,
                    chunk.endRow,
                    firstRow,
                    maxCol,
                    columnWidthsDip,
                    rowHeightsDip,
                    dpi,
                    tempDir,
                    filePrefix));
            }

            return pageFiles;
        }

        private static List<string> RenderRowChunkToFiles(
            ISheet sheet,
            int chunkStartRow,
            int chunkEndRow,
            int sheetFirstRow,
            int maxCol,
            double[] columnWidthsDip,
            double[] rowHeightsDip,
            int dpi,
            string tempDir,
            string filePrefix)
        {
            var pageFiles = new List<string>();
            if (chunkEndRow < chunkStartRow)
                return pageFiles;

            double scale = dpi / 96.0;
            var grid = new Grid
            {
                Background = Brushes.White,
                ClipToBounds = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            TextOptions.SetTextFormattingMode(grid, TextFormattingMode.Display);
            TextOptions.SetTextHintingMode(grid, TextHintingMode.Fixed);

            for (int c = 0; c < maxCol; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidthsDip[c]) });

            double renderHeightDip = 0;
            for (int r = chunkStartRow; r <= chunkEndRow; r++)
            {
                double rowHeightDip = rowHeightsDip[r - sheetFirstRow];
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeightDip) });
                renderHeightDip += rowHeightDip;
            }

            for (int r = chunkStartRow; r <= chunkEndRow; r++)
            {
                IRow row = sheet.GetRow(r);
                if (row == null) continue;

                for (int c = 0; c < maxCol; c++)
                {
                    ICell cell = row.GetCell(c);
                    if (cell == null) continue;

                    string text = cell.ToString()?.Trim();
                    if (string.IsNullOrEmpty(text))
                        continue;

                    var border = new Border
                    {
                        BorderBrush = Brushes.LightGray,
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(5, 3, 5, 3),
                        Width = columnWidthsDip[c],
                        MinHeight = rowHeightsDip[r - sheetFirstRow],
                        Child = new TextBlock
                        {
                            Text = text,
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                            TextTrimming = TextTrimming.None,
                            VerticalAlignment = System.Windows.VerticalAlignment.Center
                        }
                    };

                    Grid.SetRow(border, r - chunkStartRow);
                    Grid.SetColumn(border, c);
                    grid.Children.Add(border);
                }
            }

            double renderWidthDip = columnWidthsDip.Sum();
            grid.Measure(new Size(renderWidthDip, renderHeightDip));
            grid.Arrange(new Rect(0, 0, renderWidthDip, renderHeightDip));
            grid.UpdateLayout();

            if (renderWidthDip <= 0 || renderHeightDip <= 0)
                return pageFiles;

            int width = (int)Math.Ceiling(renderWidthDip * scale);
            int height = (int)Math.Ceiling(renderHeightDip * scale);
            int stripPixelHeight = CalculateStripPixelHeight(width);
            int stripCount = Math.Max(1, (int)Math.Ceiling((double)height / stripPixelHeight));

            for (int stripIndex = 0; stripIndex < stripCount; stripIndex++)
            {
                int offsetPixels = stripIndex * stripPixelHeight;
                int currentStripPixelHeight = Math.Min(stripPixelHeight, height - offsetPixels);
                double offsetDip = offsetPixels / scale;
                double currentStripHeightDip = currentStripPixelHeight / scale;

                var drawingVisual = new DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, renderWidthDip, currentStripHeightDip));
                    var brush = new VisualBrush(grid)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, offsetDip, renderWidthDip, currentStripHeightDip),
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, renderWidthDip, currentStripHeightDip)
                    };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, renderWidthDip, currentStripHeightDip));
                }

                var rtb = new RenderTargetBitmap(width, currentStripPixelHeight, dpi, dpi, PixelFormats.Pbgra32);
                rtb.Render(drawingVisual);

                string filePath = stripCount == 1
                    ? Path.Combine(tempDir, filePrefix + ".png")
                    : Path.Combine(tempDir, $"{filePrefix}_{stripIndex + 1:D04}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(fs);
                }

                pageFiles.Add(filePath);
            }

            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();

            return pageFiles;
        }

        private static double[] BuildColumnWidthsDip(ISheet sheet, int maxCol)
        {
            var widths = new double[maxCol];
            for (int c = 0; c < maxCol; c++)
            {
                int widthUnits = sheet.GetColumnWidth(c);
                double characters = widthUnits / 256.0;
                double pixelWidth = Math.Floor(characters * 7 + 5);
                widths[c] = Math.Max(32, pixelWidth);
            }
            return widths;
        }

        private static double[] BuildRowHeightsDip(ISheet sheet, int firstRow, int lastRow)
        {
            var heights = new double[lastRow - firstRow + 1];
            for (int r = firstRow; r <= lastRow; r++)
            {
                IRow row = sheet.GetRow(r);
                double points = row?.HeightInPoints > 0 ? row.HeightInPoints : sheet.DefaultRowHeightInPoints;
                heights[r - firstRow] = Math.Max(20, points * 96.0 / 72.0);
            }
            return heights;
        }

        private static List<(int startRow, int endRow)> BuildRowChunks(int firstRow, int lastRow, double[] rowHeightsDip, double maxChunkHeightDip)
        {
            var chunks = new List<(int startRow, int endRow)>();
            int chunkStart = firstRow;
            double currentHeightDip = 0;

            for (int r = firstRow; r <= lastRow; r++)
            {
                double rowHeightDip = rowHeightsDip[r - firstRow];
                bool shouldSplit = r > chunkStart && currentHeightDip + rowHeightDip > maxChunkHeightDip;
                if (shouldSplit)
                {
                    chunks.Add((chunkStart, r - 1));
                    chunkStart = r;
                    currentHeightDip = 0;
                }

                currentHeightDip += rowHeightDip;
            }

            if (chunkStart <= lastRow)
                chunks.Add((chunkStart, lastRow));

            return chunks;
        }

        private static int CalculateLayoutChunkPixelHeight(int pixelWidth)
        {
            // 让 Excel 表格按行分段渲染，避免超大可视对象在后续链路中被隐式降采样。
            int stripHeight = CalculateStripPixelHeight(pixelWidth);
            return Math.Max(1024, Math.Min(3072, stripHeight * 2));
        }

        private static int CalculateStripPixelHeight(int pixelWidth)
        {
            // 目标单块位图控制在约 64MB RGBA 内存以内，避免大表格渲染时峰值过高。
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
