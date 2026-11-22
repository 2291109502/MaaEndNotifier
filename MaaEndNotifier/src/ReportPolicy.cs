using System;
using System.Collections.Generic;
using System.IO;

namespace MaaEndNotify {
    static class ReportPolicy {
        internal static string Id(Run r) { return Data.First(r.InstanceId,"name:"+r.Instance); }
        internal static bool Apply(string file,Run r) {
            r.TaskId=Id(r); r.DisplayName=r.Instance;
            if(!File.Exists(file)) {
                try { Data.Save(file,"{\r\n  \"tasks\": []\r\n}\r\n"); }
                catch(IOException) { if(!File.Exists(file)) throw; }
            }
            var invalid=new InvalidDataException("tasks.json 格式有误，请检查 tasks 列表和每项的 task_id、display_name、report_enabled。");
            object doc;
            try { doc=Data.Decode<object>(Data.Read(file)); }
            catch(ArgumentException) { throw invalid; }
            catch(InvalidOperationException) { throw invalid; }
            object list=Data.Get(doc,"tasks");
            if(!(list is System.Collections.IList)) throw invalid;
            bool enabled=true;
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var rule in Data.Items(list)) {
                var map=Data.Map(rule);
                if(map==null) throw invalid;
                foreach(var k in map.Keys) if(k!="task_id" && k!="display_name" && k!="report_enabled") throw invalid;
                var id=Data.Get(rule,"task_id") as string;
                var flag=Data.Get(rule,"report_enabled"); var name=Data.Get(rule,"display_name");
                if(String.IsNullOrWhiteSpace(id) || !ids.Add(id) || !(flag is bool) || (name!=null && !(name is string))) throw invalid;
                if(id==r.TaskId) { r.DisplayName=Data.First((string)name,r.Instance); enabled=(bool)flag; }
            }
            return enabled;
        }
    }
}
