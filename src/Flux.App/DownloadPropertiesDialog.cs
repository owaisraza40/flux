using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Flux.Core;
namespace Flux.App;
public sealed class DownloadPropertiesDialog:Window
{
 public DownloadRequest? Request{get;private set;}
 public DownloadPropertiesDialog(DownloadItem item,DownloadSettings settings)
 {
  SetResourceReference(StyleProperty,typeof(Window));Title="Properties — "+item.FileName;Width=650;Height=660;MinHeight=550;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  var panel=new StackPanel{Margin=new Thickness(24)};var scroll=new ScrollViewer{Content=panel};Content=scroll;
  panel.Children.Add(new TextBlock{Text="DOWNLOAD PROPERTIES",FontSize=22,FontWeight=FontWeights.Bold,Margin=new Thickness(0,0,0,15)});
  var editable=item.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued or DownloadStatus.Scheduled or DownloadStatus.Cancelled;
  TextBox Field(string label,string value,bool allow=true){panel.Children.Add(new TextBlock{Text=label,Style=(Style)FindResource("FieldLabel")});var box=new TextBox{Text=value,IsReadOnly=!editable||!allow};panel.Children.Add(box);return box;}
  Field("SOURCE URL",item.Url,false);var filename=Field("FILE NAME",item.FileName);var folder=Field("SAVE TO",item.DestinationDirectory);
  panel.Children.Add(new TextBlock{Text=$"{item.Status} / {Display.Bytes(item.DownloadedBytes)} of {Display.Bytes(item.TotalBytes)}\nAdded {item.CreatedAt.ToLocalTime():f}\n{item.Connections} connections / {item.MediaQuality}",Foreground=Display.Brush("#9A96AB"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,8)});
  var cap=Field("SPEED LIMIT (KB/s, 0 = DEFAULT)",((item.SpeedLimitBytesPerSecond??0)/1024d).ToString("0.##",CultureInfo.CurrentCulture));
  panel.Children.Add(new TextBlock{Text="PRIORITY",Style=(Style)FindResource("FieldLabel")});var priority=new ComboBox{ItemsSource=Enum.GetValues<DownloadPriority>(),SelectedItem=item.Priority,IsEnabled=editable};panel.Children.Add(priority);
  if(!string.IsNullOrWhiteSpace(item.Error))panel.Children.Add(new TextBlock{Text=item.Error,Foreground=Display.Brush("#FF697C"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
  panel.Children.Add(new TextBlock{Text=editable?"Changes are saved with this transfer paused. Resume it from the list when ready.":"Pause an active transfer before editing its properties.",Foreground=Display.Brush("#6B6880"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,15,0,0)});
  var error=new TextBlock{Foreground=Display.Brush("#FF697C"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,10)};panel.Children.Add(error);
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var close=new Button{Content="Close",IsCancel=true,Margin=new Thickness(0,0,10,0)};buttons.Children.Add(close);var save=new Button{Content="Save properties",Style=(Style)FindResource("PrimaryButton"),IsEnabled=editable};buttons.Children.Add(save);panel.Children.Add(buttons);
  save.Click+=(_,_)=>{try{if(!Path.IsPathFullyQualified(folder.Text.Trim()))throw new ArgumentException("Choose a full destination path.");if(!double.TryParse(cap.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out var speed)||!double.IsFinite(speed)||speed<0||speed>1_000_000_000)throw new ArgumentException("Enter a valid speed limit.");Request=new DownloadRequest{Url=item.Url,FileName=DownloadNames.Sanitize(filename.Text),DestinationDirectory=folder.Text.Trim(),Connections=item.Connections,Priority=(DownloadPriority)priority.SelectedItem,SpeedLimitBytesPerSecond=speed==0?null:checked((long)(speed*1024)),ScheduledAt=item.ScheduledAt,StopAt=item.StopAt,Kind=item.Kind,MediaQuality=item.MediaQuality,UserAgent=item.UserAgent,Referrer=item.Referrer,Cookies=item.Cookies,Category=item.Category,Mirrors=new(item.Mirrors),CompletionAction=item.CompletionAction,CompletionScript=item.CompletionScript,ScanOnCompletion=item.ScanOnCompletion};DialogResult=true;}catch(Exception ex){error.Text=ex.Message;}};
 }
}
