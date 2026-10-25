using System.IO;
using Microsoft.Win32;
namespace Flux.App;
internal static class StartupRegistration
{
 public static void SetEnabled(bool enabled)
 {
  using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
  if(!enabled){key.DeleteValue("Flux",false);return;}
  var executable=Environment.ProcessPath;
  if(executable==null||!Path.GetFileName(executable).Equals("Flux.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Run the published Flux.exe to enable Windows startup.");
  key.SetValue("Flux","\""+executable+"\"",RegistryValueKind.String);
 }
}
