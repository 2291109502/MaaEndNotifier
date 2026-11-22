"""Compile the Windows acceptance harness and verify persisted records. No live HTTP."""
import hashlib
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
from contextlib import closing
from datetime import datetime


def db_checks(root):
    primary = root / "acceptance" / "queue.sqlite3"
    with closing(sqlite3.connect(primary)) as db:
        assert db.execute("pragma integrity_check").fetchone() == ("ok",)
        assert db.execute("select count(*) from task_runs").fetchone() == (3,)
        get_hash = lambda label: hashlib.sha256(label.encode()).hexdigest()
        run = db.execute(
            "select task_id,display_name,host,start_at,end_at,duration_seconds,business_status,exit_code,summary "
            "from task_runs where run_id=?", (get_hash("two-part"),)
        ).fetchone()
        task_id, name, host, start, end, duration, result, exit_code, summary = run
        assert (task_id, name, duration, exit_code) == ("fixture-id", "验收", 13, 7)
        assert host and summary and result not in ("success", "全部完成")
        assert all(datetime.fromisoformat(v.replace("Z", "+00:00")).tzinfo is not None for v in (start, end))
        assert db.execute(
            "select state,attempts from notification_outbox where run_id=?", (get_hash("crash-limit"),)
        ).fetchall() == [("dead", 2)]
        assert db.execute(
            "select count(*) from notification_outbox where run_id=?", (get_hash("disabled"),)
        ).fetchone() == (0,)
        assert db.execute(
            "select part,state,mode from notification_outbox where run_id=? order by part", (get_hash("two-part"),)
        ).fetchall() == [(1, "sent", "dingtalk"), (2, "sent", "dingtalk")]
        assert not db.execute("pragma foreign_key_check").fetchall()
        db.execute(
            "create trigger injected before insert on notification_outbox when NEW.part=2 "
            "begin select raise(abort, 'fixture transaction failure'); end"
        )
        db.commit()
    print("OK external SQLite inspection, metadata and retained business exit code")
    senderdb = root / "acceptance" / "sender.sqlite3"
    with closing(sqlite3.connect(senderdb)) as db:
        assert db.execute(
            "select count(*) from notification_outbox where state in ('pending','dead') and last_error is not null"
        ).fetchone()[0] >= 1
    for path in root.rglob("*.sqlite3"):
        with closing(sqlite3.connect(path)) as db:
            dump = "\n".join(db.iterdump())
            assert all(marker not in dump for marker in ("oapi.dingtalk.com/robot/send", "CanaryOnlyQ7uM91", "SEC_fixture_a_921b"))
    print("OK SQL audit for endpoint, token, secret and failed HTTP business reply")
    return primary


def run():
    repo = Path(__file__).resolve().parents[1]
    work = repo / "work"
    work.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="offline-acceptance-", dir=work) as temp:
        folder = Path(temp)
        env = {**os.environ, "TEMP":temp, "TMP":temp}
        candidates = [Path(os.environ["WINDIR"]) / "Microsoft.NET" / arch / "v4.0.30319" / "csc.exe"
                      for arch in ("Framework64", "Framework")]
        csc = next(p for p in candidates if p.exists())
        exe = folder / "Smoke.exe"
        reference = ("System.Core", "System.Web.Extensions", "System.Windows.Forms", "System.Drawing", "System.Security")
        subprocess.run([str(csc), "/nologo", "/codepage:65001", "/target:exe", "/main:MaaEndNotify.Smoke",
                        "/out:" + str(exe), *["/r:" + x + ".dll" for x in reference],
                        *map(str,sorted((repo/"src").glob("*.cs"))), *map(str,sorted((repo/"verify").glob("*.cs")))],
                       check=True, env=env)
        endpoint_logs = [str(Path(sys.argv[1]).resolve())] if len(sys.argv)>1 else []
        root = folder / "fixture"
        subprocess.run([str(exe), str(root), *endpoint_logs], check=True, env=env, timeout=90)
        db = db_checks(root)
        subprocess.run([str(exe), "--atomic", str(db)], check=True, env=env, timeout=20)
        with closing(sqlite3.connect(db)) as conn:
            assert conn.execute("select count(*) from task_runs where task_id='atomic-id'").fetchone()==(0,)
        print("PASS offline acceptance", flush=True)


if __name__=="__main__":
    run()
