using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using InspectionView.Core.Backends;
using InspectionView.Core.Registry;

namespace InspectionView.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // GPU 가 있으면 전역 설정에 연결 (없으면 CPU/Parallel 만 사용)
            BackendSettings.Global.Gpu = GpuRuntime.DefaultAllocator;
            // OpenCL(Intel/AMD/NVIDIA) 은 OpenCV T-API 로 사용. 바인딩이 있으면 명시적으로 켠다.
            OpenClRuntime.EnableInOpenCv();

            // Plugins 폴더의 연산자 DLL 로드
            var pluginDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
            var result = OperatorRegistry.Default.LoadPlugins(pluginDir);
            foreach (var f in result.Failures)
                System.Diagnostics.Debug.WriteLine(f.Error.Message);
        }

        /// <summary>
        /// 종료 보장: 카메라 프로듀서 같은 네이티브 라이브러리나 남은 작업이 종료를 붙잡아 프로세스가 남으면
        /// (창은 닫혔는데 exe 가 잠겨 다시 빌드가 안 되는 증상) 5초 뒤 강제로 끝낸다. 정상이면 그 전에 프로세스가 끝난다.
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(5000);
                try { Process.GetCurrentProcess().Kill(); } catch { /* 이미 끝나는 중 */ }
            })
            { IsBackground = true, Name = "Exit watchdog" };
            watchdog.Start();
        }
    }
}
