using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Flux.App.Integration;
using Flux.Core;
namespace Flux.App;
public partial class AddDownloadDialog : Window
{
 public List<DownloadRequest> Requests{get;private set;}=[];
 public AddDownloadDialog(DownloadSettings settings,BrowserDownload? captured=null)
 {
  SetResourceReference(StyleProperty,typeof(Window));InitializeComponent();FolderBox.Text=settings.DefaultDownloadDirectory;
  ConnectionsBox.ItemsSource=new[]{1,2,4,8,16,24,32};ConnectionsBox.SelectedItem=settings.ConnectionsPerDownload;
  if(ConnectionsBox.SelectedItem is null)ConnectionsBox.SelectedItem=8;
  PriorityBox.ItemsSource=Enum.GetValues<DownloadPriority>();PriorityBox.SelectedItem=DownloadPriority.Normal;
  KindBox.ItemsSource=Enum.GetValues<DownloadKind>();KindBox.SelectedItem=captured?.IsMedia==true?DownloadKind.Media:DownloadKind.Auto;
  ActionBox.ItemsSource=Enum.GetValues<CompletionAction>();ActionBox.SelectedItem=CompletionAction.None;
  CategoryBox.ItemsSource=new[]{"Automatic","Video","Music","Documents","Programs","Other"};CategoryBox.SelectedIndex=0;
  SpeedUnitBox.ItemsSource=new[]{"KB/s","MB/s"};SpeedUnitBox.SelectedIndex=0;
  StartTimeBox.Text=DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);StopTimeBox.Text=DateTime.Now.AddHours(4).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
  if(captured is not null){UrlsBox.Text=captured.Url;FileNameBox.Text=captured.FileName??"";ReferrerBox.Text=captured.Referrer??"";UserAgentBox.Text=captured.UserAgent??"";CookiesBox.Text=captured.Cookies??"";}
  Loaded+=(_,_)=>{UrlsBox.Focus();if(captured is null)TryPasteUrl();};
  PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control){Start_Click(this,new RoutedEventArgs());e.Handled=true;}};
 }
 private void TryPasteUrl(){try{if(UrlsBox.Text.Length>0||!Clipboard.ContainsText())return;var t=Clipboard.GetText().Trim();if(t.Length<=65536&&t.Split('\n',StringSplitOptions.RemoveEmptyEntries).All(l=>Uri.TryCreate(l.Trim(),UriKind.Absolute,out var u)&&u.Scheme is "http" or "https")){UrlsBox.Text=t;UrlsBox.SelectAll();}}catch{}}
 private void DragDialog(object sender,MouseButtonEventArgs e){if(e.ChangedButton==MouseButton.Left)try{DragMove();}catch(InvalidOperationException){}}
 private void Browse_Click(object sender,RoutedEventArgs e){using var d=new System.Windows.Forms.FolderBrowserDialog{Description="Choose where Flux saves downloads",UseDescriptionForTitle=true,SelectedPath=FolderBox.Text};if(d.ShowDialog()==System.Windows.Forms.DialogResult.OK)FolderBox.Text=d.SelectedPath;}
 private void ScheduleChanged(object sender,RoutedEventArgs e){if(StartButton!=null)StartButton.Content=ScheduleCheck.IsChecked==true?"SCHEDULE DOWNLOAD":"START DOWNLOAD";}
 private static DateTimeOffset ParseWhen(string text){if(!DateTime.TryParseExact(text.Trim(),"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||TimeZoneInfo.Local.IsInvalidTime(date))throw new ArgumentException("Enter a valid local date/time, such as 2026-10-25 22:30.");return new DateTimeOffset(date);}
 private static void ValidateUrl(string value){if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme is not("http" or "https")||u.UserInfo.Length>0)throw new ArgumentException("Every URL must be a complete HTTP/HTTPS link without embedded credentials.");}
 private void Start_Click(object sender,RoutedEventArgs e)
 {
  try{
   var urls=UrlsBox.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();if(urls.Length==0||urls.Length>100)throw new ArgumentException("Provide between 1 and 100 URLs.");foreach(var u in urls)ValidateUrl(u);
   var folder=FolderBox.Text.Trim();if(!Path.IsPathFullyQualified(folder))throw new ArgumentException("Choose a full destination folder path.");
   var name=FileNameBox.Text.Trim();if(urls.Length>1&&name.Length>0)throw new ArgumentException("Leave filename empty for a batch.");if(name.Length>0&&(name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new ArgumentException("Use a filename without directory separators or invalid characters.");
   DateTimeOffset? start=ScheduleCheck.IsChecked==true?ParseWhen(StartTimeBox.Text):null;DateTimeOffset? stop=StopCheck.IsChecked==true?ParseWhen(StopTimeBox.Text):null;
   if(start<=DateTimeOffset.Now)throw new ArgumentException("The start time must be in the future.");if(stop.HasValue&&stop.Value<=(start??DateTimeOffset.Now))throw new ArgumentException("Stop time must be after the start.");
   long? cap=null;if(SpeedCheck.IsChecked==true){if(!decimal.TryParse(SpeedBox.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out var speed)||speed<=0||speed>100000000)throw new ArgumentException("Enter a positive speed limit.");cap=checked((long)(speed*(SpeedUnitBox.SelectedIndex==1?1024*1024:1024)));}
   var mirrors=MirrorsBox.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToList();if(mirrors.Count>16||mirrors.Count>0&&urls.Length>1)throw new ArgumentException("Use at most 16 mirrors for one download.");foreach(var mirror in mirrors)ValidateUrl(mirror);
   var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var line in HeadersBox.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){var n=line.IndexOf(':');if(n<=0)throw new ArgumentException("Each custom header needs Name: Value.");headers.Add(line[..n].Trim(),line[(n+1)..].Trim());}
   var action=(CompletionAction)ActionBox.SelectedItem;if(action==CompletionAction.RunScript&&(!Path.IsPathFullyQualified(ScriptBox.Text.Trim())||!File.Exists(ScriptBox.Text.Trim())))throw new ArgumentException("Choose an existing trusted script/executable for the completion action.");
   Requests=urls.Select(url=>new DownloadRequest{Url=url,DestinationDirectory=folder,FileName=name.Length==0?null:name,Connections=(int)ConnectionsBox.SelectedItem,Priority=(DownloadPriority)PriorityBox.SelectedItem,Kind=(DownloadKind)KindBox.SelectedItem,Category=CategoryBox.SelectedIndex==0?null:CategoryBox.SelectedItem.ToString(),ScheduledAt=start,StopAt=stop,SpeedLimitBytesPerSecond=cap,Mirrors=mirrors.Count==0?null:new List<string>(mirrors),Referrer=string.IsNullOrWhiteSpace(ReferrerBox.Text)?null:ReferrerBox.Text.Trim(),UserAgent=string.IsNullOrWhiteSpace(UserAgentBox.Text)?null:UserAgentBox.Text.Trim(),Cookies=string.IsNullOrWhiteSpace(CookiesBox.Text)?null:CookiesBox.Text.Trim(),Headers=headers.Count==0?null:new Dictionary<string,string>(headers,StringComparer.OrdinalIgnoreCase),ScanOnCompletion=ScanCheck.IsChecked==true,CompletionAction=action,CompletionScript=string.IsNullOrWhiteSpace(ScriptBox.Text)?null:ScriptBox.Text.Trim()}).ToList();DialogResult=true;
  }catch(Exception ex)when(ex is ArgumentException or FormatException or IOException or NotSupportedException or OverflowException){ErrorText.Text=ex.Message;}
 }
}
