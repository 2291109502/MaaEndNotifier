using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MaaEndNotify {
    class Acceptance {
        // Synthetic markers allow database and log checks without reading any deployed credentials.
        const string Canary="CanaryOnlyQ7uM91";
        const string Url="https://oapi.dingtalk.com/robot/send?access_token="+Canary;
        const string Secret="SEC_fixture_a_921b";
        internal static RobotConfig MockRobot() { return RobotConfig.FromFields(Url,Secret,"关键字",10000); }
        static void Check(bool ok,string name) { Smoke.Check(ok,name); }
        internal static Run Example(string tag,bool success) {
            var r=new Run{Key=Data.Hash(tag),InstanceId="fixture-id",Instance="验收",Start="2026-11-22 10:00:00",
                End="2026-11-22 10:00:13",Signal="结束",Count=1};
            r.Tasks.Add(new Subtask{Id="1",Name="采集",Status=success?"完成":"失败",Reason=success?"":"识别超时"});
            r.CaptureHost(); return r;
        }
        static void Transport() {
            foreach(var bad in new [] {"{\"errcode\":\"0\"}","{\"errcode\":0.0}","{\"errcode\":false}","{\"errcode\":null}","{}",
                "not json","[]","null","{'errcode':0}","{errcode:0}","{\"errcode\":00}","{\"errcode\":1,\"errcode\":0}"}) {
                bool caught=false;
                try { DingTalk.CheckResponse(200,bad,null); } catch(DeliveryException) { caught=true; }
                Check(caught,"rejects malformed or non-integer business acknowledgement");
            }
            var robot=MockRobot(); var uris=new List<Uri>();
            Func<Uri,byte[],int,RobotReply> post=(target,bytes,timeout)=>{
                var doc=Data.Decode<object>(Data.Utf8.GetString(bytes));
                string body=Data.Str(Data.Get(doc,"text"),"content");
                Check(Data.Str(doc,"msgtype")=="text" && body.Contains("钉钉✓") && !body.Contains(Secret) &&
                    !body.Contains(Canary) && timeout==10000,"UTF-8 text, timeout and credential-free body");
                uris.Add(target);
                return new RobotReply{Status=201,Body="{\"errcode\":0}",RetryAfter=null};
            };
            DingTalk.SendVia(robot,"关键字 钉钉✓",post); Thread.Sleep(15); DingTalk.SendVia(robot,"关键字 钉钉✓",post);
            Check(uris.Count==2 && uris.All(u=>u.Query.Contains("&sign=") && u.Query.Contains("&timestamp=")) &&
                uris[0].Query!=uris[1].Query,"retry builds a new signed URL every time");
            bool throttled=false;
            try { DingTalk.CheckResponse(429,"{\"errcode\":0}","37"); }
            catch(DeliveryException ex) { throttled=!ex.Permanent && ex.RetryAfterMs>=36000; }
            Check(throttled,"HTTP errors override a success body and preserve Retry-After");
            bool safe=false;
            try { DingTalk.CheckResponse(200,Data.Encode(new{errcode=310000,errmsg=Url+" "+Secret}),null); }
            catch(DeliveryException ex) { safe=!ex.Message.Contains(Canary) && !ex.Message.Contains(Secret) && !ex.Message.Contains("https://"); }
            Check(safe,"business errors cannot echo service-supplied credentials");
        }
        static void Configuration(string dir) {
            string file=Path.Combine(dir,"mock-config.json");
            var config=new Dictionary<string,object> {
                {"mode","dingtalk"},{"webhook_url",Url},{"keyword","验收关键字"},{"dingtalk_secret",Secret},
                {"github_secret","unused-fixture-key"},{"timeout_seconds",12}
            };
            Data.Save(file,Data.Encode(config)); var robot=RobotConfig.Load(file);
            Check(robot.Mode=="dingtalk" && robot.Hook==Url && robot.Secret==Secret && robot.Keyword=="验收关键字" &&
                robot.TimeoutMs==12000,"configured mode, keyword, distinct signing key and timeout preserved");
            config["unexpected"]="unused"; Data.Save(file,Data.Encode(config)); bool unknown=false;
            try { RobotConfig.Load(file); } catch(InvalidOperationException){unknown=true;}
            Check(unknown,"unknown configuration key rejected"); config.Remove("unexpected");
            config["webhook_url"]="http://example.invalid/path?access_token="+Canary;
            Data.Save(file,Data.Encode(config)); bool rejected=false;
            try { RobotConfig.Load(file); } catch(Exception ex) { rejected=!ex.Message.Contains(Canary) && !ex.Message.Contains("http://"); }
            Check(rejected,"bad configured destination rejected without logging URL");
        }
        static void Queue(string dir) {
            string path=Path.Combine(dir,"queue.sqlite3"); var queue=new SqlQueue(path);
            var r=Example("two-part",false); r.PlanReason="识别失败 "+Url+" "+Secret;
            string[] parts={"失败一 "+Url,"失败二 "+Secret};
            Check(queue.Record(r,"fixture-id","验收","dingtalk",parts,7),"failed result and parts recorded");
            Check(!queue.Record(r,"fixture-id","验收","dingtalk",parts,7),"duplicate callback deduplicated");
            var second=new SqlQueue(path); var gate=new ManualResetEventSlim(false);
            long now=DingTalk.Now()+100; var claims=new SqlLease[2];
            var contenders=new [] {queue,second}.Select((q,i)=>Task.Run(()=>{
                gate.Wait(); claims[i]=q.Claim(now,3,5);
            })).ToArray();
            gate.Set(); Task.WaitAll(contenders);
            var lease=claims.Single(x=>x!=null);
            Check(lease.Part==1 && lease.Attempts==1 && queue.Claim(now,3,5)==null,"independent claimers elect one owner and hold later parts");
            Check(!lease.Content.Contains(Canary) && !lease.Content.Contains("https://"),"sensitive URL from source task removed before dispatch");
            var newer=second.Claim(now+5001,3,5);
            Check(newer!=null && newer.EventId==lease.EventId && newer.Token!=lease.Token && newer.Attempts==2,"expired lease recovered");
            Check(!queue.Ack(lease,now+5002) && !queue.Fail(lease,now+5002,"late"),"stale token cannot overwrite new lease");
            Check(second.Fail(newer,now+5003,"network",false,now+120000,3) &&
                queue.Claim(now+119999,3,5)==null,"not-due retry is never sent");
            var last=queue.Claim(now+3600000,3,5);
            Check(last!=null && last.Attempts==3 && queue.Fail(last,now+3600001,"offline",false,0,3),"normal failures exhaust retry limit");
            Check(queue.Claim(now+7200000,3,5)==null && queue.Status().Contains("dead"),"dead predecessor blocks later parts and is queryable");
            Check(queue.Retry(last.EventId)==1,"dead notification can be manually requeued");
            var retry=queue.Claim(now+7200001,3,5); Check(retry!=null && retry.Attempts==1 && retry.EventId==last.EventId &&
                queue.Ack(retry,now+7200002),"manual retry reuses event and resets its attempts");
            var part2=queue.Claim(now+7200003,3,5);
            Check(part2!=null && part2.Part==2 && queue.Ack(part2,now+7200004) && queue.Claim(now+7200005)==null,"parts delivered in order");
            var crash=Example("crash-limit",true); queue.Record(crash,"fixture-id","验收","dingtalk",new[]{"模拟投递"});
            var old=queue.Claim(now+7200010,2,5);
            var replacement=queue.Claim(now+7205011,2,5);
            Check(old!=null && replacement!=null && queue.Claim(now+7210012,2,5)==null,"repeated crashes also exhaust the limit");
            Check(queue.Record(Example("disabled",true),"silent-id","静默","dingtalk",new string[0]),"disabled reporting can retain the business result");
        }
        static void Migration(string dir) {
            string history=Path.Combine(dir,"history"); Directory.CreateDirectory(history);
            var queue=Archive.QueueFor(history); long due=DingTalk.Now()+20000;
            var delivered=Example("legacy-done",true); var split=Example("legacy-partial",false);
            string donePath=Archive.Stem(history,delivered), partialPath=Archive.Stem(history,split);
            Data.Save(donePath+".json",Data.Encode(new Packet{Report=delivered,Messages=new List<string>{"old-a"},Next=0}));
            Data.Save(donePath+".sent","existing-receipt");
            Data.Save(partialPath+".json",Data.Encode(new Packet{Report=split,Messages=new List<string>{"old-b","old-c"},
                Next=1,Failures=2,RetryAfter=due}));
            Archive.Migrate(history,queue,s=>{}); Archive.Migrate(history,queue,s=>{});
            Check(queue.Contains(delivered.Key) && queue.Contains(split.Key) && queue.Claim(due-1)==null,"legacy receipts, cursor and due time preserved");
            var lease=queue.Claim(due+100);
            Check(lease!=null && lease.RunId==split.Key && lease.Part==2 && lease.Content=="old-c" && lease.Attempts==3,
                "legacy migration retries only the remaining part with original attempt count");
            Check(queue.Ack(lease,due+200) && queue.Claim(due+300)==null && File.Exists(partialPath+".json"),"migration is idempotent and preserves original files");
        }
        static void SenderChecks(string dir) {
            var q=new SqlQueue(Path.Combine(dir,"sender.sqlite3"));
            var robot=MockRobot(); var logs=new List<string>();
            for(int i=0;i<3;i++) {
                var r=Example("batch"+i,true); q.Record(r,"fixture-id","验收","dingtalk",new[]{"body"+i});
            }
            var attempts=new List<string>(); Check(Sender.Flush(q,robot,logs.Add,attempts.Add,1,45,0)==1 && attempts.Count==1,"flush batch limit");
            Action<string> fake=s=>{attempts.Add(s); DingTalk.CheckResponse(200,Data.Encode(new{errcode=310000,errmsg=Url+" "+Secret}),null);};
            Check(Sender.Flush(q,robot,logs.Add,fake,1,45,0)==1,"HTTP business failure handled with durable nack");
            var inside=new List<string>();
            var timer=System.Diagnostics.Stopwatch.StartNew();
            int count=Sender.Flush(q,robot,logs.Add,s=>{inside.Add(s); Thread.Sleep(1100);},20,1,0);
            Check(count==0 && inside.Count==0 && timer.Elapsed.TotalSeconds<1,
                "flush does not lease work when the HTTP timeout exceeds the remaining budget");
            Check(!logs.Any(l=>l.Contains(Canary)||l.Contains(Secret)||l.Contains("https://")),"sender's logs contain no endpoint or credentials");
        }
        static void Policy(string dir) {
            string file=Path.Combine(dir,"tasks.json");
            Data.Save(file,Data.Encode(new{tasks=new[]{new{task_id="fixture-id",display_name="新计划名称",report_enabled=false}}}));
            var r=Example("policy",true);
            Check(!ReportPolicy.Apply(file,r) && r.DisplayName=="新计划名称" && r.TaskId=="fixture-id","policy can rename and silence a selected plan");
            r.InstanceId="future-plan";
            Check(ReportPolicy.Apply(file,r) && r.TaskId=="future-plan","new plans inherit reporting without new sender code");
        }
        public static void RunAll(string dir) {
            Directory.CreateDirectory(dir); Configuration(dir); Transport(); Queue(dir); Migration(Path.Combine(dir,"legacy")); SenderChecks(dir); Policy(dir);
        }
    }
}
