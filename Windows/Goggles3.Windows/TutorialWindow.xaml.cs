using System.Diagnostics;
using System.Windows;

namespace Goggles3.Windows;

public partial class TutorialWindow
{
    public TutorialWindow() => InitializeComponent();

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void ManualClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://dl.djicdn.com/downloads/DJI_Goggles_3/DJI_Goggles_3_User_Manual_EN.pdf") { UseShellExecute = true });
}
