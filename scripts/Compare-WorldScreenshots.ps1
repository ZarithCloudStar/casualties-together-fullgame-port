param([Parameter(Mandatory=$true)][string]$Folder)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Color].Assembly.Location, (Join-Path $PSHOME 'System.Private.Windows.GdiPlus.dll'), (Join-Path $PSHOME 'System.Private.Windows.Core.dll')) -TypeDefinition @'
using System;
using System.Drawing;
public static class WorldScreenshotComparison {
    public static string Compare(string folder) {
        using (var a = new Bitmap(System.IO.Path.Combine(folder,"host.png")))
        using (var b = new Bitmap(System.IO.Path.Combine(folder,"client.png"))) {
            if (a.Size != b.Size) throw new Exception("Screenshot dimensions differ.");
            double total=0, scene=0; long n=0, near=0;
            // Fixed visible world region, excluding test overlay/HUD and the
            // overlapping player animation/name labels. No image alignment edits.
            double sx=a.Width/2048.0, sy=a.Height/1152.0;
            for (int y=(int)(220*sy); y<a.Height-(int)(90*sy); y++) for (int x=(int)(280*sx); x<a.Width-(int)(70*sx); x++) {
                if (x>880*sx && x<1190*sx && y>760*sy) continue;
                var p=a.GetPixel(x,y); var q=b.GetPixel(x,y);
                int dr=Math.Abs(p.R-q.R), dg=Math.Abs(p.G-q.G), db=Math.Abs(p.B-q.B);
                total+=dr+dg+db; n++;
                if (Math.Max(dr,Math.Max(dg,db))<=16) near++;
            }
            scene=total/(n*3*255.0);
            return "{\"width\":"+a.Width+",\"height\":"+a.Height+",\"comparedPixels\":"+n+
                ",\"sceneNormalizedMAE\":"+scene.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+
                ",\"scenePixelsWithin16\":"+((double)near/n).ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+"}";
        }
    }
}
'@
$result = [WorldScreenshotComparison]::Compare((Resolve-Path -LiteralPath $Folder).Path)
$result | Set-Content -LiteralPath (Join-Path $Folder 'screenshot-comparison.json') -Encoding utf8
$result
