using System;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// 一个视频控件在屏幕像素坐标系中的矩形区域，以及其所属放映窗口句柄。
    /// </summary>
    internal sealed class VideoRegion
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
        public IntPtr Hwnd;
    }
}
