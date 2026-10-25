using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Flux.Core;
using Flux.App.Services;
using Flux.App.Integration;
using System.Windows.Threading;
namespace Flux.App;
public partial class SettingsDialog:Window
{
 public DownloadSettings Settings{get;private set;} private string? _releaseUrl;
 private readonly DispatcherTimer _statusTimer=new(){Interval=TimeSpan.FromSeconds(1)};
 public SettingsDialog(DownloadSettings settings,int initialTab=0)
 {
  SetResourceReference(StyleProperty,typeof(Window));
  Settings=settings.Clone();InitializeComponent();FolderBox.Text=settings.DefaultDownloadDirectory;ConcurrentBox.Text=settings.MaxConcurrentDownloads.ToString();RetryBox.Text=settings.RetryCount.ToString();
  OptionsTabs.SelectedIndex=Math.Clamp(initialTab,0,5);ConnectionsBox.ItemsSource=Enumerable.Range(1,32).ToArray();ConnectionsBox.SelectedItem=settings.ConnectionsPerDownload;
  SpeedBox.Text=(settings.SpeedLimitBytesPerSecond/1024d).ToString("0.##",CultureInfo.CurrentCulture);GlobalSpeedBox.Text=(settings.GlobalSpeedLimitBytesPerSecond/1024d).ToString("0.##",CultureInfo.CurrentCulture);
  SpeedUnit.ItemsSource=GlobalSpeedUnit.ItemsSource=new[]{"KB/s","MB/s"};SpeedUnit.SelectedIndex=GlobalSpeedUnit.SelectedIndex=0;CollisionBox.ItemsSource=Enum.GetValues<CollisionPolicy>();CollisionBox.SelectedItem=settings.CollisionPolicy;
  AutoSortCheck.IsChecked=settings.AutoSortCategories;TrayCheck.IsChecked=settings.MinimizeToTray;ClipboardCheck.IsChecked=settings.ClipboardMonitorEnabled;UpdatesCheck.IsChecked=settings.CheckForUpdates;NotificationsCheck.IsChecked=settings.NotificationsEnabled;DailyCheck.IsChecked=settings.DailyScheduleEnabled;DailyStartBox.Text=settings.DailyStart;DailyStopBox.Text=settings.DailyStop;ScannerBox.Text=settings.ScannerExecutable??"";
  string Category(string key,string alternate="")=>settings.CategoryDirectories.GetValueOrDefault(key)??settings.CategoryDirectories.GetValueOrDefault(alternate)??"";
  VideoFolderBox.Text=Category("Video","Videos");MusicFolderBox.Text=Category("Music","Audio");DocumentsFolderBox.Text=Category("Documents");ProgramsFolderBox.Text=Category("Programs","Applications");OtherFolderBox.Text=Category("Other");
  CompressedFolderBox.Text=Category("Archives","Compressed");StartupCheck.IsChecked=settings.StartWithWindows;SoundCheck.IsChecked=settings.SoundOnCompletion;RetryDelayBox.Text=settings.RetryDelaySeconds.ToString(CultureInfo.CurrentCulture);ScheduledSpeedBox.Text=(settings.ScheduledSpeedLimitBytesPerSecond/1024d).ToString("0.##",CultureInfo.CurrentCulture);CaptureTypesBox.Text=settings.CaptureFileTypes;WhitelistBox.Text=settings.DomainWhitelist;BlacklistBox.Text=settings.DomainBlacklist;
  YtDlpPathBox.Text=settings.YtDlpPath??"";FfmpegPathBox.Text=settings.FfmpegPath??"";JavaScriptPathBox.Text=settings.JavaScriptRuntimePath??"";DefaultQualityBox.ItemsSource=Enum.GetValues<MediaQuality>();DefaultQualityBox.SelectedItem=settings.DefaultMediaQuality;CookieBrowserBox.ItemsSource=Enum.GetValues<BrowserCookieSource>();CookieBrowserBox.SelectedItem=settings.MediaCookieBrowser;
  RefreshBrowserStatuses();_statusTimer.Tick+=(_,_)=>RefreshBrowserStatuses();_statusTimer.Start();Closed+=(_,_)=>_statusTimer.Stop();
 }
 private void RefreshBrowserStatuses(){var statuses=BrowserIntegration.GetStatuses();BrowserStatusText.Inlines.Clear();foreach(var browser in new[]{"Chrome","Edge","Firefox"}){if(BrowserStatusText.Inlines.Count>0)BrowserStatusText.Inlines.Add(new System.Windows.Documents.LineBreak());var connected=statuses.GetValueOrDefault(browser);BrowserStatusText.Inlines.Add(new System.Windows.Documents.Run(browser+": "+(connected?"Connected ✓":"Not connected — use setup instructions")){Foreground=Display.Brush(connected?"#74CA93":"#9A96AB")});}}
 private void DragDialog(object sender,MouseButtonEventArgs e){if(e.ChangedButton==MouseButton.Left)try{DragMove();}catch(InvalidOperationException){}}
 private void Browse_Click(object sender,RoutedEventArgs e){using var d=new System.Windows.Forms.FolderBrowserDialog{Description="Default download directory",UseDescriptionForTitle=true,SelectedPath=FolderBox.Text};if(d.ShowDialog()==System.Windows.Forms.DialogResult.OK)FolderBox.Text=d.SelectedPath;}
 private void BrowserSettings_Click(object sender,RoutedEventArgs e){DialogResult=false;if(Owner is MainWindow window)window.ShowBrowserPage();}
 private async void CheckUpdates_Click(object sender,RoutedEventArgs e){UpdateStatusText.Text="Checking GitHub…";try{var result=await UpdateService.CheckAsync();UpdateStatusText.Text=result is null?"You are using the current release.":"Flux "+result.Version+" is available.";_releaseUrl=result?.Url;OpenReleaseButton.Visibility=result is null?Visibility.Collapsed:Visibility.Visible;}catch(Exception ex){UpdateStatusText.Text="Could not check: "+ex.Message;}}
 private void OpenRelease_Click(object sender,RoutedEventArgs e){if(_releaseUrl is not null)Process.Start(new ProcessStartInfo(_releaseUrl){UseShellExecute=true});}
 private static long Limit(string text,int unit){if(!decimal.TryParse(text,NumberStyles.Number,CultureInfo.CurrentCulture,out var value)||value<0||value>100000000)throw new ArgumentException("Enter a valid non-negative bandwidth limit.");return checked((long)(value*(unit==1?1024*1024:1024)));}
 private void Save_Click(object sender,RoutedEventArgs e)
 {
  try{
   if(!int.TryParse(ConcurrentBox.Text,out var concurrent)||concurrent is <1 or >20)throw new ArgumentException("Simultaneous downloads must be between 1 and 20.");if(!int.TryParse(RetryBox.Text,out var retries)||retries is <0 or >10)throw new ArgumentException("Retries must be between 0 and 10.");if(!double.TryParse(RetryDelayBox.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out var retryDelay)||!double.IsFinite(retryDelay)||retryDelay<0||retryDelay>300)throw new ArgumentException("Retry delay must be between 0 and 300 seconds.");
   if(!Path.IsPathFullyQualified(FolderBox.Text.Trim()))throw new ArgumentException("Choose a full default directory.");if(DailyCheck.IsChecked==true&&(!TimeSpan.TryParseExact(DailyStartBox.Text.Trim(),@"hh\:mm",CultureInfo.InvariantCulture,out var start)||!TimeSpan.TryParseExact(DailyStopBox.Text.Trim(),@"hh\:mm",CultureInfo.InvariantCulture,out var stop)||start==stop))throw new ArgumentException("Choose different valid start/stop times in HH:mm format.");
   var directories=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var pair in new[]{("Video",VideoFolderBox),("Music",MusicFolderBox),("Archives",CompressedFolderBox),("Documents",DocumentsFolderBox),("Programs",ProgramsFolderBox),("Other",OtherFolderBox)}){var path=pair.Item2.Text.Trim();if(path.Length==0)continue;if(!Path.IsPathFullyQualified(path))throw new ArgumentException("Category directories must be full paths.");directories[pair.Item1]=path;if(pair.Item1=="Music")directories["Audio"]=path;if(pair.Item1=="Programs")directories["Applications"]=path;}
   var scanner=ScannerBox.Text.Trim();if(scanner.Length>0&&(!Path.IsPathFullyQualified(scanner)||!File.Exists(scanner)))throw new ArgumentException("The configured scanner executable does not exist.");
   string? ToolPath(string value){value=value.Trim();if(value.Length==0)return null;if(!Path.IsPathFullyQualified(value)||!File.Exists(value))throw new ArgumentException("Custom tool paths must point to existing executables.");return value;}
   Settings.YtDlpPath=ToolPath(YtDlpPathBox.Text);Settings.FfmpegPath=ToolPath(FfmpegPathBox.Text);Settings.JavaScriptRuntimePath=ToolPath(JavaScriptPathBox.Text);Settings.DefaultMediaQuality=(MediaQuality)DefaultQualityBox.SelectedItem;Settings.MediaCookieBrowser=(BrowserCookieSource)CookieBrowserBox.SelectedItem;Settings.StartWithWindows=StartupCheck.IsChecked==true;Settings.SoundOnCompletion=SoundCheck.IsChecked==true;Settings.RetryDelaySeconds=retryDelay;Settings.ScheduledSpeedLimitBytesPerSecond=Limit(ScheduledSpeedBox.Text,0);Settings.CaptureFileTypes=CaptureTypesBox.Text.Trim();Settings.DomainWhitelist=WhitelistBox.Text.Trim();Settings.DomainBlacklist=BlacklistBox.Text.Trim();
   Settings.DefaultDownloadDirectory=FolderBox.Text.Trim();Settings.MaxConcurrentDownloads=concurrent;Settings.RetryCount=retries;Settings.ConnectionsPerDownload=(int)ConnectionsBox.SelectedItem;Settings.SpeedLimitBytesPerSecond=Limit(SpeedBox.Text,SpeedUnit.SelectedIndex);Settings.GlobalSpeedLimitBytesPerSecond=Limit(GlobalSpeedBox.Text,GlobalSpeedUnit.SelectedIndex);Settings.CollisionPolicy=(CollisionPolicy)CollisionBox.SelectedItem;Settings.CategoryDirectories=directories;Settings.AutoSortCategories=AutoSortCheck.IsChecked==true;Settings.MinimizeToTray=TrayCheck.IsChecked==true;Settings.ClipboardMonitorEnabled=ClipboardCheck.IsChecked==true;Settings.CheckForUpdates=UpdatesCheck.IsChecked==true;Settings.NotificationsEnabled=NotificationsCheck.IsChecked==true;Settings.DailyScheduleEnabled=DailyCheck.IsChecked==true;Settings.DailyStart=DailyStartBox.Text.Trim();Settings.DailyStop=DailyStopBox.Text.Trim();Settings.ScannerExecutable=scanner.Length==0?null:scanner;DialogResult=true;
  }catch(Exception ex)when(ex is ArgumentException or FormatException or NotSupportedException or OverflowException){ErrorText.Text=ex.Message;}
 }
}
