using System.Windows;

namespace GwentCompanion.App;

public partial class DataContributionConsentWindow : Window
{
    public DataContributionConsentWindow() => InitializeComponent();

    public bool PublishAnonymousCurve => PublishCurveChoice.IsChecked == true;
    public bool AutomaticMonthlyUpload => AutomaticUploadChoice.IsChecked == true;

    private void KeepLocal_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Share_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
