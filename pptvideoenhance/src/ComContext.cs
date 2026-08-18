using System;
using System.Threading;
using System.Windows.Threading;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// 在专用 STA 线程上运行 Dispatcher，供 PowerPoint / WPS 的 COM 调用封送，
    /// 避免 RPC_E_SERVERCALL_RETRYLATER / CALL_REJECTED 等 COM 繁忙错误。
    /// </summary>
    internal sealed class ComContext : IDisposable
    {
        private readonly Thread _thread;
        private Dispatcher _dispatcher;
        private bool _started;

        public ComContext()
        {
            _thread = new Thread(RunLoop)
            {
                Name = "PPTVideoEnhance-Com",
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        private void RunLoop()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _thread.Start();
            // 等待 Dispatcher 在当前线程上就绪
            while (_dispatcher == null) Thread.Sleep(1);
        }

        public T Invoke<T>(Func<T> func)
        {
            if (_dispatcher == null) return default;
            return _dispatcher.Invoke(func);
        }

        public void Invoke(Action action)
        {
            if (_dispatcher == null) return;
            _dispatcher.Invoke(action);
        }

        public void BeginInvoke(Action action)
        {
            if (_dispatcher == null) return;
            _dispatcher.BeginInvoke(action);
        }

        public void Stop()
        {
            if (_dispatcher != null)
            {
                try { _dispatcher.InvokeShutdown(); } catch { }
            }
            try { if (_thread.IsAlive) _thread.Join(1000); } catch { }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
