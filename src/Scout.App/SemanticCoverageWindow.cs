using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Scout.Core;
using ListView=System.Windows.Controls.ListView;

namespace Scout.App;

internal sealed class SemanticCoverageWindow : Window
{
    private readonly CancellationTokenSource lifetime=new();
    private readonly System.Windows.Threading.DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private readonly ListView files=new(){Margin=new Thickness(24,8,24,16)};
    private readonly TextBlock status=new(){Margin=new Thickness(24,0,24,8),TextWrapping=TextWrapping.Wrap};
    private string fingerprint="";
    internal sealed record CoverageRow(string File,string Status,string Coverage,string Path);
    public SemanticCoverageWindow(SemanticService service)
    {
        Title="Content coverage";Width=960;Height=620;MinWidth=680;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WindowVisuals.Configure(this,WindowSurface.Document);
        files.Background=WindowVisuals.Brush("Surface");files.Foreground=WindowVisuals.Brush("Ink");files.BorderBrush=WindowVisuals.Brush("Line");
        var panel=new DockPanel();var header=WindowVisuals.Header(this,"Content coverage","Partial, unsupported and failed files in your enabled folders. Ordinary filename search still finds them.");DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        DockPanel.SetDock(status,Dock.Top);panel.Children.Add(status);
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(24,0,24,20)};DockPanel.SetDock(controls,Dock.Bottom);panel.Children.Add(controls);
        var refresh=new Button{Content="Refresh",Margin=new Thickness(0,0,8,0)};var close=new Button{Content="Close"};close.Click+=(_,_)=>Close();controls.Children.Add(refresh);controls.Children.Add(close);
        var columns=new GridView();
        foreach(var (title,property,width) in new[]{("File","File",180d),("Status","Status",95d),("Coverage","Coverage",350d),("Path","Path",300d)})
        {
            var text=new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(property));text.SetBinding(TextBlock.ToolTipProperty,new System.Windows.Data.Binding(property));
            text.SetValue(TextBlock.TextWrappingProperty,property=="Coverage"?TextWrapping.Wrap:TextWrapping.NoWrap);text.SetValue(FrameworkElement.MaxWidthProperty,width-16);
            text.SetValue(FrameworkElement.MarginProperty,new Thickness(0,3,0,3));
            columns.Columns.Add(new GridViewColumn{Header=title,Width=width,CellTemplate=new DataTemplate{VisualTree=text}});
        }
        files.View=columns;AutomationProperties.SetName(files,"Content coverage files");panel.Children.Add(files);Content=WindowVisuals.Frame(panel);
        async Task Load()
        {
            refresh.IsEnabled=false;var expected=service.Scope.Fingerprint;fingerprint=expected;
            try
            {
                var rows=await service.CoverageAsync(lifetime.Token);
                if(lifetime.IsCancellationRequested || expected!=service.Scope.Fingerprint)return;
                files.ItemsSource=rows.Select(file=>new CoverageRow(System.IO.Path.GetFileName(file.Path),file.Partial?"Partial":file.Status switch {"pending"=>"Queued","running"=>"Processing","unsupported"=>"Unsupported","failed"=>"Failed",_=>file.Status},
                    SemanticCoverage.Describe(file.Detail),file.Path)).ToArray();
                status.Text=rows.Length==0?"No incomplete files in the enabled scope.":$"Showing {rows.Length:N0} incomplete files (up to 500).";
            }
            catch(OperationCanceledException){}
            catch(Exception error)when(error is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {files.ItemsSource=null;status.Text="Coverage is unavailable. Check the content index and retry.";}
            finally{refresh.IsEnabled=true;}
        }
        refresh.Click+=async(_,_)=>await Load();Loaded+=async(_,_)=>await Load();
        timer.Tick+=async(_,_)=>{if(fingerprint!=service.Scope.Fingerprint){files.ItemsSource=null;await Load();}};timer.Start();
        Closed+=(_,_)=>{timer.Stop();lifetime.Cancel();files.ItemsSource=null;};
    }
}
