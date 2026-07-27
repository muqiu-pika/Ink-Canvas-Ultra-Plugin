using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.VideoControls
{
    /// <summary>
    /// 视频控件 plugin：
    /// - 注册路由 "video-insert" 接管主程序的视频插入
    /// - 订阅 ElementSelectionChanged 显示控制条
    /// - 订阅 ElementRemoved / ElementTransformed 同步状态
    /// - 通过 host.RegisterSelectionControlBar 把控制条注册到主程序 VideoControlContainer 插槽
    /// </summary>
    public class VideoControlsPlugin : IPlugin
    {
        public PluginManifest Manifest { get; private set; }

        private IPluginHost _host;
        private InkCanvas _inkCanvas;

        // 控制条 UI
        private Border _controlBar;
        private Button _btnPlayPause;
        private TextBlock _iconPlayPause;
        private Slider _sliderProgress;
        private Slider _sliderVolume;

        // 状态
        private MediaElement _selectedMediaElement;
        private DispatcherTimer _videoTimer;
        private bool _isSeeking;
        private readonly string _playGlyph = "\ue768";
        private readonly string _pauseGlyph = "\ue769";

        public void Initialize(IPluginHost host)
        {
            _host = host;
            Manifest = new PluginManifest
            {
                Id = "ink-canvas.videocontrols",
                Name = "视频控件",
                Version = "1.0.0"
            };

            _host.Log($"VideoControlsPlugin 已初始化，目录: {_host.PluginDirectory}", PluginLogLevel.Event);

            // 获取主程序画布
            _inkCanvas = _host.GetInkCanvas();

            // 构建控制条 UI
            BuildControlBar();

            // 注册到主程序插槽
            _host.RegisterSelectionControlBar(_controlBar);
            _controlBar.Visibility = Visibility.Collapsed;

            // 订阅事件
            _host.ElementSelectionChanged += OnSelectionChanged;
            _host.ElementRemoved += OnElementRemoved;
            _host.ElementTransformed += OnElementTransformed;

            // 注册路由处理器：接管主程序的 "video-insert" 路由
            _host.RegisterRouteHandler("video-insert", parameter =>
            {
                try
                {
                    string filePath = parameter as string;
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    {
                        _host.ShowNotification("视频文件无效");
                        return false;
                    }
                    InsertVideo(filePath);
                    return true;
                }
                catch (Exception ex)
                {
                    _host.Log($"video-insert 处理失败: {ex.Message}", PluginLogLevel.Error);
                    return false;
                }
            });
        }

        public void Shutdown()
        {
            try
            {
                StopTimer();
                if (_host != null)
                {
                    _host.ElementSelectionChanged -= OnSelectionChanged;
                    _host.ElementRemoved -= OnElementRemoved;
                    _host.ElementTransformed -= OnElementTransformed;
                    _host.UnregisterRouteHandler("video-insert");
                    _host.UnregisterSelectionControlBar(_controlBar);
                }
                _selectedMediaElement = null;
                _host?.Log("VideoControlsPlugin 已关闭", PluginLogLevel.Event);
            }
            catch { }
        }

        // ===== 控制条 UI 构建 =====

        private void BuildControlBar()
        {
            _iconPlayPause = new TextBlock
            {
                Text = _playGlyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _btnPlayPause = new Button
            {
                Width = 40,
                Height = 35,
                Padding = new Thickness(0),
                Content = _iconPlayPause
            };
            _btnPlayPause.Click += BtnPlayPause_Click;

            _sliderProgress = new Slider
            {
                Width = 300,
                Minimum = 0,
                Maximum = 100,
                IsMoveToPointEnabled = true
            };
            _sliderProgress.PreviewMouseDown += SliderProgress_PreviewMouseDown;
            _sliderProgress.PreviewMouseUp += SliderProgress_PreviewMouseUp;
            _sliderProgress.ValueChanged += SliderProgress_ValueChanged;

            var volumeIcon = new TextBlock
            {
                Text = "\ue767",
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };

            _sliderVolume = new Slider
            {
                Width = 140,
                Minimum = 0,
                Maximum = 100
            };
            _sliderVolume.ValueChanged += SliderVolume_ValueChanged;

            // .NET Framework 4.7.2 的 StackPanel 没有 Spacing 属性，用 Margin 手动间距
            _btnPlayPause.Margin = new Thickness(0, 0, 6, 0);
            _sliderProgress.Margin = new Thickness(0, 0, 6, 0);
            volumeIcon.Margin = new Thickness(0, 0, 4, 0);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(_btnPlayPause);
            panel.Children.Add(_sliderProgress);
            panel.Children.Add(volumeIcon);
            panel.Children.Add(_sliderVolume);

            _controlBar = new Border
            {
                Height = 50,
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = panel
            };
        }

        // ===== 视频插入 =====

        private void InsertVideo(string filePath)
        {
            if (_inkCanvas == null)
            {
                _host.ShowNotification("画布未就绪");
                return;
            }

            var savePath = Path.Combine(_host.AutoSavedStrokesLocation, "File Dependency");
            if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);

            string timestamp = "media_" + DateTime.Now.ToString("yyyyMMdd_HH_mm_ss_fff");
            string fileExtension = Path.GetExtension(filePath);
            string newFilePath = Path.Combine(savePath, timestamp + fileExtension);
            File.Copy(filePath, newFilePath, true);

            var mediaElement = new MediaElement
            {
                Source = new Uri(newFilePath),
                Name = timestamp,
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                IsHitTestVisible = true,
                Focusable = true,
                Width = 256,
                Height = 256
            };

            // 点击 MediaElement 切换播放/暂停
            mediaElement.Tag = false;
            void TogglePlayback()
            {
                bool isPlaying = mediaElement.Tag is bool b && b;
                if (isPlaying) { mediaElement.Pause(); mediaElement.Tag = false; }
                else { mediaElement.Play(); mediaElement.Tag = true; }
            }
            mediaElement.PreviewMouseLeftButtonDown += (s, e) => { e.Handled = true; TogglePlayback(); };
            mediaElement.PreviewTouchDown += (s, e) => { e.Handled = true; TogglePlayback(); };
            mediaElement.PreviewStylusDown += (s, e) => { e.Handled = true; TogglePlayback(); };

            // 居中缩放
            CenterAndScaleElement(mediaElement);

            InkCanvas.SetLeft(mediaElement, 0);
            InkCanvas.SetTop(mediaElement, 0);
            _inkCanvas.Children.Add(mediaElement);

            mediaElement.Loaded += (_, __) =>
            {
                try { if (_inkCanvas.Children.Contains(mediaElement)) mediaElement.Play(); } catch { }
            };
            mediaElement.MediaFailed += (_, args) =>
            {
                _host.Log($"媒体加载失败: {args.ErrorException?.Message}", PluginLogLevel.Error);
                _host.ShowNotification("视频文件加载失败");
            };

            _host.CommitElementInsertHistory(mediaElement);
            _host.ShowNotification($"已插入视频：{Path.GetFileName(filePath)}");
        }

        private void CenterAndScaleElement(FrameworkElement element)
        {
            double maxWidth = SystemParameters.PrimaryScreenWidth / 2;
            double maxHeight = SystemParameters.PrimaryScreenHeight / 2;
            double scaleX = maxWidth / element.Width;
            double scaleY = maxHeight / element.Height;
            double scale = Math.Min(scaleX, scaleY);

            var transformGroup = new System.Windows.Media.TransformGroup();
            transformGroup.Children.Add(new System.Windows.Media.ScaleTransform(scale, scale));

            double canvasWidth = _inkCanvas.ActualWidth;
            double canvasHeight = _inkCanvas.ActualHeight;
            double centerX = (canvasWidth - element.Width * scale) / 2;
            double centerY = (canvasHeight - element.Height * scale) / 2;

            transformGroup.Children.Add(new System.Windows.Media.TranslateTransform(centerX, centerY));
            element.RenderTransform = transformGroup;
        }

        // ===== 选择事件处理 =====

        private void OnSelectionChanged(object sender, PluginElementSelectionChangedEventArgs e)
        {
            _selectedMediaElement = null;
            if (e.SelectedElements != null)
            {
                foreach (var el in e.SelectedElements)
                {
                    if (el is MediaElement me)
                    {
                        _selectedMediaElement = me;
                        break;
                    }
                }
            }

            if (_selectedMediaElement != null)
            {
                ShowControlBar();
            }
            else
            {
                HideControlBar();
            }
        }

        private void OnElementRemoved(object sender, PluginElementEventArgs e)
        {
            // 如果被删除的是当前选中的视频元素，隐藏控制条
            if (e.Element == _selectedMediaElement)
            {
                HideControlBar();
            }
        }

        private void OnElementTransformed(object sender, PluginElementEventArgs e)
        {
            // 元素被变换时无需特殊处理（控制条嵌入在父容器中随父容器布局）
        }

        // ===== 控制条显示/隐藏 =====

        private void ShowControlBar()
        {
            if (_selectedMediaElement == null) return;
            try
            {
                _sliderVolume.Value = _selectedMediaElement.Volume * 100;
                if (_selectedMediaElement.NaturalDuration.HasTimeSpan)
                {
                    _sliderProgress.Maximum = _selectedMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
                    _sliderProgress.Value = _selectedMediaElement.Position.TotalSeconds;
                }

                _selectedMediaElement.MediaOpened -= MediaOpened;
                _selectedMediaElement.MediaOpened += MediaOpened;
                _selectedMediaElement.MediaEnded -= MediaEnded;
                _selectedMediaElement.MediaEnded += MediaEnded;

                if (_selectedMediaElement.CanPause &&
                    _selectedMediaElement.NaturalDuration.HasTimeSpan &&
                    _selectedMediaElement.Position > TimeSpan.Zero &&
                    _selectedMediaElement.Position < _selectedMediaElement.NaturalDuration.TimeSpan)
                {
                    _iconPlayPause.Text = _pauseGlyph;
                }
                else
                {
                    _iconPlayPause.Text = _playGlyph;
                }

                _controlBar.Visibility = Visibility.Visible;
                StartTimer();
            }
            catch { }
        }

        private void HideControlBar()
        {
            try
            {
                _controlBar.Visibility = Visibility.Collapsed;
                StopTimer();
                var temp = _selectedMediaElement;
                _selectedMediaElement = null;
                if (temp != null)
                {
                    temp.MediaOpened -= MediaOpened;
                    temp.MediaEnded -= MediaEnded;
                }
                _iconPlayPause.Text = _playGlyph;
            }
            catch { }
        }

        // ===== 计时器 =====

        private void StartTimer()
        {
            if (_videoTimer == null)
            {
                _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _videoTimer.Tick += VideoTimer_Tick;
            }
            _videoTimer.Start();
        }

        private void StopTimer()
        {
            _videoTimer?.Stop();
        }

        private void VideoTimer_Tick(object sender, EventArgs e)
        {
            if (_selectedMediaElement == null || _isSeeking) return;
            try
            {
                if (_selectedMediaElement.NaturalDuration.HasTimeSpan)
                {
                    _sliderProgress.Maximum = _selectedMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
                    _sliderProgress.Value = _selectedMediaElement.Position.TotalSeconds;
                }
            }
            catch { }
        }

        // ===== 按钮与滑块事件 =====

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMediaElement == null) return;
            try
            {
                if (_iconPlayPause.Text == _playGlyph)
                {
                    _selectedMediaElement.Play();
                    _iconPlayPause.Text = _pauseGlyph;
                }
                else
                {
                    if (_selectedMediaElement.CanPause) _selectedMediaElement.Pause();
                    else _selectedMediaElement.Stop();
                    _iconPlayPause.Text = _playGlyph;
                }
            }
            catch { }
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_selectedMediaElement == null) return;
            try { _selectedMediaElement.Volume = _sliderVolume.Value / 100.0; } catch { }
        }

        private void SliderProgress_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }

        private void SliderProgress_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isSeeking = false;
            SliderProgress_ValueChanged(sender, null);
        }

        private void SliderProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_selectedMediaElement == null) return;
            if (!_selectedMediaElement.NaturalDuration.HasTimeSpan) return;
            try
            {
                _selectedMediaElement.Position = TimeSpan.FromSeconds(_sliderProgress.Value);
            }
            catch { }
        }

        private void MediaOpened(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedMediaElement == null) return;
                if (_selectedMediaElement.NaturalDuration.HasTimeSpan)
                {
                    _sliderProgress.Maximum = _selectedMediaElement.NaturalDuration.TimeSpan.TotalSeconds;
                }
            }
            catch { }
        }

        private void MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                _iconPlayPause.Text = _playGlyph;
                if (_selectedMediaElement != null && _selectedMediaElement.NaturalDuration.HasTimeSpan)
                {
                    _sliderProgress.Value = _sliderProgress.Maximum;
                }
            }
            catch { }
        }
    }
}
