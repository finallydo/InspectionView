using System.Diagnostics;

namespace InspectionView.App.Viewer
{
    /// <summary>
    /// 그래픽 창 렌더링 시간 누적 (UI 스레드 전용). Camera 창이 라이브 중 1초마다 읽어 표시하고 초기화한다.
    /// </summary>
    public static class RenderStats
    {
        public static double RenderMsSum;
        public static int RenderCount;
        public static double UploadMsSum;
        public static int UploadCount;

        public static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public static void Reset()
        {
            RenderMsSum = 0;
            RenderCount = 0;
            UploadMsSum = 0;
            UploadCount = 0;
        }
    }
}
