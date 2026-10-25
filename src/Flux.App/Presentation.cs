using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Flux.Core;
namespace Flux.App;
internal sealed class UiCommand(Action execute) : ICommand { public event EventHandler? CanExecuteChanged { add { } remove { } } public bool CanExecute(object? parameter)=>true; public void Execute(object? parameter)=>execute(); }
internal static class Display
{
 public static string Bytes(long value) { if(value<0)return "—"; string[] units=["B","KB","MB","GB","TB"];double size=value;var u=0;while(size>=1024&&u<4){size/=1024;u++;}return size.ToString(u==0?"0":size>=100?"0":"0.0",CultureInfo.CurrentCulture)+" "+units[u]; }
 public static string Speed(double value)=>value<=0?"—":Bytes((long)value)+"/s";
 public static Brush Brush(string hex){var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));b.Freeze();return b;}
}
public sealed class DownloadRow : INotifyPropertyChanged
{
 public DownloadRow(DownloadItem item){Item=item;} public DownloadItem Item{get;private set;}public Guid Id=>Item.Id;
 public string FileName=>string.IsNullOrWhiteSpace(Item.FileName)?"Resolving filename…":Item.FileName;
 public string Description=>(Uri.TryCreate(Item.Url,UriKind.Absolute,out var u)?u.Host:Item.Url)+"  /  "+Item.Category;
 public string SourceDomain=>Uri.TryCreate(Item.Url,UriKind.Absolute,out var u)?u.Host:Item.Url;
 public string DateAdded=>Item.CreatedAt.ToLocalTime().ToString("dd MMM yy");
 public string CategoryText=>Item.Category switch{"Archives"=>"Compressed","Audio"=>"Music / Audio","Applications"=>"Programs",_=>Item.Category};
 public string SizeText=>Display.Bytes(Item.TotalBytes);public string SpeedText=>Display.Speed(Item.BytesPerSecond);
 public double Progress=>double.IsFinite(Item.Progress)?Math.Clamp(Item.Progress,0,100):0;
 public string ProgressText=>Progress.ToString("0.#")+"%"; public string TransferText=>Display.Bytes(Item.DownloadedBytes)+" / "+Display.Bytes(Item.TotalBytes);
 public string EtaText {get{var remaining=Item.TotalBytes-Item.DownloadedBytes;if(Item.BytesPerSecond<=0||remaining<=0)return "—";var t=TimeSpan.FromSeconds(Math.Min(remaining/Item.BytesPerSecond,31536000));return t.TotalHours>=1?$"{(int)t.TotalHours}h {t.Minutes}m":t.TotalMinutes>=1?$"{t.Minutes}m {t.Seconds}s":$"{t.Seconds}s";}}
 public string StatusLabel=>Item.Status.ToString().ToUpperInvariant();public string PriorityLabel=>Item.Priority.ToString().ToUpperInvariant();
 public string Error=>Item.Error??Item.ActivityMessage??"";public Visibility ErrorVisibility=>string.IsNullOrWhiteSpace(Error)?Visibility.Collapsed:Visibility.Visible;
 public Brush StatusBrush=>Display.Brush(Item.Status==DownloadStatus.Failed?"#FF697C":Item.Status==DownloadStatus.Completed?"#A8C3B4":Item.Status==DownloadStatus.Downloading?"#F0EEF5":"#8E899F");
 public Brush ActiveBrush=>Display.Brush(Item.Status is DownloadStatus.Downloading or DownloadStatus.Resolving?"#C8102E":"#1E1E26");
 public string PrimaryActionGlyph=>Item.Status switch{DownloadStatus.Downloading or DownloadStatus.Resolving or DownloadStatus.Queued or DownloadStatus.Scheduled=>"\uE769",DownloadStatus.Completed=>"\uE8E5",DownloadStatus.Failed=>"\uE72C",_=>"\uE768"};
 public string PrimaryActionText=>Item.Status switch{DownloadStatus.Downloading or DownloadStatus.Resolving or DownloadStatus.Queued or DownloadStatus.Scheduled=>"Pause",DownloadStatus.Completed=>"Open file",DownloadStatus.Failed=>"Retry",_=>"Resume"};
 public event PropertyChangedEventHandler? PropertyChanged;public void Update(DownloadItem item){Item=item;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(null));}
}
internal sealed class MainViewModel:INotifyPropertyChanged
{
 public ObservableCollection<DownloadRow> Rows{get;}=[];public ICollectionView RowsView{get;}public ICommand AddCommand{get;}public ICommand SearchCommand{get;}public string Filter{get;set;}="all";public string Search{get;set;}="";
 public int ActiveCount=>Rows.Count(r=>r.Item.Status is DownloadStatus.Downloading or DownloadStatus.Resolving);public int CompletedCount=>Rows.Count(r=>r.Item.Status==DownloadStatus.Completed);
 public string SpeedText=>Display.Speed(Rows.Sum(r=>r.Item.BytesPerSecond));public string TotalText=>Display.Bytes(Rows.Sum(r=>Math.Max(0,r.Item.DownloadedBytes)));public string ListLabel=>$"{RowsView.Cast<object>().Count():00} TRANSFERS";
 public event PropertyChangedEventHandler? PropertyChanged;
 public MainViewModel(Action add,Action search){AddCommand=new UiCommand(add);SearchCommand=new UiCommand(search);RowsView=CollectionViewSource.GetDefaultView(Rows);RowsView.Filter=Matches;}
 public void Update(IReadOnlyList<DownloadItem> items){var ids=items.Select(i=>i.Id).ToHashSet();for(var i=Rows.Count-1;i>=0;i--)if(!ids.Contains(Rows[i].Id))Rows.RemoveAt(i);var existing=Rows.ToDictionary(r=>r.Id);foreach(var item in items){if(existing.TryGetValue(item.Id,out var r))r.Update(item);else Rows.Add(new DownloadRow(item));}Refresh();}
 public void Refresh(){RowsView.SortDescriptions.Clear();if(Filter=="queued"){RowsView.SortDescriptions.Add(new("Item.Priority",ListSortDirection.Ascending));RowsView.SortDescriptions.Add(new("Item.QueueOrder",ListSortDirection.Ascending));}else RowsView.SortDescriptions.Add(new("Item.CreatedAt",ListSortDirection.Descending));RowsView.Refresh();PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(null));}
 private bool Matches(object obj){if(obj is not DownloadRow r)return false;if(Search.Length>0&&!r.FileName.Contains(Search,StringComparison.OrdinalIgnoreCase)&&!r.Item.Url.Contains(Search,StringComparison.OrdinalIgnoreCase))return false;return Filter switch{"active"=>r.Item.Status is DownloadStatus.Downloading or DownloadStatus.Resolving,"queued"=>r.Item.Status==DownloadStatus.Queued,"completed"=>r.Item.Status==DownloadStatus.Completed,"failed"=>r.Item.Status is DownloadStatus.Failed or DownloadStatus.Paused or DownloadStatus.Cancelled,"scheduled"=>r.Item.Status==DownloadStatus.Scheduled,"video"=>r.Item.Category.Equals("Video",StringComparison.OrdinalIgnoreCase)||r.Item.Category.Equals("Videos",StringComparison.OrdinalIgnoreCase),"music"=>r.Item.Category.Equals("Audio",StringComparison.OrdinalIgnoreCase)||r.Item.Category.Equals("Music",StringComparison.OrdinalIgnoreCase),"compressed"=>r.Item.Category.Equals("Archives",StringComparison.OrdinalIgnoreCase)||r.Item.Category.Equals("Compressed",StringComparison.OrdinalIgnoreCase),"documents"=>r.Item.Category.Equals("Documents",StringComparison.OrdinalIgnoreCase),"programs"=>r.Item.Category.Equals("Applications",StringComparison.OrdinalIgnoreCase)||r.Item.Category.Equals("Programs",StringComparison.OrdinalIgnoreCase),"other"=>!new[]{"Video","Videos","Audio","Music","Documents","Applications","Programs","Archives","Compressed"}.Contains(r.Item.Category,StringComparer.OrdinalIgnoreCase),_=>true};}
}
