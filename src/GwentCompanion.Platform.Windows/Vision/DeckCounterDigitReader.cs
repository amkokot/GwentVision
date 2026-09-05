using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Localizes the small slanted pile-counter font inside its HUD band.</summary>
public static class DeckCounterDigitReader
{
    private static readonly Lazy<HudDigitReader> SmallDigits = new(() => new(new[]{"deck-counter-digits.json","deck-counter-small-digits.json"}
        .SelectMany(name=>
        {
            var path=Path.Combine(AppContext.BaseDirectory,"vision-assets",name);
            return File.Exists(path)?JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path))??[]:[];
        }).ToArray()));
    private static readonly Lazy<HudDigitReader> Digits = new(() =>
    {
        var path=Path.Combine(AppContext.BaseDirectory,"vision-assets","deck-counter-digits.json");
        return new(File.Exists(path)?JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path))??[]:[]);
    });
    public static int? Read(PixelFrame frame, NormalizedRegion region, double minimumHeightFraction = .012)
    {
        var left=region.PixelLeft(frame.Width);var top=region.PixelTop(frame.Height);
        var width=region.PixelRight(frame.Width)-left;var height=region.PixelBottom(frame.Height)-top;
        using var mask=new Mat(height,width,MatType.CV_8UC1,Scalar.Black);
        for(var y=0;y<height;y++) for(var x=0;x<width;x++)
        {
            var p=frame.GetPixel(left+x,top+y);
            if(Math.Min(p.Red,Math.Min(p.Green,p.Blue))>=160 && Math.Max(p.Red,Math.Max(p.Green,p.Blue))-Math.Min(p.Red,Math.Min(p.Green,p.Blue))<=65)
                mask.Set(y,x,(byte)255);
        }
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
        var count=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers);var parts=new List<Rect>();
        for(var i=1;i<count;i++)
        {
            var r=new Rect(stats.At<int>(i,0),stats.At<int>(i,1),stats.At<int>(i,2),stats.At<int>(i,3));
            if(r.Height>=frame.Height*minimumHeightFraction && r.Height<=frame.Height*.032 && r.Width>=2 && r.Width<=r.Height*1.1 &&
                stats.At<int>(i,4)>=r.Height*.8) parts.Add(r);
        }
        var readings=new List<int>();var visited=new HashSet<Rect>();
        foreach(var part in parts.OrderBy(r=>r.X))
        {
            if(!visited.Add(part)) continue;
            var group=parts.Where(r=>r!=part && !visited.Contains(r) && r.X>=part.Right && r.X-part.Right<=frame.Width*.006 &&
                Math.Abs(r.Y-part.Y)<=frame.Height*.006 && Math.Abs(r.Height-part.Height)<=frame.Height*.006).ToArray();
            if(group.Length>1) continue;
            var right=part.Right;var bottom=part.Bottom;var y=part.Y;
            if(group.Length==1) {visited.Add(group[0]);right=group[0].Right;bottom=Math.Max(bottom,group[0].Bottom);y=Math.Min(y,group[0].Y);}
            var pad=Math.Max(3,(int)Math.Round(frame.Height*.005));
            var crop=new NormalizedRegion((left+part.X-pad)/(double)frame.Width,(top+y-pad)/(double)frame.Height,
                (left+right+pad)/(double)frame.Width,(top+bottom+pad)/(double)frame.Height);
            if((minimumHeightFraction<.012 ? SmallDigits.Value : Digits.Value).Read(frame,crop) is >=0 and <=99 and var number) readings.Add(number);
        }
        // Never choose one of several competing number-like groups in the crop.
        return readings.Count==1?readings[0]:null;
    }
}
