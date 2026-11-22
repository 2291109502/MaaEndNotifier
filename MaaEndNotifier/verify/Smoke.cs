using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace MaaEndNotify {
    // Exercises the live monitor and its durable outbox with a simulated recipient.
    class Smoke {
        static readonly DateTime began=new DateTime(2026,11,22,10,0,0);
        internal static void Check(bool ok,string name) { if(!ok) throw new Exception(name); Console.WriteLine("OK "+name); }
        static string Front(int seconds,string what) { return "["+Plans.Show(began.AddSeconds(seconds))+"][INFO] "+what+"\n"; }
        static string Callback(int seconds,int id,string entry,string kind,string reason) {
            var detail=Data.Encode(new {task_id=id,entry=entry,reason=reason});
            return "["+Plans.Show(began.AddSeconds(seconds))+"][INF] !!!OnEventNotify!!! [handle=true] [msg=Tasker.Task."+kind+"] [details="+detail+"]\n";
        }
        static string Start(string name,string instance,string[] entries,int[] ids,int offset) {
            var lines=Front(offset,"[Task] 实例 "+name+": 开始执行任务, 数量: "+entries.Length);
            lines+=Front(offset,"[MAA] 启动任务, 实例: "+instance+" , 任务数: "+entries.Length);
            for(int i=0;i<entries.Length;i++) lines+=Front(offset,"[MAA] 任务["+i+"]: entry="+entries[i]+", pipelineOverride=[]");
            return lines+Front(offset,"[Task] 实例 "+name+": 任务已提交, task_ids: ["+String.Join(",",ids)+"]");
        }
        static string Mark(int second,string id) { return Front(second,"[App] 收到 state-changed，已刷新运行时状态, kind: tasks-completed, instanceId="+id); }
        static void Say(string msg) {}
        static int Main(string[] args) {
            if(args.Length==2 && args[0]=="--atomic") {
                var q=new SqlQueue(args[1]); var r=Acceptance.Example("transaction-test",true);
                try { q.Record(r,"atomic-id","fixture","dingtalk",new[]{"part1","part2"}); }
                catch(Exception) { Check(!q.Contains(r.Key),"outbox insert failure rolls back the entire result"); return 0; }
                throw new Exception("Expected the test trigger to reject the transaction.");
            }
            string root=Path.GetFullPath(args[0]);
            Acceptance.RunAll(Path.Combine(root,"acceptance"));
            RobotConfig robot=Acceptance.MockRobot();
            string debug=Path.Combine(root,"debug"), history=Path.Combine(root,"history");
            Directory.CreateDirectory(debug); Directory.CreateDirectory(history);
            string ui=Path.Combine(debug,"2026-11-22-1.log"),fw=Path.Combine(debug,"maafw.log");
            Data.Save(ui,Start("日常","a",new[]{"JobA","JobB"},new[]{101,102},0));
            Data.Save(fw,Callback(1,101,"Other","Failed","action_failed")+Callback(3,101,"JobA","Succeeded","success"));
            var m=new MonitorService(root,history,began,"关键字");
            Check(m.Scan(false,true,false,began.AddSeconds(5),Say)==0,"open plan stays pending");
            Check(Directory.GetFiles(history,"*.json").Length==0 && Directory.GetFiles(Path.Combine(history,"in-progress"),"*.json",SearchOption.AllDirectories).Length==1,"open plan is checkpointed");
            // The UI and framework files both rotate before the monitor restarts. Only the last item remains in the logs.
            Data.Save(ui,Mark(8,"a")); Data.Save(fw,Callback(7,102,"JobB","Succeeded","success"));
            m=new MonitorService(root,history,began,"关键字");
            Check(m.Scan(false,true,false,began.AddSeconds(20),Say)==1,"checkpoint survives rotation and restart");
            string file=Directory.GetFiles(history,"*.json").Single();
            var report=Data.Decode<Run>(Data.Read(file));
            var messages=DingTalk.Parts(report,"关键字");
            Check(report.Verdict()=="全部完成" && report.Tasks.All(t=>t.Status=="完成"),"recovered selection and both task outcomes");
            Check(messages[0].Contains("未完成项") && messages[0].Contains("关键字"),"all-clear summary contains configured keyword");
            Check(!Directory.GetFiles(Path.Combine(history,"in-progress"),"*.json",SearchOption.AllDirectories).Any(),"finished checkpoint cleared");
            const long now=1760000000000;
            var mainQueue=Archive.QueueFor(history);
            var firstMessages=new List<string>();
            Check(Sender.Flush(mainQueue,robot,Say,firstMessages.Add,20,45,0)==1 && firstMessages.Count==1,"live plan delivered through mock transport");
            Check(Sender.Flush(mainQueue,robot,Say,firstMessages.Add,20,45,0)==0,"accepted run is not re-sent");
            var again=new MonitorService(root,history,began,"关键字");
            Data.Save(ui,Start("日常","a",new[]{"JobA","JobB"},new[]{101,102},0)+Mark(8,"a"));
            Check(again.Scan(false,true,false,began.AddMinutes(2),Say)==0,"same run does not produce another packet");
            // No terminal callback has arrived yet. Live scans wait for writers to flush.
            Data.Save(ui,Start("延迟","b",new[]{"JobC"},new[]{201},60)+Mark(62,"b"));
            Check(again.Scan(false,true,false,began.AddSeconds(63),Say)==0,"UI end waits for late framework evidence");
            Data.Save(fw,Callback(61,201,"JobC","Succeeded","cancelled"));
            Check(again.Scan(false,true,false,began.AddSeconds(72),Say)==1,"late cancellation captured");
            var latest=Directory.GetFiles(history,"*.json").Select(x=>Data.Decode<Run>(Data.Read(x))).Single(x=>x.Instance=="延迟");
            Check(latest.Verdict()=="未全部完成" && latest.Tasks[0].Status=="提前结束","Succeeded with cancelled is not counted complete");
            string offline=Path.Combine(root,"offline");
            Data.Save(ui,Start("停止","a",new[]{"P","Q"},new[]{301,302},0)+Front(20,"[Task] 实例 停止: 用户手动停止任务")+
                Start("截断","b",new[]{"R"},new[]{401},60));
            Data.Save(fw,Callback(1,301,"P","Starting","")+Callback(2,301,"P","Succeeded","success")+Callback(4,302,"Q","Starting",""));
            Check(new MonitorService(root,offline,DateTime.MinValue,"").Scan(true,true,false,began.AddHours(1),Say)==2,"manual stop and truncated export recorded offline");
            var runs=Directory.GetFiles(offline,"*.json").Select(f=>Data.Decode<Run>(Data.Read(f))).ToDictionary(x=>x.Instance);
            Check(runs["停止"].Verdict()=="已中止" && runs["停止"].Tasks[1].Status=="提前结束","manual stop identified");
            Check(runs["截断"].Verdict()=="结果待确认" && runs["截断"].End=="未记录","insufficient evidence is marked unknown");
            Data.Save(ui,Start("甲","a",new[]{"X"},new[]{501},0)+Start("乙","b",new[]{"Y"},new[]{502},2)+Mark(10,"a")+Mark(11,"b"));
            Data.Save(fw,Callback(6,501,"X","Succeeded","success")+Callback(7,502,"Y","Failed","recognition_timeout"));
            string overlapping=Path.Combine(root,"concurrent");
            new MonitorService(root,overlapping,began,"").Scan(true,true,false,began.AddHours(1),Say);
            var both=Directory.GetFiles(overlapping,"*.json").Select(f=>Data.Decode<Run>(Data.Read(f))).ToDictionary(x=>x.Instance);
            Check(both["甲"].Verdict()=="全部完成" && both["乙"].Tasks[0].Reason=="识别超时。","concurrent queues have separate results");
            var u=DingTalk.Target("https://oapi.dingtalk.com/robot/send?access_token=fixture","SEC_test_key",now);
            Check(u.AbsoluteUri.EndsWith("timestamp=1760000000000&sign=QtI40Qvk80F9KOdNf7tUpo%2FbvGe%2BX8QpORImHapD688%3D"),"DingTalk HMAC matches independent reference");
            DingTalk.CheckReply("{\"errcode\":0,\"errmsg\":\"ok\"}");
            bool rejected=false; try{DingTalk.CheckReply("{\"errcode\":310000,\"errmsg\":\"test\"}");}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"DingTalk application errors are retried even if HTTP succeeded");
            var big=report; big.Tasks.Clear();
            for(int i=0;i<24;i++) big.Tasks.Add(new Subtask{Name="长任务"+i,Status="失败",Reason=new string('错',220)});
            var parts=DingTalk.Parts(big,"关键词"); string together=String.Join("\n",parts);
            Check(parts.Count>1 && parts.All(x=>Data.Utf8.GetByteCount(x)<4000 && x.Contains("关键词")) &&
                big.Tasks.All(t=>together.Contains(t.Name+"〔")),"multipart messages preserve every selected failure");
            Check(Options.Open(Options.Seal("local-test-only"))=="local-test-only","Windows per-user settings encryption round trip");
            if(args.Length>1) {
                string realHistory=Path.Combine(root,"real","history");
                var realMonitor=new MonitorService(Catalog.NormalRoot(args[1]),realHistory,DateTime.MinValue,"MaaEnd");
                Check(realMonitor.Scan(false,true,false,began.AddDays(1),Say)==2,"two real plans archived through live path");
                var real=Directory.GetFiles(realHistory,"*.json").OrderBy(x=>x).Select(x=>Data.Decode<Run>(Data.Read(x))).ToList();
                Check(real[0].Count==20 && real[1].Count==19 && real.All(x=>x.Tasks.Count(t=>t.Status=="完成")==18),"39 real selections reconciled");
                var received=new List<string>(); Sender.Flush(Archive.QueueFor(realHistory),robot,Say,received.Add,20,45,0);
                Check(received.Count==2 && received.Any(s=>s.Contains("理智不足") && s.Contains("约 30 秒")) &&
                    received.Any(s=>s.Contains("约 142 秒")) && received.All(s=>s.Contains("武陵·星门菌 2 号路线") && !s.Contains("外部程序")),"two mock webhook deliveries contain distinct real reasons");
            }
            return 0;
        }
    }
}
