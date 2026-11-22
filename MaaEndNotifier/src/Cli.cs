using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace MaaEndNotify {
    static class Sender {
        internal static int Flush(SqlQueue queue,RobotConfig config,Action<string> log,Action<string> overridePost=null,
            int limit=20,int budgetSeconds=45,int spacingMs=4000) {
            int claimed=0;
            var watch=Stopwatch.StartNew();
            long budgetMs=budgetSeconds*1000L;
            while(claimed<limit && watch.ElapsedMilliseconds+config.TimeoutMs<=budgetMs) {
                if(claimed>0 && spacingMs>0) {
                    if(watch.ElapsedMilliseconds+spacingMs+config.TimeoutMs>budgetMs || !queue.HasDue(DingTalk.Now())) break;
                    Thread.Sleep(spacingMs);
                    if(watch.ElapsedMilliseconds+config.TimeoutMs>budgetMs) break;
                }
                var lease=queue.Claim(DingTalk.Now());
                if(lease==null) break;
                claimed++;
                try {
                    if(lease.Mode!=config.Mode) {
                        queue.Fail(lease,DingTalk.Now(),"队列消息格式与机器人配置不一致。",true);
                        log(lease.EventId+"　消息格式不匹配，请查看队列状态。"); continue;
                    }
                    string message=lease.Content;
                    if(!String.IsNullOrEmpty(config.Keyword) && !message.Contains(config.Keyword)) message=config.Keyword+"\n"+message;
                    if(overridePost!=null) overridePost(message); else config.Send(message);
                } catch(DeliveryException ex) {
                    long now=DingTalk.Now();
                    if(queue.Fail(lease,now,ex.Message,ex.Permanent,ex.RetryAfterMs>0?now+ex.RetryAfterMs:0)) log(lease.EventId+"　"+ex.Message);
                    if(ex.RetryAfterMs>0) break;
                    continue;
                } catch(Exception) {
                    if(queue.Fail(lease,DingTalk.Now(),"发送未确认，请检查连接和机器人配置。")) log(lease.EventId+"　发送未确认。");
                    continue;
                }
                if(queue.Ack(lease,DingTalk.Now())) log(lease.EventId+"　钉钉接口已接收。");
            }
            return claimed;
        }
    }
    static class Cli {
        internal static RobotConfig Settings(string settingsFile) {
            if(File.Exists(settingsFile)) {
                var saved=Data.Decode<Options>(Data.Read(settingsFile));
                if(saved!=null && !String.IsNullOrEmpty(saved.ProtectedHook))
                    return RobotConfig.FromFields(Options.Open(saved.ProtectedHook),Options.Open(saved.ProtectedSecret),
                        saved.Keyword??"MaaEnd",saved.TimeoutMs>0?saved.TimeoutMs:10000);
            }
            return RobotConfig.Load(RobotConfig.BundledPath);
        }
        public static int Main(string[] args) {
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            if(args.Length==0 || !(args[0]=="status" && args.Length==1 || args[0]=="flush" && args.Length==1 || args[0]=="retry" && args.Length==2)) {
                Console.WriteLine("notify.cmd status | flush | retry EVENT_ID"); return 2;
            }
            try {
                var history=Path.Combine(Program.Home,"history"); var queue=Archive.QueueFor(history);
                Archive.Migrate(history,queue,Console.WriteLine);
                if(args[0]=="status") { Console.WriteLine(queue.Status()); return 0; }
                if(args[0]=="retry") {
                    int n=queue.Retry(args[1]); Console.WriteLine(n==1?"通知已重新入队。":"找不到可重试的失败通知，请用 status 查询完整编号。");return n==1?0:2;
                }
                if(queue.HasDue(DingTalk.Now())) Sender.Flush(queue,Settings(Path.Combine(Program.Home,"settings.json")),Console.WriteLine);
                return 0;
            } catch(Exception ex) { Console.Error.WriteLine(Data.Clean(ex.Message)); return 1; }
        }
    }
}
