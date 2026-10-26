namespace Ink_Canvas.Plugins.DocumentToImage.UI
{
    public class ConversionProgress
    {
        public string FileName { get; set; }

        /// <summary>当前步骤（第几张 / 第几个工作表）。</summary>
        public int Current { get; set; }

        public int Total { get; set; }

        public string Message { get; set; }
    }
}
