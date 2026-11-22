using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MaaEndNotify {
    sealed class SqlLease {
        public string EventId, RunId, Mode, Content, Token;
        public int Attempts, Part;
    }

    // The monitor and command-line senders may live in separate processes.
    // Each operation opens its own connection. No HTTP runs under a database lock.
    sealed class SqlQueue {
        const int SchemaVersion=1;
        const int ApplicationId=0x4d454e44;
        readonly string path;

        public SqlQueue(string databasePath) {
            path=Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var db=Open()) {
                Transaction(db,delegate {
                    int version=Int(db.Rows("PRAGMA user_version")[0][0]);
                    int app=Int(db.Rows("PRAGMA application_id")[0][0]);
                    if(version==SchemaVersion && app==ApplicationId) return true;
                    if(version!=0 || app!=0 || db.Rows("SELECT 1 FROM sqlite_master WHERE type='table' LIMIT 1").Count>0)
                        throw new InvalidDataException("通知数据库格式不兼容，原有数据已保留。");
                    db.Exec(@"CREATE TABLE task_runs (
                        run_id TEXT PRIMARY KEY NOT NULL, task_id TEXT NOT NULL, display_name TEXT NOT NULL,
                        host TEXT, start_at TEXT, end_at TEXT, time_zone TEXT, duration_seconds INTEGER,
                        business_status TEXT NOT NULL, exit_code INTEGER, summary TEXT NOT NULL)");
                    db.Exec(@"CREATE TABLE notification_outbox (
                        event_id TEXT PRIMARY KEY NOT NULL, run_id TEXT NOT NULL REFERENCES task_runs(run_id),
                        part INTEGER NOT NULL, mode TEXT NOT NULL, target TEXT NOT NULL, content TEXT NOT NULL,
                        state TEXT NOT NULL CHECK(state IN ('pending','sending','sent','dead')),
                        attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at INTEGER NOT NULL DEFAULT 0,
                        lease_token TEXT, lease_until INTEGER, sent_at INTEGER, last_error TEXT,
                        UNIQUE(run_id,part))");
                    db.Exec("CREATE INDEX outbox_due ON notification_outbox(state,next_attempt_at,lease_until)");
                    db.Exec("PRAGMA application_id="+ApplicationId);
                    db.Exec("PRAGMA user_version="+SchemaVersion);
                    return true;
                });
            }
        }
        SqlDb Open() {
            var db=new SqlDb(path);
            try { db.Exec("PRAGMA foreign_keys=ON"); return db; }
            catch { db.Dispose(); throw; }
        }
        static int Int(string s) { return Int32.Parse(s,CultureInfo.InvariantCulture); }
        static T Transaction<T>(SqlDb db,Func<T> work) {
            db.Exec("BEGIN IMMEDIATE");
            try {
                T result=work(); db.Exec("COMMIT"); return result;
            } catch {
                try { db.Exec("ROLLBACK"); } catch { }
                throw;
            }
        }
        static string TimeValue(string text,string zoneId) {
            DateTime local;
            if(!DateTime.TryParseExact(text,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out local))
                return null;
            if(!String.IsNullOrEmpty(zoneId)) {
                try {
                    var zone=TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                    if(!zone.IsAmbiguousTime(local) && !zone.IsInvalidTime(local))
                        return new DateTimeOffset(local,zone.GetUtcOffset(local)).ToString("o",CultureInfo.InvariantCulture);
                } catch(TimeZoneNotFoundException) { } catch(InvalidTimeZoneException) { }
            }
            // Old packets may lack the originating PC's zone. Keep their actual wall time.
            return local.ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture);
        }
        public bool Contains(string runId) {
            using(var db=Open()) return db.Rows("SELECT 1 FROM task_runs WHERE run_id=?",runId).Count>0;
        }
        public bool Record(Run run,string taskId,string displayName,string mode,IList<string> messages,
            int? exitCode=null,int sentParts=0,int priorFailures=0,long retryAfter=0) {
            if(run==null || String.IsNullOrWhiteSpace(run.Key) || String.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("缺少计划运行编号或任务编号。");
            if(mode!="dingtalk" && mode!="github") throw new ArgumentException("不支持的通知格式。");
            if(messages==null || sentParts<0 || sentParts>messages.Count || priorFailures<0)
                throw new ArgumentException("通知分段或重试状态无效。");
            run.ScrubForStorage();
            string name=Data.Scrub(Data.First(displayName,run.Instance,taskId));
            string summary="选中 "+(run.Count<0?"?":run.Count.ToString())+" 项，完成 "+
                run.Tasks.Count(t=>t.Status=="完成")+" 项。 "+run.PlanReason+" "+
                String.Join(" / ",run.Tasks.Where(t=>t.Status!="完成").Select(t=>t.Name+"〔"+t.Status+"〕"+
                    Data.First(t.Details==null?"":t.Details.FirstOrDefault(),t.Reason)));
            using(var db=Open()) return Transaction(db,delegate {
                if(db.Rows("SELECT 1 FROM task_runs WHERE run_id=?",run.Key).Count!=0) return false;
                db.Exec(@"INSERT INTO task_runs(run_id,task_id,display_name,host,start_at,end_at,time_zone,
                    duration_seconds,business_status,exit_code,summary) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                    run.Key,Data.Scrub(taskId),name,Data.Scrub(run.Host??""),TimeValue(run.Start,run.TimeZoneId),
                    TimeValue(run.End,run.TimeZoneId),run.TimeZoneId,run.DurationSeconds,run.Verdict(),exitCode??run.ExitCode,
                    Data.Clip(Data.Clean(Data.Scrub(summary)),500));
                for(int i=0;i<messages.Count;i++) {
                    if(String.IsNullOrWhiteSpace(messages[i])) throw new InvalidDataException("通知分段为空。");
                    int part=i+1;
                    db.Exec(@"INSERT INTO notification_outbox(event_id,run_id,part,mode,target,content,state,
                        attempts,next_attempt_at) VALUES(?,?,?,?,?,?,?,?,?)",
                        run.Key+"."+part.ToString(CultureInfo.InvariantCulture),run.Key,part,mode,"default",
                        Data.Scrub(messages[i]),i<sentParts?"sent":"pending",i==sentParts?priorFailures:0,
                        i==sentParts?retryAfter:0L);
                }
                return true;
            });
        }
        // Later message parts must never arrive before their predecessors, including a dead predecessor.
        const string BlockedByEarlier=@"EXISTS(SELECT 1 FROM notification_outbox earlier
                WHERE earlier.run_id=q.run_id AND earlier.part<q.part AND earlier.state<>'sent')";
        const string Eligible=@"((q.state='pending' AND q.next_attempt_at<=?) OR (q.state='sending' AND q.lease_until<=?))
                AND NOT "+BlockedByEarlier;
        public bool HasDue(long nowMs) {
            using(var db=Open()) return db.Rows("SELECT 1 FROM notification_outbox q WHERE "+Eligible+
                " LIMIT 1",nowMs,nowMs).Count!=0;
        }
        public SqlLease Claim(long nowMs,int maxAttempts=8,int leaseSeconds=120) {
            if(maxAttempts<1 || leaseSeconds<1) throw new ArgumentOutOfRangeException("重试次数或租约时长");
            using(var db=Open()) return Transaction(db,delegate {
                // A process could crash after every claim. Those crashes consume the attempt budget.
                db.Exec(@"UPDATE notification_outbox SET state='dead',lease_token=NULL,lease_until=NULL,
                    last_error=COALESCE(last_error,'尝试次数已用完，未取得送达回执。')
                    WHERE attempts>=? AND (state='pending' OR (state='sending' AND lease_until<=?))",maxAttempts,nowMs);
                var rows=db.Rows(@"SELECT event_id,run_id,part,mode,content,attempts FROM notification_outbox q
                    WHERE "+Eligible+@" ORDER BY CASE q.state WHEN 'sending' THEN q.lease_until
                    ELSE q.next_attempt_at END,q.rowid LIMIT 1",nowMs,nowMs);
                if(rows.Count==0) return null;
                var r=rows[0];
                var lease=new SqlLease{EventId=r[0],RunId=r[1],Part=Int(r[2]),Mode=r[3],Content=r[4],
                    Attempts=Int(r[5])+1,Token=Guid.NewGuid().ToString("N")};
                db.Exec(@"UPDATE notification_outbox SET state='sending',lease_token=?,lease_until=?,attempts=?
                    WHERE event_id=?",lease.Token,checked(nowMs+leaseSeconds*1000L),lease.Attempts,lease.EventId);
                return lease;
            });
        }
        public bool Ack(SqlLease lease,long nowMs) {
            using(var db=Open()) return db.Exec(@"UPDATE notification_outbox SET state='sent',sent_at=?,
                lease_token=NULL,lease_until=NULL,last_error=NULL WHERE event_id=? AND state='sending'
                AND lease_token=? AND lease_until>?",nowMs,lease.EventId,lease.Token,nowMs)==1;
        }
        public bool Fail(SqlLease lease,long nowMs,string safeError,bool permanent=false,long retryAfterMs=0,int maxAttempts=8) {
            if(maxAttempts<1) throw new ArgumentOutOfRangeException("maxAttempts");
            int jitter=(int)(BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(),0)%5001);
            long wait=Math.Min(1800000L,(30000L<<Math.Min(6,Math.Max(0,lease.Attempts-1)))+jitter);
            long next=Math.Max(nowMs+wait,retryAfterMs);
            using(var db=Open()) return db.Exec(@"UPDATE notification_outbox SET state=CASE WHEN ?=1 OR attempts>=?
                THEN 'dead' ELSE 'pending' END, next_attempt_at=?,lease_token=NULL,lease_until=NULL,last_error=?
                WHERE event_id=? AND state='sending' AND lease_token=? AND lease_until>?",
                permanent?1:0,maxAttempts,next,Data.Clip(Data.Clean(Data.Scrub(safeError??"未取得送达回执。")),300),
                lease.EventId,lease.Token,nowMs)==1;
        }
        public int Retry(string eventId) {
            if(String.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("请指定要重试的通知编号。");
            using(var db=Open()) return db.Exec(@"UPDATE notification_outbox SET state='pending',attempts=0,
                next_attempt_at=0,lease_token=NULL,lease_until=NULL,last_error=NULL
                WHERE event_id=? AND state='dead'",eventId);
        }
        public string Status() {
            using(var db=Open()) {
                var states=db.Rows("SELECT state,COUNT(*) FROM notification_outbox GROUP BY state").ToDictionary(r=>r[0],r=>r[1]);
                var text=new StringBuilder("计划记录 "+db.Rows("SELECT COUNT(*) FROM task_runs")[0][0]+"　");
                foreach(string state in new[]{"pending","sending","sent","dead"}) {
                    string count; states.TryGetValue(state,out count); text.Append(state+"="+(count??"0")+"  ");
                }
                var blocked=db.Rows("SELECT COUNT(*) FROM notification_outbox q WHERE q.state='pending' AND "+BlockedByEarlier)[0][0];
                if(blocked!="0") text.Append("（等待前一段送达 "+blocked+"）");
                foreach(var r in db.Rows(@"SELECT event_id,attempts,last_error FROM notification_outbox
                    WHERE state='dead' ORDER BY rowid"))
                    text.AppendLine().Append("dead "+r[0]+"　尝试 "+r[1]+" 次　"+r[2]);
                foreach(var r in db.Rows(@"SELECT event_id,attempts,last_error FROM notification_outbox
                    WHERE state='pending' AND last_error IS NOT NULL ORDER BY next_attempt_at LIMIT 10"))
                    text.AppendLine().Append("pending "+r[0]+"　尝试 "+r[1]+" 次　"+r[2]);
                return text.ToString().TrimEnd();
            }
        }
    }
}
