using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MaaEndNotify {
    // Only the selected queue and its observed results are needed after log rotation.
    class Checkpoint {
        public Run Report {get;set;}
        public long Began {get;set;}
        public bool HostSeen {get;set;}
        public Dictionary<string,long> Terminal {get;set;}

        internal static Dictionary<string,Checkpoint> Restore(string dir, Plans plans, DateTime cutoff) {
            var saved=new Dictionary<string,Checkpoint>();
            if(!Directory.Exists(dir)) return saved;
            foreach(string file in Directory.GetFiles(dir,"*.json")) {
                var item=Data.Decode<Checkpoint>(Data.Read(file));
                DateTime begin=new DateTime(item.Began);
                if(begin<cutoff) continue;
                var report=item.Report;
                var p=plans.All.FirstOrDefault(x=>x.Report.Key==report.Key);
                if(p==null) {
                    p=new Plan{Begin=begin,QueueSeen=true,Id=report.InstanceId,
                        Report=new Run{Key=report.Key,Instance=report.Instance,InstanceId=report.InstanceId,Start=report.Start,Count=report.Count}};
                    plans.All.Add(p);
                    var next=plans.All.Where(x=>x.Report.Instance==report.Instance && x.Begin>p.Begin).OrderBy(x=>x.Begin).FirstOrDefault();
                    if(next!=null) p.Limit=next.Begin;
                }
                if(p.Report.Count<0) p.Report.Count=report.Count;
                p.Id=Data.First(p.Id,report.InstanceId); p.Report.InstanceId=p.Id;
                for(int i=0;i<report.Tasks.Count && i<p.Report.Count;i++) {
                    if(p.Report.Tasks.Count==i) p.Report.Tasks.Add(new Subtask());
                    var t=p.Report.Tasks[i]; var old=report.Tasks[i];
                    if(!Compatible(t,old)) continue;
                    t.Id=Data.First(t.Id,old.Id); if(t.Id=="") t.Id=null;
                    if(String.IsNullOrEmpty(t.Entry)) { t.Entry=old.Entry; t.Name=old.Name; }
                }
                saved[report.Key]=item;
            }
            return saved;
        }
        static bool Compatible(Subtask t,Subtask old) {
            return (String.IsNullOrEmpty(t.Id) || String.IsNullOrEmpty(old.Id) || t.Id==old.Id) &&
                (String.IsNullOrEmpty(t.Entry) || String.IsNullOrEmpty(old.Entry) || t.Entry==old.Entry);
        }
        internal void MergeEvidence(Plan p) {
            foreach(var t in p.Report.Tasks.Where(x=>!String.IsNullOrEmpty(x.Id))) {
                var old=Report.Tasks.FirstOrDefault(x=>x.Id==t.Id && Compatible(t,x));
                if(old==null) continue;
                long ticks;
                if(Terminal.TryGetValue(t.Id,out ticks)) {
                    var oldEnd=new DateTime(ticks);
                    DateTime current;
                    if(!p.Terminal.TryGetValue(t.Id,out current) || current<oldEnd) {
                        p.Terminal[t.Id]=oldEnd; t.Status=old.Status; t.Reason=old.Reason; t.Details=old.Details;
                        if(p.LastResult<oldEnd) p.LastResult=oldEnd;
                    } else if(current==oldEnd && t.Details.Count==0 && old.Details.Count>0) {
                        t.Details=old.Details;
                        if(t.Status=="完成" && old.Status=="部分完成") { t.Status=old.Status; t.Reason=old.Reason; }
                    }
                } else if(t.Status=="等待" && old.Status=="执行中") { t.Status=old.Status; t.Hint=old.Hint; }
            }
        }
        internal static void Write(string dir, Plan p, bool seen) {
            var item=new Checkpoint{Began=p.Begin.Ticks,Report=p.Report,HostSeen=seen,
                Terminal=p.Terminal.ToDictionary(k=>k.Key,k=>k.Value.Ticks)};
            string file=Path.Combine(dir,p.Report.Key+".json"), data=Data.Encode(item);
            if(!File.Exists(file) || Data.Read(file)!=data) Data.Save(file,data);
        }
        internal static void Done(string dir,string key) {
            string file=Path.Combine(dir,key+".json");
            if(File.Exists(file)) File.Delete(file);
        }
    }
}
