using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsage.Daily;
using CodexUsage.DailyPanel;

internal static class RenderMatrix
{
    // These are target-window renders, not native monitor/DPI or ordinary EXE acceptance.
    internal static int Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)) throw new IOException("Matrix output must be a new directory.");
        Directory.CreateDirectory(output);
        var rows=new List<object>();var failed=0;
        var normal=Program.Fixture() with { Weekly=new(25,Program.Now.AddHours(2),Program.Now) };
        var states=new Dictionary<string,DailyPresentationState>
        {
            ["normal"]=new(){Snapshot=normal},
            ["unknown"]=new(){Snapshot=normal with {InputTokens=null,OutputTokens=null,AverageTps=null,ProcessingSeconds=null,SourceCount=0,Weekly=null}},
            ["zero"]=new(){Snapshot=normal with {InputTokens=0,OutputTokens=0,CachedInputTokens=0,ReasoningTokens=0,AverageTps=0}},
            ["partial"]=new(){Snapshot=normal with {IsPartial=true,Issues=[new(DailyIssueCodes.SourceUnreadable),new(DailyIssueCodes.LegacyCoverageUnknown)]}},
            ["paused"]=new(){Snapshot=normal,IsPaused=true},
            ["busy"]=new(){Snapshot=normal with {IsPartial=true},IsBusy=true},
            ["error"]=new(){Snapshot=normal,RefreshFailed=true},
            ["long-text"]=new(){Snapshot=normal with {IsPartial=true,Issues=[new(DailyIssueCodes.CoverageLost),new(DailyIssueCodes.HistoricalClosureMissing),new(DailyIssueCodes.LegacyCoverageUnknown),new(DailyIssueCodes.MetadataMissing)]},IsPaused=true}
        };
        foreach(var theme in new[]{ThemePreference.Light,ThemePreference.Dark})
        foreach(var language in new[]{LanguagePreference.ZhCN,LanguagePreference.EnUS})
        foreach(var scale in new[]{1d,1.25,1.5,1.75,2d})
        foreach(var (name,state) in states)
        {
            var window=new DailyWindow{ShowActivated=false,Left=-20000,Top=-20000};
            var key=$"{theme}-{language}-{scale*100:0}-{name}";
            try
            {
                window.Configure(new(1,theme,language));
                window.SetState(state,Program.Now,Program.Zone);
                window.Show();window.UpdateLayout();
                if(name=="long-text")
                {
                    Program.ById<Expander>(window,"StatisticsDetails").IsExpanded=true;
                    Program.ById<Expander>(window,"Preferences").IsExpanded=true;
                    window.UpdateLayout();
                    Program.ById<ScrollViewer>(window,"StatisticsScroll").ScrollToEnd();window.UpdateLayout();
                }
                var footer= new[]{"CopyStats","RefreshStats","OpenLogs","ExitPanel"}.Select(id=>
                {
                    var b=Program.ById<Button>(window,id);var p=b.TranslatePoint(new Point(),window);
                    return new{id,x=p.X,y=p.Y,width=b.ActualWidth,height=b.ActualHeight,visible=b.IsVisible,
                        inside=p.X>=0 && p.Y>=0 && p.X+b.ActualWidth<=window.ActualWidth && p.Y+b.ActualHeight<=window.ActualHeight};
                }).ToArray();
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.Width*scale),(int)Math.Ceiling(window.Height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
                bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file=Path.Combine(output,key+".png");using(var stream=File.Create(file))encoder.Save(stream);
                var pass=footer.All(f=>f.visible&&f.inside);
                if(!pass)failed++;
                rows.Add(new{key,theme=theme.ToString(),language=language.ToString(),renderScale=scale,state=name,
                    actualWindowDpi=VisualTreeHelper.GetDpi(window).PixelsPerInchX,pass,footer,file,
                    sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),copy=window.CopyText});
            }
            catch(Exception e){failed++;rows.Add(new{key,pass=false,error=e.ToString()});}
            finally{window.Close();}
        }
        File.WriteAllText(Path.Combine(output,"matrix.json"),JsonSerializer.Serialize(new{
            kind="160 synthetic render-state observations; not 160 independent product tests and not actual DPI acceptance",
            fixedNow=Program.Now,zone=Program.Zone.Id,total=rows.Count,failed,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Render observations {rows.Count-failed}/{rows.Count}; output {output}");
        return failed==0?0:1;
    }
}
