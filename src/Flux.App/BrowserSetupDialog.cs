using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Flux.App.Integration;
namespace Flux.App;
public sealed class BrowserSetupDialog:Window
{
 public BrowserSetupDialog()
 {
  SetResourceReference(StyleProperty,typeof(Window));Title="Connect your browser — Flux";Width=620;Height=490;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
  var panel=new StackPanel{Margin=new Thickness(26)};Content=panel;panel.Children.Add(new TextBlock{Text="CONNECT YOUR BROWSER",FontSize=24,FontWeight=FontWeights.Bold});
  panel.Children.Add(new TextBlock{Text="Flux registered its native bridge for Chrome, Edge, and Firefox. Install the bundled extension to capture downloads.",Foreground=Display.Brush("#9A96AB"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,18,0,20),LineHeight=22});
  panel.Children.Add(new TextBlock{Text="Chrome / Edge\nOpen the browser's Extensions page, enable Developer mode, choose Load unpacked, and select the chromium folder below. Open the Flux extension and select Connect.\n\nFirefox\nOpen about:debugging → This Firefox → Load Temporary Add-on, then select firefox/manifest.json. Persistent Firefox installation requires Mozilla signing.",TextWrapping=TextWrapping.Wrap,LineHeight=22});
  var path=Path.Combine(AppContext.BaseDirectory,"browser-extension");panel.Children.Add(new TextBox{Text=path,IsReadOnly=true,Margin=new Thickness(0,18,0,15)});
  var row=new WrapPanel();Button Action(string text,Action action){var button=new Button{Content=text,Margin=new Thickness(0,0,8,8)};button.Click+=(_,_)=>{try{action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Flux",MessageBoxButton.OK,MessageBoxImage.Information);}};row.Children.Add(button);return button;}
  Action("Open extension folder",()=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true}));Action("Read setup guide",()=>Process.Start(new ProcessStartInfo(File.Exists(BrowserIntegration.SetupInstructionsPath)?BrowserIntegration.SetupInstructionsPath:"https://github.com/owaisraza40/flux/blob/main/docs/BROWSER-INTEGRATION.md"){UseShellExecute=true}));Action("Done",()=>Close()).Style=(Style)FindResource("PrimaryButton");panel.Children.Add(row);
 }
}
