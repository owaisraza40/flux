using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Flux.App.Integration;
using Flux.Core;
namespace Flux.App;
public partial class MainWindow:Window
{
 private readonly DownloadManager _manager;private readonly MainViewModel _view;private readonly DispatcherTimer _timer;private readonly bool _testMode;
 private readonly Queue<BrowserDownload> _browserQueue=[];private bool _processingBrowserQueue;private bool _dialogOpen;private bool _closing;private bool _allowClose;private bool _explicitExit;private DateTime _statusExpires;private DateTime _lastClipboardCheck;private string _lastClipboard="";private Point _dragStart;private DownloadRow? _dragRow;
 private System.Windows.Forms.NotifyIcon? _tray;private HwndSource? _windowSource;private const int HotkeyId=0xF10D;private bool _hotkeyRegistered;
 [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(IntPtr hWnd,int id,uint modifiers,uint key);
 [DllImport("user32.dll",SetLastError=true)]private static extern bool UnregisterHotKey(IntPtr hWnd,int id);
 public MainWindow(DownloadManager manager,bool testMode=false)
 {
  SetResourceReference(StyleProperty,typeof(Window));
  _manager=manager;_testMode=testMode;InitializeComponent();_view=new MainViewModel(()=>OpenAddDialog(),()=>SearchBox.Focus());DataContext=_view;ExtensionIdBox.Text=BrowserIntegration.ChromiumExtensionId;
  _timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};_timer.Tick+=(_,_)=>{RefreshDownloads();CheckClipboard();};_timer.Start();RefreshDownloads();SetNavigation(NavAll,"all");
  if(!testMode)CreateTray();else ShowStatus("UI TEST MODE / ISOLATED DATA",false,3600);
  PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Q&&Keyboard.Modifiers==ModifierKeys.Control){RequestExit();e.Handled=true;}};
 }
 public void SetBridgeStatus(string message,bool error=false){BridgeStatus.Text=message;BridgeStatus.Foreground=Display.Brush(error?"#FF697C":"#9A96AB");}
 private void RefreshDownloads()
 {
  if(_closing)return;_view.Update(_manager.GetSnapshot());EmptyState.Visibility=_view.RowsView.IsEmpty?Visibility.Visible:Visibility.Collapsed;var filtered=_view.Filter!="all"||_view.Search.Length>0;
  EmptyTitle.Text=filtered?"NOTHING HERE.\nYET.":"READY FOR\nWHAT’S NEXT.";EmptySubtitle.Text=filtered?"Try another filter or add a URL to this collection.":"Add a URL. Set your pace. Take control of the transfer.";
  if(_statusExpires<DateTime.UtcNow){StatusText.Text=$"SYSTEM READY / {_view.ActiveCount:00} ACTIVE / {_view.CompletedCount:00} COMPLETE";StatusText.Foreground=Display.Brush("#6B6880");}
 }
 public void ShowStatus(string message,bool error=false,int seconds=8){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>ShowStatus(message,error,seconds));return;}StatusText.Text=message;StatusText.ToolTip=message;StatusText.Foreground=Display.Brush(error?"#FF697C":"#C8A6AF");_statusExpires=DateTime.UtcNow.AddSeconds(seconds);}
 public void ActivateFromOutside(){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(ActivateFromOutside);return;}Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();}
 public void NotifyCompleted(DownloadItem item){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>NotifyCompleted(item));return;}ShowStatus("Completed: "+item.FileName);if(!_testMode&&_manager.Settings.NotificationsEnabled)_tray?.ShowBalloonTip(5000,"Flux / Download complete",item.FileName,System.Windows.Forms.ToolTipIcon.Info);}
 public Task AddIncomingUrlsAsync(string[] urls)=>AddBrowserDownloadsAsync(urls.Select(u=>new BrowserDownload{Url=u}).ToArray());
 public Task AddBrowserDownloadsAsync(BrowserDownload[] downloads)
 {
  ActivateFromOutside();foreach(var item in downloads.Take(100)){if(_browserQueue.Count>=100)break;_browserQueue.Enqueue(item);}ProcessBrowserQueue();return Task.CompletedTask;
 }
 private void ProcessBrowserQueue(){if(_processingBrowserQueue||_dialogOpen||_closing||_browserQueue.Count==0)return;_processingBrowserQueue=true;try{OpenAddDialog(_browserQueue.Dequeue());}finally{_processingBrowserQueue=false;if(_browserQueue.Count>0&&!_dialogOpen)Dispatcher.BeginInvoke(ProcessBrowserQueue);}}
 private async void OpenAddDialog(BrowserDownload? captured=null)
 {
  if(_closing||_dialogOpen){if(captured!=null&&_browserQueue.Count<100)_browserQueue.Enqueue(captured);return;}ActivateFromOutside();_dialogOpen=true;
  try{var dialog=new AddDownloadDialog(_manager.Settings,captured){Owner=this};if(dialog.ShowDialog()!=true)return;var count=0;foreach(var request in dialog.Requests){try{await _manager.AddAsync(request);count++;}catch(Exception ex){MessageBox.Show(this,"Could not add "+request.Url+"\n\n"+ex.Message,"Flux",MessageBoxButton.OK,MessageBoxImage.Warning);}}ShowStatus($"Added {count} transfer(s).");RefreshDownloads();}
  finally{_dialogOpen=false;if(!_processingBrowserQueue&&_browserQueue.Count>0)_ = Dispatcher.BeginInvoke(ProcessBrowserQueue);}
 }
 private void Add_Click(object sender,RoutedEventArgs e)=>OpenAddDialog();
 private void Search_TextChanged(object sender,TextChangedEventArgs e){if(_view==null)return;_view.Search=SearchBox.Text.Trim();RefreshDownloads();}
 private void Nav_Click(object sender,RoutedEventArgs e){if(sender is Button button&&button.Tag is string tag)SetNavigation(button,tag);}
 public void ShowBrowserPage()=>SetNavigation(NavBrowser,"browser");
 private void SetNavigation(Button button,string tag)
 {
  foreach(var nav in FindVisualChildren<Button>(this).Where(b=>b.Style==TryFindResource("NavButton")&&b.Tag is string)){nav.Background=Brushes.Transparent;nav.Foreground=Display.Brush("#9A96AB");nav.BorderThickness=new Thickness(0);}
  button.Background=Display.Brush("#251019");button.Foreground=Display.Brush("#F0EEF5");button.BorderBrush=Display.Brush("#C8102E");button.BorderThickness=new Thickness(2,0,0,0);
  BrowserPage.Visibility=tag=="browser"?Visibility.Visible:Visibility.Collapsed;DownloadPage.Visibility=tag=="browser"?Visibility.Collapsed:Visibility.Visible;SearchBox.Visibility=tag=="browser"?Visibility.Hidden:Visibility.Visible;_view.Filter=tag;ReorderHint.Visibility=tag=="queued"?Visibility.Visible:Visibility.Collapsed;
  PageTitle.Text=tag switch{"active"=>"Downloading","queued"=>"Queued","completed"=>"Completed","failed"=>"Failed","scheduled"=>"Scheduled","video"=>"Video","music"=>"Music","documents"=>"Documents","programs"=>"Programs","other"=>"Other",_=>"All downloads"};RefreshDownloads();
 }
 private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)where T:DependencyObject{for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);if(c is T value)yield return value;foreach(var descendant in FindVisualChildren<T>(c))yield return descendant;}}
 private async Task RunActionAsync(Func<Task> action){try{await action();RefreshDownloads();}catch(Exception ex){ShowStatus(ex.Message,true,20);MessageBox.Show(this,ex.Message,"Flux",MessageBoxButton.OK,MessageBoxImage.Warning);}}
 private async Task ActOnRowAsync(DownloadRow row){await RunActionAsync(async()=>{switch(row.Item.Status){case DownloadStatus.Downloading:case DownloadStatus.Resolving:case DownloadStatus.Queued:case DownloadStatus.Scheduled:await _manager.PauseAsync(row.Id);break;case DownloadStatus.Completed:OpenPath(row.Item.FilePath);break;case DownloadStatus.Failed:await _manager.RetryAsync(row.Id);break;default:await _manager.ResumeAsync(row.Id);break;}});}
 private async void RowAction_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.Tag is DownloadRow row)await ActOnRowAsync(row);}
 private async void ContextAction_Click(object sender,RoutedEventArgs e){if((sender as MenuItem)?.Tag is DownloadRow row)await ActOnRowAsync(row);}
 private async void ContextRetry_Click(object sender,RoutedEventArgs e){if((sender as MenuItem)?.Tag is DownloadRow row)await RunActionAsync(()=>_manager.RetryAsync(row.Id));}
 private async void Priority_Click(object sender,RoutedEventArgs e){if(sender is MenuItem menu&&menu.Tag is DownloadRow row&&Enum.TryParse<DownloadPriority>(menu.CommandParameter?.ToString(),out var priority))await RunActionAsync(()=>_manager.SetPriorityAsync(row.Id,priority));}
 private void OpenFolder(DownloadRow row){try{if(File.Exists(row.Item.FilePath))Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+row.Item.FilePath+"\""){UseShellExecute=true});else OpenPath(row.Item.DestinationDirectory);}catch(Exception ex){ShowStatus("Cannot open folder: "+ex.Message,true);}}
 private void OpenFolder_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.Tag is DownloadRow row)OpenFolder(row);}
 private void ContextFolder_Click(object sender,RoutedEventArgs e){if((sender as MenuItem)?.Tag is DownloadRow row)OpenFolder(row);}
 private async Task RemoveRowAsync(DownloadRow row){if(row.Item.Status!=DownloadStatus.Completed&&MessageBox.Show(this,"Remove “"+row.FileName+"” and its unfinished data?\nCompleted files are kept.","Remove transfer",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;await RunActionAsync(()=>_manager.RemoveAsync(row.Id,true));}
 private async void Remove_Click(object sender,RoutedEventArgs e){if((sender as FrameworkElement)?.Tag is DownloadRow row)await RemoveRowAsync(row);}
 private async void ContextRemove_Click(object sender,RoutedEventArgs e){if((sender as MenuItem)?.Tag is DownloadRow row)await RemoveRowAsync(row);}
 private async void PauseAll_Click(object sender,RoutedEventArgs e){await RunActionAsync(_manager.PauseAllAsync);ShowStatus("All transfers paused.");}
 private async void ResumeAll_Click(object sender,RoutedEventArgs e){await RunActionAsync(_manager.ResumeAllAsync);ShowStatus("Queue resumed.");}
 private async void ClearCompleted_Click(object sender,RoutedEventArgs e){await RunActionAsync(_manager.ClearCompletedAsync);ShowStatus("Completed history cleared. Files are kept.");}
 private async void Settings_Click(object sender,RoutedEventArgs e){_dialogOpen=true;try{var dialog=new SettingsDialog(_manager.Settings){Owner=this};if(dialog.ShowDialog()==true){await RunActionAsync(()=>_manager.SaveSettingsAsync(dialog.Settings));ShowStatus("Preferences saved.");}}finally{_dialogOpen=false;if(_browserQueue.Count>0)_ = Dispatcher.BeginInvoke(ProcessBrowserQueue);}}
 private void RegisterBrowser_Click(object sender,RoutedEventArgs e){try{var id=ExtensionIdBox.Text.Trim();var browser=(sender as Button)?.Tag?.ToString();var path=browser switch{"chrome"=>BrowserIntegration.RegisterChrome(id),"edge"=>BrowserIntegration.RegisterEdge(id),_=>BrowserIntegration.RegisterFirefox(BrowserIntegration.FirefoxExtensionId)};BrowserFeedback.Text="Host registered. Pair from the extension.\n"+path;}catch(Exception ex){BrowserFeedback.Text=ex.Message;ShowStatus(ex.Message,true);}}
 private void UnregisterBrowsers_Click(object sender,RoutedEventArgs e){try{BrowserIntegration.UnregisterAll();BrowserFeedback.Text="Browser registrations removed.";}catch(Exception ex){BrowserFeedback.Text=ex.Message;}}
 private void ExtensionFolder_Click(object sender,RoutedEventArgs e){try{OpenPath(Path.Combine(AppContext.BaseDirectory,"browser-extension","chromium"));}catch(Exception ex){ShowStatus(ex.Message,true);}}
 private void DownloadsList_MouseDoubleClick(object sender,MouseButtonEventArgs e){if(DownloadsList.SelectedItem is DownloadRow row&&row.Item.Status==DownloadStatus.Completed)try{OpenPath(row.Item.FilePath);}catch(Exception ex){ShowStatus(ex.Message,true);}}
 private static void OpenPath(string path){if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("The file or folder is no longer available.",path);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
 private void DownloadsList_PreviewMouseLeftButtonDown(object sender,MouseButtonEventArgs e){_dragStart=e.GetPosition(DownloadsList);_dragRow=(ItemsControl.ContainerFromElement(DownloadsList,e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as DownloadRow;}
 private void DownloadsList_PreviewMouseMove(object sender,MouseEventArgs e){if(_view.Filter!="queued"||_dragRow==null||e.LeftButton!=MouseButtonState.Pressed)return;var point=e.GetPosition(DownloadsList);if(Math.Abs(point.X-_dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-_dragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;var row=_dragRow;_dragRow=null;DragDrop.DoDragDrop(DownloadsList,row,DragDropEffects.Move);}
 private async void DownloadsList_Drop(object sender,DragEventArgs e){if(_view.Filter!="queued"||!e.Data.GetDataPresent(typeof(DownloadRow)))return;var source=e.Data.GetData(typeof(DownloadRow)) as DownloadRow;var target=(ItemsControl.ContainerFromElement(DownloadsList,e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as DownloadRow;if(source==null||target==null||source.Id==target.Id)return;var snapshot=_manager.GetSnapshot().ToList();var index=snapshot.FindIndex(i=>i.Id==target.Id);if(index>=0)await RunActionAsync(()=>_manager.MoveAsync(source.Id,index));e.Handled=true;}
 private void CreateTray()
 {
  var menu=new System.Windows.Forms.ContextMenuStrip{BackColor=System.Drawing.Color.FromArgb(17,17,22),ForeColor=System.Drawing.Color.FromArgb(240,238,245),ShowImageMargin=false};
  void Add(string text,Action action){var item=menu.Items.Add(text);item.Click+=(_,_)=>Dispatcher.BeginInvoke(action);}
  Add("Open Flux",ActivateFromOutside);Add("Add URL  Ctrl+Alt+D",()=>OpenAddDialog());menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());Add("Pause all",()=>PauseAll_Click(this,new RoutedEventArgs()));Add("Resume all",()=>ResumeAll_Click(this,new RoutedEventArgs()));menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());Add("Exit Flux",RequestExit);
  var icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??System.Drawing.SystemIcons.Application;
  _tray=new System.Windows.Forms.NotifyIcon{Icon=icon,Text="Flux / Transfer control",ContextMenuStrip=menu,Visible=true};_tray.DoubleClick+=(_,_)=>Dispatcher.BeginInvoke(ActivateFromOutside);
 }
 public void RequestExit(){_explicitExit=true;Close();}
 private void Window_StateChanged(object? sender,EventArgs e){if(!_testMode&&WindowState==WindowState.Minimized&&_manager.Settings.MinimizeToTray)Hide();}
 private void Window_SourceInitialized(object? sender,EventArgs e){if(_testMode)return;_windowSource=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);_windowSource?.AddHook(HotkeyHook);_hotkeyRegistered=RegisterHotKey(new WindowInteropHelper(this).Handle,HotkeyId,0x0001|0x0002|0x4000,0x44);if(!_hotkeyRegistered)ShowStatus("Ctrl+Alt+D is already in use by another application.",true,15);}
 private IntPtr HotkeyHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){if(message==0x0312&&wParam.ToInt32()==HotkeyId){Dispatcher.BeginInvoke(()=>OpenAddDialog());handled=true;}return IntPtr.Zero;}
 private void CheckClipboard(){if(_testMode||_closing||_dialogOpen||!_manager.Settings.ClipboardMonitorEnabled||DateTime.UtcNow-_lastClipboardCheck<TimeSpan.FromSeconds(2))return;_lastClipboardCheck=DateTime.UtcNow;try{if(!Clipboard.ContainsText())return;var text=Clipboard.GetText().Trim();if(text==_lastClipboard||text.Length>8192)return;_lastClipboard=text;if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https"))return;var ext=Path.GetExtension(uri.AbsolutePath);if(!new[]{".zip",".7z",".rar",".exe",".msi",".iso",".pdf",".epub",".mp4",".mkv",".webm",".mp3",".flac",".m3u8",".mpd"}.Contains(ext,StringComparer.OrdinalIgnoreCase))return;OpenAddDialog(new BrowserDownload{Url=text,IsMedia=ext is ".m3u8" or ".mpd"});}catch{}}
 private void TitleBar_MouseLeftButtonDown(object sender,MouseButtonEventArgs e){if(e.ChangedButton!=MouseButton.Left)return;if(e.ClickCount==2)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;else try{DragMove();}catch(InvalidOperationException){}}
 private void Minimize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;private void Maximize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;private void Close_Click(object sender,RoutedEventArgs e)=>Close();
 private async void Window_Closing(object? sender,CancelEventArgs e)
 {
  if(_allowClose)return;e.Cancel=true;if(!_explicitExit&&!_testMode&&_manager.Settings.MinimizeToTray){Hide();ShowStatus("Flux is running in the system tray.");return;}if(_closing)return;_closing=true;_timer.Stop();IsEnabled=false;StatusText.Text="SAVING TRANSFER STATE…";
  if(_hotkeyRegistered)UnregisterHotKey(new WindowInteropHelper(this).Handle,HotkeyId);_windowSource?.RemoveHook(HotkeyHook);_tray?.Dispose();
  try{if(Application.Current is FluxApplication app)await app.StopAsync();}catch(Exception ex){MessageBox.Show(this,"Some state could not be saved: "+ex.Message,"Flux",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{_allowClose=true;Close();Application.Current.Shutdown((Application.Current as FluxApplication)?.DiagnosticExitCode??0);}
 }
}
