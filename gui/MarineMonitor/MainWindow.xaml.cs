using System.Windows;

namespace MarineMonitor;
public partial class MainWindow : Window
{
    public MainViewModel Model { get; } = new();
    private bool closing;
    public MainWindow()
    {
        InitializeComponent(); DataContext = Model;
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            await Model.CloseAsync(); Close();
        };
    }
}
