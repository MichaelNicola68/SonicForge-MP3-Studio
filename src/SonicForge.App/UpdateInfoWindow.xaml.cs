using System.Windows;

namespace SonicForge.App;

public partial class UpdateInfoWindow : Window
{
    private UpdateManifest? _availableManifest;
    public UpdateInfoWindow()
    {
        InitializeComponent();
        VersionText.Text = VersionInfoService.DisplayVersion;
        SourceText.Text = UpdateCheckService.DefaultSourcePath;
        StatusText.Text = "Bereit fuer Updatepruefung und Paketvorbereitung.";
    }
    private void Check_Click(object sender, RoutedEventArgs e)
    {
        PrepareButton.IsEnabled = false; _availableManifest = null; HideDetails();
        try
        {
            var result = UpdateCheckService.Check();
            if (result.UpdateAvailable)
            {
                _availableManifest = result.Manifest;
                StatusText.Text = "Eine neuere Version ist verfuegbar.";
                AvailableVersionText.Text = result.Manifest.Version; NotesText.Text = result.Manifest.ReleaseNotes;
                AvailableLabel.Visibility = AvailableVersionText.Visibility = Visibility.Visible;
                NotesLabel.Visibility = NotesText.Visibility = Visibility.Visible;
                PrepareButton.IsEnabled = true;
            }
            else StatusText.Text = "SonicForge ist auf dem aktuellen Stand.";
        }
        catch(Exception ex){StatusText.Text="Updatepruefung fehlgeschlagen: "+ex.Message;}
    }
    private void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_availableManifest is null) return;
        var answer=System.Windows.MessageBox.Show("Updatepaket jetzt pruefen, Sicherungsmarke erstellen und zur spaeteren Installation vorbereiten?\n\nEs werden noch keine Programmdateien ersetzt.","SonicForge Update-System",System.Windows.MessageBoxButton.YesNo,System.Windows.MessageBoxImage.Question);
        if(answer!=System.Windows.MessageBoxResult.Yes)return;
        try
        {
            var r=UpdatePreparationService.Prepare(_availableManifest);
            StatusText.Text="Paket erfolgreich geprueft und vorbereitet.";
            PreparationText.Text=$"SHA256: {r.Sha256}\nSicherung: {r.BackupPath}\nStaging: {r.StagingPath}";
            PreparationLabel.Visibility=PreparationText.Visibility=Visibility.Visible;
            PrepareButton.IsEnabled=false; InstallTestButton.IsEnabled=true; RollbackTestButton.IsEnabled=true;
        }
        catch(Exception ex){StatusText.Text="Vorbereitung fehlgeschlagen: "+ex.Message;System.Windows.MessageBox.Show(StatusText.Text,"SonicForge Update-System",System.Windows.MessageBoxButton.OK,System.Windows.MessageBoxImage.Warning);}
    }
    private void HideDetails(){AvailableLabel.Visibility=AvailableVersionText.Visibility=Visibility.Collapsed;NotesLabel.Visibility=NotesText.Visibility=Visibility.Collapsed;PreparationLabel.Visibility=PreparationText.Visibility=Visibility.Collapsed;}

    private void InstallTest_Click(object sender, RoutedEventArgs e)
    {
        if (_availableManifest is null) return;
        var answer = System.Windows.MessageBox.Show("Kontrollierte Testinstallation im isolierten LocalAppData-Bereich starten?\n\nDer laufende SonicForge-Programmstand wird nicht ersetzt.", "SonicForge Update-System", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var result = UpdateTransactionService.InstallTest(_availableManifest, false);
        StatusText.Text = result.Message;
        PreparationText.Text = $"Testinstallation: {result.InstallRoot}\nTransaktionsbackup: {result.BackupRoot}";
        PreparationLabel.Visibility = PreparationText.Visibility = Visibility.Visible;
    }

    private void RollbackTest_Click(object sender, RoutedEventArgs e)
    {
        if (_availableManifest is null) return;
        var answer = System.Windows.MessageBox.Show("Erzwungenen Fehler ausloesen und automatischen Rueckfall pruefen?\n\nNur der isolierte Testbereich wird verwendet.", "SonicForge Rueckfalltest", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var result = UpdateTransactionService.InstallTest(_availableManifest, true);
        StatusText.Text = result.Message;
        PreparationText.Text = $"Rueckfall ausgefuehrt: {result.RollbackPerformed}\nTestbereich: {result.InstallRoot}\nTransaktionsbackup: {result.BackupRoot}";
        PreparationLabel.Visibility = PreparationText.Visibility = Visibility.Visible;
    }
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
