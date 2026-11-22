using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MaaEndNotify {
    class Packet {
        public Run Report {get;set;}
        public List<string> Messages {get;set;}
        public int Next {get;set;}
        public int Failures {get;set;}
        public long RetryAfter {get;set;}
    }
    static class Archive {
        internal static string Stem(string dir, Run r) {
            return Path.Combine(dir, r.Start.Replace("-","").Replace(":","").Replace(' ','-')+"-"+r.Key.Substring(0,12));
        }
        internal static SqlQueue QueueFor(string history) {
            return new SqlQueue(Path.Combine(Directory.GetParent(Path.GetFullPath(history)).FullName,"notifications.sqlite3"));
        }
        internal static void Write(string dir, Run r, string keyword, bool live) {
            string stem=Stem(dir,r);
            if(live) {
                r.CaptureHost();
                var enabled=ReportPolicy.Apply(Path.Combine(Directory.GetParent(Path.GetFullPath(dir)).FullName,"tasks.json"),r);
                r.ScrubForStorage();
                QueueFor(dir).Record(r,r.TaskId,r.DisplayName,"dingtalk",enabled?DingTalk.Parts(r,keyword):new List<string>(),r.ExitCode);
            }
            if(!File.Exists(stem+".txt")) Data.Save(stem+".txt",String.Join(Environment.NewLine,r.Lines(true))+Environment.NewLine);
            if(!File.Exists(stem+".json")) Data.Save(stem+".json",Data.Encode(r));
        }
        internal static void Migrate(string dir,SqlQueue queue,Action<string> log) {
            if(!Directory.Exists(dir)) return;
            string marker=Path.Combine(dir,"outbox-v2.imported");
            if(File.Exists(marker)) return;
            bool complete=true;
            foreach(string file in Directory.GetFiles(dir,"*.json")) {
                Packet p;
                try { p=Data.Decode<Packet>(Data.Read(file)); }
                catch(ArgumentException) { complete=false; log("旧记录无法读取　"+Path.GetFileName(file)); continue; }
                catch(InvalidOperationException) { complete=false; log("旧记录无法读取　"+Path.GetFileName(file)); continue; }
                if(p==null || p.Report==null) continue;
                if(p.Messages==null || p.Next<0 || p.Next>p.Messages.Count || String.IsNullOrEmpty(p.Report.Key)) {
                    complete=false; log("旧通知缺少投递进度　"+Path.GetFileName(file)); continue;
                }
                var r=p.Report;
                int done=File.Exists(Path.ChangeExtension(file,".sent"))?p.Messages.Count:p.Next;
                r.TaskId=ReportPolicy.Id(r); r.DisplayName=Data.First(r.DisplayName,r.Instance);
                queue.Record(r,r.TaskId,r.DisplayName,"dingtalk",p.Messages,r.ExitCode,done,p.Failures,p.RetryAfter);
            }
            if(complete) {
                try { Data.Save(marker,"2"); }
                catch(IOException) { if(!File.Exists(marker)) throw; }
            }
        }
    }
    class MonitorService {
        public readonly string Root, DebugDir, History;
        readonly Catalog catalog;
        readonly DateTime since;
        readonly string keyword, checkpointDir;
        string signature;
        DateTime nextFull=DateTime.MinValue, lastLive=DateTime.MinValue, lastProbe=DateTime.MinValue;
        DateTime settleAt=DateTime.MaxValue;
        bool wasAlive, hostInCheckpoint, havePending;
        SqlQueue queue;
        DateTime absentSince=DateTime.MinValue;
        readonly HashSet<string> saved=new HashSet<string>();
        internal static DateTime LoadCutoff(string root,string baseDir,DateTime now) {
            string file=Path.Combine(baseDir,"watch-"+Data.Hash(root.ToUpperInvariant()).Substring(0,16)+".txt");
            if(File.Exists(file)) return DateTime.ParseExact(Data.Read(file),"O",System.Globalization.CultureInfo.InvariantCulture);
            now=now.AddTicks(-(now.Ticks%TimeSpan.TicksPerSecond));
            Data.Save(file,now.ToString("O")); return now;
        }
        public MonitorService(string root,string history,DateTime cutoff,string keyword) {
            Root=root; DebugDir=Path.Combine(root,"debug"); History=history; since=cutoff; this.keyword=keyword;
            checkpointDir=Path.Combine(history,"in-progress",Data.Hash(root.ToUpperInvariant()).Substring(0,16));
            catalog=new Catalog(root);
        }
        static void Finish(Plan p) {
            foreach(var t in p.Report.Tasks) {
                if(t.Status!="等待" && t.Status!="执行中") continue;
                bool active=t.Status=="执行中";
                if(p.Report.Signal=="中止" || (p.Report.Signal=="失败" && t.Id==null)) {
                    t.Status=active?"提前结束":(t.Id==null?"未执行":"结果未确认");
                    t.Reason=Data.First(p.Report.PlanReason,"计划中止，软件没有记录该项结果。");
                } else {
                    t.Status="结果未确认";
                    t.Reason=active?"记录到任务启动，缺少最终状态。":"日志缺少该项的执行结果。";
                }
            }
        }
        public int Scan(bool replay,bool includePending,bool exited,DateTime now,Action<string> log) {
            settleAt=DateTime.MaxValue;
            if(!replay && queue==null) {
                var db=Archive.QueueFor(History); Archive.Migrate(History,db,log); queue=db;
            }
            var plans=new Plans(Root,catalog,since);
            var cache=replay?new Dictionary<string,Checkpoint>():Checkpoint.Restore(checkpointDir,plans,since);
            plans.ResolveMarks();
            var active=plans.All.Where(p=>p.QueueSeen && (replay || p.Begin>=since) && !saved.Contains(p.Report.Key)).ToList();
            if(!replay) active.RemoveAll(p=>{
                if(!File.Exists(Archive.Stem(History,p.Report)+".json") || !queue.Contains(p.Report.Key)) return false;
                Checkpoint.Done(checkpointDir,p.Report.Key); saved.Add(p.Report.Key); return true;
            });
            havePending=active.Count!=0;
            if(active.Count==0) return 0;
            hostInCheckpoint=active.Any(p=>cache.ContainsKey(p.Report.Key) && cache[p.Report.Key].HostSeen);
            if(!replay && !includePending && !exited && active.All(p=>p.End==default(DateTime) && p.Limit==DateTime.MaxValue) &&
                !plans.Marks.Any(m=>!m.Used && active.Any(p=>p.Covers(m.At)))) {
                foreach(var p in active) {
                    Checkpoint item;
                    if(cache.TryGetValue(p.Report.Key,out item)) item.MergeEvidence(p);
                    Checkpoint.Write(checkpointDir,p,wasAlive || (item!=null && item.HostSeen));
                }
                return 0;
            }
            new Evidence(active,catalog,DebugDir,log);
            foreach(var p in active) { Checkpoint item; if(cache.TryGetValue(p.Report.Key,out item)) item.MergeEvidence(p); }
            plans.CloseRuns(replay,exited,now);
            int count=0;
            foreach(var p in active) {
                // The UI can announce completion before the framework has flushed the final callbacks.
                bool flushing=!replay && !exited && p.Report.Signal=="结束" && !p.AllTerminal && now<p.End.AddSeconds(9);
                if(flushing && p.End.AddSeconds(9)<settleAt) settleAt=p.End.AddSeconds(9);
                if(p.End==default(DateTime) || flushing) {
                    Checkpoint item;
                    if(!replay) Checkpoint.Write(checkpointDir,p,wasAlive || (cache.TryGetValue(p.Report.Key,out item) && item.HostSeen));
                    continue;
                }
                Finish(p); Archive.Write(History,p.Report,keyword,!replay);
                if(!replay) Checkpoint.Done(checkpointDir,p.Report.Key);
                saved.Add(p.Report.Key); count++;
                log(p.Report.Instance+"　"+p.Report.Verdict()+"　"+p.Report.Tasks.Count(t=>t.Status=="完成")+"/"+(p.Report.Count<0?"?":p.Report.Count.ToString()));
            }
            havePending=active.Any(p=>!saved.Contains(p.Report.Key));
            return count;
        }
        bool IsRunning() {
            foreach(string name in new [] {"MaaEnd","mxu"}) foreach(var proc in Process.GetProcessesByName(name)) {
                try {
                    string dir=Path.GetDirectoryName(proc.MainModule.FileName);
                    if(String.Equals(dir.TrimEnd('\\'),Root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) return true;
                } catch(System.ComponentModel.Win32Exception) {
                    // The game host may be elevated. Its presence keeps crash inference conservative.
                    return true;
                } catch(InvalidOperationException) { } finally { proc.Dispose(); }
            }
            return false;
        }
        public void Tick(Action<string> log) {
            if(!Directory.Exists(DebugDir)) { log("等待 MaaEnd 创建 debug 日志目录。"); return; }
            DateTime now=DateTime.Now;
            bool exit=false;
            if(now>=lastProbe.AddSeconds(4)) {
                bool live=IsRunning();
                if(live) { wasAlive=true; lastLive=now; absentSince=DateTime.MinValue; }
                else if(absentSince==DateTime.MinValue) absentSince=now;
                exit=!live && ((wasAlive && (now-lastLive).TotalSeconds>=9) ||
                    (hostInCheckpoint && havePending && (now-absentSince).TotalSeconds>=12));
                lastProbe=now;
            }
            string sig=String.Join("|",Directory.GetFiles(DebugDir,"*.log").Where(Plans.UiLog).OrderBy(x=>x).Select(f=>{
                var fi=new FileInfo(f); return fi.Name+"/"+fi.Length+"/"+fi.LastWriteTimeUtc.Ticks;
            }));
            bool full=havePending && (now>=nextFull || now>=settleAt);
            if(sig==signature && !exit && !full) return;
            Scan(false,full,exit,now,log);
            signature=sig;
            if(exit) wasAlive=false;
            if(full || nextFull==DateTime.MinValue) nextFull=now.AddSeconds(90);
        }
    }
}
