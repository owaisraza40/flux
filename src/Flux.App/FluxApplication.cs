using System.IO;
using System.Windows;
using Flux.Core;
using Flux.App.Integration;
using Flux.App.Services;
namespace Flux.App;
public sealed class FluxApplication : Application
{
 private readonly string[] _initialUrls;
 private readonly bool _testData;
 private readonly string? _screenshotPath;
 private readonly bool _exitAfterScreenshot;
 private readonly bool _startInBackground;
 private readonly CancellationTokenSource _serverCancellation=new();
 private Task? _server,_websocket;
 private DownloadManager? _manager;
 private CompletionService? _completion;
 private bool _stopped;
 public int DiagnosticExitCode { get; private set; }
 public FluxApplication(string[] initialUrls,bool testData,string? screenshotPath=null,bool exitAfterScreenshot=false,bool startInBackground=false)
 {
  if(screenshotPath!=null&&!testData)throw new ArgumentException("Screenshot export is available only in UI test mode.");
  _initialUrls=initialUrls;_testData=testData;_screenshotPath=screenshotPath;_exitAfterScreenshot=exitAfterScreenshot;_startInBackground=startInBackground;
  if(screenshotPath!=null)System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
  ShutdownMode=ShutdownMode.OnExplicitShutdown;
  Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("Theme.xaml",UriKind.Relative)});
  DispatcherUnhandledException+=(_,e)=>{e.Handled=true;if(MainWindow is MainWindow window)window.ShowStatus("Something went wrong: "+e.Exception.Message,true);else MessageBox.Show(e.Exception.Message,"Flux",MessageBoxButton.OK,MessageBoxImage.Error);};
 }
 private void Diagnostic(string message)
 {
  if(!_testData)return;
  try{var directory=Path.Combine(Path.GetTempPath(),"Flux-ui-preview");Directory.CreateDirectory(directory);File.AppendAllText(Path.Combine(directory,"startup-diagnostic.log"),DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine);}catch{}
 }
 protected override async void OnStartup(StartupEventArgs e)
 {
  base.OnStartup(e);Diagnostic("OnStartup entered");
  try
  {
   var directory=_testData?Path.Combine(Path.GetTempPath(),"Flux-ui-preview"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Flux");
   _manager=new DownloadManager(directory){ExternalProcessor=new MediaDownloadProcessor(directory)};
   await _manager.InitializeAsync();
   if(_testData){var isolated=_manager.Settings;isolated.DefaultDownloadDirectory=Path.Combine(directory,"downloads");isolated.AutoSortCategories=false;await _manager.SaveSettingsAsync(isolated);}
   _completion=new CompletionService(_manager);
   var window=new MainWindow(_manager,_testData);MainWindow=window;
   _manager.DownloadCompleted+=(_,item)=>Dispatcher.BeginInvoke(async()=>{try{await _completion.HandleAsync(item);window.NotifyCompleted(item);}catch(Exception ex){window.ShowStatus("Completion action failed: "+ex.Message,true,30);}});
   if(!_startInBackground&&_initialUrls.Length==0||_screenshotPath!=null)window.Show();
   else{window.ShowActivated=false;window.Show();window.Hide();window.ShowActivated=true;}
   Diagnostic("Main window initialized; visible="+window.IsVisible+"; background="+_startInBackground);
   WebSocketBridge.RecentDownloadsProvider=()=>_manager.GetSnapshot().OrderByDescending(item=>item.CreatedAt).Take(10).Select(item=>(object)new{fileName=item.FileName,status=item.Status.ToString(),progress=item.Progress}).ToArray();
   WebSocketBridge.PageLinksReceived=urls=>Dispatcher.BeginInvoke(()=>window.AddPageLinks(urls));
   WebSocketBridge.CaptureSettingsProvider=()=>new
   {
    extensions=_manager.Settings.CaptureFileTypes.Split([',',';',' ','\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries),
    allowDomains=_manager.Settings.DomainWhitelist.Split(['\r','\n',','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries),
    blockDomains=_manager.Settings.DomainBlacklist.Split(['\r','\n',','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
   };
   _server=NativeBridge.RunDownloadServerAsync(downloads=>Dispatcher.BeginInvoke(()=>{if(downloads.Length>0)_=window.AddBrowserDownloadsAsync(downloads);else window.ActivateFromOutside();}),_serverCancellation.Token);
   _websocket=WebSocketBridge.RunAsync(download=>Dispatcher.BeginInvoke(()=>_=window.AddBrowserDownloadsAsync([download])),_serverCancellation.Token);
   window.SetBridgeStatus("WebSocket: localhost:6543 / paired extensions only");
   _=ObserveServerAsync(_server,window,false);_=ObserveServerAsync(_websocket,window,true);
   if(_screenshotPath!=null){Diagnostic("Export queued: "+_screenshotPath);_=CaptureAndMaybeExitAsync(window);return;}
   if(!_testData)
   {
    try{var firstLaunch=BrowserIntegration.EnsureRegistered();if(_manager.Settings.StartWithWindows)StartupRegistration.SetEnabled(true);if(firstLaunch&&!_startInBackground&&_initialUrls.Length==0)new BrowserSetupDialog{Owner=window}.ShowDialog();}
    catch(Exception ex){window.ShowStatus("Browser setup: "+ex.Message,true,60);}
   }
   if(_initialUrls.Length>0)await window.AddIncomingUrlsAsync(_initialUrls);
   if(!_testData)
   {
    if(_manager.LastPersistenceError!=null)window.ShowStatus(_manager.LastPersistenceError,true,60);
    var recoverable=_manager.GetSnapshot().Where(item=>item.Status==DownloadStatus.Paused&&item.DownloadedBytes>0).ToArray();
    if(!_startInBackground&&recoverable.Length>0&&MessageBox.Show(window,$"Flux found {recoverable.Length} unfinished download(s). Resume them?","Resume transfers",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
     foreach(var item in recoverable)await _manager.ResumeAsync(item.Id);
    if(_manager.Settings.CheckForUpdates)_=CheckUpdateAsync(window);
   }
  }
  catch(Exception ex){Diagnostic("Startup error: "+ex);MessageBox.Show("Unable to open Flux: "+ex.Message,"Flux",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
 }
 private async Task CheckUpdateAsync(MainWindow window)
 {
  try{var update=await UpdateService.CheckAsync(_serverCancellation.Token);if(update!=null)window.ShowStatus("Flux "+update.Version+" is available. Open Options to download.",false,90);}
  catch(OperationCanceledException){}catch(Exception ex){window.ShowStatus("Update check unavailable: "+ex.Message,false,12);}
 }
 private async Task ObserveServerAsync(Task server,MainWindow window,bool websocket)
 {
  try{await server;}catch(OperationCanceledException){}catch(Exception ex){window.ShowStatus("Browser bridge unavailable: "+ex.Message,true,30);if(websocket)window.SetBridgeStatus("WebSocket unavailable: "+ex.Message,true);}
 }
 private async Task CaptureAndMaybeExitAsync(MainWindow window)
 {
  try
  {
   await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
   await UiScreenshotExporter.CaptureAsync(window,_screenshotPath!);
   var folder=Path.GetDirectoryName(_screenshotPath!)!;
   var stem=Path.GetFileNameWithoutExtension(_screenshotPath);
   var add=new AddDownloadDialog(_manager!.Settings,new BrowserDownload{Url=""},Path.Combine(Path.GetTempPath(),"Flux-ui-preview"));
   try{await UiScreenshotExporter.CaptureAsync(add,Path.Combine(folder,stem+"-add-popup.png"));}finally{add.Close();}
   var tabs=new[]{"general","connections","browser","media","scheduler","notifications"};
   for(var i=0;i<tabs.Length;i++)
   {
    var options=new SettingsDialog(_manager.Settings,i);
    try{await UiScreenshotExporter.CaptureAsync(options,Path.Combine(folder,stem+"-options-"+tabs[i]+".png"));}finally{options.Close();}
   }
   Diagnostic("Main window, separate add popup and six options tab screenshots saved");
   window.ShowStatus("UI screenshots saved: "+folder,false,120);
  }
  catch(Exception ex){DiagnosticExitCode=1;Diagnostic("Screenshot error: "+ex);try{Directory.CreateDirectory(Path.GetDirectoryName(_screenshotPath!)!);File.WriteAllText(_screenshotPath+".error.txt",ex.ToString());}catch{}window.ShowStatus("Screenshot failed: "+ex.Message,true,120);}
  finally{if(_exitAfterScreenshot)window.RequestExit();}
 }
 public async Task StopAsync()
 {
  if(_stopped)return;_stopped=true;Diagnostic("Stop entered");_serverCancellation.Cancel();
  if(_manager!=null)await _manager.DisposeAsync();
  foreach(var task in new[]{_server,_websocket})if(task!=null)try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch{}
  WebSocketBridge.RecentDownloadsProvider=null;WebSocketBridge.CaptureSettingsProvider=null;WebSocketBridge.PageLinksReceived=null;Diagnostic("Stop completed");
 }
}
