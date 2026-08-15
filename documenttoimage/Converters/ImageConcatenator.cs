using Ink_Canvas.Plugins.DocumentToImage.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace Ink_Canvas.Plugins.DocumentToImage.Converters
{
    /// <summary>
    /// 将多张图片按垂直方向拼接成不同长度的长图。
    /// 支持从磁盘文件流式读取，避免所有页面同时驻留内存。
    /// </summary>
    internal static class ImageConcatenator
    {
        /// <summary>
        /// 单张长照片的最大高度（像素）。超过则拆分为多张，保证每张清晰且 WPF/GPU 渲染安全。
        /// 取 4096 以兼容所有常见 GPU 纹理限制（4096 是最保守的通用上限），
        /// 同时覆盖 200% DPI 缩放场景（4096*2=8192，刚好在 GPU 安全边界内）。
        /// 避免超高照片被 WPF/GPU 隐式降采样导致模糊。
        /// </summary>
        public const int MaxChunkHeight = 4096;

        /// <summary>
        /// 单张长照片的最大宽度（像素）。与高度限制保持一致，
        /// 防止宽页（如 A3 横版、宽表格）在高 DPI 下超出 GPU 纹理宽度限制。
        /// </summary>
        public const int MaxChunkWidth = 4096;

        /// <summary>
        /// 单张长照片的最大像素数。
        /// 额外限制总像素量，作为宽/高限制的补充约束。
        /// 4096x4096 ≈ 16M 像素，对应约 64MB RGBA 位图。
        /// </summary>
        public const long MaxChunkPixels = 16L * 1024 * 1024;

        /// <summary>
        /// 从磁盘 PNG 文件列表流式拼接为一张或多张长图。
        /// 逐文件读取尺寸和像素数据，避免 OOM；按 MaxChunkHeight/MaxChunkWidth/MaxChunkPixels 分块，
        /// 每块内不缩放保持清晰。
        /// </summary>
        public static List<BitmapImage> ConcatenateToChunks(List<string> pageFiles, IProgress<ConversionProgress> progress = null)
        {
            if (pageFiles == null || pageFiles.Count == 0)
                throw new ArgumentException("没有可拼接的图片", nameof(pageFiles));

            if (pageFiles.Count == 1)
            {
                // 单文件：检查尺寸是否超限，超限则拆分为多条
                return SplitSingleFileIfNeeded(pageFiles[0], progress);
            }

            int total = pageFiles.Count;

            // 第一遍：读取所有页面尺寸
            int maxWidth = 0;
            int totalHeight = 0;
            var pageDimensions = new List<(int w, int h, float dpiX, float dpiY)>(total);

            for (int i = 0; i < pageFiles.Count; i++)
            {
                using (var img = System.Drawing.Image.FromFile(pageFiles[i]))
                {
                    int w = img.Width;
                    int h = img.Height;
                    float dpiX = img.HorizontalResolution;
                    float dpiY = img.VerticalResolution;
                    pageDimensions.Add((w, h, dpiX, dpiY));
                    if (w > maxWidth) maxWidth = w;
                    totalHeight += h;
                }
            }

            if (maxWidth <= 0 || totalHeight <= 0)
                throw new InvalidOperationException("无法拼接无效尺寸的图片");

            // 按 MaxChunkHeight / MaxChunkWidth / MaxChunkPixels 三重限制分块：块内页面不缩放
            var chunks = new List<List<int>>();
            var current = new List<int>();
            int currentHeight = 0;
            int currentWidth = 0;
            for (int i = 0; i < pageFiles.Count; i++)
            {
                int w = pageDimensions[i].w;
                int h = pageDimensions[i].h;
                int nextWidth = current.Count == 0 ? w : Math.Max(currentWidth, w);
                int nextHeight = currentHeight + h;

                // 新块的第一个文件：检查单文件是否超限，若超限则强制拆分为独立块
                // （正常情况下转换器已按 strip 高度切割，不会超限，此处为兜底）
                if (current.Count == 0)
                {
                    if (h > MaxChunkHeight || w > MaxChunkWidth || (long)w * h > MaxChunkPixels)
                    {
                        // 单文件就超限：直接作为一个块（GDI+ 拼接时会居中放置）
                        current.Add(i);
                        currentHeight = h;
                        currentWidth = w;
                        continue;
                    }
                }

                bool exceedsHeight = current.Count > 0 && nextHeight > MaxChunkHeight;
                bool exceedsWidth = nextWidth > MaxChunkWidth;
                bool exceedsPixels = current.Count > 0 && (long)nextWidth * nextHeight > MaxChunkPixels;

                if (exceedsHeight || exceedsWidth || exceedsPixels)
                {
                    chunks.Add(current);
                    current = new List<int>();
                    currentHeight = 0;
                    currentWidth = 0;

                    // 检查当前文件是否单独超限
                    if (h > MaxChunkHeight || w > MaxChunkWidth || (long)w * h > MaxChunkPixels)
                    {
                        current.Add(i);
                        currentHeight = h;
                        currentWidth = w;
                    }
                    else
                    {
                        current.Add(i);
                        currentHeight += h;
                        if (w > currentWidth) currentWidth = w;
                    }
                }
                else
                {
                    current.Add(i);
                    currentHeight += h;
                    if (w > currentWidth) currentWidth = w;
                }
            }
            if (current.Count > 0) chunks.Add(current);

            var results = new List<BitmapImage>();
            int chunkCount = chunks.Count;

            for (int c = 0; c < chunkCount; c++)
            {
                var chunk = chunks[c];

                // 计算本块尺寸（不缩放，保持原始 DPI 清晰度）
                int cw = 0, ch = 0;
                foreach (int idx in chunk)
                {
                    if (pageDimensions[idx].w > cw) cw = pageDimensions[idx].w;
                    ch += pageDimensions[idx].h;
                }

                // 若块尺寸超过安全上限（兜底保护，正常分块逻辑应避免到此），
                // 不进行缩放——直接按实际尺寸输出，由上层多分块处理
                string tempPath = Path.Combine(Path.GetTempPath(), $"icu_doc_chunk_{Guid.NewGuid():N}.png");
                try
                {
                    using (var bitmap = new System.Drawing.Bitmap(cw, ch, PixelFormat.Format32bppArgb))
                    {
                        // 统一设为 96 DPI，与 WPF 默认 DPI 一致，避免加载时 DPI 换算导致的尺寸偏差
                        bitmap.SetResolution(96, 96);
                        using (var g = Graphics.FromImage(bitmap))
                        {
                            g.Clear(Color.White);

                            // 像素精确 1:1 复制：
                            // - 使用 DrawImage(..., srcRect, GraphicsUnit.Pixel) 矩形重载，
                            //   明确指定像素单位，避免 GDI+ 根据源/目标 DPI 差异自动缩放
                            // - HighQualityBicubic 配合 PixelOffsetMode.HighQuality 保证
                            //   1:1 复制时像素对齐，无模糊
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                            int currentY = 0;
                            foreach (int idx in chunk)
                            {
                                var (w, h, _, _) = pageDimensions[idx];
                                int x = (cw - w) / 2;

                                // 从磁盘流式读取单页图片，用完即释放
                                using (var pageImg = System.Drawing.Image.FromFile(pageFiles[idx]))
                                {
                                    var destRect = new Rectangle(x, currentY, w, h);
                                    var srcRect = new Rectangle(0, 0, pageImg.Width, pageImg.Height);
                                    g.DrawImage(pageImg, destRect, srcRect, GraphicsUnit.Pixel);
                                }
                                currentY += h;
                            }
                        }

                        bitmap.Save(tempPath, ImageFormat.Png);
                    }

                    results.Add(LoadBitmapImageFromFile(tempPath));
                    try { File.Delete(tempPath); } catch { }

                    if (chunkCount > 1)
                    {
                        progress?.Report(new ConversionProgress
                        {
                            FileName = null,
                            Current = c + 1,
                            Total = chunkCount,
                            Message = $"文档较大，已拆分为 {chunkCount} 张长照片（第 {c + 1}/{chunkCount} 张）"
                        });
                    }
                }
                catch
                {
                    try { File.Delete(tempPath); } catch { }
                    throw;
                }
            }

            return results;
        }

        /// <summary>
        /// 单文件时检查尺寸是否超限：若单页/单条超过 MaxChunkHeight，则切为多个 BitmapImage 返回。
        /// 这是兜底逻辑——正常情况下转换器已按 strip 高度切割，单个 PNG 不会超过 4096px。
        /// </summary>
        private static List<BitmapImage> SplitSingleFileIfNeeded(string filePath, IProgress<ConversionProgress> progress)
        {
            using (var img = System.Drawing.Image.FromFile(filePath))
            {
                int w = img.Width;
                int h = img.Height;

                // 尺寸在安全范围内：直接加载
                if (h <= MaxChunkHeight && w <= MaxChunkWidth && (long)w * h <= MaxChunkPixels)
                {
                    return new List<BitmapImage> { LoadBitmapImageFromFile(filePath) };
                }

                // 宽度或像素数超限：先等比缩放到安全宽度范围内，再按高度切块
                // （正常文档页宽一般不超过 1600px，此为极端尺寸的兜底保护）
                double widthScale = 1.0;
                if (w > MaxChunkWidth)
                {
                    widthScale = (double)MaxChunkWidth / w;
                }
                long pixelCount = (long)w * h;
                if (pixelCount > MaxChunkPixels && widthScale >= 1.0)
                {
                    double pixelScale = Math.Sqrt((double)MaxChunkPixels / pixelCount);
                    widthScale = Math.Min(widthScale, pixelScale);
                }

                int finalW, finalH;
                if (widthScale < 1.0)
                {
                    finalW = Math.Max(1, (int)Math.Round(w * widthScale));
                    finalH = Math.Max(1, (int)Math.Round(h * widthScale));
                }
                else
                {
                    finalW = w;
                    finalH = h;
                }

                // 按高度切分为多个块
                int stripCount = (int)Math.Ceiling((double)finalH / MaxChunkHeight);
                var results = new List<BitmapImage>();

                for (int s = 0; s < stripCount; s++)
                {
                    int offsetY = s * MaxChunkHeight;
                    int stripH = Math.Min(MaxChunkHeight, finalH - offsetY);
                    int stripW = finalW;

                    string tempPath = Path.Combine(Path.GetTempPath(), $"icu_doc_split_{Guid.NewGuid():N}.png");
                    try
                    {
                        using (var bitmap = new System.Drawing.Bitmap(stripW, stripH, PixelFormat.Format32bppArgb))
                        {
                            bitmap.SetResolution(96, 96);
                            using (var g = Graphics.FromImage(bitmap))
                            {
                                g.Clear(Color.White);
                                g.CompositingQuality = CompositingQuality.HighQuality;
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                                if (widthScale < 1.0)
                                {
                                    // 整体缩放后再切块：从源图对应区域等比绘制到目标
                                    int srcOffsetY = (int)Math.Round(offsetY / widthScale);
                                    int srcStripH = (int)Math.Round(stripH / widthScale);
                                    srcStripH = Math.Min(srcStripH, h - srcOffsetY);
                                    var destRect = new Rectangle(0, 0, stripW, stripH);
                                    var srcRect = new Rectangle(0, srcOffsetY, w, srcStripH);
                                    g.DrawImage(img, destRect, srcRect, GraphicsUnit.Pixel);
                                }
                                else
                                {
                                    // 1:1 像素复制
                                    var destRect = new Rectangle(0, 0, stripW, stripH);
                                    var srcRect = new Rectangle(0, offsetY, stripW, stripH);
                                    g.DrawImage(img, destRect, srcRect, GraphicsUnit.Pixel);
                                }
                            }
                            bitmap.Save(tempPath, ImageFormat.Png);
                        }
                        results.Add(LoadBitmapImageFromFile(tempPath));
                        try { File.Delete(tempPath); } catch { }
                    }
                    catch
                    {
                        try { File.Delete(tempPath); } catch { }
                        throw;
                    }
                }

                progress?.Report(new ConversionProgress
                {
                    FileName = null,
                    Current = 1,
                    Total = stripCount,
                    Message = $"文档较大，已拆分为 {stripCount} 张长照片（第 1/{stripCount} 张）"
                });

                return results;
            }
        }

        public static BitmapImage LoadBitmapImageFromFile(string filePath)
        {
            var bitmapImage = new BitmapImage();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmapImage.StreamSource = fs;
                bitmapImage.EndInit();
            }
            bitmapImage.Freeze();
            return bitmapImage;
        }
    }
}
