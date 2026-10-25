using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Net.Http;
using Flux.App.Services;
using Flux.App.Integration;
using Flux.Core;
namespace Flux.App;
public partial class AddDownloadDialog : Window
{
 public List<DownloadRequest> Requests{get;private set;}=[];
 private readonly DownloadSettings _settings;private readonly string _dataDirectory;private CancellationTokenSource? _metadataCancellation;private MediaMetadata? _metadata;private string? _metadataUrl;private bool _starting;private bool _initialized;
 public AddDownloadDialog(DownloadSettings settings,BrowserDownload? captured=null,string? dataDirectory=null)
 {
  _settings=settings.Clone();_dataDirectory=dataDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Flux");SetResourceReference(StyleProperty,typeof(Window));InitializeComponent();FolderBox.Text=settings.DefaultDownloadDirectory;
  ConnectionsBox.ItemsSource=Enumerable.Range(1,32).ToArray();ConnectionsBox.SelectedItem=settings.ConnectionsPerDownload;
  if(ConnectionsBox.SelectedItem is null)ConnectionsBox.SelectedItem=8;
  PriorityBox.ItemsSource=Enum.GetValues<DownloadPriority>();PriorityBox.SelectedItem=DownloadPriority.Normal;
  KindBox.ItemsSource=Enum.GetValues<DownloadKind>();KindBox.SelectedItem=captured?.IsMedia==true?DownloadKind.Media:DownloadKind.Auto;
  ActionBox.ItemsSource=Enum.GetValues<CompletionAction>();ActionBox.SelectedItem=CompletionAction.None;
  CategoryBox.ItemsSource=new[]{"Automatic","Video","Music","Compressed","Documents","Programs","Other"};CategoryBox.SelectedIndex=0;
  SpeedUnitBox.ItemsSource=new[]{"KB/s","MB/s"};SpeedUnitBox.SelectedIndex=0;
  StartTimeBox.Text=DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);StopTimeBox.Text=DateTime.Now.AddHours(4).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
  if(captured is not null){UrlsBox.Text=captured.Url;FileNameBox.Text=captured.FileName??"";ReferrerBox.Text=captured.Referrer??"";UserAgentBox.Text=captured.UserAgent??"";CookiesBox.Text=captured.Cookies??"";MediaTitleText.Text=captured.PageTitle??"Video information";}
  _initialized=true;Closed+=(_,_)=>{_metadataCancellation?.Cancel();};Loaded+=async(_,_)=>{UrlsBox.Focus();if(captured is null)TryPasteUrl();await ResolveDetailsAsync();};
  PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control){Start_Click(this,new RoutedEventArgs());e.Handled=true;}};
 }
 private static bool IsVideoUrl(string text){if(!Uri.TryCreate(text,UriKind.Absolute,out var uri))return false;var host=uri.Host;var sites=new[]{"youtube.com","youtu.be","vimeo.com","dailymotion.com","facebook.com","fb.watch","twitter.com","x.com","instagram.com"};return sites.Any(site=>host.Equals(site,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+site,StringComparison.OrdinalIgnoreCase))||new[]{".mp4",".mkv",".webm",".mov",".m3u8",".mpd"}.Contains(Path.GetExtension(uri.AbsolutePath),StringComparer.OrdinalIgnoreCase);}
 private async void Urls_LostFocus(object sender,RoutedEventArgs e)=>await ResolveDetailsAsync();
 private async void ResolveMedia_Click(object sender,RoutedEventArgs e){_metadataUrl=null;await ResolveDetailsAsync();}
 private async void Kind_Changed(object sender,SelectionChangedEventArgs e){if(_initialized)await ResolveDetailsAsync();}
 private void Quality_Changed(object sender,SelectionChangedEventArgs e){if(_metadata==null)return;var ext=QualityBox.SelectedValue is MediaQuality.AudioMp3?".mp3":".mp4";var name=FileNameBox.Text.Trim();if(string.IsNullOrEmpty(name)||name.Equals("watch",StringComparison.OrdinalIgnoreCase))name=_metadata.SuggestedFileName;FileNameBox.Text=DownloadNames.Sanitize(Path.GetFileNameWithoutExtension(name)+ext);}
 private Dictionary<string,string> RequestHeaders(){var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);if(!string.IsNullOrWhiteSpace(UserAgentBox.Text))headers["User-Agent"]=UserAgentBox.Text.Trim();if(!string.IsNullOrWhiteSpace(ReferrerBox.Text))headers["Referer"]=ReferrerBox.Text.Trim();if(!string.IsNullOrWhiteSpace(CookiesBox.Text))headers["Cookie"]=CookiesBox.Text.Trim();return headers;}
 private async Task ResolveDetailsAsync()
 {
  if(!_initialized||_starting)return;var url=UrlsBox.Text.Trim();if(url.Contains('\n')||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("https" or "http"))return;
  var media=KindBox.SelectedItem is DownloadKind.Media||KindBox.SelectedItem is DownloadKind.Auto&&IsVideoUrl(url);MediaPanel.Visibility=media?Visibility.Visible:Visibility.Collapsed;
  if(_metadataUrl==url&&(!media||_metadata!=null))return;_metadataCancellation?.Cancel();_metadataCancellation?.Dispose();var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(3));_metadataCancellation=cancellation;_metadata=null;_metadataUrl=url;var initialName=FileNameBox.Text.Trim();
  try{
   if(media){MediaTitleText.Text="Loading video information…";MediaStatusText.Text="Checking available formats and JavaScript challenge support…";QualityBox.ItemsSource=null;StartButton.IsEnabled=false;var metadata=await new MediaMetadataService(_dataDirectory).GetAsync(url,_settings,RequestHeaders(),cancellation.Token);if(cancellation.IsCancellationRequested||UrlsBox.Text.Trim()!=url)return;_metadata=metadata;MediaTitleText.Text=metadata.Title;MediaStatusText.Text=metadata.TotalBytes>0?Display.Bytes(metadata.TotalBytes):"Choose a format to download";QualityBox.ItemsSource=metadata.Qualities;QualityBox.SelectedValue=metadata.Qualities.Any(q=>q.Quality==_settings.DefaultMediaQuality)?_settings.DefaultMediaQuality:MediaQuality.Best;if(string.IsNullOrWhiteSpace(initialName)||initialName.Equals("watch",StringComparison.OrdinalIgnoreCase)||initialName==DownloadNames.FromUrl(uri))FileNameBox.Text=metadata.SuggestedFileName;Quality_Changed(QualityBox,new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));await LoadThumbnailAsync(metadata.ThumbnailUrl,cancellation.Token);}
   else{using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};using var request=new HttpRequestMessage(HttpMethod.Head,uri);foreach(var header in RequestHeaders())request.Headers.TryAddWithoutValidation(header.Key,header.Value);using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation.Token);FileInfoText.Text=response.Content.Headers.ContentLength is long size?"Size: "+Display.Bytes(size):"Size will be determined when the download starts.";var disposition=response.Content.Headers.ContentDisposition;var name=disposition?.FileNameStar??disposition?.FileName?.Trim('"');if(!string.IsNullOrWhiteSpace(name)&&FileNameBox.Text.Trim()==initialName&&string.IsNullOrWhiteSpace(initialName))FileNameBox.Text=DownloadNames.Sanitize(name);}
  }catch(OperationCanceledException){if(!cancellation.IsCancellationRequested||ReferenceEquals(_metadataCancellation,cancellation))MediaStatusText.Text="Video lookup timed out or was cancelled. Refresh to retry.";}
  catch(Exception ex){if(media){MediaTitleText.Text="Video information unavailable";MediaStatusText.Text=ex.Message;ErrorText.Text="Could not load video formats. Refresh after correcting media settings or network access.";}else FileInfoText.Text="Size and server filename will be resolved during download.";}
  finally{if(ReferenceEquals(_metadataCancellation,cancellation))StartButton.IsEnabled=!media||_metadata!=null;}
 }
 private async Task LoadThumbnailAsync(string? url,CancellationToken token){try{if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https"))return;using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(8)};using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);if(!response.IsSuccessStatusCode||response.Content.Headers.ContentLength>4_000_000)return;await using var source=await response.Content.ReadAsStreamAsync(token);using var memory=new MemoryStream();var buffer=new byte[8192];int read;while((read=await source.ReadAsync(buffer,token))>0){if(memory.Length+read>4_000_000)return;memory.Write(buffer,0,read);}memory.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=224;bitmap.StreamSource=memory;bitmap.EndInit();bitmap.Freeze();ThumbnailImage.Source=bitmap;}catch{}}
 private void TryPasteUrl(){try{if(UrlsBox.Text.Length>0||!Clipboard.ContainsText())return;var t=Clipboard.GetText().Trim();if(t.Length<=65536&&t.Split('\n',StringSplitOptions.RemoveEmptyEntries).All(l=>Uri.TryCreate(l.Trim(),UriKind.Absolute,out var u)&&u.Scheme is "http" or "https")){UrlsBox.Text=t;UrlsBox.SelectAll();}}catch{}}
 private void DragDialog(object sender,MouseButtonEventArgs e){if(e.ChangedButton==MouseButton.Left)try{DragMove();}catch(InvalidOperationException){}}
 private void Browse_Click(object sender,RoutedEventArgs e){using var d=new System.Windows.Forms.FolderBrowserDialog{Description="Choose where Flux saves downloads",UseDescriptionForTitle=true,SelectedPath=FolderBox.Text};if(d.ShowDialog()==System.Windows.Forms.DialogResult.OK)FolderBox.Text=d.SelectedPath;}
 private void ScheduleChanged(object sender,RoutedEventArgs e){if(StartButton!=null)StartButton.Content=ScheduleCheck.IsChecked==true?"SCHEDULE DOWNLOAD":"START DOWNLOAD";}
 private static DateTimeOffset ParseWhen(string text){if(!DateTime.TryParseExact(text.Trim(),"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||TimeZoneInfo.Local.IsInvalidTime(date))throw new ArgumentException("Enter a valid local date/time, such as 2026-10-25 22:30.");return new DateTimeOffset(date);}
 private static void ValidateUrl(string value){if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme is not("http" or "https")||u.UserInfo.Length>0)throw new ArgumentException("Every URL must be a complete HTTP/HTTPS link without embedded credentials.");}
 private async void Start_Click(object sender,RoutedEventArgs e)
 {
  if(_starting)return;
  try{
   await ResolveDetailsAsync();_starting=true;
   var urls=UrlsBox.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();if(urls.Length==0||urls.Length>100)throw new ArgumentException("Provide between 1 and 100 URLs.");foreach(var u in urls)ValidateUrl(u);
   var folder=FolderBox.Text.Trim();if(!Path.IsPathFullyQualified(folder))throw new ArgumentException("Choose a full destination folder path.");
   var media=urls.Length==1&&(KindBox.SelectedItem is DownloadKind.Media||KindBox.SelectedItem is DownloadKind.Auto&&IsVideoUrl(urls[0]));if(media&&(_metadata==null||_metadataUrl!=urls[0]))throw new ArgumentException("Load the available video formats before starting.");var name=FileNameBox.Text.Trim();if(media&&(name.Length==0||name.Equals("watch",StringComparison.OrdinalIgnoreCase)))name=_metadata!.SuggestedFileName;if(urls.Length>1&&name.Length>0)throw new ArgumentException("Leave filename empty for a batch.");if(name.Length>0&&(name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new ArgumentException("Use a filename without directory separators or invalid characters.");
   DateTimeOffset? start=ScheduleCheck.IsChecked==true?ParseWhen(StartTimeBox.Text):null;DateTimeOffset? stop=StopCheck.IsChecked==true?ParseWhen(StopTimeBox.Text):null;
   if(start<=DateTimeOffset.Now)throw new ArgumentException("The start time must be in the future.");if(stop.HasValue&&stop.Value<=(start??DateTimeOffset.Now))throw new ArgumentException("Stop time must be after the start.");
   long? cap=null;if(SpeedCheck.IsChecked==true){if(!decimal.TryParse(SpeedBox.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out var speed)||speed<=0||speed>100000000)throw new ArgumentException("Enter a positive speed limit.");cap=checked((long)(speed*(SpeedUnitBox.SelectedIndex==1?1024*1024:1024)));}
   var mirrors=MirrorsBox.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToList();if(mirrors.Count>16||mirrors.Count>0&&urls.Length>1)throw new ArgumentException("Use at most 16 mirrors for one download.");foreach(var mirror in mirrors)ValidateUrl(mirror);
   var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var line in HeadersBox.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)){var n=line.IndexOf(':');if(n<=0)throw new ArgumentException("Each custom header needs Name: Value.");headers.Add(line[..n].Trim(),line[(n+1)..].Trim());}
   var action=(CompletionAction)ActionBox.SelectedItem;if(action==CompletionAction.RunScript&&(!Path.IsPathFullyQualified(ScriptBox.Text.Trim())||!File.Exists(ScriptBox.Text.Trim())))throw new ArgumentException("Choose an existing trusted script/executable for the completion action.");
   Requests=urls.Select(url=>new DownloadRequest{Url=url,DestinationDirectory=folder,FileName=name.Length==0?null:name,Connections=(int)ConnectionsBox.SelectedItem,Priority=(DownloadPriority)PriorityBox.SelectedItem,Kind=media?DownloadKind.Media:(DownloadKind)KindBox.SelectedItem,MediaQuality=media&&QualityBox.SelectedValue is MediaQuality quality?quality:_settings.DefaultMediaQuality,Category=CategoryBox.SelectedIndex==0?null:CategoryBox.SelectedItem.ToString()=="Compressed"?"Archives":CategoryBox.SelectedItem.ToString(),ScheduledAt=start,StopAt=stop,SpeedLimitBytesPerSecond=cap,Mirrors=mirrors.Count==0?null:new List<string>(mirrors),Referrer=string.IsNullOrWhiteSpace(ReferrerBox.Text)?null:ReferrerBox.Text.Trim(),UserAgent=string.IsNullOrWhiteSpace(UserAgentBox.Text)?null:UserAgentBox.Text.Trim(),Cookies=string.IsNullOrWhiteSpace(CookiesBox.Text)?null:CookiesBox.Text.Trim(),Headers=headers.Count==0?null:new Dictionary<string,string>(headers,StringComparer.OrdinalIgnoreCase),ScanOnCompletion=ScanCheck.IsChecked==true,CompletionAction=action,CompletionScript=string.IsNullOrWhiteSpace(ScriptBox.Text)?null:ScriptBox.Text.Trim()}).ToList();DialogResult=true;
  }catch(Exception ex)when(ex is ArgumentException or FormatException or IOException or NotSupportedException or OverflowException){ErrorText.Text=ex.Message;}
  finally{_starting=false;}
 }
}
