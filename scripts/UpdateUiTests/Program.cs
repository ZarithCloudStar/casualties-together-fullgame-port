using System;
using System.IO;
using Together;
using UnityEngine;

static class Program {
    static int passed;
    static void Check(bool value,string name) { if(!value)throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name);passed++; }
    static void Throws(Action run,string name) { bool threw=false; try{run();}catch{threw=true;} Check(threw,name); }
    static int Main(string[] args) {
        try { Run(args); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run(string[] args) {
        string local="5.5.0-cu-port-bugfix1";
        Check(ReleaseUpdate.Compare("v5.5.0-cu-port-bugfix1",local)==0,"v prefix and release identity");
        Check(ReleaseUpdate.Compare("5.5.0-cu-port",local)<0,"old base release never prompts downgrade");
        Check(ReleaseUpdate.Compare("5.5.0-cu-port-bugfix2",local)>0,"next bugfix prompts update");
        Check(ReleaseUpdate.Compare("5.5.0-cu-port-bugfix10","5.5.0-cu-port-bugfix2")>0,"numeric patch ordering 10 > 2");
        Check(ReleaseUpdate.Compare("5.6.0",local)>0,"minor release ordering");
        Check(ReleaseUpdate.Compare("5.4.99",local)<0,"older minor release");
        Check(ReleaseUpdate.Compare("6.0.0",local)>0,"major release ordering");
        Check(ReleaseUpdate.Compare(local+"+buildA",local+"+buildB")==0,"unordered build hashes do not create update loops");
        Check(ReleaseUpdate.Compare("5.6.0-rc2","5.6.0")<0,"release candidate precedes stable");
        Check(ReleaseUpdate.Compare("5.6.0-beta10","5.6.0-beta2")>0,"numeric prerelease ordering");
        Check(ReleaseUpdate.Compare("5.5.1-cu-port-test2","5.5.1-cu-port-test1")>0,"numeric test package ordering");
        Check(ReleaseUpdate.Compare("5.5.1-cu-port","5.5.1-cu-port-test1")>0,"stable port follows test package");
        Throws(()=>ReleaseUpdate.Compare("nightly",local),"unknown tag cannot claim newest");
        Throws(()=>ReleaseUpdate.Compare(new string('9',200)+".1.1",local),"overflow version rejected");
        Check(ReleaseUpdate.NormalizeSource(null)==ReleaseUpdate.DefaultSource,"production default without config file");
        Check(ReleaseUpdate.NormalizeSource(" ")==ReleaseUpdate.DefaultSource,"blank config uses production default");
        Check(ReleaseUpdate.NormalizeSource(ReleaseUpdate.ReleasePage)==ReleaseUpdate.DefaultSource,"GitHub webpage converts to JSON API");
        Check(ReleaseUpdate.NormalizeSource("http://127.0.0.1:8099/latest.json").StartsWith("http://127.0.0.1"),"explicit local demo override supported");
        Check(ReleaseUpdate.NormalizeSource("http://localhost:8099/latest.json").Contains("localhost"),"localhost override supported");
        Throws(()=>ReleaseUpdate.NormalizeSource("http://example.com/latest.json"),"public insecure update source rejected");
        Throws(()=>ReleaseUpdate.NormalizeSource("file:///C:/secret"),"file source rejected");
        Throws(()=>ReleaseUpdate.NormalizeSource("https://user:secret@example.com"),"credentials in source rejected");
        var demo=ReleaseUpdate.Parse(File.ReadAllText(args[1]));
        Check(demo.version=="5.5.0-cu-port-bugfix2"&&ReleaseUpdate.Compare(demo.version,local)>0,"existing local demonstration JSON still works");
        var real=ReleaseUpdate.Parse(File.ReadAllText(args[0]));
        Check(real.fromGitHub&&real.version==local,"live GitHub release JSON parsed");
        Check(real.url.Contains("/releases/tag/")&&real.publishedAt>0,"release page and publication time from GitHub");
        Check(real.notes.Contains("\n")&&!real.notes.Contains("\\r\\n"),"literal escaped release paragraphs repaired");
        Check(ReleaseUpdate.Compare(real.version,local)==0,"actual current release is up-to-date");
        Check(ReleaseUpdate.Parse("{\"version\":\"5.5.0-cu-port-bugfix1\",\"build\":\"DIFFERENT\"}").version==local,"same version with another hash remains same release");
        Check(ReleaseUpdate.Parse("{\"version\":\"5.6.0\"}").url==ReleaseUpdate.ReleasePage,"missing link falls back to supplied official page");
        Check(ReleaseUpdate.Parse("{\"tag_name\":\"v5.6.0\",\"assets\":[{\"browser_download_url\":\"https://example.com/mod.zip\"}]}").url.EndsWith("mod.zip"),"valid release asset fallback");
        Throws(()=>ReleaseUpdate.Parse("{\"message\":\"Not Found\"}"),"GitHub error payload cannot report newest");
        Throws(()=>ReleaseUpdate.Parse("<html>error</html>"),"HTML response rejected");
        Throws(()=>ReleaseUpdate.Parse(""),"empty response rejected");
        Throws(()=>ReleaseUpdate.Parse(new string('x',ReleaseUpdate.MaxManifestChars+1)),"oversized response rejected");
        Throws(()=>ReleaseUpdate.Parse("{\"version\":\"5.6.0\",\"draft\":true}"),"draft excluded");
        Throws(()=>ReleaseUpdate.Parse("{\"version\":\"5.6.0\",\"prerelease\":true}"),"prerelease excluded");
        Throws(()=>ReleaseUpdate.Parse("{\"version\":\"5.6.0\",\"download_url\":\"javascript:evil\"}"),"script URL rejected");
        Throws(()=>ReleaseUpdate.Parse("{\"version\":\"5.6.0\",\"published_at\":9223372036854775807}"),"invalid timestamp rejected");
        Throws(()=>ReleaseUpdate.Parse("{\"version\":\"5.6.0\",\"build\":\"a\\nb\"}"),"multiline build identity rejected");
        Check(ReleaseUpdate.CommunityGroup=="130925719"&&ReleaseUpdate.DownloadPage.Contains("pwd=wzsw")&&ReleaseUpdate.DownloadCode=="wzsw","provided download and community information");
        // Dirty inherited button geometry used to clip Chinese text. Exercise the
        // production style factory with deliberate old-style pollution.
        GUI.skin.label.fixedHeight=3;GUI.skin.label.fontSize=60;GUI.skin.label.contentOffset=new Vector2(0,100);
        var style=MenuDialogUI.Text();
        Check(ReferenceEquals(style.font,CoopModAssets.gamefont)&&style.fontSize==UIBullshit.GetCurFontSize(),"normal multiplayer font and size reused");
        Check(style.fixedHeight==0&&style.fixedWidth==0&&style.contentOffset.y==0&&style.clipping==TextClipping.Overflow,"inherited fixed metrics and offsets removed");
        Check(GUI.skin.label.fixedHeight==3&&GUI.skin.label.contentOffset.y==100,"other menu styles remain untouched");
        Check(MenuDialogUI.TextHeight("中文标题\n保存时间\n版本与构建",style,400)>style.lineHeight*2,"multiline rows reserve text height");
        foreach(var size in new[]{(640,480),(800,600),(960,540),(1280,720),(1920,1080),(2560,1440)}) {
            Screen.width=size.Item1;Screen.height=size.Item2;UIBullshit.uiScale=size.Item2/1080f;
            var panel=MenuDialogUI.Panel(1080,740,out float scale);
            Check(panel.x>=10&&panel.y>=10&&panel.xMax<=Screen.width-10&&panel.yMax<=Screen.height-10,"dialog fits "+size.Item1+"x"+size.Item2);
        }
        GUI.NextClick=true;GUI.enabled=true;
        Check(!MenuDialogUI.Button(new Rect(0,0,300,44),"当前已是最新版本",1,false),"up-to-date button cannot activate");
        Check(GUI.enabled,"disabled drawing restores global GUI state");
        Check(MenuDialogUI.Button(new Rect(0,0,300,44),"有新版本推送",1,true,true),"new release button activates");
        Check(GUI.LastLabel.normal.textColor.r==MenuDialogUI.Accent.r&&GUI.LastLabel.normal.textColor.g==MenuDialogUI.Accent.g,"new release caption retains orange accent");
        UpdateFallbackTests.Run(Check, Throws).GetAwaiter().GetResult();
        Console.WriteLine("ALL "+passed+" CHECKS PASSED (UI geometry uses stubs; native glyph rendering untested)");
    }
}
