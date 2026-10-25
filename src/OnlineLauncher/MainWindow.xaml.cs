using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace OnlineLauncher;
public partial class MainWindow : Window
{
 private readonly CommunityApiClient _api = new();
 public MainWindow(){InitializeComponent(); Loaded += async (_, _) => await RefreshServiceStatusAsync();}
 private async Task RefreshServiceStatusAsync(){var ok=await _api.CheckBootstrapAsync();StatusDot.Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? "#58B783" : "#D6A63D"));ApiStatusText.Text=ok ? "Community API online" : "API unavailable";}
 private void ShowPage(string eyebrow,string title,string message){PageEyebrow.Text=eyebrow;PageTitle.Text=title;PageContent.Children.Clear();PageContent.Children.Add(new Border{Background=(Brush)FindResource("PanelBrush"),CornerRadius=new CornerRadius(7),Padding=new Thickness(24),Child=new TextBlock{Text=message,Foreground=new SolidColorBrush(Color.FromRgb(197,199,204)),FontSize=14,TextWrapping=TextWrapping.Wrap}});}
 private void Home_Click(object s,RoutedEventArgs e)=>ShowPage("1.61ONLINE NETWORK","Welcome back","Your community dashboard is ready.");
 private void Social_Click(object s,RoutedEventArgs e)=>ShowPage("COMMUNITY","Social","Friends, player search and account linking will appear here when the backend social endpoints are connected.");
 private void Sessions_Click(object s,RoutedEventArgs e)=>ShowPage("PLAY TOGETHER","Sessions","Community-hosted sessions will be listed here. This does not connect to Rockstar Online services.");
 private void Profile_Click(object s,RoutedEventArgs e)=>ShowPage("YOUR ACCOUNT","Profile","Sign in to view your 1.61ONLINE community profile.");
 private void Settings_Click(object s,RoutedEventArgs e)=>ShowPage("PREFERENCES","Settings","Launcher settings and the supported game installation path will be configured here.");
 private void SignIn_Click(object s,RoutedEventArgs e)=>ShowPage("ACCOUNT","Sign in","Account authentication UI will be connected to the community API in the next milestone.");
}