using AppTrace.Helpers;
using System.Windows;

namespace AppTrace
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ListViewSort _listView;

        public MainWindow()
        {
            InitializeComponent();

            _listView = new ListViewSort(InstalledPrograms);


            Loaded += async (s, e) =>
                    {
                        var reader = new InstalledAppsReader();
                        var apps = await Task.Run(() => reader.GetInstalledApps());
                        InstalledPrograms.ItemsSource = apps;
                        ProgramCountLabel.Content = InstalledPrograms.Items.Count.ToString();
                    };
        }
    }
}