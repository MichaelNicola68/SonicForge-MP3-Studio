using System.Windows;
namespace SonicForge.App;
public partial class StartCenterWindow : Window
{
 public StartCenterWindow(){InitializeComponent();}
 void Cd_Click(object s,RoutedEventArgs e){new CdImportWindow{Owner=this}.ShowDialog();}
 void Analog_Click(object s,RoutedEventArgs e)=>OpenRecorder("Schallplatte oder Kassette",false);
 void Streaming_Click(object s,RoutedEventArgs e)=>OpenRecorder("Streamingaufnahme",true);
 void Musician_Click(object s,RoutedEventArgs e)=>OpenRecorder("Mikrofon oder Instrument",false);
 void OpenRecorder(string mode,bool autoHint){var w=new RecordingStudioWindow{Owner=this};w.ConfigureMode(mode);w.Show();if(autoHint)System.Windows.MessageBox.Show(w,"Wähle für Streaming die Systemton- oder Loopback-Quelle. Für automatische Titeltrennung öffne im Aufnahmestudio den Automatikmodus.","Streamingaufnahme",MessageBoxButton.OK,MessageBoxImage.Information);}
 void Library_Click(object s,RoutedEventArgs e){Close();Owner?.Activate();}
 void Wishlist_Click(object s,RoutedEventArgs e){new WishlistWindow{Owner=this}.Show();}
 void Spotify_Click(object s,RoutedEventArgs e){OpenSpotifyApp("spotify:","https://open.spotify.com/");}
 static void OpenSpotifyApp(string appUri,string fallback){try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appUri){UseShellExecute=true});}catch{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fallback){UseShellExecute=true});}}
 void Radio_Click(object s,RoutedEventArgs e){new RadioPodcastWindow{Owner=this}.Show();}
}
