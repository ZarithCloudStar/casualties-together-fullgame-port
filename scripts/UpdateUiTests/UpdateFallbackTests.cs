using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Together;

static class UpdateFallbackTests {
    internal static async Task Run(Action<bool,string> check, Action<Action,string> throws) {
        string api=ReleaseUpdate.DefaultSource, page=ReleaseUpdate.ReleasePage;
        string target="https://github.com/"+ReleaseUpdate.Repository+"/releases/tag/v5.5.0-cu-port-bugfix2";
        check(UpdateSourceProbe.ReleasePageFor(api)==page,"API fallback points to the exact same repository");
        check(UpdateSourceProbe.ReleasePageFor("https://api.github.com/repos/Another/Project/releases/latest")=="https://github.com/Another/Project/releases/latest","custom GitHub repository remains isolated");
        foreach(string source in new[]{"https://example.com/latest.json","http://127.0.0.1:8099/latest.json","https://api.github.com.evil/repos/a/b/releases/latest","http://api.github.com/repos/a/b/releases/latest","https://user@api.github.com/repos/a/b/releases/latest","https://api.github.com:8443/repos/a/b/releases/latest","https://api.github.com/repos/a/b/releases/latest?x=1"})
            check(UpdateSourceProbe.ReleasePageFor(source)==null,"no unexpected fallback for "+source);
        var redirected=UpdateSourceProbe.FromRedirect(page,target);
        check(redirected.version=="5.5.0-cu-port-bugfix2"&&redirected.publishedAt==0&&redirected.build=="","redirect supplies version without inventing release metadata");
        check(redirected.notes.Contains("完整更新说明")&&redirected.url==target,"fallback explains where to read full notes");
        check(UpdateSourceProbe.FromRedirect(page,"/"+ReleaseUpdate.Repository+"/releases/tag/v5.5.0-cu-port-bugfix2").version==redirected.version,"relative release redirect supported");
        foreach(string location in new[]{"https://evil.example/releases/tag/v5.6.0","http://github.com/"+ReleaseUpdate.Repository+"/releases/tag/v5.6.0","https://github.com/other/repo/releases/tag/v5.6.0","https://github.com/"+ReleaseUpdate.Repository+"/tags/v5.6.0","https://github.com/login","https://github.com/"+ReleaseUpdate.Repository+"/releases/tag/nightly",target+"/extra",target+"?token=x",target+"#fragment",target.Replace("github.com/","user@github.com/"),target.Replace("github.com/","github.com:8443/"),target+"%2Fother",""})
            throws(()=>UpdateSourceProbe.FromRedirect(page,location),"unsafe/nonrelease redirect rejected: "+location);
        var calls=new List<(string url,bool follow)>();
        var live=await UpdateSourceProbe.CheckAsync(api,(url,follow)=>{
            calls.Add((url,follow));
            return Task.FromResult(new UpdateSourceProbe.Response{status=200,text="{\"tag_name\":\"v5.5.0-cu-port-bugfix2\",\"body\":\"发布说明\"}"});
        });
        check(live.info.version==redirected.version&&calls.Count==1&&calls[0].follow,"live API success does not need fallback");
        calls.Clear();
        var offline=await UpdateSourceProbe.CheckAsync(api,(url,follow)=>{
            calls.Add((url,follow));
            return Task.FromResult(follow ? new UpdateSourceProbe.Response{status=0,error="Unable to complete SSL connection"} : new UpdateSourceProbe.Response{status=302,location=target,error="Redirect limit exceeded"});
        });
        check(offline.info.version==redirected.version&&offline.sourceLabel.Contains("备用"),"API SSL failure recovered by release page");
        check(calls.Count==2&&calls[1].url==page&&!calls[1].follow,"fallback reads Location without downloading the release page");
        check(offline.failures.Count==1&&offline.failures[0].Contains("SSL"),"primary failure remains in diagnostics after fallback success");
        foreach(var failedResponse in new[]{
            new UpdateSourceProbe.Response{status=403,error="rate limited"},
            new UpdateSourceProbe.Response{status=404,error="not found"},
            new UpdateSourceProbe.Response{status=200,text="<html>login</html>"},
            new UpdateSourceProbe.Response{status=200,text="{\"message\":\"Not Found\"}"},
            new UpdateSourceProbe.Response{status=200,text=""}}) {
            var recovered=await UpdateSourceProbe.CheckAsync(api,(url,follow)=>Task.FromResult(follow?failedResponse:new UpdateSourceProbe.Response{status=302,location=target}));
            check(recovered.info!=null,"HTTP/rate limit/invalid payload can recover through fallback");
        }
        var thrown=await UpdateSourceProbe.CheckAsync(api,(url,follow)=>follow?Task.FromException<UpdateSourceProbe.Response>(new TimeoutException("timeout")):Task.FromResult(new UpdateSourceProbe.Response{status=302,location=target}));
        check(thrown.info!=null,"transport exception falls back instead of leaving a stuck check");
        var bothFailed=await UpdateSourceProbe.CheckAsync(api,(url,follow)=>Task.FromResult(new UpdateSourceProbe.Response{status=0,error="TLS failure"}));
        check(bothFailed.info==null&&bothFailed.failures.Count==2,"both failed endpoints do not claim up-to-date");
        var htmlFallback=await UpdateSourceProbe.CheckAsync(api,(url,follow)=>Task.FromResult(new UpdateSourceProbe.Response{status=follow?500:200,text="<html>page</html>"}));
        check(htmlFallback.info==null,"HTML page without a version redirect is not guessed as latest");
        int customCalls=0;
        var custom=await UpdateSourceProbe.CheckAsync("http://127.0.0.1:8099/latest.json",(url,follow)=>{customCalls++;return Task.FromResult(new UpdateSourceProbe.Response{status=500});});
        check(custom.info==null&&customCalls==1,"failed custom demo never silently switches to production repository");
        var messages=new List<string>();
        await UpdateSourceProbe.CheckAsync(api,(url,follow)=>Task.FromResult(follow?new UpdateSourceProbe.Response{status=500}:new UpdateSourceProbe.Response{status=302,location=target}),messages.Add);
        check(messages.Count==2&&messages[1].Contains("备用"),"checking status identifies the fallback stage");

        string root=Path.Combine(@"F:\cu-port\verification", "update-cache-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file=Path.Combine(root,"update_cache.json"); var now=DateTimeOffset.UtcNow;
        check(UpdateResultCache.Read(file,api,now)==null,"missing cache is a clean first run");
        UpdateResultCache.Write(file,api,redirected,offline.sourceLabel,now);
        var saved=UpdateResultCache.Read(file,api,now);
        check(saved.info.version==redirected.version&&saved.sourceLabel==offline.sourceLabel,"successful fallback persists to disk and reloads");
        check(Math.Abs((saved.checkedAt-now).TotalSeconds)<1,"cache records successful check time");
        check(UpdateResultCache.Read(file,"http://127.0.0.1:8099/latest.json",now)==null,"production cache cannot contaminate a demo source");
        check(UpdateResultCache.Read(file,api,now.AddDays(8))==null,"cache expires after seven days");
        check(UpdateResultCache.Read(file,api,now.AddMinutes(-10))==null,"future-dated cache rejected");
        string good=File.ReadAllText(file);
        throws(()=>UpdateResultCache.Write(file,api,new ReleaseUpdate.Info{version="broken"},"bad",now),"invalid response never replaces good cache");
        check(File.ReadAllText(file)==good,"invalid replacement preserves exact previous cache");
        using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            throws(()=>UpdateResultCache.Write(file,api,ReleaseUpdate.Parse("{\"version\":\"5.6.0\"}"),"new",now),"cache IO failure propagates to nonfatal live-result guard");
        }
        check(File.ReadAllText(file)==good,"failed atomic replacement retains old cache");
        check(!Directory.GetFiles(root,"*.tmp-*").Any(),"failed cache stages are cleaned");
        UpdateResultCache.Write(file,api,ReleaseUpdate.Parse("{\"version\":\"5.6.0\"}"),"GitHub 发布接口",now);
        check(UpdateResultCache.Read(file,api,now).info.version=="5.6.0","successful new live result replaces cached version");
        File.WriteAllText(file,"broken");
        throws(()=>UpdateResultCache.Read(file,api,now),"corrupt cache is reported to ignore-cache guard");
        File.WriteAllText(file,new JObject{["schema"]=99,["source"]=api}.ToString());
        check(UpdateResultCache.Read(file,api,now)==null,"unknown cache schema ignored");
        Console.WriteLine("Cache test data: "+root);
    }
}
