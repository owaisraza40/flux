using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
namespace Flux.App;
public static class ProgressAnimation
{
 public static readonly DependencyProperty ValueProperty=DependencyProperty.RegisterAttached("Value",typeof(double),typeof(ProgressAnimation),new PropertyMetadata(0d,Changed));
 public static void SetValue(DependencyObject obj,double value)=>obj.SetValue(ValueProperty,value);
 public static double GetValue(DependencyObject obj)=>(double)obj.GetValue(ValueProperty);
 private static void Changed(DependencyObject obj,DependencyPropertyChangedEventArgs e){if(obj is not ProgressBar bar)return;var target=Math.Clamp(double.IsFinite((double)e.NewValue)?(double)e.NewValue:0,0,100);bar.BeginAnimation(RangeBase.ValueProperty,new DoubleAnimation(bar.Value,target,TimeSpan.FromMilliseconds(230)){EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});}
}
