using System.Linq;
using System.Windows;
using InspectionView.Core.Backends;
using InspectionView.Core.Registry;

namespace InspectionView.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var reg = OperatorRegistry.Default;
            OperatorList.Text = "Registered operators:\n" +
                                string.Join("\n", reg.All.Select(d => $"  [{d.Category}] {d.Signature}   backends: {string.Join("/", d.Backends.Select(b => b.Kind))}"));

            StatusText.Text = $"{reg.Count} operator(s) | GPU: {(GpuRuntime.IsAvailable ? "available" : "not available")}";
        }

        private void OnExit(object sender, RoutedEventArgs e) => Close();
    }
}
