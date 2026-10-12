using System;
using System.IO;
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

            // Plugins 폴더의 연산자 DLL 로드
            var pluginDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
            var result = OperatorRegistry.Default.LoadPlugins(pluginDir);
            foreach (var f in result.Failures)
                System.Diagnostics.Debug.WriteLine(f.Error.Message);
        }
    }
}
