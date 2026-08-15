using Ink_Canvas.Plugins.DocumentToImage.UI;
using PdfiumViewer;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Ink_Canvas.Plugins.DocumentToImage.Converters
{
    /// <summary>
    /// 使用 PdfiumViewer 将 PDF 每一页渲染为 BitmapImage。
    /// 需要随插件携带 pdfium.dll（PdfiumViewer NuGet 包会自动复制）。
    /// </summary>
    internal static class PdfToImageConverter
    {
        /// <summary>
        /// 将 PDF 逐页渲染为 PNG 文件写入磁盘，返回临时文件路径列表。
        /// 避免所有页面同时驻留内存导致 OOM。
        /// </summary>
        public static List<string> ConvertToFiles(string pdfPath, int dpi, string tempDir, IProgress<ConversionProgress> progress)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("PDF 文件不存在", pdfPath);

            string fileName = Path.GetFileName(pdfPath);
            progress?.Report(new ConversionProgress
            {
                FileName = fileName,
                Message = "正在读取 PDF 页面..."
            });

            var pageFiles = new List<string>();

            using (var document = PdfDocument.Load(pdfPath))
            {
                int pageCount = document.PageCount;
                if (pageCount <= 0)
                    throw new InvalidOperationException("PDF 没有可渲染的页面");

                for (int i = 0; i < pageCount; i++)
                {
                    progress?.Report(new ConversionProgress
                    {
                        FileName = fileName,
                        Current = i + 1,
                        Total = pageCount,
                        Message = $"正在转换 PDF 第 {i + 1}/{pageCount} 页..."
                    });

                    var size = document.PageSizes[i];
                    int width = Math.Max(1, (int)(size.Width * dpi / 72.0));
                    int height = Math.Max(1, (int)(size.Height * dpi / 72.0));

                    using (Bitmap bitmap = (Bitmap)document.Render(i, width, height, dpi, dpi, PdfRenderFlags.Annotations))
                    {
                        pageFiles.AddRange(SaveBitmapToFiles(bitmap, tempDir, $"page_{i + 1:D04}"));
                    }
                }
            }

            return pageFiles;
        }

        private static List<string> SaveBitmapToFiles(Bitmap bitmap, string tempDir, string filePrefix)
        {
            var pageFiles = new List<string>();
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return pageFiles;

            int stripPixelHeight = CalculateStripPixelHeight(bitmap.Width);
            int stripCount = Math.Max(1, (int)Math.Ceiling((double)bitmap.Height / stripPixelHeight));

            for (int stripIndex = 0; stripIndex < stripCount; stripIndex++)
            {
                int offsetY = stripIndex * stripPixelHeight;
                int currentStripHeight = Math.Min(stripPixelHeight, bitmap.Height - offsetY);
                using (var stripBitmap = new Bitmap(bitmap.Width, currentStripHeight, PixelFormat.Format32bppArgb))
                {
                    stripBitmap.SetResolution(bitmap.HorizontalResolution, bitmap.VerticalResolution);
                    using (var g = Graphics.FromImage(stripBitmap))
                    {
                        g.Clear(Color.White);
                        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(
                            bitmap,
                            new Rectangle(0, 0, bitmap.Width, currentStripHeight),
                            new Rectangle(0, offsetY, bitmap.Width, currentStripHeight),
                            GraphicsUnit.Pixel);
                    }

                    string filePath = stripCount == 1
                        ? Path.Combine(tempDir, filePrefix + ".png")
                        : Path.Combine(tempDir, $"{filePrefix}_{stripIndex + 1:D04}.png");
                    stripBitmap.Save(filePath, ImageFormat.Png);
                    pageFiles.Add(filePath);
                }
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
