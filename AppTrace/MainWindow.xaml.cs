using AppTrace.Helpers;
using System.Windows;

namespace AppTrace
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            Loaded += async (s, e) =>
            {
                var reader = new InstalledAppsReader();
                var apps = await Task.Run(() => reader.GetInstalledApps());
                InstalledPrograms.ItemsSource = apps;
                ProgramCountLabel.Content = InstalledPrograms.Items.Count.ToString();
            };
        }

        private void InstalledPrograms_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {

        }
    }
}