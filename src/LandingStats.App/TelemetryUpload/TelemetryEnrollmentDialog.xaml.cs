using System.Windows;

namespace LandingStats.App.TelemetryUpload;

public partial class TelemetryEnrollmentDialog : Window
{
    public TelemetryEnrollmentDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => InviteCodeText.Focus();
    }

    public string InviteCode => InviteCodeText.Text.Trim();

    private void OnEnrollClick(object sender, RoutedEventArgs eventArgs)
    {
        if (InviteCode.Length < 20)
        {
            ErrorText.Text = "The invitation code is incomplete.";
            return;
        }
        DialogResult = true;
    }
}
